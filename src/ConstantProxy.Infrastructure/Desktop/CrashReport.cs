using System.Globalization;
using System.Text;

namespace ConstantProxy.Infrastructure.Desktop;

/// <summary>
/// Writes a small plain-text crash report next to the logs (SPEC §50). It contains the version, the platform and the
/// exception, and deliberately nothing from the configuration, so it is safe to attach to a bug report.
/// </summary>
public static class CrashReport
{
    /// <summary>Returns the path of the written report, or null if it could not be written.</summary>
    public static string? Write(string directory, Exception exception, string version, DateTimeOffset now, string source = "unhandled exception")
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{now.UtcDateTime.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)}Z.txt");
            File.WriteAllText(path, Format(exception, version, now, source), Encoding.UTF8);
            Prune(directory);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // already crashing; never throw from the crash handler
        }
    }

    public static string Format(Exception exception, string version, DateTimeOffset now, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine("constantproxy crash report");
        sb.AppendLine("==========================");
        sb.Append("Version: ").AppendLine(version);
        sb.Append("Time (UTC): ").AppendLine(now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append("Source: ").AppendLine(source);
        sb.Append("OS: ").AppendLine(EnvironmentInfo.OsVersion);
        sb.Append(".NET: ").AppendLine(EnvironmentInfo.RuntimeVersion);
        sb.AppendLine();
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }

    /// <summary>Keeps the folder tidy: only the ten newest reports are retained.</summary>
    private static void Prune(string directory)
    {
        foreach (var old in new DirectoryInfo(directory).GetFiles("crash-*.txt").OrderByDescending(f => f.Name).Skip(10))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
