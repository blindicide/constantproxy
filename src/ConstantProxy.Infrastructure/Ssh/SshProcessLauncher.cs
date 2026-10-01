using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ConstantProxy.Infrastructure.Ssh;

/// <summary>
/// Starts ssh directly with an explicit argument list: no cmd.exe, no shell, no quoting (SPEC §5, §72).
/// </summary>
public sealed class SshProcessLauncher : ISshProcessLauncher
{
    private readonly SshLocator locator;

    public SshProcessLauncher(SshLocator? locator = null)
    {
        this.locator = locator ?? new SshLocator();
    }

    public ISshProcess Start(SshLaunchSpec spec)
    {
        var path = locator.Resolve(spec.Executable)
                   ?? throw new SshLaunchException(LaunchFailureKind.NotFound, $"Executable not found: '{(string.IsNullOrWhiteSpace(spec.Executable) ? "ssh" : spec.Executable)}'.");

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new SshLaunchException(LaunchFailureKind.Other, "The process did not start.");
            }
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new SshLaunchException(MapNativeError(ex.NativeErrorCode), $"{ex.Message} (native error {ex.NativeErrorCode})", ex);
        }
        catch (InvalidOperationException ex)
        {
            process.Dispose();
            throw new SshLaunchException(LaunchFailureKind.Other, ex.Message, ex);
        }

        try
        {
            process.StandardInput.Close(); // ssh -N never reads stdin; avoid inheriting an open pipe
        }
        catch (IOException)
        {
            // already closed by an immediate exit
        }

        var wrapped = new LocalSshProcess(process);
        wrapped.BeginObserving();
        return wrapped;
    }

    private static LaunchFailureKind MapNativeError(int code) => code switch
    {
        2 or 3 => LaunchFailureKind.NotFound,            // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND / ENOENT
        5 or 13 => LaunchFailureKind.AccessDenied,        // ERROR_ACCESS_DENIED / EACCES
        8 or 193 or 216 => LaunchFailureKind.InvalidExecutable, // ENOEXEC / ERROR_BAD_EXE_FORMAT
        _ => LaunchFailureKind.Other,
    };
}
