using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chuo.Plugins.DailyFolder;

/// <summary>
/// 設定で有効にしたとき、今日より前の空のフォルダを削除する（要件定義書 7.4）。起動の少し後と、その後は1時間ごとに行う。
/// 設定を有効にしたときも、すぐに行う。空のフォルダには何も入っていないので、ごみ箱には移さずに削除する。
/// </summary>
public sealed class EmptyFolderCleaner(
    DailyFolders folders, IOptionsMonitor<DailyFolderOptions> options, ILogger<EmptyFolderCleaner> logger) : BackgroundService
{
    /// <summary>起動を遅くしないよう、少し待ってから行う。</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var change = options.OnChange(o =>
        {
            if (o.DeleteEmptyFolders)
            {
                _ = Task.Run(Clean, stoppingToken);
            }
        });
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                Clean();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal void Clean()
    {
        if (!options.CurrentValue.DeleteEmptyFolders)
        {
            return;
        }
        lock (_gate)
        {
            foreach (var folder in folders.EmptyPastFolders())
            {
                try
                {
                    // 空でなければ失敗する（調べた後にファイルが置かれても、消さない）
                    Directory.Delete(folder.Path, recursive: false);
                    logger.LogInformation("空のフォルダ {Folder} を削除した", folder.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "空のフォルダ {Folder} を削除できなかった", folder.Path);
                }
            }
        }
    }
}
