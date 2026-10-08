namespace TwitchDropsMiner.Core;

public enum LogLevel { Debug = 0, Call = 1, Info = 2, Warning = 3, Error = 4 }

/// <summary>
/// Простой логгер. Сообщения уровня не ниже <see cref="Level"/> уходят в окно «Вывод»
/// (через <see cref="Sink"/>) и, при включённом --log, в файл log.txt.
/// </summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Error;
    public static bool DebugWs { get; set; }
    public static bool DebugGql { get; set; }
    public static Action<string>? Sink { get; set; }

    private static StreamWriter? _file;
    private static readonly Lock _fileLock = new();

    public static void EnableFile(string path)
    {
        lock (_fileLock)
        {
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        }
    }

    public static void Close()
    {
        lock (_fileLock) { _file?.Dispose(); _file = null; }
    }

    public static void Write(LogLevel level, string message)
    {
        lock (_fileLock)
        {
            _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}:\t{level,7}:\t{message}");
        }
        if (level >= Level) Sink?.Invoke($"{level.ToString().ToUpperInvariant()}: {message}");
    }

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Call(string m) => Write(LogLevel.Call, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warning(string m) => Write(LogLevel.Warning, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static void Ws(string m) { if (DebugWs) Write(LogLevel.Error, "[WS] " + m); else Debug("[WS] " + m); }
    public static void GqlLog(string m) { if (DebugGql) Write(LogLevel.Error, "[GQL] " + m); else Debug("[GQL] " + m); }
}
