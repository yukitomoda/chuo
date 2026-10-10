using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;

namespace WinLauncherSpike;

internal static class Program
{
    /// <summary>プロセス起動時点のタイムスタンプ。起動から待機状態になるまでの時間の計測に使う。</summary>
    public static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    public static SpikeOptions Options { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("この検証は Windows 専用です。");
            return 1;
        }

        Options = SpikeOptions.Parse(args);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}
