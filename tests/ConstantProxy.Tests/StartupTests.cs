using ConstantProxy.App;

namespace ConstantProxy.Tests
{
    public class StartupTests
    {
        [Theory]
        [InlineData("--version")]
        [InlineData("--VERSION")]
        public void VersionExitsSuccessfullyWithoutConstructingWpf(string argument)
        {
            App.App.FailOnConstruction = true;
            ConsoleOutput.LastLine = null;
            Assert.Equal(0, Program.Main(new[] { "--startup", argument }));
            Assert.Equal($"constantproxy {VersionInfo.Version}", ConsoleOutput.LastLine);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("--startup")]
        [InlineData("--version-extra")]
        public void OtherArgumentsConstructAndRunGui(string? argument)
        {
            App.App.FailOnConstruction = false;
            ConsoleOutput.LastLine = null;
            Assert.Equal(23, Program.Main(argument is null ? Array.Empty<string>() : new[] { argument }));
            Assert.Null(ConsoleOutput.LastLine);
        }
    }
}

// Stand-ins make entering WPF observable while running the production entry point on any OS.
namespace ConstantProxy.App
{
    internal sealed class App
    {
        public static bool FailOnConstruction { get; set; }

        public App()
        {
            if (FailOnConstruction)
            {
                throw new InvalidOperationException("The version command must not construct WPF.");
            }
        }

        public int Run()
        {
            return 23;
        }
    }

    internal static class ConsoleOutput
    {
        public static string? LastLine { get; set; }
        public static void WriteLine(string text) => LastLine = text;
    }
}
