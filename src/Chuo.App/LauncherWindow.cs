using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
public sealed partial class LauncherWindow : Window
{
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
    private readonly TextBlock _completionHint;
    private readonly TextBlock _panelTitle;
    private readonly TextBlock _error;
    private readonly ListBox _panel;
    private readonly StackPanel _panelArea;
    private TextPresenter? _inputPresenter;
    private CancellationTokenSource? _searchCancellation;
    private bool _prewarming;
    private bool _hiding;
    private bool _menuOpen;
    private bool _dialogOpen;
    /// <summary>押されている修飾キー。Ctrl+Shift の間、管理者として実行することを行の色で示すのに使う。</summary>
    private KeyModifiers _heldModifiers;

    public LauncherWindow(
        SearchService search, IShell shell, IClipboard clipboard, ILauncherWindowIntegration integration, IconCache icons,
        SettingsLauncher settingsLauncher, ItemPreferences preferences, IOptionsMonitor<ChuoSettings> settings, UpdateService updates,
        SkinService skins, ILogger<LauncherWindow> logger)
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
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _input = new TextBox
        {
            PlaceholderText = "検索…",
            Margin = new Thickness(12),
        };
        BindResource(_input, FontSizeProperty, InputFontSizeKey);
        _input.TextChanged += (_, _) =>
        {
            // 打ち始めたらアクションパネルを閉じ、検索に戻る
            ClosePanel();
            ClearError();
            OnInputChanged();
            UpdateCompletionHint();
        };
        _input.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.SelectionStartProperty
                || e.Property == TextBox.SelectionEndProperty)
            {
                UpdateCompletionHint();
            }
        };
        _completionHint = new TextBlock { Opacity = 0.4, IsHitTestVisible = false, IsVisible = false };
        _input.TemplateApplied += (_, e) => AttachCompletionHint(e.NameScope.Find<TextPresenter>("PART_TextPresenter"));

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
        _results.SelectionChanged += (_, _) =>
        {
            UpdateCompletionHint();
            ClearError();
            UpdateAdminHighlight();
        };
        // アクションが失敗したことを知らせる1行（理由はログに書く）
        _error = new TextBlock { Margin = new Thickness(16, 0, 16, 8), IsVisible = false, TextTrimming = TextTrimming.CharacterEllipsis };
        BindResource(_error, TextBlock.ForegroundProperty, ErrorKey);

        _panelTitle = new TextBlock { Opacity = 0.6, Margin = new Thickness(16, 0, 16, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        BindResource(_panelTitle, TextBlock.FontSizeProperty, PanelTitleFontSizeKey);
        BindResource(_panelTitle, TextBlock.ForegroundProperty, SubtextKey);
        _panel = new ListBox { ItemTemplate = CreatePanelEntryTemplate(), Margin = new Thickness(12, 0, 12, 12), Focusable = false };
        AddSelectionStyles(_panel);
        _panel.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                ExecutePanelEntry();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _panelArea = new StackPanel { IsVisible = false, Children = { _panelTitle, _panel } };

        // 背景の画像は、大きさに影響しないようブラシで敷く（スキン、設計 8.1）
        _skinRoot = new Border { Child = new StackPanel { Children = { _input, _error, _results, _panelArea, CreateUpdateNotice(updates) } } };
        Content = _skinRoot;
        AttachSkins(skins);

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => TrackModifiers(e, pressed: true), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => TrackModifiers(e, pressed: false), RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) =>
        {
            // 離れている間に修飾キーを離しても KeyUp は来ないので、押していないことにする
            _heldModifiers = KeyModifiers.None;
            UpdateAdminHighlight();
            // 右クリックのメニューやダイアログを開いている間は、ウィンドウが非アクティブになっても隠さない
            if (!_prewarming && !_menuOpen && !_dialogOpen && IsVisible)
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
            MoveToScreen();
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
        ClosePanel();
        ClearError();
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

    /// <summary>
    /// 補完の続きを、入力欄の文字と同じ位置に重ねて表示する。入力欄のテンプレートの中で文字を描く TextPresenter の隣に置き、
    /// 位置、フォント、横のスクロールをそろえる。
    /// </summary>
    private void AttachCompletionHint(TextPresenter? presenter)
    {
        (_completionHint.Parent as Panel)?.Children.Remove(_completionHint);
        _inputPresenter = presenter;
        if (presenter?.Parent is not Panel panel)
        {
            _logger.LogWarning("入力欄のテンプレートに補完を表示する場所がない");
            return;
        }
        _completionHint.HorizontalAlignment = presenter.HorizontalAlignment;
        _completionHint.VerticalAlignment = presenter.VerticalAlignment;
        _completionHint.Margin = presenter.Margin;
        panel.Children.Insert(panel.Children.IndexOf(presenter), _completionHint);
        presenter.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextPresenter.PreeditTextProperty)
            {
                UpdateCompletionHint();
            }
        };
    }

    /// <summary>選んでいる項目の補完の続き。カーソルが末尾にあり、範囲を選んでおらず、IME で変換していないときだけ出す。</summary>
    private string? CompletionSuffix()
    {
        var text = _input.Text ?? "";
        return _results.SelectedItem is SearchResult result
            && _input.SelectionStart == _input.SelectionEnd && _input.CaretIndex == text.Length
            && string.IsNullOrEmpty(_inputPresenter?.PreeditText)
                ? result.CompletionSuffix(text)
                : null;
    }

    private void UpdateCompletionHint()
    {
        if (CompletionSuffix() is not { } suffix)
        {
            _completionHint.IsVisible = false;
            return;
        }
        // 入力した部分は透明にして幅だけを取り、続きを入力の後ろにそろえる
        _completionHint.Inlines =
        [
            new Run(_input.Text) { Foreground = Brushes.Transparent },
            new Run(suffix),
        ];
        _completionHint.IsVisible = true;
    }

    /// <summary>選んでいる項目の補完を入力欄に入れる。</summary>
    private void AcceptCompletion()
    {
        if (CompletionSuffix() is not null && _results.SelectedItem is SearchResult { Completion: { } completion })
        {
            _input.Text = completion;
            _input.CaretIndex = completion.Length;
        }
    }

    /// <param name="selectId">検索し直した後に選ぶ項目のグローバルな識別子。結果になければ <paramref name="selectIndex"/> の位置を選ぶ。</param>
    private async void OnInputChanged(int selectIndex = 0, string? selectId = null)
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
        var found = selectId is null ? -1 : results.ToList().FindIndex(r => r.GlobalId == selectId);
        _results.SelectedIndex = results.Count == 0 ? -1 : found >= 0 ? found : Math.Min(selectIndex, results.Count - 1);
        if (_results.SelectedIndex >= 0)
        {
            _results.ScrollIntoView(_results.SelectedIndex);
        }
    }

    /// <summary>
    /// お気に入りや無視を変えた後に検索し直す。操作した項目の並びが変わったら、それを選び直す。
    /// 無視して結果から消えたときは、同じ位置（次の項目）を選ぶ。
    /// </summary>
    private void Refresh(SearchResult changed) => OnInputChanged(Math.Max(0, _results.SelectedIndex), changed.GlobalId);

    private void ToggleFavorite(SearchResult result)
    {
        var favorite = !_preferences.Current.IsFavorite(result.GlobalId);
        UpdatePreference(result, () => _preferences.SetFavorite(result.GlobalId, favorite));
    }

    private void ToggleIgnored(SearchResult result)
    {
        var ignored = !_preferences.Current.IsIgnored(result.GlobalId);
        UpdatePreference(result, () => _preferences.SetIgnored(result.GlobalId, ignored));
    }

    /// <summary>エイリアスのダイアログを開き、保存したら検索し直す（要件定義書 6.1）。</summary>
    private async void EditAliases(SearchResult result)
    {
        _dialogOpen = true;
        IReadOnlyList<string>? aliases;
        try
        {
            aliases = await AliasDialog.EditAsync(this, result.Item.Title, _preferences.Current.AliasesOf(result.GlobalId).Select(a => a.Text));
        }
        finally
        {
            _dialogOpen = false;
        }
        Activate();
        _input.Focus();
        if (aliases is not null)
        {
            UpdatePreference(result, () => _preferences.SetAliases(result.GlobalId, aliases));
        }
    }

    private void UpdatePreference(SearchResult result, Action update)
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
        Refresh(result);
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
        foreach (var entry in EntriesFor(result))
        {
            if (entry.StartsGroup && menuItems.Count > 0)
            {
                menuItems.Add(new Separator());
            }
            menuItems.Add(MenuItemFor(entry.Title, entry.Gesture, entry.Execute));
        }

        var menu = new ContextMenu { ItemsSource = menuItems };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _input.Focus();
        };
        menu.Open(container);
    }

    /// <summary>アクションパネルと右クリックのメニューの1行。</summary>
    /// <param name="StartsGroup">前の行との間に区切りを入れる（項目のアクションと、ホストの操作の境目）。</param>
    private sealed record PanelEntry(string Title, string? Gesture, Action Execute, bool StartsGroup = false);

    /// <summary>項目のアクション（プラグインが決める）と、ホストがすべての項目に付ける操作（お気に入り、無視、エイリアス）。</summary>
    private List<PanelEntry> EntriesFor(SearchResult result)
    {
        var entries = new List<PanelEntry>();
        for (var i = 0; i < result.Item.Actions.Count; i++)
        {
            var index = i;
            var action = result.Item.Actions[i];
            entries.Add(new PanelEntry(action.Title, ActionGesture(action, i), () => ExecuteSelected(index)));
        }
        var preference = _preferences.Current;
        entries.Add(new PanelEntry(
            preference.IsFavorite(result.GlobalId) ? "お気に入りから外す" : "お気に入りに追加", "Ctrl+D", () => ToggleFavorite(result), StartsGroup: true));
        entries.Add(new PanelEntry(
            preference.IsIgnored(result.GlobalId) ? "無視を解除" : "無視する", "Ctrl+Delete", () => ToggleIgnored(result)));
        entries.Add(new PanelEntry("エイリアスを設定…", "Ctrl+E", () => EditAliases(result)));
        return entries;
    }

    /// <summary>
    /// 修飾キーの押し下げを追う。修飾キーそのものの KeyDown と KeyUp では、<see cref="KeyEventArgs.KeyModifiers"/> が
    /// 押す前と後のどちらの状態かがプラットフォームで違うので、キーから足し引きする。
    /// </summary>
    private void TrackModifiers(KeyEventArgs e, bool pressed)
    {
        var modifiers = e.KeyModifiers;
        var changed = e.Key switch
        {
            Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
            Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
            Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
            Key.LWin or Key.RWin => KeyModifiers.Meta,
            _ => KeyModifiers.None,
        };
        _heldModifiers = pressed ? modifiers | changed : modifiers & ~changed;
        UpdateAdminHighlight();
    }

    /// <summary>
    /// Ctrl+Shift を押している間、選んでいる行の色を変え、Enter で管理者として実行することを示す。
    /// その項目に管理者として実行するアクションがなければ変えない。
    /// </summary>
    private void UpdateAdminHighlight()
    {
        var active = _heldModifiers == (KeyModifiers.Control | KeyModifiers.Shift)
            && _results.SelectedItem is SearchResult result
            && result.Item.Actions.Any(a => a.Kind == ItemActionKind.RunAsAdministrator);
        _results.Classes.Set(AdminClass, active);
    }

    /// <summary>
    /// 新しい版があるときに、下の端に出す知らせ（要件定義書 10 章）。クリックで更新し、入れ替えた後にランチャーを開き直す。
    /// </summary>
    private static Control CreateUpdateNotice(UpdateService updates)
    {
        var notice = new TextBlock
        {
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 10),
        };
        BindResource(notice, TextBlock.FontSizeProperty, SmallFontSizeKey);
        BindResource(notice, TextBlock.ForegroundProperty, LinkKey);
        void Refresh()
        {
            notice.Text = updates.Status ?? $"v{updates.AvailableVersion} に更新";
            notice.IsVisible = updates.AvailableVersion is not null;
        }
        Refresh();
        updates.Changed += Refresh;
        notice.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _ = updates.ApplyAsync("--show");
        };
        return notice;
    }

    private void ClearError() => _error.IsVisible = false;

    private bool PanelOpen => _panelArea.IsVisible;

    /// <summary>選んでいる項目のアクションパネルを、結果の一覧の代わりに出す（要件定義書 5.2）。</summary>
    private void OpenPanel()
    {
        if (_results.SelectedItem is not SearchResult result)
        {
            return;
        }
        _panelTitle.Text = result.Item.Title;
        _panel.ItemsSource = EntriesFor(result);
        _panel.SelectedIndex = 0;
        _results.IsVisible = false;
        _panelArea.IsVisible = true;
        _completionHint.IsVisible = false;
    }

    private void ClosePanel()
    {
        if (!PanelOpen)
        {
            return;
        }
        _panelArea.IsVisible = false;
        _results.IsVisible = _results.ItemCount > 0;
        UpdateCompletionHint();
    }

    /// <summary>パネルで選んでいる行を実行する。実行の前にパネルを閉じ、結果の一覧に戻しておく。</summary>
    private void ExecutePanelEntry()
    {
        if (_panel.SelectedItem is PanelEntry entry)
        {
            ClosePanel();
            entry.Execute();
        }
    }

    private static FuncDataTemplate<PanelEntry> CreatePanelEntryTemplate() =>
        new((entry, _) =>
        {
            var row = new DockPanel { Margin = new Thickness(4, 4) };
            if (entry is null)
            {
                return row;
            }
            if (entry.Gesture is not null)
            {
                var key = new TextBlock { Text = entry.Gesture, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
                BindResource(key, TextBlock.FontSizeProperty, SmallFontSizeKey);
                BindResource(key, TextBlock.ForegroundProperty, SubtextKey);
                DockPanel.SetDock(key, Dock.Right);
                row.Children.Add(key);
            }
            var title = new TextBlock { Text = entry.Title };
            BindResource(title, TextBlock.FontSizeProperty, TitleFontSizeKey);
            BindResource(title, TextBlock.ForegroundProperty, TextKey);
            row.Children.Add(title);
            if (!entry.StartsGroup)
            {
                return row;
            }
            // ホストの操作の前に区切りの線を引く
            return new StackPanel
            {
                Children =
                {
                    BindResource(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 4) }, Border.BackgroundProperty, SeparatorKey),
                    row,
                },
            };
        });

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
    private static string? ActionGesture(ItemAction action, int index) => (action.Kind, index) switch
    {
        (ItemActionKind.RunAsAdministrator, _) => "Ctrl+Shift+Enter",
        (_, 0) => "Enter",
        (_, 1) => "Ctrl+Enter",
        _ => null,
    };

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (PanelOpen && OnPanelKeyDown(e))
        {
            e.Handled = true;
            return;
        }

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
            case Key.Enter when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                // Windows の慣習（スタートメニューやファイル名を指定して実行）に合わせる
                ExecuteSelected(ItemActionKind.RunAsAdministrator);
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
            case Key.Right when e.KeyModifiers == KeyModifiers.None && CaretAtEnd() && _results.SelectedItem is not null:
                // 入力の末尾で → を押すと、アクションパネルを開く（要件定義書 5.2）。それ以外ではカーソルを動かす
                OpenPanel();
                break;
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                // Tab は補完だけに使う。補完がないときも、フォーカスを入力欄から移さない（要件定義書 8 章）
                AcceptCompletion();
                break;
            case Key.E when e.KeyModifiers == KeyModifiers.Control && _results.SelectedItem is SearchResult aliased:
                // エイリアスの設定（要件定義書 6.1）
                EditAliases(aliased);
                break;
            case Key.OemComma when e.KeyModifiers == KeyModifiers.Control:
                // 設定画面を開く（要件定義書 9.1）
                HideLauncher();
                _settingsLauncher.Open();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>アクションパネルを開いている間のキー。処理したら true。それ以外のキー（Ctrl+D など）は通常どおり扱う。</summary>
    private bool OnPanelKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Left when e.KeyModifiers == KeyModifiers.None:
                ClosePanel();
                return true;
            case Key.Down:
                MoveSelection(_panel, 1);
                return true;
            case Key.Up:
                MoveSelection(_panel, -1);
                return true;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                ExecutePanelEntry();
                return true;
            case Key.Right or Key.Tab:
                return true;
            default:
                // 修飾キーの付いた操作（Ctrl+D など）は、パネルを閉じてから通常どおり扱う
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    ClosePanel();
                }
                return false;
        }
    }

    private bool CaretAtEnd() =>
        _input.CaretIndex == (_input.Text?.Length ?? 0) && _input.SelectionStart == _input.SelectionEnd;

    private void MoveSelection(int delta) => MoveSelection(_results, delta);

    private static void MoveSelection(ListBox list, int delta)
    {
        var count = list.ItemCount;
        if (count == 0)
        {
            return;
        }
        list.SelectedIndex = (list.SelectedIndex + delta + count) % count;
        list.ScrollIntoView(list.SelectedIndex);
    }

    /// <summary>選んでいる項目の、指定した種類のアクションを実行する。なければ何もしない。</summary>
    private void ExecuteSelected(ItemActionKind kind)
    {
        if (_results.SelectedItem is SearchResult result
            && result.Item.Actions.ToList().FindIndex(a => a.Kind == kind) is var index and >= 0)
        {
            ExecuteSelected(index);
        }
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
            _error.Text = $"「{action.Title}」に失敗した";
            _error.IsVisible = true;
            return;
        }

        switch (outcome)
        {
            case ActionResult.CloseResult:
                HideLauncher();
                break;
            case ActionResult.SetQueryResult setQuery:
                var unchanged = _input.Text == setQuery.Text;
                _input.Text = setQuery.Text;
                _input.CaretIndex = setQuery.Text.Length;
                if (unchanged)
                {
                    // 入力が同じでも、アクションで結果が変わった（保持の切り替えなど）ので検索し直す。選んでいた位置は保つ
                    OnInputChanged(Math.Max(0, _results.SelectedIndex));
                }
                break;
        }
    }

    /// <summary>Ctrl+Shift を押していて、選んでいる行を管理者として実行できるときに一覧に付けるクラス。</summary>
    private const string AdminClass = "admin";

    /// <summary>
    /// 選択している行とマウスを重ねた行の背景。色はスキンのリソース（<see cref="ApplySkin"/>）から取る。
    /// Fluent の既定はアクセントの色が濃く文字が読みにくいので、自前の色にする。
    /// </summary>
    private static void AddSelectionStyles(ListBox list)
    {
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
        // 管理者として実行するときの色（UAC の盾の色に寄せた橙）。ほかの状態より優先するので最後に置く
        list.Styles.Add(new Style(x => x.OfType<ListBox>().Class(AdminClass).Descendant().OfType<ListBoxItem>().Class(":selected")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension(AdminBackgroundKey)) },
        });
    }

    /// <summary>アイコン、タイトル、サブタイトルを並べる行のテンプレート。</summary>
    private FuncDataTemplate<SearchResult> CreateItemTemplate() =>
        new((_, _) =>
        {
            var icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0) };
            var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            BindResource(title, TextBlock.FontSizeProperty, TitleFontSizeKey);
            BindResource(title, TextBlock.ForegroundProperty, TextKey);
            var subtitle = new TextBlock { Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
            BindResource(subtitle, TextBlock.FontSizeProperty, SubtitleFontSizeKey);
            BindResource(subtitle, TextBlock.ForegroundProperty, SubtextKey);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
            var favorite = new TextBlock { Text = "★", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            BindResource(favorite, TextBlock.ForegroundProperty, TextKey);
            // エイリアスは、右の端に1つずつ小さな枠で囲んで出す（要件定義書 6.1）
            var aliases = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
            var row = new DockPanel { Children = { icon, favorite, aliases, text } };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(favorite, Dock.Right);
            DockPanel.SetDock(aliases, Dock.Right);

            // 行は使い回されるので、DataContext が変わるたびに中身を差し替える
            row.DataContextChanged += async (_, _) =>
            {
                if (row.DataContext is not SearchResult { Item: var item } result)
                {
                    return;
                }
                favorite.IsVisible = _preferences.Current.IsFavorite(result.GlobalId);
                var itemAliases = _preferences.Current.AliasesOf(result.GlobalId);
                aliases.Children.Clear();
                aliases.Children.AddRange(itemAliases.Select(a => AliasChip(a.Text)));
                aliases.IsVisible = itemAliases.Length > 0;
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

    private static Border AliasChip(string alias)
    {
        var text = new TextBlock { Text = alias, VerticalAlignment = VerticalAlignment.Center };
        BindResource(text, TextBlock.FontSizeProperty, SubtitleFontSizeKey);
        BindResource(text, TextBlock.ForegroundProperty, SubtextKey);
        return BindResource(
            new Border
            {
                Child = text,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1),
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Border.BorderBrushProperty, SeparatorKey);
    }

    /// <summary>設定に従い、マウスカーソルのある画面かメインの画面の、中央のやや上に移動する。</summary>
    private void MoveToScreen()
    {
        var screen = _settings.CurrentValue.Window.Screen == LauncherScreen.Cursor
            && _integration.GetCursorPosition() is var (x, y)
            ? Screens.ScreenFromPoint(new PixelPoint(x, y))
            : null;
        screen ??= Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)(Width * screen.Scaling);
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
