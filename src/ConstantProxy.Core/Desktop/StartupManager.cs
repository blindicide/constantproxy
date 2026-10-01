namespace ConstantProxy.Core.Desktop;

/// <summary>Per-user "run at logon" storage (the Windows Run key in the real implementation).</summary>
public interface IStartupRegistry
{
    string? GetValue(string name);

    void SetValue(string name, string value);

    void DeleteValue(string name);
}

/// <summary>
/// Manages the optional "Start constantproxy with Windows" entry (SPEC §32). It only ever touches its own
/// value, keeps it pointing at the running executable, and never starts the tunnel by itself: connecting
/// automatically is a separate setting.
/// </summary>
public sealed class StartupManager
{
    public const string ValueName = "constantproxy";
    public const string StartupArgument = "--startup";

    private readonly IStartupRegistry registry;
    private readonly string executablePath;

    public StartupManager(IStartupRegistry registry, string executablePath)
    {
        this.registry = registry;
        this.executablePath = executablePath;
    }

    public string Command
    {
        get
        {
            if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
            {
                throw new InvalidOperationException("The executable path is not usable for a startup entry.");
            }

            return $"\"{executablePath}\" {StartupArgument}";
        }
    }

    public bool IsEnabled => !string.IsNullOrEmpty(registry.GetValue(ValueName));

    /// <summary>True when an entry exists but points somewhere else (the application was moved or reinstalled).</summary>
    public bool NeedsRepair => registry.GetValue(ValueName) is { Length: > 0 } current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase);

    public void Apply(bool enabled)
    {
        if (enabled)
        {
            if (!string.Equals(registry.GetValue(ValueName), Command, StringComparison.Ordinal))
            {
                registry.SetValue(ValueName, Command);
            }
        }
        else if (registry.GetValue(ValueName) is not null)
        {
            registry.DeleteValue(ValueName);
        }
    }

    /// <summary>Re-points an existing entry at the current executable; does nothing if the feature is off.</summary>
    public bool RepairIfNeeded()
    {
        if (!NeedsRepair)
        {
            return false;
        }

        registry.SetValue(ValueName, Command);
        return true;
    }
}
