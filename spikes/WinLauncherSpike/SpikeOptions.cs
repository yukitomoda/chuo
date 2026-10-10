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
            }
        }
        return options;
    }
}
