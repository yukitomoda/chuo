using Avalonia;

namespace WinLauncherSpike;

/// <summary>コマンドライン引数で指定する検証の条件。</summary>
public sealed class SpikeOptions
{
    /// <summary>グローバルホットキー（例: "Alt+Space"、"Ctrl+Shift+K"）。</summary>
    public string Hotkey { get; private set; } = "Alt+Space";

    /// <summary>表示時に IME をオフにするか。</summary>
    public bool ImeOff { get; private set; } = true;

    /// <summary>非表示にするときに IME を表示前の状態に戻すか。</summary>
    public bool ImeRestore { get; private set; }

    /// <summary>IME を操作する方法。</summary>
    public ImeMethod ImeMethod { get; private set; } = ImeMethod.ImeWnd;

    /// <summary>描画方式（null なら Avalonia の既定）。</summary>
    public Win32RenderingMode? RenderingMode { get; private set; }

    /// <summary>ウィンドウの合成方式（null なら Avalonia の既定）。</summary>
    public Win32CompositionMode? CompositionMode { get; private set; }

    /// <summary>描画を専用のスレッドではなく UI スレッドで行うか。</summary>
    public bool RenderOnUiThread { get; private set; }

    /// <summary>非表示にしたときに GC を実行し、ワーキングセットを OS に返すか。</summary>
    public bool TrimOnHide { get; private set; }

    public static SpikeOptions Parse(string[] args)
    {
        var options = new SpikeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hotkey" when i + 1 < args.Length:
                    options.Hotkey = args[++i];
                    break;
                case "--no-ime-off":
                    options.ImeOff = false;
                    break;
                case "--ime-restore":
                    options.ImeRestore = true;
                    break;
                case "--render" when i + 1 < args.Length:
                    options.RenderingMode = args[++i].ToLowerInvariant() switch
                    {
                        "angle" => Win32RenderingMode.AngleEgl,
                        "wgl" => Win32RenderingMode.Wgl,
                        "software" => Win32RenderingMode.Software,
                        "vulkan" => Win32RenderingMode.Vulkan,
                        var other => throw new ArgumentException($"--render の値が不正です: {other}"),
                    };
                    break;
                case "--composition" when i + 1 < args.Length:
                    options.CompositionMode = args[++i].ToLowerInvariant() switch
                    {
                        "winui" => Win32CompositionMode.WinUIComposition,
                        "dcomp" => Win32CompositionMode.DirectComposition,
                        "lowlatency" => Win32CompositionMode.LowLatencyDxgiSwapChain,
                        "redirection" => Win32CompositionMode.RedirectionSurface,
                        var other => throw new ArgumentException($"--composition の値が不正です: {other}"),
                    };
                    break;
                case "--render-on-ui-thread":
                    options.RenderOnUiThread = true;
                    break;
                case "--trim-on-hide":
                    options.TrimOnHide = true;
                    break;
                case "--ime-method" when i + 1 < args.Length:
                    options.ImeMethod = Enum.Parse<ImeMethod>(args[++i], ignoreCase: true);
                    break;
            }
        }
        return options;
    }
}
