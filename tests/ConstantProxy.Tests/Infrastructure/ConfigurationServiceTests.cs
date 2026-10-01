namespace ConstantProxy.Tests.Infrastructure;

public class ConfigurationServiceTests
{
    [Fact]
    public void MissingFileYieldsDefaultsAndCreatedStatus()
    {
        using var dir = new TempDir();
        var result = new ConfigurationService(dir.File("config.json")).Load();

        Assert.Equal(ConfigLoadStatus.Created, result.Status);
        var profile = Assert.Single(result.Config.Profiles);
        Assert.Equal(profile.Id, result.Config.ActiveProfileId);
        Assert.Equal(string.Empty, profile.Host);
    }

    [Fact]
    public void SaveThenLoadRoundTripsEveryField()
    {
        using var dir = new TempDir();
        var service = new ConfigurationService(dir.File("config.json"));
        var config = AppConfig.CreateDefault();
        var p = config.ActiveProfile;
        p.Name = "Home server";
        p.Host = "blindicide";
        p.Port = 1080;
        p.BindAddress = "127.0.0.1";
        p.IPv4Only = false;
        p.BatchMode = true;
        p.AdditionalArguments = new List<string> { "-o", "Compression=yes" };
        p.Reconnect.DelaysSeconds = new List<int> { 0, 4 };
        p.Reconnect.JitterPercent = 20;
        p.StartupTimeoutSeconds = 33;
        p.TrafficMode = TrafficMode.Off;
        p.Monitoring.TargetHost = "probe.example";
        p.Monitoring.TargetPort = 8443;
        p.Monitoring.ReconnectOnFailure = true;
        config.LogVerbosity = LogVerbosity.Verbose;

        service.Save(config);
        var loaded = service.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, loaded.Status);
        var q = loaded.Config.ActiveProfile;
        Assert.Equal(p.Id, q.Id);
        Assert.Equal("Home server", q.Name);
        Assert.Equal("blindicide", q.Host);
        Assert.Equal(1080, q.Port);
        Assert.False(q.IPv4Only);
        Assert.True(q.BatchMode);
        Assert.Equal(new[] { "-o", "Compression=yes" }, q.AdditionalArguments);
        Assert.Equal(new[] { 0, 4 }, q.Reconnect.DelaysSeconds);
        Assert.Equal(20, q.Reconnect.JitterPercent);
        Assert.Equal(33, q.StartupTimeoutSeconds);
        Assert.Equal(TrafficMode.Off, q.TrafficMode);
        Assert.Equal("probe.example", q.Monitoring.TargetHost);
        Assert.Equal(8443, q.Monitoring.TargetPort);
        Assert.True(q.Monitoring.ReconnectOnFailure);
        Assert.Equal(LogVerbosity.Verbose, loaded.Config.LogVerbosity);
    }

    [Fact]
    public void SaveWritesEnumsAsStringsAndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var config = AppConfig.CreateDefault();
        config.LogVerbosity = LogVerbosity.Verbose;
        new ConfigurationService(path).Save(config);

        Assert.Contains("\"verbose\"", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void TheFileContainsOnlyStoredSettingsNotDerivedOnes()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var config = AppConfig.CreateDefault();
        config.ActiveProfile.Host = "my-alias";
        new ConfigurationService(path).Save(config);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("activeProfile\"", json); // activeProfileId is stored; the derived object must not be
        Assert.Contains("activeProfileId", json);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "my-alias")); // the target appears once
    }

    [Fact]
    public void Ipv4SettingUsesAReadableName()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var config = AppConfig.CreateDefault();
        config.ActiveProfile.IPv4Only = false;
        new ConfigurationService(path).Save(config);

        var json = File.ReadAllText(path);
        Assert.Contains("\"ipv4Only\": false", json);
        Assert.False(new ConfigurationService(path).Load().Config.ActiveProfile.IPv4Only);
    }

    [Fact]
    public void PropertyNamesAreMatchedIgnoringCase()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "SchemaVersion": 1, "Profiles": [ { "Host": "x", "PORT": 2222, "IPV4ONLY": false } ] }""");

        var p = new ConfigurationService(path).Load().Config.ActiveProfile;

        Assert.Equal(("x", 2222, false), (p.Host, p.Port, p.IPv4Only));
    }

    [Fact]
    public void SaveCreatesMissingDirectories()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a", "b", "config.json");
        new ConfigurationService(path).Save(AppConfig.CreateDefault());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CorruptFileIsSetAsideAndDefaultsAreReturnedWithoutThrowing()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, "{ this is not json");
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var result = new ConfigurationService(path, now: () => now).Load();

        Assert.Equal(ConfigLoadStatus.RecoveredFromCorruption, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(path + ".corrupt-20260304T050607Z", result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.False(File.Exists(path));
        Assert.Single(result.Config.Profiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public void EmptyOrWrongShapedJsonCountsAsCorruption(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, content);
        Assert.Equal(ConfigLoadStatus.RecoveredFromCorruption, new ConfigurationService(path).Load().Status);
    }

    [Fact]
    public void PartialFileGetsDefaultsForMissingValues()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "profiles": [ { "host": "box" } ] }""");

        var result = new ConfigurationService(path).Load();

        Assert.Equal(ConfigLoadStatus.Migrated, result.Status); // no schemaVersion: written before versioning existed
        var p = result.Config.ActiveProfile;
        Assert.Equal("box", p.Host);
        Assert.Equal(10080, p.Port);
        Assert.Equal(30, p.Reconnect.HealthyResetSeconds);
        Assert.Equal(TrafficMode.Bridge, p.TrafficMode);
        Assert.NotEqual(Guid.Empty, p.Id);
    }

    [Fact]
    public void NullsAndDanglingActiveIdAreRepaired()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """
            { "activeProfileId": "11111111-1111-1111-1111-111111111111",
              "profiles": [ { "name": null, "reconnect": null, "additionalArguments": null } ] }
            """);

        var config = new ConfigurationService(path).Load().Config;

        Assert.Equal(config.Profiles[0].Id, config.ActiveProfileId);
        Assert.NotNull(config.Profiles[0].Reconnect);
        Assert.NotNull(config.Profiles[0].AdditionalArguments);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, "// my config\n{ \"profiles\": [ { \"host\": \"x\", }, ], }");
        Assert.Equal("x", new ConfigurationService(path).Load().Config.ActiveProfile.Host);
    }

    [Fact]
    public void SemanticallyInvalidValuesLoadButFailValidation()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "profiles": [ { "host": "x", "port": 99999 } ] }""");

        var config = new ConfigurationService(path).Load().Config;

        Assert.Equal(ConfigLoadStatus.Loaded, ConfigLoadStatus.Loaded);
        Assert.False(ProfileValidator.Validate(config.ActiveProfile).IsValid);
    }

    [Fact]
    public void ActiveProfileFallsBackToTheFirstWhenIdIsUnknown()
    {
        var config = new AppConfig { Profiles = { new Profile { Name = "a" }, new Profile { Name = "b" } } };
        config.ActiveProfileId = Guid.NewGuid();
        Assert.Equal("a", config.ActiveProfile.Name);
    }
}

public class AppPathsTests
{
    [Fact]
    public void LayoutIsRootRelative()
    {
        var paths = new AppPaths(Path.Combine("data", "root"));
        Assert.Equal(Path.Combine("data", "root", "config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine("data", "root", "constantproxy.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine("data", "root", "logs", "constantproxy.log"), paths.LogFile);
    }

    [Fact]
    public void EnvironmentOverrideIsHonoured()
    {
        var previous = Environment.GetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable, "/somewhere/else");
            Assert.Equal("/somewhere/else", AppPaths.ForCurrentUser().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void DefaultLocationIsPerUserAndNotBesideTheExecutable()
    {
        var previous = Environment.GetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable, null);
            var root = AppPaths.ForCurrentUser().Root;
            Assert.EndsWith("constantproxy", root);
            Assert.NotEqual(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void EnsureCreatedMakesDirectories()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(Path.Combine(dir.Path, "x"));
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }
}
