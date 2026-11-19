using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Chuo.Abstractions;
using Chuo.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Chuo.App;

/// <summary>
/// プラグインからの通知を、フォーカスを奪わない最前面のウィンドウで画面の右下に並べて出す（設計 8 章）。
/// ユーザーが閉じるか操作を選ぶまで残る。
/// </summary>
public sealed class NotificationService(ISystemSound sound, ILogger<NotificationService> logger) : INotifier
{
    private const double MarginDip = 16;
    private const double GapDip = 10;

    private readonly List<NotificationWindow> _open = [];

    public void Show(Notification notification) =>
        Dispatcher.UIThread.Post(() => ShowOnUiThread(notification));

    /// <summary>開いている通知をすべて閉じる。アプリの終了時に UI スレッドで呼ぶ。</summary>
    public void CloseAll()
    {
        foreach (var window in _open.ToArray())
        {
            window.Close();
        }
    }

    private void ShowOnUiThread(Notification notification)
    {
        var window = new NotificationWindow(notification, logger);
        _open.Add(window);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
            {
                Reposition();
            }
        };
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Reposition();
        };
        window.Show();
        Reposition();

        if (notification.PlaySound)
        {
            try
            {
                sound.PlayNotification();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "通知音を鳴らせなかった");
            }
        }
    }

    /// <summary>開いている通知を、メインの画面の右下に下から積み上げて並べる。</summary>
    private void Reposition()
    {
        if (_open.Count == 0)
        {
            return;
        }
        var screen = _open[0].Screens.Primary ?? _open[0].Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var margin = (int)Math.Round(MarginDip * scale);
        var gap = (int)Math.Round(GapDip * scale);
        var bottom = area.Y + area.Height - margin;

        foreach (var window in _open)
        {
            var width = (int)Math.Round((window.Bounds.Width > 0 ? window.Bounds.Width : window.Width) * scale);
            var height = (int)Math.Round((window.Bounds.Height > 0 ? window.Bounds.Height : 110) * scale);
            var x = area.X + area.Width - width - margin;
            var y = bottom - height;
            window.Position = new PixelPoint(x, y);
            bottom = y - gap;
        }
    }
}

/// <summary>通知1件分のウィンドウ。フォーカスを奪わずに最前面に出し、閉じるまで残す。</summary>
internal sealed class NotificationWindow : Window
{
    private const string BackgroundKey = "ChuoNotifyBackground";
    private const string BorderKey = "ChuoNotifyBorder";
    private const string AccentKey = "ChuoNotifyAccent";
    private const string TextKey = "ChuoNotifyText";
    private const string SubtextKey = "ChuoNotifySubtext";

    public NotificationWindow(Notification notification, ILogger logger)
    {
        Title = notification.Title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            [BackgroundKey] = new SolidColorBrush(Color.Parse("#FFFFFF")),
            [BorderKey] = new SolidColorBrush(Color.Parse("#C8C8C8")),
            [AccentKey] = new SolidColorBrush(Color.Parse("#0067C0")),
            [TextKey] = new SolidColorBrush(Color.Parse("#E4000000")),
            [SubtextKey] = new SolidColorBrush(Color.Parse("#9E000000")),
        };
        Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        {
            [BackgroundKey] = new SolidColorBrush(Color.Parse("#2B2B2B")),
            [BorderKey] = new SolidColorBrush(Color.Parse("#4A4A4A")),
            [AccentKey] = new SolidColorBrush(Color.Parse("#60CDFF")),
            [TextKey] = new SolidColorBrush(Color.Parse("#FFFFFFFF")),
            [SubtextKey] = new SolidColorBrush(Color.Parse("#B3FFFFFF")),
        };

        var body = new StackPanel { Margin = new Thickness(14, 12), Spacing = 6 };
        var title = new TextBlock
        {
            Text = notification.Title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        BindResource(title, TextBlock.ForegroundProperty, TextKey);
        body.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(notification.Message))
        {
            var message = new TextBlock
            {
                Text = notification.Message,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            BindResource(message, TextBlock.ForegroundProperty, SubtextKey);
            body.Children.Add(message);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (notification.Actions is not null)
        {
            foreach (var action in notification.Actions)
            {
                var button = new Button { Content = action.Label };
                button.Click += (_, _) =>
                {
                    try
                    {
                        action.Execute();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "通知の操作「{Label}」に失敗した", action.Label);
                    }
                    Close();
                };
                buttons.Children.Add(button);
            }
        }
        var close = new Button { Content = "閉じる", Classes = { "accent" } };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        var accent = new Border { Width = 4 };
        BindResource(accent, Border.BackgroundProperty, AccentKey);
        DockPanel.SetDock(accent, Dock.Left);

        var layout = new DockPanel { Children = { accent, body } };
        var frame = new Border { BorderThickness = new Thickness(1), Child = layout };
        BindResource(frame, Border.BackgroundProperty, BackgroundKey);
        BindResource(frame, Border.BorderBrushProperty, BorderKey);
        Content = frame;
    }

    private static void BindResource(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key, value => value ?? AvaloniaProperty.UnsetValue));
}
