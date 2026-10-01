namespace ConstantProxy.Core.Models;

/// <summary>
/// One SSH SOCKS tunnel configuration. Defaults exist only for convenience; the SSH target is intentionally empty
/// and must be supplied by the user (SPEC §2.2, §6).
/// </summary>
public enum TrafficMode
{
    /// <summary>
    /// constantproxy listens on the configured port and relays to ssh on an internal loopback port, counting
    /// exactly the bytes that pass through. Accurate without elevated privileges or unrelated system traffic.
    /// </summary>
    Bridge,

    /// <summary>ssh listens on the configured port directly; no traffic figures are available.</summary>
    Off,
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Default";

    /// <summary>OpenSSH host alias or hostname. OpenSSH resolves it via its own configuration.</summary>
    public string Host { get; set; } = string.Empty;

    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 10080;

    /// <summary>Empty means automatic detection; otherwise a path or command name.</summary>
    public string SshExecutable { get; set; } = string.Empty;

    public bool IPv4Only { get; set; } = true;

    public int ServerAliveInterval { get; set; } = 30;

    public int ServerAliveCountMax { get; set; } = 3;

    public bool ExitOnForwardFailure { get; set; } = true;

    /// <summary>Adds <c>-o BatchMode=yes</c> so ssh fails instead of waiting for interactive input.</summary>
    public bool BatchMode { get; set; }

    /// <summary>Extra arguments inserted before the target, one list entry per process argument.</summary>
    public List<string> AdditionalArguments { get; set; } = new();

    /// <summary>How long to wait for the SOCKS listener to appear before the attempt counts as failed (SPEC §13).</summary>
    public int StartupTimeoutSeconds { get; set; } = 15;

    public ReconnectSettings Reconnect { get; set; } = new();

    public HealthCheckSettings Monitoring { get; set; } = new();

    /// <summary>How proxy traffic is measured (SPEC §16).</summary>
    public TrafficMode TrafficMode { get; set; } = TrafficMode.Bridge;

    public Profile Clone()
    {
        var copy = (Profile)MemberwiseClone();
        copy.AdditionalArguments = new List<string>(AdditionalArguments);
        copy.Reconnect = Reconnect.Clone();
        copy.Monitoring = Monitoring.Clone();
        return copy;
    }
}
