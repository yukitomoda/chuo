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
}
