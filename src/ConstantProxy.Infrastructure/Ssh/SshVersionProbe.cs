using System.Diagnostics;
using System.Text;

namespace ConstantProxy.Infrastructure.Ssh;

/// <summary>Runs <c>ssh -V</c> to report the installed OpenSSH version for diagnostics (SPEC §65, §84).</summary>
public static class SshVersionProbe
{
    /// <summary>Returns the first line OpenSSH prints (it writes the version to stderr), or null if it could not be run.</summary>
    public static async Task<string?> QueryAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-V");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return FirstLine(await stderr.ConfigureAwait(false)) ?? FirstLine(await stdout.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }
        }
    }

    public static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // cannot be killed from here; the OS reclaims it with this process
        }
    }
}
