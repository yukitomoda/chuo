using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Chuo.Abstractions;
using Chuo.Core;
using Chuo.Core.Items;
using Chuo.Core.Platform;
using Chuo.Core.Plugins;
using Chuo.Core.Search;
using Chuo.Core.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Chuo.App;

/// <summary>
/// 設定画面。常駐するメモリを増やさないよう、開くたびに作り、閉じたら捨てる（設計 8 章）。
/// 変更はその場で <c>settings.json</c> に書き込み、構成の再読み込みで本体とプラグインに反映する（要件定義書 9.1）。
/// TODO: 一般のページの設定、項目のページのエイリアスとお気に入り、開発のページ。
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>テキストの入力は、打ち終わるまで待ってから書き込む。</summary>
    private static readonly TimeSpan TextWriteDelay = TimeSpan.FromMilliseconds(500);

    private readonly PluginManager _plugins;
    private readonly IConfiguration _configuration;
    private readonly SettingsStore _store;
    private readonly IShell _shell;
    private readonly IClipboard _clipboard;
    private readonly ILogger _logger;
    private readonly SearchService _search;
    private readonly ItemPreferences _preferences;
    private readonly IAutoStart _autoStart;
    private readonly ListBox _pluginList;
    private readonly ContentControl _page = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 8), TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(
        PluginManager plugins, IConfiguration configuration, SettingsStore store, IShell shell, IClipboard clipboard,
        SearchService search, ItemPreferences preferences, IAutoStart autoStart, ILogger<SettingsWindow> logger)
    {
        _autoStart = autoStart;
        _search = search;
        _preferences = preferences;
        _plugins = plugins;
        _configuration = configuration;
        _store = store;
        _shell = shell;
        _clipboard = clipboard;
        _logger = logger;

        Title = "chuo の設定";
        Width = 860;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var pluginList = _pluginList = new ListBox
        {
            ItemsSource = SortedPlugins(),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PluginInfo>((p, _) =>
                new TextBlock
                {
                    Text = p is { IsEnabled: false } ? $"{p.Name}（無効）" : p?.Name,
                    Opacity = p is { IsEnabled: false } ? 0.6 : 1,
                    Margin = new Thickness(4, 2),
                }),
        };
        // 本体のページとプラグインの一覧は別の ListBox にし、片方を選んだらもう片方の選択を外す
        var generalList = new ListBox { ItemsSource = new[] { "一般", "項目" } };
        generalList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((t, _) =>
            new TextBlock { Text = t, Margin = new Thickness(4, 2) });
        generalList.SelectionChanged += (_, _) =>
        {
            if (generalList.SelectedIndex >= 0)
            {
                pluginList.SelectedIndex = -1;
                if (generalList.SelectedIndex == 0)
                {
                    ShowGeneral();
                }
                else
                {
                    ShowItems();
                }
            }
        };
        pluginList.SelectionChanged += (_, _) =>
        {
            if (pluginList.SelectedItem is PluginInfo plugin)
            {
                generalList.SelectedIndex = -1;
                ShowPlugin(plugin);
            }
        };

        var heading = new TextBlock { Text = "プラグイン", FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 16, 12, 4) };
        var navigation = new ScrollViewer
        {
            Width = 240,
            Content = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { generalList, heading, pluginList } },
        };

        var body = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);
        body.Children.Add(new ScrollViewer { Content = _page, Padding = new Thickness(24, 16) });

        var root = new DockPanel();
        DockPanel.SetDock(navigation, Dock.Left);
        root.Children.Add(navigation);
        root.Children.Add(new Border { Width = 1, Background = Brushes.Gray, Opacity = 0.3 });
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        generalList.SelectedIndex = 0;

        // 有効と無効を切り替えると、プラグインの読み込みかアンロードが済んでから一覧とページを作り直す
        Action changed = () => Dispatcher.UIThread.Post(RefreshPlugins);
        plugins.PluginsChanged += changed;
        Closed += (_, _) => plugins.PluginsChanged -= changed;

        // ランチャーや items.json の手での編集で無視が変わったら、項目のページを作り直す
        Action preferencesChanged = () => Dispatcher.UIThread.Post(() =>
        {
            if (_page.Content is Control { Tag: ItemsPageTag })
            {
                ShowItems();
            }
        });
        preferences.Changed += preferencesChanged;
        Closed += (_, _) => preferences.Changed -= preferencesChanged;
    }

    private List<PluginInfo> SortedPlugins() => [.. _plugins.Plugins.OrderBy(p => p.Name, StringComparer.CurrentCulture)];

    /// <summary>プラグインの一覧を作り直す。プラグインのページを開いていれば、選び直して作り直す。</summary>
    private void RefreshPlugins()
    {
        var selectedId = (_pluginList.SelectedItem as PluginInfo)?.Id;
        var plugins = SortedPlugins();
        _pluginList.ItemsSource = plugins;
        if (selectedId is not null)
        {
            _pluginList.SelectedIndex = plugins.FindIndex(p => p.Id == selectedId);
        }
    }

    /// <summary>一般のページ。TODO: ホットキー、自動起動、テーマ、表示位置、最大表示件数、IME（要件定義書 9.1）。</summary>
    private void ShowGeneral()
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "一般", FontSize = 22, FontWeight = FontWeight.SemiBold });

        if (_autoStart.IsSupported)
        {
            panel.Children.Add(SectionHeading("起動"));
            panel.Children.Add(CreateAutoStart());
        }

        panel.Children.Add(SectionHeading("ウィンドウ"));
        var window = _configuration.GetSection(ChuoSettings.WindowSection);
        foreach (var field in SettingsSchema.Describe(typeof(WindowSettings)))
        {
            panel.Children.Add(CreateField(field, window, $"{ChuoSettings.WindowSection}:{field.Key}"));
        }

        panel.Children.Add(SectionHeading("ログ"));
        var directory = ChuoPaths.LogDirectory;
        var path = new SelectableTextBlock { Text = directory, TextWrapping = TextWrapping.Wrap };
        var open = new Button { Content = "フォルダを開く" };
        open.Click += (_, _) => Run("ログのフォルダを開けなかった", () =>
        {
            Directory.CreateDirectory(directory);
            _shell.Open(directory);
        });
        var copy = new Button { Content = "パスをコピー" };
        copy.Click += async (_, _) =>
        {
            try
            {
                await _clipboard.SetTextAsync(directory);
                ShowInfo("ログのフォルダのパスをコピーした。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ログのフォルダのパスをコピーできなかった");
                ShowError($"パスをコピーできなかった: {ex.Message}");
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, copy } };
        panel.Children.Add(Labeled("ログのフォルダ", "日ごとのファイルに書き込む。不具合を報告するときに使う", new StackPanel { Spacing = 6, Children = { path, buttons } }));

        _page.Content = panel;
        _status.Text = "";
    }

    /// <summary>
    /// サインイン時に起動するか（要件定義書 9.1）。値は OS の側に持つ。別の場所の chuo が登録されていれば、オフとして表示し、
    /// その場所を出す（勝手に置き換えない）。
    /// </summary>
    private Control CreateAutoStart()
    {
        string? registered = null;
        try
        {
            registered = _autoStart.GetRegisteredPath();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自動起動の設定を読めなかった");
        }
        var isCurrent = registered is not null && string.Equals(
            Path.GetFullPath(registered), Path.GetFullPath(_autoStart.CurrentPath), StringComparison.OrdinalIgnoreCase);

        var panel = new StackPanel { Spacing = 4 };
        var check = new CheckBox { Content = "サインインしたときに起動する", IsChecked = isCurrent };
        var other = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DarkOrange };
        void ShowOther(string? path)
        {
            other.Text = path is null ? "" : $"別の場所の chuo が登録されている: {path}\nオンにすると、いま動いている chuo に置き換える。";
            other.IsVisible = path is not null;
        }
        ShowOther(isCurrent ? null : registered);

        check.IsCheckedChanged += (_, _) => Run("自動起動の設定を変えられなかった", () =>
        {
            _autoStart.SetEnabled(check.IsChecked == true);
            ShowOther(null);
        });

        panel.Children.Add(check);
        panel.Children.Add(other);
        return Labeled(null, "ランチャーは隠したまま、タスクトレイに常駐する", panel);
    }

    private const string ItemsPageTag = "items";

    /// <summary>項目のページ。エイリアスの一覧と編集、無視した項目の一覧と無視の解除（要件定義書 6.1、6.3、9.1）。</summary>
    private void ShowItems()
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Tag = ItemsPageTag };
        panel.Children.Add(new TextBlock { Text = "項目", FontSize = 22, FontWeight = FontWeight.SemiBold });
        var pluginNames = _plugins.Plugins.ToDictionary(p => p.Id, p => p.Name);

        panel.Children.Add(SectionHeading("エイリアス"));
        panel.Children.Add(Caption("ランチャーで付けたエイリアス（Ctrl+E か右クリックのメニュー）。入力と一致すると最上位に、先頭と一致すると上のほうに出る"));
        var aliased = _preferences.Current.Aliases
            .Select(p => (Result: _search.Resolve(p.Key), Aliases: p.Value))
            .OrderBy(p => p.Result.Item.Title, StringComparer.CurrentCulture)
            .ToList();
        if (aliased.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "エイリアスを付けた項目はない。", Opacity = 0.7, Margin = new Thickness(0, 4) });
        }
        foreach (var (result, aliases) in aliased)
        {
            var edit = new Button { Content = "編集…" };
            var remove = new Button { Content = "削除" };
            var globalId = result.GlobalId;
            var title = result.Item.Title;
            // 書き込むと Changed でページが作り直される
            edit.Click += async (_, _) =>
            {
                if (await AliasDialog.EditAsync(this, title, aliases.Select(a => a.Text)) is { } edited)
                {
                    Write(() => _preferences.SetAliases(globalId, edited));
                }
            };
            remove.Click += (_, _) => Write(() => _preferences.SetAliases(globalId, []));
            panel.Children.Add(ItemRow(title, $"{pluginNames.GetValueOrDefault(result.PluginId, result.PluginId)} ・ {string.Join("  ", aliases.Select(a => a.Text))}", edit, remove));
        }

        panel.Children.Add(SectionHeading("無視"));
        panel.Children.Add(Caption("ランチャーで無視にした項目（Ctrl+Delete か右クリックのメニュー）。検索の結果に出ない"));

        var ignored = _preferences.Current.Ignored
            .Select(_search.Resolve)
            .OrderBy(r => r.Item.Title, StringComparer.CurrentCulture)
            .ToList();
        if (ignored.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "無視している項目はない。", Opacity = 0.7, Margin = new Thickness(0, 4) });
        }
        foreach (var result in ignored)
        {
            var pluginName = pluginNames.GetValueOrDefault(result.PluginId, result.PluginId);
            var unignore = new Button { Content = "無視を解除" };
            var globalId = result.GlobalId;
            // 解除すると Changed でページが作り直される
            unignore.Click += (_, _) => Write(() => _preferences.SetIgnored(globalId, false));
            panel.Children.Add(ItemRow(
                result.Item.Title, result.Item.Subtitle is { Length: > 0 } subtitle ? $"{pluginName} ・ {subtitle}" : pluginName, unignore));
        }

        _page.Content = panel;
        _status.Text = "";
    }

    /// <summary>項目のページの1行。タイトルと説明を左に、ボタンを右に並べる。</summary>
    private static DockPanel ItemRow(string title, string detail, params Button[] buttons)
    {
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons)
        {
            actions.Children.Add(button);
        }

        var row = new DockPanel { Margin = new Thickness(0, 2) };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(text);
        return row;
    }

    /// <summary>失敗したら画面に出す。</summary>
    private void Run(string failure, Action action)
    {
        try
        {
            action();
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Failure}", failure);
            ShowError($"{failure}: {ex.Message}");
        }
    }

    private void ShowPlugin(PluginInfo plugin)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = plugin.Name, FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Caption($"{plugin.Id}  バージョン {plugin.Version}"));

        var enabledKey = PluginManager.EnabledKey(plugin.Id);
        var enabled = new CheckBox { Content = "このプラグインを使う", IsChecked = plugin.IsEnabled };
        // 有効は既定なので、キーを消して書き込む
        enabled.IsCheckedChanged += (_, _) => Write(() => _store.Set(enabledKey, enabled.IsChecked == true ? null : JsonValue.Create(false)));
        panel.Children.Add(Labeled(null, "オフにすると、プラグインを読み込まず、検索にも出さない", enabled));

        if (!plugin.IsEnabled)
        {
            _page.Content = panel;
            _status.Text = "";
            return;
        }
        if (plugin.LoadError is { } error)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"読み込めなかった: {error}",
                Foreground = Brushes.IndianRed,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (var provider in plugin.Providers)
        {
            panel.Children.Add(SectionHeading(plugin.Providers.Count > 1 ? $"検索（{provider.Name}）" : "検索"));
            AddProviderSettings(panel, plugin, provider);
        }

        foreach (var type in plugin.SettingsTypes)
        {
            panel.Children.Add(SectionHeading("設定"));
            var section = _configuration.GetSection(PluginManager.SettingsSection(plugin.Id));
            foreach (var field in SettingsSchema.Describe(type))
            {
                panel.Children.Add(CreateField(field, section, $"{PluginManager.SettingsSection(plugin.Id)}:{field.Key}"));
            }
        }

        _page.Content = panel;
        _status.Text = "";
    }

    /// <summary>キーワードと、全体検索に含めるかの上書き（要件定義書 2.1、9.1）。</summary>
    private void AddProviderSettings(StackPanel panel, PluginInfo plugin, ProviderInfo provider)
    {
        var keywordKey = ProviderRegistry.ProviderSettingKey(plugin.Id, provider.Name, ProviderRegistry.KeywordKey);
        var keyword = new TextBox
        {
            Text = _configuration[keywordKey] ?? provider.DefaultKeyword ?? "",
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        BindText(keyword, text => _store.Set(keywordKey, JsonValue.Create(text.Trim())));
        panel.Children.Add(Labeled("キーワード", "入力の先頭にこの語と空白を付けると、このプラグインだけを検索する。空にするとキーワードなし", keyword));

        if (provider.CanIncludeInGlobal)
        {
            var globalKey = ProviderRegistry.ProviderSettingKey(plugin.Id, provider.Name, ProviderRegistry.IncludeInGlobalKey);
            var includeInGlobal = new CheckBox
            {
                Content = "全体検索に含める",
                IsChecked = bool.TryParse(_configuration[globalKey], out var value) ? value : provider.DefaultIncludeInGlobal,
            };
            includeInGlobal.IsCheckedChanged += (_, _) => Write(() => _store.Set(globalKey, JsonValue.Create(includeInGlobal.IsChecked == true)));
            panel.Children.Add(Labeled(null, "オフにすると、キーワードを付けたときだけ検索する", includeInGlobal));
        }
    }

    private Control CreateField(SettingField field, IConfiguration section, string key)
    {
        var value = field.Read(section);
        switch (field.Kind)
        {
            case SettingKind.Boolean:
                var check = new CheckBox { Content = field.DisplayName, IsChecked = value as bool? ?? false };
                check.IsCheckedChanged += (_, _) => Write(() => _store.Set(key, field.ToJson(check.IsChecked == true)));
                return Labeled(null, field.Description, check);

            case SettingKind.Integer or SettingKind.Number:
                var number = new NumericUpDown
                {
                    Value = Convert.ToDecimal(value ?? 0),
                    Minimum = field.Minimum is { } min ? (decimal)min : decimal.MinValue,
                    Maximum = field.Maximum is { } max ? (decimal)max : decimal.MaxValue,
                    Increment = 1,
                    FormatString = field.Kind == SettingKind.Integer ? "0" : "",
                    Width = 200,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                number.ValueChanged += (_, _) =>
                {
                    if (number.Value is { } v)
                    {
                        Write(() => _store.Set(key, field.ToJson(v)));
                    }
                };
                return Labeled(field.DisplayName, field.Description, number);

            case SettingKind.Choice:
                return Labeled(field.DisplayName, field.Description,
                    CreateChoice(field, value, 200, name => Write(() => _store.Set(key, field.ToJson(name)))));

            case SettingKind.Text or SettingKind.Folder or SettingKind.File:
                var text = new TextBox { Text = value as string ?? "" };
                BindText(text, t => _store.Set(key, field.ToJson(t)));
                var input = field.Kind == SettingKind.Text
                    ? (Control)text
                    : WithBrowseButton(text, field.Kind == SettingKind.Folder, path => text.Text = path);
                return Labeled(field.DisplayName, field.Description, input);

            case SettingKind.ObjectList:
                return Labeled(field.DisplayName, field.Description, CreateObjectListEditor(field, (List<Dictionary<string, object?>>)value!, key));

            default:
                var strings = ((IReadOnlyList<string>)value!).ToList();
                return Labeled(field.DisplayName, field.Description, CreateListEditor(
                    strings, s => s, s => s, (_, _) => [], () => _store.Set(key, field.ToJson(strings)),
                    field.Kind switch { SettingKind.FolderList => true, SettingKind.FileList => false, _ => null }));
        }
    }

    /// <summary>設定のクラスのリストの編集。代表の値（パスなど）を一覧に出し、残りの値は行の中で編集する。</summary>
    private Control CreateObjectListEditor(SettingField field, List<Dictionary<string, object?>> items, string key)
    {
        var primary = field.PrimaryItemField!;
        void Save() => _store.Set(key, field.ToJson(items));

        IEnumerable<Control> CreateItemControls(Dictionary<string, object?> item, Action save)
        {
            foreach (var itemField in field.ItemFields!.Where(f => f != primary))
            {
                if (CreateItemControl(itemField, item, save) is { } control)
                {
                    yield return control;
                }
            }
        }

        return CreateListEditor(
            items, item => item.GetValueOrDefault(primary.Key) as string ?? "", field.CreateItem, CreateItemControls, Save,
            primary.Kind switch { SettingKind.Folder => true, SettingKind.File => false, _ => null });
    }

    /// <summary>リストの要素の、代表以外の値1つ分の入力欄。</summary>
    private Control? CreateItemControl(SettingField field, Dictionary<string, object?> item, Action save)
    {
        var value = item.GetValueOrDefault(field.Key) ?? field.DefaultValue;
        switch (field.Kind)
        {
            case SettingKind.Boolean:
                var check = new CheckBox { Content = field.DisplayName, IsChecked = value as bool? ?? false };
                check.IsCheckedChanged += (_, _) =>
                {
                    item[field.Key] = check.IsChecked == true;
                    Write(save);
                };
                ToolTip.SetTip(check, field.Description);
                return check;

            case SettingKind.Integer or SettingKind.Number:
                var number = new NumericUpDown
                {
                    Value = Convert.ToDecimal(value ?? 0),
                    Minimum = field.Minimum is { } min ? (decimal)min : decimal.MinValue,
                    Maximum = field.Maximum is { } max ? (decimal)max : decimal.MaxValue,
                    FormatString = field.Kind == SettingKind.Integer ? "0" : "",
                    Width = 120,
                };
                number.ValueChanged += (_, _) =>
                {
                    if (number.Value is { } v)
                    {
                        item[field.Key] = field.Kind == SettingKind.Integer ? (long)v : (double)v;
                        Write(save);
                    }
                };
                return ItemLabeled(field, number);

            case SettingKind.Choice:
                return ItemLabeled(field, CreateChoice(field, value, 120, name =>
                {
                    item[field.Key] = name;
                    Write(save);
                }));

            case SettingKind.Text or SettingKind.Folder or SettingKind.File:
                var text = new TextBox { Text = value as string ?? "", Width = 200 };
                BindText(text, t =>
                {
                    item[field.Key] = t;
                    save();
                });
                return ItemLabeled(field, field.Kind == SettingKind.Text
                    ? text
                    : WithBrowseButton(text, field.Kind == SettingKind.Folder, path => text.Text = path));

            default:
                return null;
        }
    }

    /// <summary>
    /// リストの編集。行ごとに削除でき、下の入力欄から追加する。
    /// </summary>
    /// <param name="label">一覧に出す文字列。重複の判定にも使う。</param>
    /// <param name="create">入力欄の文字列から要素を作る。</param>
    /// <param name="itemControls">行に並べる、要素の値を編集する入力欄。</param>
    /// <param name="browseFolder">true ならフォルダ、false ならファイルの「参照…」を付ける。null なら付けない。</param>
    private Control CreateListEditor<T>(
        List<T> items, Func<T, string> label, Func<string, T> create,
        Func<T, Action, IEnumerable<Control>> itemControls, Action save, bool? browseFolder)
    {
        var rows = new StackPanel { Spacing = 4 };
        var input = new TextBox { PlaceholderText = browseFolder == true ? "フォルダのパス" : "" };

        void Save() => Write(save);

        void Render()
        {
            rows.Children.Clear();
            if (items.Count == 0)
            {
                rows.Children.Add(Caption("（なし）"));
            }
            foreach (var item in items)
            {
                var remove = new Button { Content = "削除", Margin = new Thickness(8, 0, 0, 0) };
                remove.Click += (_, _) =>
                {
                    items.Remove(item);
                    Render();
                    Save();
                };
                var header = new DockPanel();
                DockPanel.SetDock(remove, Dock.Right);
                header.Children.Add(remove);
                var text = label(item);
                var title = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(title, text);
                header.Children.Add(title);

                // 要素の値の入力欄は、数が増えても収まるよう、代表の値の下に折り返して並べる
                var controls = itemControls(item, save).ToList();
                if (controls.Count == 0)
                {
                    rows.Children.Add(header);
                    continue;
                }
                var values = new WrapPanel { Margin = new Thickness(16, 2, 0, 0) };
                foreach (var control in controls)
                {
                    control.Margin = new Thickness(0, 0, 12, 4);
                    values.Children.Add(control);
                }
                rows.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 0, 6), Children = { header, values } });
            }
        }

        void Add(string? text)
        {
            var value = text?.Trim();
            if (string.IsNullOrEmpty(value) || items.Any(i => label(i) == value))
            {
                return;
            }
            items.Add(create(value));
            input.Text = "";
            Render();
            Save();
        }

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Add(input.Text);
                e.Handled = true;
            }
        };
        var addButton = new Button { Content = "追加" };
        addButton.Click += (_, _) => Add(input.Text);

        var addRow = new DockPanel();
        DockPanel.SetDock(addButton, Dock.Right);
        addRow.Children.Add(addButton);
        if (browseFolder is { } folder)
        {
            var browse = new Button { Content = "参照…", Margin = new Thickness(0, 0, 4, 0) };
            browse.Click += async (_, _) => Add(await BrowseAsync(folder));
            DockPanel.SetDock(browse, Dock.Right);
            addRow.Children.Add(browse);
        }
        addRow.Children.Add(input);

        Render();
        return new StackPanel { Spacing = 6, Children = { rows, addRow } };
    }

    /// <summary>選択肢。表示名を並べ、選んだ値の名前を <paramref name="changed"/> に渡す。</summary>
    private static ComboBox CreateChoice(SettingField field, object? value, double minWidth, Action<string> changed)
    {
        var choice = new ComboBox
        {
            ItemsSource = field.ChoiceLabels ?? field.Choices,
            SelectedIndex = field.ChoiceIndex(value),
            MinWidth = minWidth,
        };
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex >= 0)
            {
                changed(field.Choices[choice.SelectedIndex]);
            }
        };
        return choice;
    }

    /// <summary>リストの要素の値に、表示名を左に付ける。</summary>
    private static Control ItemLabeled(SettingField field, Control input)
    {
        var label = new TextBlock { Text = field.DisplayName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(label, field.Description);
        input.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { label, input } };
    }

    private Control WithBrowseButton(TextBox text, bool folder, Action<string> picked)
    {
        var browse = new Button { Content = "参照…", Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            if (await BrowseAsync(folder) is { } path)
            {
                picked(path);
            }
        };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(text);
        return row;
    }

    private async Task<string?> BrowseAsync(bool folder)
    {
        try
        {
            if (folder)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "フォルダを選ぶ" });
                return folders.FirstOrDefault()?.TryGetLocalPath();
            }
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "ファイルを選ぶ" });
            return files.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "選択のダイアログを開けなかった");
            ShowError("選択のダイアログを開けなかった。パスを直接入力してください。");
            return null;
        }
    }

    /// <summary>テキストの変更を、入力が止まってから書き込む。フォーカスを外したときはすぐに書き込む。</summary>
    private void BindText(TextBox box, Action<string> write)
    {
        var timer = new DispatcherTimer { Interval = TextWriteDelay };
        var pending = false;
        // 表示しただけで TextChanged が起きることがあるので、値が変わったときだけ書き込む
        var written = box.Text ?? "";
        void Flush()
        {
            timer.Stop();
            if (pending && (box.Text ?? "") != written)
            {
                written = box.Text ?? "";
                Write(() => write(written));
            }
            pending = false;
        }
        timer.Tick += (_, _) => Flush();
        box.TextChanged += (_, _) =>
        {
            pending = true;
            timer.Stop();
            timer.Start();
        };
        box.LostFocus += (_, _) => Flush();
        Closing += (_, _) => Flush();
    }

    /// <summary>書き込みに失敗したら画面に出す（要件定義書 9.1）。</summary>
    private void Write(Action write)
    {
        try
        {
            write();
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定を書き込めなかった");
            ShowError($"設定を書き込めなかった: {ex.Message}");
        }
    }

    private void ShowInfo(string message)
    {
        _status.Text = message;
        _status.Foreground = Brushes.Gray;
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        _status.Foreground = Brushes.IndianRed;
    }

    private static Control Labeled(string? label, string? description, Control input)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        if (label is not null)
        {
            panel.Children.Add(new TextBlock { Text = label });
        }
        panel.Children.Add(input);
        if (!string.IsNullOrEmpty(description))
        {
            panel.Children.Add(Caption(description));
        }
        return panel;
    }

    private static TextBlock SectionHeading(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 0) };

    private static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
}
