using System.Diagnostics;

namespace ConstantProxy.Infrastructure.Ssh;

/// <summary>A real child process wrapped as <see cref="ISshProcess"/>. Output is consumed asynchronously so pipes never fill.</summary>
internal sealed class LocalSshProcess : ISshProcess
{
    private readonly Process process;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalSshProcess(Process process)
    {
        this.process = process;
        Pid = process.Id;
        StartTimeUtc = DateTimeOffset.UtcNow;
    }

    public int Pid { get; }

    public DateTimeOffset StartTimeUtc { get; }

    public Task<int> Exited => exited.Task;

    public event Action<SshOutputLine>? OutputReceived;

    public bool HasExited => exited.Task.IsCompleted || SafeHasExited();

    /// <summary>Attaches readers and the exit watcher. Called once, right after the process started.</summary>
    public void BeginObserving()
    {
        process.OutputDataReceived += (_, e) => Publish(OutputStream.StandardOutput, e.Data);
        process.ErrorDataReceived += (_, e) => Publish(OutputStream.StandardError, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.Exited += (_, _) => _ = Task.Run(CompleteExit);

        // The process may have ended before the handler was attached.
        if (SafeHasExited())
        {
            _ = Task.Run(CompleteExit);
        }
    }

    public bool RequestStop()
    {
        try
        {
            return !SafeHasExited() && process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Kill()
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
            // access denied or already exiting; the exit watcher reports the real outcome
        }
    }

    public void Dispose()
    {
        if (exited.Task.IsCompleted)
        {
            process.Dispose();
        }
    }

    private void Publish(OutputStream stream, string? data)
    {
        if (data is not null)
        {
            OutputReceived?.Invoke(new SshOutputLine(stream, data));
        }
    }

    private void CompleteExit()
    {
        var code = -1;
        try
        {
            process.WaitForExit(); // drains the async readers
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // process object disposed or never started; report the unknown code
        }

        exited.TrySetResult(code);
    }

    private bool SafeHasExited()
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
