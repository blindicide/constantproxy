using System.Runtime.CompilerServices;
using ConstantProxy.Core;

namespace ConstantProxy.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Handle CLI requests before constructing Application or initializing the WPF dispatcher.
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            ConsoleOutput.WriteLine($"{VersionInfo.ProductName} {VersionInfo.Version}");
            return 0;
        }

        return RunGui();
    }

    // Keep WPF types out of Main's JIT compilation as well as its CLI execution path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunGui()
    {
        var app = new App();
        return app.Run();
    }
}
