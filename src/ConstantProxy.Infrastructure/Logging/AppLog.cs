using System.Text;

namespace ConstantProxy.Infrastructure.Logging;

public sealed record LogEntry(DateTimeOffset TimeUtc, LogSeverity Severity, string Source, string Message);

/// <summary>
/// File + in-memory ring logger (SPEC §24, §25). Thread-safe, never throws, size-capped with one rolled file.
/// </summary>
public sealed class AppLog : IAppLog
{
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly object gate = new();
    private readonly string? filePath;
    private readonly int memoryCapacity;
    private readonly Func<DateTimeOffset> now;
    private readonly LinkedList<LogEntry> memory = new();
    private long fileSize = -1;

    public AppLog(string? filePath, LogSeverity minimum = LogSeverity.Information, int memoryCapacity = 2000, Func<DateTimeOffset>? now = null)
    {
        this.filePath = filePath;
        this.memoryCapacity = memoryCapacity;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        MinimumSeverity = minimum;
    }

    public event Action<LogEntry>? EntryAdded;

    public LogSeverity MinimumSeverity { get; set; }

    public void Log(LogSeverity severity, string source, string message, Exception? exception = null)
    {
        if (severity < MinimumSeverity)
        {
            return;
        }

        var text = exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})";
        var entry = new LogEntry(now(), severity, source, text);
        lock (gate)
        {
            memory.AddLast(entry);
            while (memory.Count > memoryCapacity)
            {
                memory.RemoveFirst();
            }

            WriteToFile(entry);
        }

        var handlers = EntryAdded;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<LogEntry>>())
        {
            try
            {
                handler(entry);
            }
            catch (Exception)
            {
                // a misbehaving subscriber must never break logging or starve other subscribers
            }
        }
    }

    public IReadOnlyList<LogEntry> Snapshot(string? source = null)
    {
        lock (gate)
        {
            return memory.Where(e => source is null || e.Source == source).ToArray();
        }
    }

    public static string Format(LogEntry entry) =>
        $"{entry.TimeUtc:yyyy-MM-ddTHH:mm:ss.fffZ} [{Tag(entry.Severity),-5}] {entry.Source}: {entry.Message}";

    private static string Tag(LogSeverity severity) => severity switch
    {
        LogSeverity.Debug => "DEBUG",
        LogSeverity.Information => "INFO",
        LogSeverity.Warning => "WARN",
        _ => "ERROR",
    };

    private void WriteToFile(LogEntry entry)
    {
        if (filePath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (fileSize < 0)
            {
                fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            }

            if (fileSize > MaxFileBytes)
            {
                File.Move(filePath, filePath + ".1", overwrite: true);
                fileSize = 0;
            }

            var line = Format(entry) + Environment.NewLine;
            File.AppendAllText(filePath, line, Encoding.UTF8);
            fileSize += Encoding.UTF8.GetByteCount(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; the in-memory ring still has the entry.
        }
    }
}
