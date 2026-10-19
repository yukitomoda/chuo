using Chuo.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chuo.Plugins.DailyFolder;

/// <summary>全体検索で、今日のフォルダを開く項目を出す。</summary>
[CatalogProvider(IncludeInGlobal = true)]
public sealed class DailyFolderCatalogProvider : ICatalogProvider
{
    private readonly DailyFolders _folders;

    public DailyFolderCatalogProvider(DailyFolders folders) => _folders = folders;

    public Task InitializeAsync(ICatalog catalog, CancellationToken cancellationToken)
    {
        catalog.ReplaceAll(
        [
            new CatalogItem
            {
                Id = "today",
                Title = "Daily Folder",
                Subtitle = "今日のフォルダを開く（なければ作る）",
                Keywords = ["今日のフォルダ"],
                Actions = [DailyFolderCommands.OpenToday(_folders)],
            },
        ]);
        return Task.CompletedTask;
    }
}

/// <summary>
/// キーワードの後のサブコマンドで操作する。
/// 何も付けなければ今日のフォルダ、<c>prune</c> で古いフォルダの削除、<c>keep</c> で削除しないフォルダの指定。
/// </summary>
[LiveQueryProvider(Keyword = "daily")]
public sealed class DailyFolderCommands : ILiveQueryProvider
{
    private const string Prune = "prune";
    private const string Keep = "keep";

    private readonly DailyFolders _folders;
    private readonly ILogger<DailyFolderCommands> _logger;

    /// <summary>直前の prune で移せなかったフォルダの知らせ。次に prune の一覧を出すときに一度だけ出す。</summary>
    private PruneError? _pruneError;

    private sealed record PruneError(string Title, string Detail);

    public DailyFolderCommands(DailyFolders folders, ILogger<DailyFolderCommands> logger)
    {
        _folders = folders;
        _logger = logger;
    }

    public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
    {
        // サブコマンドの後で入力欄を書き換えるため、キーワードの部分（例: "daily "）を取っておく
        var prefix = query.RawText[..^query.Text.Length];
        var text = query.Text.TrimStart();
        var space = text.IndexOf(' ');
        var command = space < 0 ? text : text[..space];
        var argument = space < 0 ? "" : text[(space + 1)..].Trim();

        IReadOnlyList<ResultItem> items = command.ToLowerInvariant() switch
        {
            Prune when space >= 0 || text.Length == Prune.Length => PruneItems(prefix),
            Keep when space >= 0 || text.Length == Keep.Length => KeepItems(prefix, argument),
            _ => CommandItems(prefix, text),
        };
        return ValueTask.FromResult(items);
    }

    internal static ItemAction OpenToday(DailyFolders folders) =>
        new("開く", ctx =>
        {
            ctx.Shell.Open(folders.EnsureToday());
            return ValueTask.FromResult(ActionResult.Close);
        });

    /// <summary>今日のフォルダと、入力に合うサブコマンド。</summary>
    private List<ResultItem> CommandItems(string prefix, string text)
    {
        var items = new List<ResultItem>();
        if (text.Length == 0)
        {
            items.Add(new ResultItem
            {
                Id = "today",
                Title = "今日のフォルダを開く",
                Subtitle = _folders.TodayPath,
                Actions = [OpenToday(_folders), ItemAction.CopyText(_folders.TodayPath, "パスをコピー")],
            });
        }
        (string Name, string Description)[] commands =
        [
            (Prune, $"古いフォルダをごみ箱に移す（{_folders.RetentionDays} 日より前のもの。保持したものは残す）"),
            (Keep, "フォルダを保持して、prune で削除しないようにする"),
        ];
        foreach (var (name, description) in commands.Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new ResultItem
            {
                Id = "command:" + name,
                Title = name,
                Subtitle = description,
                Completion = name + " ",
                Actions = [new ItemAction("選ぶ", _ => ValueTask.FromResult(ActionResult.SetQuery(prefix + name + " ")))],
            });
        }
        return items;
    }

    /// <summary>削除する件数を示す項目（Enter で削除する）と、削除するフォルダの一覧。</summary>
    private List<ResultItem> PruneItems(string prefix)
    {
        var candidates = _folders.PruneCandidates();
        var refresh = prefix + Prune + " ";
        var items = new List<ResultItem>();
        if (Interlocked.Exchange(ref _pruneError, null) is { } error)
        {
            items.Add(new ResultItem { Id = "prune:error", Title = error.Title, Subtitle = error.Detail });
        }
        if (candidates.Count == 0)
        {
            items.Add(new ResultItem
            {
                Id = "prune:none",
                Title = "削除するフォルダはない",
                Subtitle = $"{_folders.RetentionDays} 日より前のフォルダと、保持したフォルダは残す",
            });
            return items;
        }

        items.Add(
            new ResultItem
            {
                Id = "prune:run",
                Title = $"{candidates.Count} 件のフォルダをごみ箱に移す",
                Subtitle = $"{candidates[^1].Name} 〜 {candidates[0].Name}（残したいフォルダは Ctrl+Enter で保持する）",
                Actions =
                [
                    new ItemAction("ごみ箱に移す", async ctx =>
                    {
                        await PruneAsync(candidates, ctx.CancellationToken);
                        return ActionResult.SetQuery(refresh);
                    }),
                ],
            });
        items.AddRange(candidates.Select(folder => FolderItem(folder, refresh)));
        return items;
    }

    /// <summary>フォルダの一覧。Enter で保持を切り替える。</summary>
    private List<ResultItem> KeepItems(string prefix, string filter)
    {
        var refresh = prefix + Keep + " " + filter;
        var folders = _folders.List().Where(f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (folders.Count == 0)
        {
            return [new ResultItem { Id = "keep:none", Title = "フォルダがない", Subtitle = filter.Length == 0 ? null : $"「{filter}」を含む名前のフォルダはない" }];
        }
        return
        [
            .. folders.Select(folder => new ResultItem
            {
                Id = "folder:" + folder.Name,
                Title = folder.Kept ? $"{folder.Name}（保持している）" : folder.Name,
                Subtitle = folder.Kept ? "Enter で保持を解除する" : "Enter で保持する",
                Actions = [ToggleKept(folder, refresh), ItemAction.Open(folder.Path)],
            }),
        ];
    }

    /// <summary>削除するフォルダの項目。Enter で開き、Ctrl+Enter で保持する。</summary>
    private ResultItem FolderItem(DailyFolderEntry folder, string refresh) =>
        new()
        {
            Id = "folder:" + folder.Name,
            Title = folder.Name,
            Subtitle = folder.Path,
            Actions = [ItemAction.Open(folder.Path), ToggleKept(folder, refresh)],
        };

    private ItemAction ToggleKept(DailyFolderEntry folder, string refresh) =>
        new(folder.Kept ? "保持を解除する" : "保持する", _ =>
        {
            _folders.ToggleKept(folder.Name);
            return ValueTask.FromResult(ActionResult.SetQuery(refresh));
        });

    private async Task PruneAsync(IReadOnlyList<DailyFolderEntry> candidates, CancellationToken cancellationToken)
    {
        var moved = 0;
        Exception? firstError = null;
        foreach (var folder in candidates)
        {
            try
            {
                await Trash.MoveAsync(folder.Path, cancellationToken);
                moved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{Folder} をごみ箱に移せなかった", folder.Path);
                firstError ??= ex;
            }
        }
        _logger.LogInformation("{Moved} / {Count} 件のフォルダをごみ箱に移した", moved, candidates.Count);
        if (firstError is not null)
        {
            _pruneError = new PruneError($"{candidates.Count - moved} 件のフォルダをごみ箱に移せなかった", firstError.Message);
        }
    }
}
