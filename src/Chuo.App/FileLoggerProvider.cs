using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Chuo.App;

/// <summary>
/// ログをファイルに書く。日ごとにファイルを切り替え、行にカテゴリ（プラグインの型の名前空間を含む）を出す（要件定義書 10 章）。
/// </summary>
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _date;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(LevelName(level)).Append(' ')
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            try
            {
                GetWriter(DateOnly.FromDateTime(now)).WriteLine(line);
            }
            catch (IOException)
            {
                // ログが書けなくても本体の動作は続ける
            }
        }
    }

    private StreamWriter GetWriter(DateOnly date)
    {
        if (_writer is null || date != _date)
        {
            _writer?.Dispose();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"chuo-{date:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
            {
                AutoFlush = true,
            };
            _date = date;
        }
        return _writer;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
