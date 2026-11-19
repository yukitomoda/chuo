using System.Text.Json;

namespace Chuo.Plugins.Reminders;

/// <summary>登録済みのリマインダー1件。</summary>
public sealed record ReminderEntry(string Id, string Title, DateTimeOffset RemindAt);

/// <summary>
/// リマインダーの永続化と管理。プラグインのデータフォルダの <c>reminders.json</c> に保存する。
/// </summary>
public sealed class ReminderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<ReminderEntry> _entries;

    public ReminderStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    /// <summary>待機中のリマインダーを、日時の近い順に返す。</summary>
    public IReadOnlyList<ReminderEntry> List()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    /// <summary>次に通知する時刻。登録がなければ null。</summary>
    public DateTimeOffset? PeekNextDue()
    {
        lock (_gate)
        {
            return _entries.Count > 0 ? _entries[0].RemindAt : null;
        }
    }

    public ReminderEntry Add(string title, DateTimeOffset remindAt)
    {
        var entry = new ReminderEntry(Guid.NewGuid().ToString("N")[..8], title, remindAt);
        lock (_gate)
        {
            _entries.Add(entry);
            _entries.Sort(static (a, b) => a.RemindAt.CompareTo(b.RemindAt));
            Save();
        }
        return entry;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _entries.RemoveAll(e => e.Id == id) > 0;
            if (removed)
            {
                Save();
            }
            return removed;
        }
    }

    /// <summary><paramref name="now"/> 以前になったリマインダーを取り出して保存から消す。</summary>
    public IReadOnlyList<ReminderEntry> TakeDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_entries.Count == 0 || _entries[0].RemindAt > now)
            {
                return [];
            }
            var count = 0;
            while (count < _entries.Count && _entries[count].RemindAt <= now)
            {
                count++;
            }
            var due = _entries.GetRange(0, count);
            _entries.RemoveRange(0, count);
            Save();
            return due;
        }
    }

    private static List<ReminderEntry> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            var entries = JsonSerializer.Deserialize<List<ReminderEntry>>(File.ReadAllText(path), JsonOptions) ?? [];
            entries.Sort(static (a, b) => a.RemindAt.CompareTo(b.RemindAt));
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save()
    {
        if (Path.GetDirectoryName(_path) is { } dir)
        {
            Directory.CreateDirectory(dir);
        }
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
