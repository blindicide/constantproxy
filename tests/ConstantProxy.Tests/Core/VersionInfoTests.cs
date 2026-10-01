using System.Text.RegularExpressions;

namespace ConstantProxy.Tests.Core;

public class VersionInfoTests
{
    [Fact]
    public void VersionIsSemverFromBuildMetadata() =>
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"), VersionInfo.Version);

    [Fact]
    public void BuildMetadataSuffixIsStripped()
    {
        // The Core assembly's informational version may carry "+<commit>"; it must never leak into the display version.
        Assert.DoesNotContain('+', VersionInfo.Version);
    }

    [Fact]
    public void RepositoryUrlComesFromBuildMetadata() => Assert.StartsWith("https://github.com/", VersionInfo.RepositoryUrl);

    [Fact]
    public void ReportedVersionIsTheOneDefinedInDirectoryBuildProps()
    {
        // The version is defined once (SPEC §41); About, diagnostics and --version must all show exactly that number.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(dir!.FullName, "Directory.Build.props"));
        var declared = props.Descendants("Version").Single().Value;
        Assert.Equal(declared, VersionInfo.Version);
    }
}
