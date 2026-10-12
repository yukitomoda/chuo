using Chuo.Abstractions;
using Chuo.Core.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chuo.Core.Tests;

public class SearchServiceTests
{
    private readonly ProviderRegistry _registry = new();
    private readonly ChuoSettings _settings = new();

    private SearchService CreateService() =>
        new(_registry, new StaticOptionsMonitor<ChuoSettings>(_settings), NullLogger<SearchService>.Instance);

    private ProviderCatalog AddCatalog(string pluginId, string? keyword, bool includeInGlobal, params string[] titles)
    {
        var catalog = new ProviderCatalog();
        catalog.ReplaceAll(titles.Select(t => new CatalogItem { Id = t, Title = t }));
        _registry.Add(new CatalogRegistration(pluginId, "Test", catalog, keyword, includeInGlobal));
        return catalog;
    }

    [Fact]
    public async Task 全体検索は全体検索に含めるカタログだけを対象にする()
    {
        AddCatalog("apps", null, true, "Firefox", "Files");
        AddCatalog("files", "f", false, "firefox.txt");

        var results = await CreateService().SearchAsync("fire", TestContext.Current.CancellationToken);

        Assert.Equal(["Firefox"], results.Select(r => r.Item.Title));
        Assert.Equal("apps:Firefox", results[0].GlobalId);
    }

    [Fact]
    public async Task キーワードを付けるとそのプロバイダだけを検索する()
    {
        AddCatalog("apps", null, true, "Firefox");
        AddCatalog("files", "f", false, "firefox.txt");

        var results = await CreateService().SearchAsync("f fire", TestContext.Current.CancellationToken);

        Assert.Equal(["firefox.txt"], results.Select(r => r.Item.Title));
    }

    [Fact]
    public async Task 登録されていない語はキーワードとして扱わない()
    {
        AddCatalog("apps", null, true, "Visual Studio Code");

        var results = await CreateService().SearchAsync("visual code", TestContext.Current.CancellationToken);

        Assert.Single(results);
    }

    [Fact]
    public async Task グローバルトリガーに一致したライブクエリの結果を上に置く()
    {
        AddCatalog("apps", null, true, "123 Free Games");
        var provider = new EchoProvider();
        _registry.Add(new LiveQueryRegistration("calc", "Echo", provider, "=", new System.Text.RegularExpressions.Regex(@"^[\d+]+$"), TimeSpan.FromSeconds(1)));

        var results = await CreateService().SearchAsync("1+2", TestContext.Current.CancellationToken);

        Assert.Equal("echo:1+2", results[0].Item.Title);
        Assert.Null(provider.LastQuery!.Keyword);
    }

    [Fact]
    public async Task 時間内に返さないライブクエリは結果に含めない()
    {
        _registry.Add(new LiveQueryRegistration("slow", "Slow", new SlowProvider(), "s", null, TimeSpan.FromMilliseconds(50)));
        _registry.Add(new LiveQueryRegistration("echo", "Echo", new EchoProvider(), "s", null, TimeSpan.FromSeconds(1)));

        var results = await CreateService().SearchAsync("s abc", TestContext.Current.CancellationToken);

        Assert.Equal(["echo:abc"], results.Select(r => r.Item.Title));
    }

    [Fact]
    public async Task 最大件数で切り詰める()
    {
        _settings.MaxResults = 2;
        AddCatalog("apps", null, true, "a1", "a2", "a3");

        var results = await CreateService().SearchAsync("a", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task カタログの変更は次の検索に反映される()
    {
        var catalog = AddCatalog("apps", null, true, "Firefox");
        catalog.AddOrUpdate([new CatalogItem { Id = "chrome", Title = "Chrome" }]);
        catalog.Remove(["Firefox"]);
        var service = CreateService();

        Assert.Empty(await service.SearchAsync("fire", TestContext.Current.CancellationToken));
        Assert.Single(await service.SearchAsync("chr", TestContext.Current.CancellationToken));
    }

    private sealed class EchoProvider : ILiveQueryProvider
    {
        public Query? LastQuery { get; private set; }

        public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return ValueTask.FromResult<IReadOnlyList<ResultItem>>([new ResultItem { Id = "1", Title = $"echo:{query.Text}" }]);
        }
    }

    private sealed class SlowProvider : ILiveQueryProvider
    {
        public async ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }
    }
}

/// <summary>値が変わらない <see cref="IOptionsMonitor{TOptions}"/>。</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
