using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConstantProxy.App;

/// <summary>
/// Lets a GUI-subsystem executable print a line for command-line switches such as <c>--version</c>. When stdout is
/// redirected (a file, a pipe, CI) the normal console writer is used; when started from an interactive console the
/// parent's console is attached and written to directly, because attaching alone does not give .NET a stdout handle.
/// </summary>
internal static class ConsoleOutput
{
    private const int StdOutputHandle = -11;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const int AttachParentProcess = -1;

    public static void WriteLine(string text)
    {
        if (HasStandardOutput())
        {
            Console.WriteLine(text);
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return; // started from Explorer or a service: there is nowhere to print
        }

        using var handle = CreateFile("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return;
        }

        using var stream = new FileStream(handle, FileAccess.Write);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine(text);
    }

    private static bool HasStandardOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
