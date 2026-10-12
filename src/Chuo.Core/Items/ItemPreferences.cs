using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Chuo.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Chuo.Core.Items;

/// <summary>
/// 項目ごとのユーザー設定（お気に入り、無視）を <c>items.json</c> に保存する（要件定義書 6 章、設計 6 章）。
/// キーは項目のグローバルな識別子（<c>&lt;pluginId&gt;:&lt;itemId&gt;</c>）。
/// 検索のスレッドからは <see cref="Current"/> の不変のスナップショットを読む。
/// ファイルを手で編集したときは、自動で読み直す。
/// </summary>
public sealed class ItemPreferences : IDisposable
{
    public const string FavoriteKey = "favorite";
    public const string IgnoreKey = "ignore";

    /// <summary>エディタの保存は書き込みが続くことがあるので、落ち着いてから読み直す。</summary>
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    // 項目の ID はファイルのパスのこともあるので、大文字と小文字を区別する
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _reloadTimer;

    public ItemPreferences(string path, ILogger<ItemPreferences> logger)
    {
        _path = path;
        _logger = logger;
        _reloadTimer = new Timer(_ => Reload());
        Reload();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "{Path} の変更を監視できない", path);
        }
    }

    /// <summary>今の設定。変更のたびに新しいものに置き換わる。</summary>
    public ItemPreferenceSnapshot Current { get; private set; } = ItemPreferenceSnapshot.Empty;

    /// <summary>ファイルを読み直したか、変更したとき。どのスレッドからも発生する。</summary>
    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer.Dispose();
    }

    /// <summary>お気に入りに追加するか、外す。追加したものは一覧の末尾に並ぶ。</summary>
    public void SetFavorite(string globalId, bool favorite) =>
        Update(globalId, entry =>
        {
            if (!favorite)
            {
                entry.Remove(FavoriteKey);
            }
            else if (entry[FavoriteKey] is null)
            {
                entry[FavoriteKey] = Current.Favorites.Count == 0 ? 1 : Current.Favorites.Values.Max() + 1;
            }
        });

    /// <summary>無視するか、無視を解除する。</summary>
    public void SetIgnored(string globalId, bool ignored) =>
        Update(globalId, entry =>
        {
            if (ignored)
            {
                entry[IgnoreKey] = true;
            }
            else
            {
                entry.Remove(IgnoreKey);
            }
        });

    private void Update(string globalId, Action<JsonObject> change)
    {
        lock (_gate)
        {
            // 手で編集した内容を消さないよう、書き込む直前に読み直す。読めなければ、上書きせずに失敗させる
            var root = TryRead() ?? throw new InvalidDataException($"{_path} を読めないので、書き込まない");
            if (root[globalId] is not JsonObject entry)
            {
                entry = new JsonObject(NodeOptions);
                root[globalId] = entry;
            }
            change(entry);
            if (entry.Count == 0)
            {
                root.Remove(globalId);
            }
            JsonFile.WriteAtomic(_path, root);
            Apply(root);
        }
        Changed?.Invoke();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);

    private void Reload()
    {
        lock (_gate)
        {
            if (TryRead() is not { } root)
            {
                return;
            }
            Apply(root);
        }
        Changed?.Invoke();
    }

    /// <summary>読めなければ null。壊れたファイルで今の設定を消さないよう、前の内容を使い続ける。</summary>
    private JsonObject? TryRead()
    {
        try
        {
            return JsonFile.ReadObject(_path, NodeOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "{Path} を読めなかった", _path);
            return null;
        }
    }

    private void Apply(JsonObject root)
    {
        Current = ItemPreferenceSnapshot.From(root);
    }
}

/// <summary>ある時点の、項目ごとのユーザー設定。</summary>
public sealed class ItemPreferenceSnapshot
{
    public static ItemPreferenceSnapshot Empty { get; } = new(FrozenDictionary<string, int>.Empty, FrozenSet<string>.Empty);

    private ItemPreferenceSnapshot(FrozenDictionary<string, int> favorites, FrozenSet<string> ignored)
    {
        Favorites = favorites;
        Ignored = ignored;
        FavoritesInOrder = [.. favorites.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key)];
    }

    /// <summary>お気に入りの、グローバルな識別子と表示順。</summary>
    public FrozenDictionary<string, int> Favorites { get; }

    /// <summary>お気に入りの、グローバルな識別子を表示順に並べたもの。</summary>
    public IReadOnlyList<string> FavoritesInOrder { get; }

    /// <summary>無視する項目の、グローバルな識別子。</summary>
    public FrozenSet<string> Ignored { get; }

    public bool IsFavorite(string globalId) => Favorites.ContainsKey(globalId);

    public bool IsIgnored(string globalId) => Ignored.Contains(globalId);

    internal static ItemPreferenceSnapshot From(JsonObject root)
    {
        var favorites = new Dictionary<string, int>(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, node) in root)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }
            if (entry[ItemPreferences.FavoriteKey] is JsonValue favorite && favorite.TryGetValue<int>(out var order))
            {
                favorites[id] = order;
            }
            if (entry[ItemPreferences.IgnoreKey] is JsonValue ignore && ignore.TryGetValue<bool>(out var value) && value)
            {
                ignored.Add(id);
            }
        }
        return new ItemPreferenceSnapshot(favorites.ToFrozenDictionary(StringComparer.Ordinal), ignored.ToFrozenSet(StringComparer.Ordinal));
    }
}
