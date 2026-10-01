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
}
