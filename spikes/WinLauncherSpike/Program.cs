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

        var win32Options = new Win32PlatformOptions { ShouldRenderOnUIThread = Options.RenderOnUiThread };
        if (Options.RenderingMode is { } renderingMode)
        {
            win32Options.RenderingMode = [renderingMode];
        }
        if (Options.CompositionMode is { } compositionMode)
        {
            win32Options.CompositionMode = [compositionMode];
        }

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(win32Options)
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}
