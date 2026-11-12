using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Chuo.Plugins.DailyFolder;

/// <summary>日付ごとのフォルダ1つ。</summary>
public sealed record DailyFolderEntry(string Name, string Path, DateOnly Date, bool Kept);

/// <summary>日付ごとのフォルダの一覧、今日のフォルダの作成、古いフォルダの掃除。</summary>
public sealed class DailyFolders(IOptionsMonitor<DailyFolderOptions> options, KeptFolders kept, TimeProvider time)
{
    public DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public int RetentionDays => Math.Max(1, options.CurrentValue.RetentionDays);

    public string TodayPath => System.IO.Path.Combine(Root, Today.ToString(Format, CultureInfo.InvariantCulture));

    private string Root => options.CurrentValue.Root is { Length: > 0 } root ? root : DailyFolderOptions.DefaultRoot;

    private string Format => options.CurrentValue.NameFormat is { Length: > 0 } format ? format : "yyyy-MM-dd";

    /// <summary>今日のフォルダ。まだ作っていなくても返す（作る前に保持できるように）。</summary>
    public DailyFolderEntry TodayEntry()
    {
        var name = Today.ToString(Format, CultureInfo.InvariantCulture);
        return new DailyFolderEntry(name, TodayPath, Today, kept.Contains(name));
    }

    /// <summary>今日のフォルダを（なければ作って）返す。</summary>
    public string EnsureToday() => Directory.CreateDirectory(TodayPath).FullName;

    /// <summary>名前が日付の書式に合うフォルダを、新しい順に返す。</summary>
    public IReadOnlyList<DailyFolderEntry> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        var format = Format;
        return
        [
            .. Directory.EnumerateDirectories(Root)
                .Select(path => (Path: path, Name: System.IO.Path.GetFileName(path)))
                .Where(d => DateOnly.TryParseExact(d.Name, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .Select(d => new DailyFolderEntry(
                    d.Name, d.Path, DateOnly.ParseExact(d.Name, format, CultureInfo.InvariantCulture), kept.Contains(d.Name)))
                .OrderByDescending(d => d.Date),
        ];
    }

    /// <summary>prune で削除するフォルダ。残す日数より古く、保持していないもの。</summary>
    public IReadOnlyList<DailyFolderEntry> PruneCandidates()
    {
        var oldest = Today.AddDays(-(RetentionDays - 1));
        return [.. List().Where(d => d.Date < oldest && !d.Kept)];
    }

    /// <summary>今日より前のフォルダを、新しい順に <paramref name="count"/> 件まで返す。</summary>
    public IReadOnlyList<DailyFolderEntry> Recent(int count) => [.. List().Where(d => d.Date < Today).Take(count)];

    public bool ToggleKept(string name) => kept.Toggle(name);
}

/// <summary>保持するフォルダの名前。プラグインのデータのフォルダに JSON で保存する。</summary>
public sealed class KeptFolders(string path)
{
    private readonly Lock _gate = new();
    private HashSet<string>? _names;

    public bool Contains(string name)
    {
        lock (_gate)
        {
            return Load().Contains(name);
        }
    }

    /// <summary>保持を切り替え、切り替えた後に保持しているかを返す。</summary>
    public bool Toggle(string name)
    {
        lock (_gate)
        {
            var names = Load();
            var keptNow = names.Add(name) || !names.Remove(name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(names.Order(StringComparer.Ordinal)));
            return keptNow;
        }
    }

    private HashSet<string> Load()
    {
        if (_names is null)
        {
            _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                _names.UnionWith(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? []);
            }
        }
        return _names;
    }
}

/// <summary>フォルダをごみ箱に移す。</summary>
internal static class Trash
{
    public static async Task MoveAsync(string path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin),
                cancellationToken);
            return;
        }

        var start = new ProcessStartInfo("gio") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("trash");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("gio を起動できなかった");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new IOException($"gio trash が失敗した（{process.ExitCode}）: {error.Trim()}");
        }
    }
}
