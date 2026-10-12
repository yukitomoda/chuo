using Microsoft.Extensions.DependencyInjection;

namespace Chuo.App;

/// <summary>設定画面を開く。開いていれば前面に出す。UI スレッドで使う。</summary>
public sealed class SettingsLauncher(IServiceProvider services)
{
    private SettingsWindow? _window;

    /// <summary>設定画面を閉じたときに呼ばれる。ランチャーが隠れていればメモリを OS に返すのに使う。</summary>
    public event Action? Closed;

    public void Open()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        _window = services.GetRequiredService<SettingsWindow>();
        _window.Closed += (_, _) =>
        {
            _window = null;
            Closed?.Invoke();
        };
        _window.Show();
        _window.Activate();
    }
}
