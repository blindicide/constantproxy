namespace ConstantProxy.Tests.Infrastructure;

public class SshLocatorTests
{
    [Fact]
    public void AutomaticDetectionFindsSshOnPathOnWindows()
    {
        var existing = new HashSet<string> { @"C:\Tools\ssh.exe" };
        var locator = new SshLocator(existing.Contains, @"C:\Windows;C:\Tools", isWindows: true, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Tools\ssh.exe", locator.Resolve(""));
    }

    [Fact]
    public void AutomaticDetectionFallsBackToTheWindowsBuiltInOpenSsh()
    {
        var existing = new HashSet<string> { @"C:\Windows\System32\OpenSSH\ssh.exe" };
        var locator = new SshLocator(existing.Contains, @"C:\Nothing", isWindows: true, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Windows\System32\OpenSSH\ssh.exe", locator.Resolve(null));
    }

    [Fact]
    public void ExplicitPathIsUsedVerbatimWhenItExists()
    {
        var locator = new SshLocator(p => p == @"D:\ssh\ssh.exe", "", isWindows: true);
        Assert.Equal(@"D:\ssh\ssh.exe", locator.Resolve(@"D:\ssh\ssh.exe"));
        Assert.Null(locator.Resolve(@"D:\gone\ssh.exe"));
    }

    [Fact]
    public void BareNameGetsExeSuffixOnWindows()
    {
        var locator = new SshLocator(p => p == @"C:\Bin\myssh.exe", @"C:\Bin", isWindows: true);
        Assert.Equal(@"C:\Bin\myssh.exe", locator.Resolve("myssh"));
    }

    [Fact]
    public void UnixStylePathSearch()
    {
        var locator = new SshLocator(p => p == "/usr/bin/ssh", "/bin:/usr/bin", isWindows: false);
        Assert.Equal("/usr/bin/ssh", locator.Resolve(""));
    }

    [Fact]
    public void ReturnsNullWhenNothingIsFound() =>
        Assert.Null(new SshLocator(_ => false, "/bin", isWindows: false).Resolve(""));
}
