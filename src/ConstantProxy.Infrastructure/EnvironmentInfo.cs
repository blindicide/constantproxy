using System.Runtime.InteropServices;

namespace ConstantProxy.Infrastructure;

/// <summary>Facts about the machine for the diagnostics report.</summary>
public static class EnvironmentInfo
{
    public static string OsVersion => RuntimeInformation.OSDescription;

    public static string RuntimeVersion => RuntimeInformation.FrameworkDescription;
}
