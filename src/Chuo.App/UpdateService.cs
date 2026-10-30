using System.Reflection;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Chuo.App;

/// <summary>
/// 本体の更新（要件定義書 10 章、設計 10.2）。GitHub のリリースを起動の少し後と、その後は1日ごとに確かめ、
/// 新しい版があれば知らせる。更新はユーザーが選んだときだけ行い、ダウンロードしてから再起動して入れ替える。
/// インストーラーで入れたときだけ働く（zip を展開したものや開発中のビルドでは、手で更新する）。
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/yukitomoda/chuo";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager? _manager;
    private Timer? _timer;
    private UpdateInfo? _available;
    private bool _busy;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新の仕組みを初期化できなかった");
        }
    }

    /// <summary>インストーラーで入れたもので、アプリの中から更新できる。</summary>
    public bool IsSupported => _manager is not null;

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    /// <summary>更新できる版。なければ null。</summary>
    public string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();

    /// <summary>一度でも確かめ終えた。</summary>
    public bool IsChecked { get; private set; }

    /// <summary>状態の表示（「確認中…」「ダウンロード中 40%」など）。何もしていなければ null。</summary>
    public string? Status { get; private set; }

    /// <summary>状態が変わった。UI スレッドで呼ぶ。</summary>
    public event Action? Changed;

    /// <summary>更新を入れるためにアプリを終了する処理。App が設定する。</summary>
    public Action? Shutdown { get; set; }

    public void Start()
    {
        if (_manager is null)
        {
            return;
        }
        // 起動を遅くしないよう、少し待ってから確かめる
        _timer = new Timer(_ => Dispatcher.UIThread.Post(() => _ = CheckAsync()), null, FirstCheckDelay, CheckInterval);
    }

    /// <summary>新しい版があるか確かめる。UI スレッドで呼ぶ。</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || _busy)
        {
            return;
        }
        _busy = true;
        SetStatus("確認中…");
        try
        {
            _available = await Task.Run(_manager.CheckForUpdatesAsync);
            IsChecked = true;
            SetStatus(null);
            if (_available is not null)
            {
                _logger.LogInformation("{Version} に更新できる", AvailableVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新を確かめられなかった");
            SetStatus("確認できなかった");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 新しい版をダウンロードし、アプリを終了して入れ替える。入れ替えた後は <paramref name="restartArgs"/> を付けて起動し直す。
    /// UI スレッドで呼ぶ。
    /// </summary>
    public async Task ApplyAsync(params string[] restartArgs)
    {
        if (_manager is null || _available is not { } update || _busy)
        {
            return;
        }
        _busy = true;
        try
        {
            SetStatus("ダウンロード中…");
            await _manager.DownloadUpdatesAsync(update, progress => Dispatcher.UIThread.Post(() => SetStatus($"ダウンロード中 {progress}%")));
            _logger.LogInformation("{Version} に更新するため終了する", AvailableVersion);
            // 入れ替える小さなプロセスを起動し、このアプリが終わるのを待たせる
            _manager.WaitExitThenApplyUpdates(update, silent: false, restart: true, restartArgs);
            SetStatus("再起動中…");
            Shutdown?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新できなかった");
            SetStatus("更新できなかった");
            _busy = false;
        }
    }

    private void SetStatus(string? status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose() => _timer?.Dispose();
}
