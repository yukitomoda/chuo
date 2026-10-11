using Microsoft.Extensions.Hosting;

namespace Chuo.Core.Hosting;

/// <summary>
/// 何もしない <see cref="IHostLifetime"/>。既定の ConsoleLifetime は Ctrl+C を拾って起動時のメッセージを出すが、
/// chuo の本体とプラグインのホストの寿命は UI（Avalonia）が決めるので使わない。
/// </summary>
public sealed class NullHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
