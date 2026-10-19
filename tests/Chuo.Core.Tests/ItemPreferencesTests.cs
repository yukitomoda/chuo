using System.Text.Json.Nodes;
using Chuo.Abstractions;
using Chuo.Core.Items;
using Chuo.Core.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chuo.Core.Tests;

public sealed class ItemPreferencesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("chuo-items-").FullName;

    private string ItemsFile => Path.Combine(_directory, "items.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ItemPreferences Create() => new(ItemsFile, NullLogger<ItemPreferences>.Instance);

    [Fact]
    public void お気に入りは追加した順に並び_ファイルに保存する()
    {
        using (var preferences = Create())
        {
            preferences.SetFavorite("apps:b", true);
            preferences.SetFavorite("apps:a", true);
            preferences.SetFavorite("apps:b", true); // 追加済みなら順番は変えない
            preferences.SetIgnored("files:C:\\x.txt", true);

            Assert.Equal(["apps:b", "apps:a"], preferences.Current.FavoritesInOrder);
            Assert.True(preferences.Current.IsIgnored("files:C:\\x.txt"));
        }

        using var reloaded = Create();
        Assert.Equal(["apps:b", "apps:a"], reloaded.Current.FavoritesInOrder);
        Assert.True(reloaded.Current.IsIgnored("files:C:\\x.txt"));
    }

    [Fact]
    public void 外すと項目ごと消し_他の値は残す()
    {
        File.WriteAllText(ItemsFile, """
            {
              // 手で書いたエイリアス
              "apps:a": { "aliases": ["x"], "favorite": 3 },
              "apps:b": { "ignore": true },
            }
            """);
        using var preferences = Create();

        preferences.SetFavorite("apps:a", false);
        preferences.SetIgnored("apps:b", false);

        var root = JsonNode.Parse(File.ReadAllText(ItemsFile))!.AsObject();
        Assert.Equal("""{"aliases":["x"]}""", root["apps:a"]!.ToJsonString());
        Assert.False(root.ContainsKey("apps:b"));
        Assert.Empty(preferences.Current.Favorites);
    }

    [Fact]
    public void 識別子の大文字と小文字を区別する()
    {
        using var preferences = Create();
        preferences.SetFavorite("files:/a/File", true);

        Assert.False(preferences.Current.IsFavorite("files:/a/file"));
    }

    [Fact]
    public void 読めないファイルは上書きしない()
    {
        File.WriteAllText(ItemsFile, "{ broken");
        using var preferences = Create();

        Assert.ThrowsAny<Exception>(() => preferences.SetFavorite("apps:a", true));
        Assert.Equal("{ broken", File.ReadAllText(ItemsFile));
    }
}

public sealed class SearchWithPreferencesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("chuo-items-").FullName;
    private readonly ProviderRegistry _registry = new();
    private readonly ItemPreferences _preferences;
    private readonly SearchService _search;

    public SearchWithPreferencesTests()
    {
        _preferences = new ItemPreferences(Path.Combine(_directory, "items.json"), NullLogger<ItemPreferences>.Instance);
        _search = new SearchService(
            _registry, new StaticOptionsMonitor<ChuoSettings>(new ChuoSettings()), NullLogger<SearchService>.Instance, _preferences);
        var catalog = new ProviderCatalog();
        catalog.ReplaceAll(new[] { "Firefox", "Fire Tool", "Files", "Uninstall Fire" }.Select(t => new CatalogItem { Id = t, Title = t }));
        _registry.Add(new CatalogRegistration("apps", "Test", catalog, null, IncludeInGlobal: true));
    }

    public void Dispose()
    {
        _preferences.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<IReadOnlyList<string>> SearchAsync(string text) =>
        [.. (await _search.SearchAsync(text, TestContext.Current.CancellationToken)).Select(r => r.Item.Title)];

    [Fact]
    public void グローバルな識別子から項目を探し_なければ識別子を名前にする()
    {
        var found = _search.Resolve("apps:Firefox");
        Assert.Equal(("Firefox", "apps", "apps:Firefox"), (found.Item.Title, found.PluginId, found.GlobalId));

        var missing = _search.Resolve("old:gone");
        Assert.Equal(("old:gone", "old", "old:gone"), (missing.Item.Title, missing.PluginId, missing.GlobalId));
    }

    [Fact]
    public async Task お気に入りを上に出す()
    {
        Assert.Equal("Firefox", (await SearchAsync("fire"))[0]);

        _preferences.SetFavorite("apps:Fire Tool", true);

        Assert.Equal("Fire Tool", (await SearchAsync("fire"))[0]);
    }

    [Fact]
    public async Task 入力が空ならお気に入りを順に出す()
    {
        Assert.Empty(await SearchAsync(""));

        _preferences.SetFavorite("apps:Files", true);
        _preferences.SetFavorite("apps:Firefox", true);
        _preferences.SetFavorite("apps:Removed", true);

        Assert.Equal(["Files", "Firefox"], await SearchAsync(""));
    }

    [Fact]
    public async Task 無視した項目は出さず_ignored_で出す()
    {
        _preferences.SetIgnored("apps:Uninstall Fire", true);
        _preferences.SetIgnored("apps:Removed", true);

        Assert.DoesNotContain("Uninstall Fire", await SearchAsync("fire"));
        Assert.Equal(["Uninstall Fire", "apps:Removed"], (await SearchAsync(":ignored")).Order(StringComparer.Ordinal));
        Assert.Equal(["Uninstall Fire"], await SearchAsync(":ignored uninst"));
    }

    [Fact]
    public async Task エイリアスの前方一致はあいまいな一致より上に出し_補完にエイリアスを出す()
    {
        _preferences.SetAliases("apps:Files", ["browser", "fx"]);

        var results = await _search.SearchAsync("br", TestContext.Current.CancellationToken);

        Assert.Equal("Files", results[0].Item.Title);
        Assert.Equal("owser", results[0].CompletionSuffix("br"));
        Assert.Equal("Files", (await SearchAsync("f"))[0]);
    }

    [Fact]
    public async Task エイリアスの完全一致はライブクエリの結果より上に固定する()
    {
        _registry.Add(new LiveQueryRegistration("live", "Live", new FixedProvider(), null, new System.Text.RegularExpressions.Regex("^ff$"), TimeSpan.FromSeconds(1)));
        _preferences.SetAliases("apps:Uninstall Fire", ["ff"]);
        _preferences.SetFavorite("apps:Firefox", true);

        Assert.Equal(["Uninstall Fire", "live", "Firefox"], (await SearchAsync("ff")).Take(3));
    }

    [Fact]
    public async Task 無視した項目はエイリアスが一致しても出さない()
    {
        _preferences.SetAliases("apps:Files", ["browser"]);
        _preferences.SetIgnored("apps:Files", true);

        Assert.DoesNotContain("Files", await SearchAsync("browser"));
    }

    [Fact]
    public void エイリアスは空白を除き重複を捨て_空ならキーを消す()
    {
        _preferences.SetAliases("apps:Files", [" browser ", "Browser", "", "fx"]);
        Assert.Equal(["browser", "fx"], _preferences.Current.AliasesOf("apps:Files").Select(a => a.Text));

        _preferences.SetAliases("apps:Files", []);
        Assert.Empty(_preferences.Current.Aliases);
        Assert.DoesNotContain("apps:Files", File.ReadAllText(Path.Combine(_directory, "items.json")));
    }

    private sealed class FixedProvider : ILiveQueryProvider
    {
        public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ResultItem>>([new ResultItem { Id = "live", Title = "live" }]);
    }
}
