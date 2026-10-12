using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Chuo.Abstractions;
using Chuo.Core.Search;
using Chuo.Core.Settings;
using Chuo.Plugins.Shortcuts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chuo.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("chuo-settings-").FullName;

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ファイルがなければ作って書き込む()
    {
        new SettingsStore(SettingsFile).Set("plugins:chuo.shortcuts:settings:folders", new JsonArray("C:\\a", "D:\\b"));

        var configuration = new ConfigurationBuilder().AddJsonFile(SettingsFile).Build();
        Assert.Equal(["C:\\a", "D:\\b"], configuration.GetSection("plugins:chuo.shortcuts:settings:folders").Get<string[]>()!);
    }

    [Fact]
    public void ほかの値とコメントのある既存のファイルに書き足す()
    {
        File.WriteAllText(SettingsFile, """
            {
              // コメント
              "Hotkey": "Ctrl+Space",
              "Plugins": { "chuo.websearch": { "settings": { "engineName": "Bing" } } },
            }
            """);

        new SettingsStore(SettingsFile).Set("plugins:chuo.websearch:settings:urlTemplate", JsonValue.Create("https://example.com/?q={0}"));

        var configuration = new ConfigurationBuilder().AddJsonFile(SettingsFile).Build();
        Assert.Equal("Ctrl+Space", configuration["hotkey"]);
        Assert.Equal("Bing", configuration["plugins:chuo.websearch:settings:engineName"]);
        Assert.Equal("https://example.com/?q={0}", configuration["plugins:chuo.websearch:settings:urlTemplate"]);
        // 大文字と小文字が違う既存のキーには、元の綴りのまま書き足す
        Assert.DoesNotContain("\"plugins\"", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void null_を書き込むとキーを削除する()
    {
        var store = new SettingsStore(SettingsFile);
        store.Set("a:b", JsonValue.Create(1));
        store.Set("a:c", JsonValue.Create(2));

        store.Set("a:b", null);
        store.Set("x:y", null);

        var configuration = new ConfigurationBuilder().AddJsonFile(SettingsFile).Build();
        Assert.Null(configuration["a:b"]);
        Assert.Equal("2", configuration["a:c"]);
        Assert.Null(configuration["x"]);
    }
}

public class SettingsSchemaTests
{
    public enum Mode
    {
        Fast,
        Accurate,
    }

    public sealed class SampleOptions
    {
        [Display(Name = "フォルダ", Order = 2), FolderPath]
        public List<string> Folders { get; set; } = [];

        [Display(Name = "有効", Description = "説明", Order = 1)]
        public bool Enabled { get; set; } = true;

        [Range(1, 50)]
        public int Count { get; set; } = 8;

        public Mode Mode { get; set; } = Mode.Accurate;

        public string Name { get; set; } = "既定";

        public Dictionary<string, string> Unsupported { get; set; } = [];

        public string ReadOnly => "";
    }

    public sealed class Entry
    {
        [Display(Order = 1)]
        public bool Recursive { get; set; } = true;

        [FolderPath]
        public string Path { get; set; } = "";

        public List<string> Nested { get; set; } = [];
    }

    public sealed class ObjectListOptions
    {
        public List<Entry> Entries { get; set; } = [];

        // 文字列の値がない要素は、一覧に出せないので対応しない
        public List<Recursive> NoPrimary { get; set; } = [];
    }

    public sealed class Recursive
    {
        public bool Value { get; set; }
    }

    [Fact]
    public void 設定のクラスのリストは要素の入力欄を持つ()
    {
        var fields = SettingsSchema.Describe(typeof(ObjectListOptions), out var unsupported);

        var entries = Assert.Single(fields);
        Assert.Equal(SettingKind.ObjectList, entries.Kind);
        Assert.Equal(["path", "recursive"], entries.ItemFields!.Select(f => f.Key));
        Assert.Equal("path", entries.PrimaryItemField?.Key);
        Assert.Equal(["Entry.Nested（List`1）", "NoPrimary（List`1）"], unsupported);
    }

    [Fact]
    public void 設定のクラスのリストを読み書きする()
    {
        var entries = SettingsSchema.Describe(typeof(ObjectListOptions)).Single();
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["entries:0:path"] = "/a",
                ["entries:0:recursive"] = "false",
                ["entries:1"] = "/b",
            })
            .Build();

        var items = (List<Dictionary<string, object?>>)entries.Read(section)!;

        Assert.Equal([("/a", false), ("/b", true)], items.Select(i => ((string?)i["path"], (bool?)i["recursive"])));
        items.Add(entries.CreateItem("/c"));
        Assert.Equal(
            """[{"path":"/a","recursive":false},{"path":"/b","recursive":true},{"path":"/c","recursive":true}]""",
            entries.ToJson(items)!.ToJsonString());
        Assert.Equal([], (List<Dictionary<string, object?>>)entries.Read(new ConfigurationBuilder().Build())!);
    }

    [Fact]
    public void プロパティから入力欄を作り_表示の順に並べる()
    {
        var fields = SettingsSchema.Describe(typeof(SampleOptions), out var unsupported);

        Assert.Equal(["count", "mode", "name", "enabled", "folders"], fields.Select(f => f.Key));
        Assert.Equal(
            [SettingKind.Integer, SettingKind.Choice, SettingKind.Text, SettingKind.Boolean, SettingKind.FolderList],
            fields.Select(f => f.Kind));
        Assert.Equal(["Unsupported（Dictionary`2）"], unsupported);

        var enabled = fields.Single(f => f.Key == "enabled");
        Assert.Equal(("有効", "説明", (object?)true), (enabled.DisplayName, enabled.Description, enabled.DefaultValue));
        var count = fields.Single(f => f.Key == "count");
        Assert.Equal((1d, 50d, (object?)8L), (count.Minimum, count.Maximum, count.DefaultValue));
        Assert.Equal(["Fast", "Accurate"], fields.Single(f => f.Key == "mode").Choices);
    }

    [Fact]
    public void 設定の値を読み_なければ既定値を返す()
    {
        var fields = SettingsSchema.Describe(typeof(SampleOptions)).ToDictionary(f => f.Key);
        var section = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["count"] = "20",
                ["folders:0"] = "/a",
                ["folders:1"] = "/b",
            })
            .Build();

        Assert.Equal(20L, fields["count"].Read(section));
        Assert.Equal(["/a", "/b"], (IEnumerable<string>)fields["folders"].Read(section)!);
        Assert.Equal(true, fields["enabled"].Read(section));
        Assert.Equal("Accurate", fields["mode"].Read(section));
    }

    [Fact]
    public void 書き込む値を_JSON_にする()
    {
        var fields = SettingsSchema.Describe(typeof(SampleOptions)).ToDictionary(f => f.Key);

        Assert.Equal("12", fields["count"].ToJson(12m)!.ToJsonString());
        Assert.Equal("""["/a"]""", fields["folders"].ToJson(new List<string> { "/a" })!.ToJsonString());
        Assert.Equal("false", fields["enabled"].ToJson(false)!.ToJsonString());
    }
}

public class ProviderOverrideTests
{
    private readonly Dictionary<string, string?> _values = [];

    private async Task<IReadOnlyList<string>> SearchAsync(string text)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_values).Build();
        var registry = new ProviderRegistry(configuration);
        var catalog = new ProviderCatalog();
        catalog.ReplaceAll([new CatalogItem { Id = "1", Title = "Firefox" }]);
        registry.Add(new CatalogRegistration("chuo.shortcuts", "ShortcutCatalogProvider", catalog, "l", IncludeInGlobal: true));
        var search = new SearchService(registry, new StaticOptionsMonitor<ChuoSettings>(new ChuoSettings()), NullLogger<SearchService>.Instance);
        var results = await search.SearchAsync(text, TestContext.Current.CancellationToken);
        return [.. results.Select(r => r.Item.Title)];
    }

    private const string Prefix = "plugins:chuo.shortcuts:providers:ShortcutCatalogProvider:";

    [Fact]
    public async Task 上書きがなければ宣言した値を使う()
    {
        Assert.Equal(["Firefox"], await SearchAsync("fire"));
        Assert.Equal(["Firefox"], await SearchAsync("l fire"));
    }

    [Fact]
    public async Task 全体検索に含めない設定にすると_キーワードを付けたときだけ出す()
    {
        _values[Prefix + "includeInGlobal"] = "false";

        Assert.Empty(await SearchAsync("fire"));
        Assert.Equal(["Firefox"], await SearchAsync("l fire"));
    }

    [Fact]
    public async Task キーワードを変えられる()
    {
        _values[Prefix + "keyword"] = "sc";
        _values[Prefix + "includeInGlobal"] = "false";

        Assert.Empty(await SearchAsync("l fire"));
        Assert.Equal(["Firefox"], await SearchAsync("sc fire"));
    }
}

public sealed class ShortcutScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("chuo-shortcuts-").FullName;

    public ShortcutScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        foreach (var name in new[] { "a.lnk", "b.EXE", "c.txt", Path.Combine("sub", "d.url") })
        {
            File.WriteAllText(Path.Combine(_root, name), "");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static IEnumerable<string> Names(IEnumerable<string> paths) => paths.Select(Path.GetFileName).Order()!;

    [Fact]
    public void 対象の拡張子のファイルだけを_サブフォルダも含めて探す() =>
        Assert.Equal(["a.lnk", "b.EXE", "d.url"], Names(ShortcutScanner.Scan([Folder(_root)], ShortcutScanner.WindowsExtensions)));

    [Fact]
    public void サブフォルダを対象にしない() =>
        Assert.Equal(["a.lnk", "b.EXE"], Names(ShortcutScanner.Scan([Folder(_root, false)], ShortcutScanner.WindowsExtensions)));

    [Fact]
    public void 重なったフォルダと存在しないフォルダを扱う() =>
        Assert.Equal(
            ["a.lnk", "b.EXE", "d.url"],
            Names(ShortcutScanner.Scan(
                [Folder(_root, false), Folder(Path.Combine(_root, "sub")), Folder(_root), Folder(Path.Combine(_root, "none")), Folder(" ")],
                ShortcutScanner.WindowsExtensions)));

    [Fact]
    public void 以前の文字列のリストの設定も読める()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["folders:0"] = "/a",
                ["folders:1:path"] = "/b",
                ["folders:1:includeSubfolders"] = "false",
            })
            .Build();

        var options = configuration.Get<ShortcutsOptions>()!;
        ShortcutsPlugin.ReadPlainFolders(options, configuration);
        var folders = options.Folders;

        Assert.Equal([("/a", true), ("/b", false)], folders.Select(f => (f.Path, f.IncludeSubfolders)));
    }

    private static ShortcutFolder Folder(string path, bool includeSubfolders = true) =>
        new() { Path = path, IncludeSubfolders = includeSubfolders };
}
