using System.IO.Pipes;
using System.Text;

namespace ConstantProxy.Infrastructure.Desktop;

/// <summary>
/// Allows only one GUI instance per user (SPEC §38). The first instance owns a named mutex and listens on a
/// per-user named pipe; later instances ask it to show its window and exit. The mutex must be acquired and disposed
/// on the same thread (the UI thread in the application).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string ActivateCommand = "ACTIVATE";
    private const string AcknowledgeReply = "OK";

    private readonly Mutex mutex;
    private readonly string pipeName;
    private CancellationTokenSource? cts;
    private Task? listener;
    private bool disposed;

    private SingleInstanceGuard(Mutex mutex, string pipeName)
    {
        this.mutex = mutex;
        this.pipeName = pipeName;
    }

    /// <summary>A per-user name; two users on the same machine never block each other.</summary>
    public static string NameFor(string userName)
    {
        var safe = new string(userName.Where(char.IsLetterOrDigit).ToArray());
        return "constantproxy-" + (safe.Length == 0 ? "user" : safe);
    }

    /// <summary>Returns the guard if this process is the first instance, or null when another instance already runs.</summary>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // the previous owner crashed; ownership has passed to us
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, name + "-activate");
    }

    /// <summary>
    /// Asks the running instance to come to the foreground and waits for its acknowledgement, retrying until
    /// <paramref name="timeout"/>. Returns false if none confirmed in time.
    /// </summary>
    public static bool TryActivateExisting(string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var remaining = deadline - DateTime.UtcNow;
            if (TryActivateOnce(name, remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1)))
            {
                return true;
            }

            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static bool TryActivateOnce(string name, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name + "-activate", PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(Math.Max((int)timeout.TotalMilliseconds, 1));
            var bytes = Encoding.UTF8.GetBytes(ActivateCommand + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var reader = new StreamReader(client, Encoding.UTF8);
            var reply = reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
            return string.Equals(reply?.Trim(), AcknowledgeReply, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Starts listening for activation requests; <paramref name="onActivate"/> runs on a background thread.</summary>
    public void StartListening(Action onActivate)
    {
        if (listener is not null)
        {
            return;
        }

        cts = new CancellationTokenSource();
        var token = cts.Token;
        listener = Task.Run(() => ListenAsync(onActivate, token));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cts?.Cancel();
        try
        {
            listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the listener ends by cancellation
        }

        cts?.Dispose();
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not owned by this thread (disposed from elsewhere); the OS releases it when the process ends
        }

        mutex.Dispose();
    }

    private async Task ListenAsync(Action onActivate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // The next instance is created right away so a client arriving while one is being served finds a listener.
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                continue; // client vanished before we could serve it
            }

            _ = Task.Run(() => ServeAsync(server, onActivate, token), CancellationToken.None);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream server, Action onActivate, CancellationToken token)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
                read.CancelAfter(TimeSpan.FromSeconds(2)); // a stalled client must not hold a listener forever
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(read.Token).ConfigureAwait(false);
                if (string.Equals(line?.Trim(), ActivateCommand, StringComparison.Ordinal))
                {
                    onActivate();
                    var reply = Encoding.UTF8.GetBytes(AcknowledgeReply + "\n");
                    await server.WriteAsync(reply, read.Token).ConfigureAwait(false);
                    await server.FlushAsync(read.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // client gone or too slow; nothing to acknowledge
            }
        }
    }
}
