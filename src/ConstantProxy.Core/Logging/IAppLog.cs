namespace ConstantProxy.Core.Logging;

public enum LogSeverity
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>Minimal structured logging sink (SPEC §24). Implementations must be thread-safe and must not throw.</summary>
public interface IAppLog
{
    void Log(LogSeverity severity, string source, string message, Exception? exception = null);
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string source, string message) => log.Log(LogSeverity.Debug, source, message);

    public static void Info(this IAppLog log, string source, string message) => log.Log(LogSeverity.Information, source, message);

    public static void Warn(this IAppLog log, string source, string message, Exception? ex = null) => log.Log(LogSeverity.Warning, source, message, ex);

    public static void Error(this IAppLog log, string source, string message, Exception? ex = null) => log.Log(LogSeverity.Error, source, message, ex);
}

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    public void Log(LogSeverity severity, string source, string message, Exception? exception = null)
    {
    }
}
