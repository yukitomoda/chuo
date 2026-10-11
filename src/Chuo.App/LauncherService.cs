using Avalonia.Threading;
using Chuo.Abstractions;

namespace Chuo.App;

/// <summary>プラグインにランチャーの操作を公開する。どのスレッドから呼んでもよい。</summary>
public sealed class LauncherService : ILauncher
{
    private LauncherWindow? _window;

    public LauncherWindow Window => _window ?? throw new InvalidOperationException("ウィンドウがまだ作られていません。");

    public void Attach(LauncherWindow window) => _window = window;

    public void Show(string? query = null) => Dispatcher.UIThread.Post(() => Window.ShowLauncher(query));

    public void Hide() => Dispatcher.UIThread.Post(() => Window.HideLauncher());
}

/// <summary>Avalonia のクリップボードを使う実装。テキストは OS を問わず扱える。</summary>
public sealed class AvaloniaClipboard(LauncherService launcher) : IClipboard
{
    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default) =>
        await Dispatcher.UIThread.InvokeAsync(async () =>
            launcher.Window.Clipboard is { } clipboard ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) : null);

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (launcher.Window.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            }
        });
}
