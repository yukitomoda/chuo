using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Chuo.Abstractions;
using Chuo.Core;
using Chuo.Core.Items;
using Chuo.Core.Platform;
using Chuo.Core.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.App;

/// <summary>
/// ランチャーのウィンドウ。起動時に一度だけ生成し、以後は表示と非表示を切り替えるだけにする（設計 4.1）。
/// </summary>
public sealed class LauncherWindow : Window
{
    private const double WindowWidth = 680;

    private readonly SearchService _search;
    private readonly IShell _shell;
    private readonly IClipboard _clipboard;
    private readonly ILauncherWindowIntegration _integration;
    private readonly IconCache _icons;
    private readonly SettingsLauncher _settingsLauncher;
    private readonly ItemPreferences _preferences;
    private readonly IOptionsMonitor<ChuoSettings> _settings;
    private readonly ILogger<LauncherWindow> _logger;
    private readonly TextBox _input;
    private readonly ListBox _results;
    private CancellationTokenSource? _searchCancellation;
    private bool _prewarming;
    private bool _hiding;
    private bool _menuOpen;

    public LauncherWindow(
        SearchService search, IShell shell, IClipboard clipboard, ILauncherWindowIntegration integration, IconCache icons,
        SettingsLauncher settingsLauncher, ItemPreferences preferences, IOptionsMonitor<ChuoSettings> settings, ILogger<LauncherWindow> logger)
    {
        _search = search;
        _shell = shell;
        _clipboard = clipboard;
        _integration = integration;
        _icons = icons;
        _settingsLauncher = settingsLauncher;
        _preferences = preferences;
        _settings = settings;
        _logger = logger;

        Title = "chuo";
        Width = WindowWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _input = new TextBox
        {
            PlaceholderText = "検索…",
            FontSize = 22,
            Margin = new Thickness(12),
        };
        _input.TextChanged += (_, _) => OnInputChanged();

        _results = new ListBox
        {
            ItemTemplate = CreateItemTemplate(),
            Margin = new Thickness(12, 0, 12, 12),
            IsVisible = false,
        };
        AddSelectionStyles(_results);
        // 一覧をクリックしても入力欄からフォーカスを移さない
        _results.Focusable = false;
        _results.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                ExecuteSelected(0);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _results.ContextRequested += OnResultContextRequested;

        Content = new StackPanel { Children = { _input, _results } };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            // 右クリックのメニューを開いている間は、ウィンドウが非アクティブになっても隠さない
            if (!_prewarming && !_menuOpen && IsVisible)
            {
                HideLauncher();
            }
        };
    }

    /// <summary>
    /// ウィンドウのハンドルの生成と初回の描画を済ませておく。
    /// 画面外に一度表示してから隠すことで、初回の表示だけが遅くなるのを防ぐ（技術検証 #1）。
    /// </summary>
    public async Task PrewarmAsync()
    {
        _prewarming = true;
        Position = new PixelPoint(-32000, -32000);
        Show();
        if (TryGetPlatformHandle()?.Handle is { } handle)
        {
            _integration.Attach(handle);
        }
        await WaitForFramesAsync(2);
        Hide();
        _prewarming = false;
    }

    /// <summary>ホットキーを押したときの処理。UI スレッドで呼ぶ。</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideLauncher();
        }
        else
        {
            ShowLauncher();
        }
    }

    /// <summary>ランチャーを表示する。<paramref name="query"/> を指定すると入力欄に入れる。UI スレッドで呼ぶ。</summary>
    public void ShowLauncher(string? query = null)
    {
        if (!IsVisible)
        {
            MoveToCursorScreen();
            Show();
            Activate();
            _integration.OnShown(_settings.CurrentValue.Ime.OffOnShow);
        }

        if (query is not null)
        {
            _input.Text = query;
            _input.CaretIndex = query.Length;
        }
        else
        {
            // 前回の入力を残しておき、打ち始めたら置き換わるように全選択する
            _input.SelectAll();
        }
        if (string.IsNullOrEmpty(_input.Text))
        {
            // 入力が空のときはお気に入りを出す。入力が変わらないと検索しないので、ここで検索する
            OnInputChanged();
        }
        _input.Focus();
    }

    /// <summary>ランチャーを隠す。UI スレッドで呼ぶ。</summary>
    public void HideLauncher()
    {
        // Hide() の中で Deactivated が発生し、もう一度呼ばれるので無視する
        if (_hiding || !IsVisible)
        {
            return;
        }

        _hiding = true;
        try
        {
            Hide();
        }
        finally
        {
            _hiding = false;
        }
        TrimMemoryIfEnabled();
    }

    /// <summary>
    /// 隠している間に、使っていないメモリを OS に返す。次の表示が遅くならないことを確認済み（技術検証 #5）。
    /// </summary>
    public void TrimMemoryIfEnabled()
    {
        if (_settings.CurrentValue.Rendering.TrimMemoryOnHide)
        {
            // 隠す処理を妨げないよう、後に回す
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible)
                {
                    _integration.TrimMemory();
                }
            }, DispatcherPriority.Background);
        }
    }

    private async void OnInputChanged(int selectIndex = 0)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new CancellationTokenSource();

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await _search.SearchAsync(_input.Text ?? "", cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "検索に失敗した");
            return;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        _results.ItemsSource = results;
        _results.IsVisible = results.Count > 0;
        _results.SelectedIndex = results.Count > 0 ? Math.Min(selectIndex, results.Count - 1) : -1;
    }

    /// <summary>お気に入りや無視を変えた後に、選択の位置を保ったまま検索し直す。</summary>
    private void Refresh() => OnInputChanged(Math.Max(0, _results.SelectedIndex));

    private void ToggleFavorite(SearchResult result)
    {
        var favorite = !_preferences.Current.IsFavorite(result.GlobalId);
        UpdatePreference(() => _preferences.SetFavorite(result.GlobalId, favorite));
    }

    private void ToggleIgnored(SearchResult result)
    {
        var ignored = !_preferences.Current.IsIgnored(result.GlobalId);
        UpdatePreference(() => _preferences.SetIgnored(result.GlobalId, ignored));
    }

    private void UpdatePreference(Action update)
    {
        try
        {
            update();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "項目ごとのユーザー設定を書き込めなかった");
            return;
        }
        Refresh();
    }

    /// <summary>右クリックで、項目のアクションと、お気に入りや無視の操作のメニューを出す。</summary>
    private void OnResultContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: SearchResult result } container)
        {
            return;
        }
        _results.SelectedItem = result;
        e.Handled = true;

        var menuItems = new List<Control>();
        for (var i = 0; i < result.Item.Actions.Count; i++)
        {
            var index = i;
            menuItems.Add(MenuItemFor(result.Item.Actions[i].Title, ActionGesture(i), () => ExecuteSelected(index)));
        }
        if (menuItems.Count > 0)
        {
            menuItems.Add(new Separator());
        }
        var preference = _preferences.Current;
        menuItems.Add(MenuItemFor(
            preference.IsFavorite(result.GlobalId) ? "お気に入りから外す" : "お気に入りに追加", "Ctrl+D", () => ToggleFavorite(result)));
        menuItems.Add(MenuItemFor(
            preference.IsIgnored(result.GlobalId) ? "無視を解除" : "無視する", "Ctrl+Delete", () => ToggleIgnored(result)));

        var menu = new ContextMenu { ItemsSource = menuItems };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _input.Focus();
        };
        menu.Open(container);
    }

    private static MenuItem MenuItemFor(string header, string? gesture, Action execute)
    {
        // InputGesture では Enter が「Return」と表示されるので、キーの表示は自分で並べる
        var content = new DockPanel { MinWidth = 220 };
        if (gesture is not null)
        {
            var key = new TextBlock { Text = gesture, Opacity = 0.6, Margin = new Thickness(24, 0, 0, 0) };
            DockPanel.SetDock(key, Dock.Right);
            content.Children.Add(key);
        }
        content.Children.Add(new TextBlock { Text = header });
        var item = new MenuItem { Header = content };
        item.Click += (_, _) => execute();
        return item;
    }

    /// <summary>アクションに割り当てたキー（<see cref="OnPreviewKeyDown"/> と合わせる）。</summary>
    private static string? ActionGesture(int index) => index switch
    {
        0 => "Enter",
        1 => "Ctrl+Enter",
        _ => null,
    };

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher();
                break;
            case Key.Down:
                MoveSelection(1);
                break;
            case Key.Up:
                MoveSelection(-1);
                break;
            case Key.Enter:
                // 2つめ以降のアクションは修飾キーに割り当てる（要件定義書 5.2）
                ExecuteSelected(e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1 : 0);
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control && _results.SelectedItem is SearchResult favorite:
                // お気に入りの追加と削除（要件定義書 6.2）
                ToggleFavorite(favorite);
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.Control && _results.SelectedItem is SearchResult ignored:
                // 無視と、その解除（要件定義書 6.3）
                ToggleIgnored(ignored);
                break;
            case Key.OemComma when e.KeyModifiers == KeyModifiers.Control:
                // 設定画面を開く（要件定義書 9.1）
                HideLauncher();
                _settingsLauncher.Open();
                break;
            case Key.Q when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                // TODO: 終了の操作は、設定画面やシステムのコマンドを作るまでの仮のもの
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        var count = _results.ItemCount;
        if (count == 0)
        {
            return;
        }
        _results.SelectedIndex = (_results.SelectedIndex + delta + count) % count;
        _results.ScrollIntoView(_results.SelectedIndex);
    }

    private async void ExecuteSelected(int actionIndex)
    {
        if (_results.SelectedItem is not SearchResult result || actionIndex >= result.Item.Actions.Count)
        {
            return;
        }

        var action = result.Item.Actions[actionIndex];
        ActionResult outcome;
        try
        {
            outcome = await action.Execute(new ActionContext(_shell, _clipboard, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Item} の「{Action}」に失敗した", result.GlobalId, action.Title);
            return;
        }

        switch (outcome)
        {
            case ActionResult.CloseResult:
                HideLauncher();
                break;
            case ActionResult.SetQueryResult setQuery:
                _input.Text = setQuery.Text;
                _input.CaretIndex = setQuery.Text.Length;
                break;
        }
    }

    private const string SelectedBackgroundKey = "ChuoSelectedBackground";
    private const string SelectedPointerOverBackgroundKey = "ChuoSelectedPointerOverBackground";
    private const string PointerOverBackgroundKey = "ChuoPointerOverBackground";

    /// <summary>
    /// 選択している行の背景。Fluent の既定はアクセントの色が濃く文字が読みにくいので、明るい色にする。
    /// マウスを重ねた行は、選択している行より目立たないよう、それより淡くする。
    /// </summary>
    private static void AddSelectionStyles(ListBox list)
    {
        list.Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            [SelectedBackgroundKey] = new SolidColorBrush(Color.Parse("#D3E8FB")),
            [SelectedPointerOverBackgroundKey] = new SolidColorBrush(Color.Parse("#C5E0F9")),
            [PointerOverBackgroundKey] = new SolidColorBrush(Color.Parse("#E6E6E6")),
        };
        list.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        {
            [SelectedBackgroundKey] = new SolidColorBrush(Color.Parse("#2F4F6F")),
            [SelectedPointerOverBackgroundKey] = new SolidColorBrush(Color.Parse("#375C80")),
            [PointerOverBackgroundKey] = new SolidColorBrush(Color.Parse("#3A3A3A")),
        };

        Style BackgroundStyle(Func<Selector?, Selector> state, string key) =>
            new(x => state(x.OfType<ListBoxItem>()).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension(key)) },
            };

        // 後に足したものが優先されるので、選択の状態を後に置く
        list.Styles.Add(BackgroundStyle(x => x!.Class(":pointerover"), PointerOverBackgroundKey));
        list.Styles.Add(BackgroundStyle(x => x!.Class(":selected"), SelectedBackgroundKey));
        list.Styles.Add(BackgroundStyle(x => x!.Class(":selected").Class(":pointerover"), SelectedPointerOverBackgroundKey));
        list.Styles.Add(BackgroundStyle(x => x!.Class(":selected").Class(":pressed"), SelectedPointerOverBackgroundKey));
    }

    /// <summary>アイコン、タイトル、サブタイトルを並べる行のテンプレート。</summary>
    private FuncDataTemplate<SearchResult> CreateItemTemplate() =>
        new((_, _) =>
        {
            var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
            var title = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
            var subtitle = new TextBlock { FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
            var favorite = new TextBlock { Text = "★", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            var row = new DockPanel { Children = { icon, favorite, text } };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(favorite, Dock.Right);

            // 行は使い回されるので、DataContext が変わるたびに中身を差し替える
            row.DataContextChanged += async (_, _) =>
            {
                if (row.DataContext is not SearchResult { Item: var item } result)
                {
                    return;
                }
                favorite.IsVisible = _preferences.Current.IsFavorite(result.GlobalId);
                title.Text = item.Title;
                subtitle.Text = item.Subtitle;
                subtitle.IsVisible = !string.IsNullOrEmpty(item.Subtitle);

                if (item.Icon is null)
                {
                    icon.Source = null;
                    return;
                }
                if (_icons.TryGet(item.Icon, out var cached))
                {
                    icon.Source = cached;
                    return;
                }
                icon.Source = null;
                var loaded = await _icons.LoadAsync(item.Icon);
                // 読み込んでいる間に、行が別の項目に使い回されていたら何もしない
                if (row.DataContext is SearchResult { Item: var current } && ReferenceEquals(current, item))
                {
                    icon.Source = loaded;
                }
            };
            return row;
        }, supportsRecycling: true);

    /// <summary>マウスカーソルのあるモニタの、中央のやや上に移動する。</summary>
    private void MoveToCursorScreen()
    {
        var screen = _integration.GetCursorPosition() is var (x, y)
            ? Screens.ScreenFromPoint(new PixelPoint(x, y))
            : null;
        screen ??= Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)(WindowWidth * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + area.Height / 4);
    }

    private Task WaitForFramesAsync(int frames)
    {
        var tcs = new TaskCompletionSource();

        void Request(int remaining) => RequestAnimationFrame(_ =>
        {
            if (remaining <= 1)
            {
                tcs.TrySetResult();
            }
            else
            {
                Request(remaining - 1);
            }
        });

        Request(frames);
        return tcs.Task;
    }
}
