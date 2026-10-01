using System.Diagnostics;
using System.Text.Json;

namespace ConstantProxy.Infrastructure.Desktop;

/// <summary>An ssh process constantproxy started, identified well enough to recognise it later without PID-reuse mistakes.</summary>
public sealed record RegisteredProcess(int Pid, DateTimeOffset StartTimeUtc, string ProcessName);

public sealed record ProcessSnapshot(string Name, DateTimeOffset StartTimeUtc);

/// <summary>Looks at and ends processes; an interface so orphan cleanup can be tested without touching real ones.</summary>
public interface IProcessInspector
{
    /// <summary>Returns null when no process with this id is running.</summary>
    ProcessSnapshot? Inspect(int pid);

    void Kill(int pid);
}

public sealed class SystemProcessInspector : IProcessInspector
{
    public ProcessSnapshot? Inspect(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return null;
            }

            return new ProcessSnapshot(process.ProcessName, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // not running, or not inspectable; either way it is not ours to touch
        }
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }
}

/// <summary>
/// Remembers which ssh processes constantproxy started so that, after a crash, leftovers can be cleaned up (SPEC §82).
/// It is deliberately conservative: a process is only ended if its PID, its start time and its name all match what was
/// recorded, so an unrelated ssh session (or a reused PID) is never touched.
/// </summary>
public sealed class ChildProcessRegistry
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string path;
    private readonly IProcessInspector inspector;
    private readonly IAppLog log;
    private readonly object gate = new();

    public ChildProcessRegistry(string path, IProcessInspector? inspector = null, IAppLog? log = null)
    {
        this.path = path;
        this.inspector = inspector ?? new SystemProcessInspector();
        this.log = log ?? NullAppLog.Instance;
    }

    public void Register(RegisteredProcess process)
    {
        lock (gate)
        {
            var all = LoadUnlocked().Where(p => p.Pid != process.Pid).ToList();
            all.Add(process);
            SaveUnlocked(all);
        }
    }

    public void Unregister(int pid)
    {
        lock (gate)
        {
            var all = LoadUnlocked();
            if (all.RemoveAll(p => p.Pid == pid) > 0)
            {
                SaveUnlocked(all);
            }
        }
    }

    public IReadOnlyList<RegisteredProcess> Load()
    {
        lock (gate)
        {
            return LoadUnlocked();
        }
    }

    /// <summary>Ends processes recorded by a previous run that are provably still ours. Returns how many were ended.</summary>
    public int CleanupOrphans()
    {
        lock (gate)
        {
            var killed = 0;
            foreach (var entry in LoadUnlocked())
            {
                var snapshot = inspector.Inspect(entry.Pid);
                if (snapshot is null)
                {
                    continue; // already gone: nothing to do beyond forgetting it
                }

                var sameProcess = string.Equals(snapshot.Name, entry.ProcessName, StringComparison.OrdinalIgnoreCase)
                                  && (snapshot.StartTimeUtc - entry.StartTimeUtc).Duration() <= StartTimeTolerance;
                if (sameProcess)
                {
                    log.Warn("cleanup", $"Ending leftover ssh process {entry.Pid} from a previous run.");
                    inspector.Kill(entry.Pid);
                    killed++;
                }
                else
                {
                    log.Info("cleanup", $"PID {entry.Pid} now belongs to a different process; leaving it alone.");
                }
            }

            SaveUnlocked(new List<RegisteredProcess>()); // everything recorded was either handled or stale
            return killed;
        }
    }

    private List<RegisteredProcess> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new List<RegisteredProcess>();
            }

            return JsonSerializer.Deserialize<List<RegisteredProcess>>(File.ReadAllText(path), Json) ?? new List<RegisteredProcess>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Warn("cleanup", "The child process registry could not be read; treating it as empty.", ex);
            return new List<RegisteredProcess>();
        }
    }

    private void SaveUnlocked(List<RegisteredProcess> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("cleanup", "The child process registry could not be saved.", ex);
        }
    }
}
