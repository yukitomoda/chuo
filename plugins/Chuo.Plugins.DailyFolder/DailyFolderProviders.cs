using Chuo.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chuo.Plugins.DailyFolder;

/// <summary>
/// キーワードの後のサブコマンドで操作する。
/// 何も付けなければ今日のフォルダと直近のフォルダ、<c>prune</c> で古いフォルダの削除、<c>keep</c> で削除しないフォルダの指定。
/// </summary>
[LiveQueryProvider(Keyword = "daily")]
public sealed class DailyFolderCommands : ILiveQueryProvider
{
    private const string Prune = "prune";
    private const string Keep = "keep";

    /// <summary>キーワードだけのときに出す、今日より前のフォルダの数。</summary>
    private const int RecentCount = 5;

    private readonly DailyFolders _folders;
    private readonly ILogger<DailyFolderCommands> _logger;

    /// <summary>直前にごみ箱に移せなかったフォルダの知らせ。次に一覧を出すときに一度だけ先頭に出す。</summary>
    private TrashError? _trashError;

    private sealed record TrashError(string Title, string Detail);

    public DailyFolderCommands(DailyFolders folders, ILogger<DailyFolderCommands> logger)
    {
        _folders = folders;
        _logger = logger;
    }

    public ValueTask<IReadOnlyList<ResultItem>> QueryAsync(Query query, CancellationToken cancellationToken)
    {
        // サブコマンドの後で入力欄を書き換えるため、キーワードの部分（例: "daily "）を作っておく。
        // キーワードだけ（空白なし）の入力でも呼ばれるので、入力欄からは切り出さない
        var prefix = query.Keyword + " ";
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

    /// <summary>今日のフォルダ、直近のフォルダと、入力に合うサブコマンド。</summary>
    private List<ResultItem> CommandItems(string prefix, string text)
    {
        var items = new List<ResultItem>();
        if (text.Length == 0)
        {
            TakeTrashError(items);
            var today = _folders.TodayEntry();
            items.Add(new ResultItem
            {
                Id = "today",
                Title = today.Kept ? "★ 今日のフォルダを開く" : "今日のフォルダを開く",
                Subtitle = today.Kept ? $"保持中 ・ {today.Path}" : today.Path,
                // 直近のフォルダと同じく、Ctrl+Enter で保持を切り替える
                Actions = [OpenToday(_folders), ToggleKept(today, prefix), ItemAction.CopyText(today.Path, "パスをコピー")],
            });
            items.AddRange(_folders.Recent(RecentCount).Select(folder => RecentItem(folder, prefix)));
        }
        (string Name, string Description)[] commands =
        [
            (Prune, $"{_folders.RetentionDays} 日より古いフォルダを削除"),
            (Keep, "prune で削除しないフォルダを選ぶ"),
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
        TakeTrashError(items);
        if (candidates.Count == 0)
        {
            items.Add(new ResultItem
            {
                Id = "prune:none",
                Title = "削除するフォルダはない",
            });
            return items;
        }

        items.Add(
            new ResultItem
            {
                Id = "prune:run",
                Title = $"{candidates.Count} 件のフォルダをごみ箱に移す",
                Subtitle = $"{candidates[^1].Name} 〜 {candidates[0].Name}（ごみ箱へ）",
                Actions =
                [
                    new ItemAction("ごみ箱に移す", async ctx =>
                    {
                        await MoveToTrashAsync(candidates, ctx.CancellationToken);
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
            return [new ResultItem { Id = "keep:none", Title = "フォルダがない" }];
        }
        return
        [
            .. folders.Select(folder => new ResultItem
            {
                Id = "folder:" + folder.Name,
                Title = folder.Kept ? $"★ {folder.Name}" : folder.Name,
                Subtitle = folder.Kept ? "保持中 ・ Enter で解除" : "Enter で保持",
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
            Subtitle = "Ctrl+Enter で保持",
            Actions = [ItemAction.Open(folder.Path), ToggleKept(folder, refresh)],
        };

    /// <summary>直近のフォルダの項目。Enter で開き、保持の切り替えとごみ箱に移すことはアクションパネルから選ぶ。</summary>
    private ResultItem RecentItem(DailyFolderEntry folder, string refresh) =>
        new()
        {
            Id = "folder:" + folder.Name,
            Title = folder.Kept ? $"★ {folder.Name}" : folder.Name,
            Subtitle = folder.Kept ? "保持中" : null,
            Actions =
            [
                ItemAction.Open(folder.Path),
                ToggleKept(folder, refresh),
                new ItemAction("ごみ箱に移す", async ctx =>
                {
                    await MoveToTrashAsync([folder], ctx.CancellationToken);
                    return ActionResult.SetQuery(refresh);
                }),
                ItemAction.CopyText(folder.Path, "パスをコピー"),
            ],
        };

    private void TakeTrashError(List<ResultItem> items)
    {
        if (Interlocked.Exchange(ref _trashError, null) is { } error)
        {
            items.Add(new ResultItem { Id = "trash:error", Title = error.Title, Subtitle = error.Detail });
        }
    }

    private ItemAction ToggleKept(DailyFolderEntry folder, string refresh) =>
        new(folder.Kept ? "保持を解除する" : "保持する", _ =>
        {
            _folders.ToggleKept(folder.Name);
            return ValueTask.FromResult(ActionResult.SetQuery(refresh));
        });

    private async Task MoveToTrashAsync(IReadOnlyList<DailyFolderEntry> candidates, CancellationToken cancellationToken)
    {
        var moved = 0;
        Exception? firstError = null;
        foreach (var folder in candidates)
        {
            try
            {
                await Trash.MoveAsync(folder.Path, cancellationToken);
                moved++;
                // 消したフォルダの保持は残さない（同じ名前のフォルダを後で作っても保持にならないように）
                if (folder.Kept)
                {
                    _folders.ToggleKept(folder.Name);
                }
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
            _trashError = new TrashError($"{candidates.Count - moved} 件のフォルダをごみ箱に移せなかった", firstError.Message);
        }
    }
}
