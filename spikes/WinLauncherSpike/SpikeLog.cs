using System.Diagnostics;
using System.Text;

namespace WinLauncherSpike;

/// <summary>計測結果を %LOCALAPPDATA%\chuo-spike\spike.log に追記する。</summary>
public sealed class SpikeLog
{
    private readonly object _gate = new();

    public string FilePath { get; }

    public SpikeLog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "chuo-spike");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "spike.log");
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (_gate)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
        }
        Debug.WriteLine(line);
    }

    /// <summary>2つのタイムスタンプの差をミリ秒で返す。</summary>
    public static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    /// <summary>
    /// 現在のプロセスのメモリ使用量を表す文字列を返す。
    /// private はコミット済みのプライベートメモリ、privateWS はそのうち物理メモリに載っている量
    /// （タスクマネージャーの「メモリ」列）を表す。
    /// </summary>
    public static string MemorySummary()
    {
        using var process = Process.GetCurrentProcess();
        return $"workingSet={Mb(process.WorkingSet64)} " +
               $"private={Mb(process.PrivateMemorySize64)} " +
               $"privateWS={PrivateWorkingSet()} " +
               $"managedHeap={Mb(GC.GetTotalMemory(false))}";
    }

    private static string PrivateWorkingSet()
    {
        var size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>();
        return Native.GetProcessMemoryInfo(Native.GetCurrentProcess(), out var counters, size)
            ? Mb((long)counters.PrivateWorkingSetSize)
            : "n/a";
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1}MB";
}
