using Chuo.Abstractions;
using Chuo.Core.Plugins;
using Chuo.Core.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chuo.Core.Tests;

public class PluginLoadingTests
{
    private static readonly string PluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");

    [Theory]
    [InlineData("1.x", true)]
    [InlineData("1.0", true)]
    [InlineData("1.1", false)]
    [InlineData("2.x", false)]
    [InlineData("1", true)]
    public void hostApiの互換性(string required, bool expected) =>
        Assert.Equal(expected, new PluginManifest("id", "name", "1.0.0", "a.dll", required).IsCompatible(new Version(1, 0)));

    [Fact]
    public async Task 同梱プラグインを読み込んで検索に使い_停止するとアンロードされる()
    {
        var weak = await LoadSearchAndStopAsync(TestContext.Current.CancellationToken);

        // スレッドプールのスレッドがプラグインのコードから抜けきるまで、少し待つことがある
        for (var i = 0; i < 50 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.False(weak.IsAlive, "プラグインの ALC がアンロードされなかった");
    }

    /// <summary>プラグインへの参照がテストのメソッドに残らないよう、別のメソッドにする。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadSearchAndStopAsync(CancellationToken cancellationToken)
    {
        var registry = new ProviderRegistry();
        var options = Options.Create(new PluginManagerOptions());
        options.Value.BundledDirectories.Add(PluginsDirectory);
        var manager = new PluginManager(
            registry, new ServiceCollection().BuildServiceProvider(), new ConfigurationBuilder().Build(), options,
            NullLogger<PluginManager>.Instance);
        var search = new SearchService(registry, new StaticSettings(), NullLogger<SearchService>.Instance);

        await manager.StartAsync(cancellationToken);
        var results = await search.SearchAsync("1+2*3", cancellationToken);
        var weak = new WeakReference(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(
            registry.LiveQueries[0].Provider.GetType().Assembly));
        await manager.StopAsync(cancellationToken);

        Assert.Equal("7", Assert.Single(results).Item.Title);
        Assert.Equal("chuo.calculator", results[0].PluginId);
        Assert.Empty(registry.LiveQueries);
        return weak;
    }

    [Fact]
    public async Task 無効にしたプラグインは読み込まず_有効にすると読み込む()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new ProviderRegistry();
        var options = Options.Create(new PluginManagerOptions());
        options.Value.BundledDirectories.Add(PluginsDirectory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PluginManager.EnabledKey("chuo.calculator")] = "false" })
            .Build();
        var manager = new PluginManager(
            registry, new ServiceCollection().BuildServiceProvider(), configuration, options, NullLogger<PluginManager>.Instance);

        await manager.StartAsync(cancellationToken);
        try
        {
            var disabled = Assert.Single(manager.Plugins, p => p.Id == "chuo.calculator");
            Assert.False(disabled.IsEnabled);
            Assert.Empty(disabled.Providers);
            Assert.DoesNotContain(registry.LiveQueries, q => q.PluginId == "chuo.calculator");

            var changed = new TaskCompletionSource();
            manager.PluginsChanged += () => changed.TrySetResult();
            configuration[PluginManager.EnabledKey("chuo.calculator")] = "true";
            configuration.Reload();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            var enabled = Assert.Single(manager.Plugins, p => p.Id == "chuo.calculator");
            Assert.True(enabled.IsEnabled);
            Assert.Contains(registry.LiveQueries, q => q.PluginId == "chuo.calculator");

            changed = new TaskCompletionSource();
            configuration[PluginManager.EnabledKey("chuo.calculator")] = "false";
            configuration.Reload();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Assert.False(Assert.Single(manager.Plugins, p => p.Id == "chuo.calculator").IsEnabled);
            Assert.DoesNotContain(registry.LiveQueries, q => q.PluginId == "chuo.calculator");
        }
        finally
        {
            await manager.StopAsync(cancellationToken);
        }
    }

    private sealed class StaticSettings : IOptionsMonitor<ChuoSettings>
    {
        public ChuoSettings CurrentValue { get; } = new();

        public ChuoSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ChuoSettings, string?> listener) => null;
    }
}
