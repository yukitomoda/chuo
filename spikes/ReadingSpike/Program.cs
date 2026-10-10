using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ReadingSpike;

// 引数なしで起動すると、エンジンごとに子プロセスを起動して計測する（メモリを互いに干渉させないため）。
// 引数にエンジン名を指定すると、そのエンジンだけを計測する。
if (args.Length == 0)
{
    foreach (var name in EngineFactory.Names)
    {
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, name) { UseShellExecute = false })!;
        child.WaitForExit();
    }
    return 0;
}

var engineName = args[0];
var bulkCount = args.Length > 1 ? int.Parse(args[1]) : 20_000;

var before = Memory.Snapshot();
var start = Stopwatch.GetTimestamp();
var engine = EngineFactory.Create(engineName);
var loadMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
var afterLoad = Memory.Snapshot();

// 初回の呼び出し（JIT とページフォールトを含む）
start = Stopwatch.GetTimestamp();
engine.GetReading(Samples.Items[0]);
var firstMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

Console.WriteLine($"=== {engineName}");
Console.WriteLine($"load={loadMs:F1}ms first={firstMs:F2}ms");

if (engineName != "none")
{
    foreach (var item in Samples.Items)
    {
        var reading = engine.GetReading(item.Normalize(NormalizationForm.FormKC));
        Console.WriteLine($"  {item} → {reading} → {Romaji.FromKatakana(reading)}");
    }

    foreach (var (query, target) in Samples.RomajiQueries)
    {
        var romaji = Romaji.FromKatakana(engine.GetReading(target));
        Console.WriteLine($"  [{(romaji.StartsWith(query) ? "OK" : "NG")}] {query} → {target}（{romaji}）");
    }
}

// 多数の項目に読みを付与したときの時間（ファイル検索のインデックスを想定）
var (bulkMs, bulkChars) = Measure(Samples.Bulk(bulkCount));
var afterBulk = Memory.Snapshot();

// 辞書の広い範囲を引かせたときの時間とメモリ（メモリマップドファイルの常駐量の上限の目安）
var (randomMs, randomChars) = Measure(Samples.RandomKanji(bulkCount));
var afterRandom = Memory.Snapshot();

engine.Dispose();
engine = null;
var afterDispose = Memory.Snapshot();

Console.WriteLine($"bulk  : {bulkCount} items {bulkMs:F0}ms ({bulkMs * 1000 / bulkCount:F1}us/item, {bulkChars} chars)");
Console.WriteLine($"random: {bulkCount} items {randomMs:F0}ms ({randomMs * 1000 / bulkCount:F1}us/item, {randomChars} chars)");
Console.WriteLine($"memory before   : {before}");
Console.WriteLine($"memory load     : {afterLoad}");
Console.WriteLine($"memory bulk     : {afterBulk}");
Console.WriteLine($"memory random   : {afterRandom}");
Console.WriteLine($"memory disposed : {afterDispose}");
GC.KeepAlive(engine);
return 0;

(double Ms, int Chars) Measure(IEnumerable<string> items)
{
    var list = items.ToList();
    var begin = Stopwatch.GetTimestamp();
    var chars = 0;
    foreach (var item in list)
    {
        chars += Romaji.FromKatakana(engine!.GetReading(item.Normalize(NormalizationForm.FormKC))).Length;
    }
    return (Stopwatch.GetElapsedTime(begin).TotalMilliseconds, chars);
}

internal readonly record struct Memory(double WorkingSetMb, double PrivateMb, double ManagedMb, double RssAnonMb, double RssFileMb)
{
    public static Memory Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        var (anon, file) = ReadRss();
        return new Memory(p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0, anon, file);
    }

    /// <summary>Linux では RSS を匿名メモリとファイルの割り当て（メモリマップドファイルなど）に分けて取得する。</summary>
    private static (double Anon, double File) ReadRss()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (double.NaN, double.NaN);
        }
        var status = File.ReadAllText("/proc/self/status");
        double Kb(string key) => double.Parse(Regex.Match(status, key + @":\s+(\d+)").Groups[1].Value) / 1024.0;
        return (Kb("RssAnon"), Kb("RssFile"));
    }

    public override string ToString() =>
        $"workingSet={WorkingSetMb:F1}MB private={PrivateMb:F1}MB managed={ManagedMb:F1}MB rssAnon={RssAnonMb:F1}MB rssFile={RssFileMb:F1}MB";
}
