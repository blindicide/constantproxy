namespace ConstantProxy.Infrastructure.Config;

/// <summary>Per-user data locations (SPEC §23). Nothing is written beside the executable.</summary>
public sealed class AppPaths
{
    public const string DataDirEnvironmentVariable = "CONSTANTPROXY_DATA_DIR";

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "config.json");

    public string DatabaseFile => Path.Combine(Root, "constantproxy.db");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string LogFile => Path.Combine(LogsDirectory, "constantproxy.log");

    public static AppPaths ForCurrentUser()
    {
        var overridePath = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return new AppPaths(overridePath);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "constantproxy"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}
