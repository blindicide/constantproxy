using System.Globalization;
using System.Net;
using ConstantProxy.Core.Traffic;

namespace ConstantProxy.Core.Ssh;

/// <summary>Generates the ssh.exe argument list for a profile (SPEC §5, §72). Never builds a shell string.</summary>
public static class SshArgumentBuilder
{
    /// <summary>
    /// Builds the argument list. <paramref name="listen"/> lets the traffic bridge move ssh's local listener to an
    /// internal loopback endpoint while the profile's port stays the user-facing one.
    /// </summary>
    public static IReadOnlyList<string> Build(Profile profile, SshListenOverride? listen = null)
    {
        var args = new List<string>();
        if (profile.IPv4Only)
        {
            args.Add("-4");
        }

        args.Add("-N");
        args.Add("-D");
        args.Add(FormatEndpoint(listen?.Address ?? profile.BindAddress, listen?.Port ?? profile.Port));

        AddOption(args, "ServerAliveInterval", profile.ServerAliveInterval.ToString(CultureInfo.InvariantCulture));
        AddOption(args, "ServerAliveCountMax", profile.ServerAliveCountMax.ToString(CultureInfo.InvariantCulture));
        if (profile.ExitOnForwardFailure)
        {
            AddOption(args, "ExitOnForwardFailure", "yes");
        }

        if (profile.BatchMode)
        {
            AddOption(args, "BatchMode", "yes");
        }

        args.AddRange(profile.AdditionalArguments);
        args.Add(profile.Host);
        return args;
    }

    /// <summary>Human-readable command line for display (SPEC §75). Quoting is cosmetic only.</summary>
    public static string FormatCommandLine(string executable, IEnumerable<string> arguments) =>
        CommandLineSplitter.Join(new[] { string.IsNullOrWhiteSpace(executable) ? "ssh" : executable }.Concat(arguments));

    public static string FormatEndpoint(string bindAddress, int port)
    {
        var host = bindAddress.Trim();
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            host = $"[{host.Trim('[', ']')}]";
        }

        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void AddOption(List<string> args, string name, string value)
    {
        args.Add("-o");
        args.Add($"{name}={value}");
    }
}
