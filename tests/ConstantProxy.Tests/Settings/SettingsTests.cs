namespace ConstantProxy.Tests.Settings;

public class SettingsDraftTests
{
    private static (AppConfig Config, Profile Profile) Defaults()
    {
        var config = AppConfig.CreateDefault();
        config.ActiveProfile.Host = "blindicide";
        config.ActiveProfile.Name = "Home";
        return (config, config.ActiveProfile);
    }

    private static SettingsBuildResult Build(SettingsDraft draft, Profile original, ProfileRepository? repo = null) =>
        draft.Build(original, repo, fileExists: _ => true, directoryExists: _ => true);

    [Fact]
    public void RoundTripsAnUntouchedConfigurationWithoutChanges()
    {
        var (config, profile) = Defaults();
        profile.AdditionalArguments = new List<string> { "-o", "Compression=yes", "-i", @"C:\keys\my id" };
        profile.Reconnect.DelaysSeconds = new List<int> { 0, 3, 8 };

        var result = Build(SettingsDraft.From(config, profile), profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal(profile.Id, result.Profile.Id);
        Assert.Equal(profile.AdditionalArguments, result.Profile.AdditionalArguments);
        Assert.Equal(new[] { 0, 3, 8 }, result.Profile.Reconnect.DelaysSeconds);
        Assert.Equal(profile.Port, result.Profile.Port);
        Assert.Equal(config.Notifications.MinimumOutageSeconds, result.Notifications.MinimumOutageSeconds);
        Assert.Equal(config.Analytics.RetentionDays, result.Analytics.RetentionDays);
        Assert.Equal(config.Interface.CloseToTray, result.Interface.CloseToTray);
    }

    [Fact]
    public void EditedValuesLandInTheRightPlaces()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.Host = "  vps  ";
        d.Port = "1080";
        d.BindAddress = "127.0.0.2";
        d.IPv4Only = false;
        d.ServerAliveInterval = "15";
        d.ServerAliveCountMax = "4";
        d.ExitOnForwardFailure = false;
        d.StartupTimeoutSeconds = "30";
        d.AutoReconnect = false;
        d.ReconnectDelays = "0, 2; 5 20";
        d.MaxDelaySeconds = "20";
        d.HealthyResetSeconds = "60";
        d.JitterPercent = "10";
        d.HealthHost = "check.example";
        d.HealthPort = "8443";
        d.HealthInterval = "20";
        d.HealthTimeout = "6";
        d.FailureThreshold = "4";
        d.ReconnectOnFailure = true;
        d.ReconnectAfterFailures = "8";
        d.StoreHistory = false;
        d.RetentionDays = 365;
        d.DatabasePath = " D:\\data\\x.db ";
        d.Language = "RU";
        d.StartMinimized = true;
        d.StartWithWindows = true;
        d.NotifyOnRecovery = false;
        d.MinimumOutageSeconds = "25";
        d.AdditionalArguments = "-v -o \"A=b c\"";
        d.BatchMode = true;
        d.TrafficMode = TrafficMode.Off;
        d.LogVerbosity = LogVerbosity.Verbose;

        var r = Build(d, profile);

        Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => e.Field + ":" + e.Issue.Code)));
        var p = r.Profile;
        Assert.Equal(("vps", 1080, "127.0.0.2", false), (p.Host, p.Port, p.BindAddress, p.IPv4Only));
        Assert.Equal((15, 4, false, 30), (p.ServerAliveInterval, p.ServerAliveCountMax, p.ExitOnForwardFailure, p.StartupTimeoutSeconds));
        Assert.Equal((false, 20, 60, 10), (p.Reconnect.Enabled, p.Reconnect.MaxDelaySeconds, p.Reconnect.HealthyResetSeconds, p.Reconnect.JitterPercent));
        Assert.Equal(new[] { 0, 2, 5, 20 }, p.Reconnect.DelaysSeconds);
        Assert.Equal(("check.example", 8443, 20, 6, 4), (p.Monitoring.TargetHost, p.Monitoring.TargetPort, p.Monitoring.IntervalSeconds, p.Monitoring.TimeoutSeconds, p.Monitoring.FailureThreshold));
        Assert.True(p.Monitoring.ReconnectOnFailure);
        Assert.Equal(8, p.Monitoring.ReconnectAfterFailures);
        Assert.Equal(new[] { "-v", "-o", "A=b c" }, p.AdditionalArguments);
        Assert.True(p.BatchMode);
        Assert.Equal(TrafficMode.Off, p.TrafficMode);
        Assert.Equal((false, 365, @"D:\data\x.db"), (r.Analytics.StoreHistory, r.Analytics.RetentionDays, r.Analytics.DatabasePath));
        Assert.Equal(("ru", true, true), (r.Interface.Language, r.Interface.StartMinimized, r.Interface.StartWithWindows));
        Assert.Equal((true, false, 25), (r.Notifications.NotifyOnFailure, r.Notifications.NotifyOnRecovery, r.Notifications.MinimumOutageSeconds));
        Assert.Equal(LogVerbosity.Verbose, r.LogVerbosity);
    }

    [Theory]
    [InlineData(nameof(SettingsDraft.Port), "abc")]
    [InlineData(nameof(SettingsDraft.Port), "")]
    [InlineData(nameof(SettingsDraft.Port), "12.5")]
    [InlineData(nameof(SettingsDraft.ServerAliveInterval), "x")]
    [InlineData(nameof(SettingsDraft.ServerAliveCountMax), " ")]
    [InlineData(nameof(SettingsDraft.StartupTimeoutSeconds), "1e3")]
    [InlineData(nameof(SettingsDraft.MaxDelaySeconds), "?")]
    [InlineData(nameof(SettingsDraft.HealthyResetSeconds), "-")]
    [InlineData(nameof(SettingsDraft.JitterPercent), "ten")]
    [InlineData(nameof(SettingsDraft.HealthPort), "http")]
    [InlineData(nameof(SettingsDraft.HealthInterval), "1,5")]
    [InlineData(nameof(SettingsDraft.HealthTimeout), "")]
    [InlineData(nameof(SettingsDraft.FailureThreshold), "a")]
    [InlineData(nameof(SettingsDraft.ReconnectAfterFailures), "b")]
    [InlineData(nameof(SettingsDraft.MinimumOutageSeconds), "soon")]
    public void NonNumericInputIsReportedOnItsOwnFieldAndOnlyThere(string property, string text)
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        typeof(SettingsDraft).GetProperty(property)!.SetValue(d, text);

        var r = Build(d, profile);

        Assert.False(r.IsValid);
        var issue = Assert.Single(r.Errors);
        Assert.Equal(property, issue.Field);
        Assert.Equal("number.invalid", issue.Issue.Code);
    }

    [Fact]
    public void RetryDelayListMustBeNumbers()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.ReconnectDelays = "0, one, 5";
        var issue = Assert.Single(Build(d, profile).Errors);
        Assert.Equal((nameof(SettingsDraft.ReconnectDelays), "list.invalid"), (issue.Field, issue.Issue.Code));
    }

    [Theory]
    [InlineData(nameof(SettingsDraft.Port), "0", "port.range")]
    [InlineData(nameof(SettingsDraft.Port), "65536", "port.range")]
    [InlineData(nameof(SettingsDraft.Host), "", "host.empty")]
    [InlineData(nameof(SettingsDraft.Host), "-oBad", "host.invalid")]
    [InlineData(nameof(SettingsDraft.BindAddress), "nope", "bind.invalid")]
    [InlineData(nameof(SettingsDraft.ServerAliveInterval), "0", "timing.positive")]
    [InlineData(nameof(SettingsDraft.StartupTimeoutSeconds), "0", "timing.positive")]
    [InlineData(nameof(SettingsDraft.ReconnectDelays), "", "reconnect.delays")]
    [InlineData(nameof(SettingsDraft.ReconnectDelays), "5, 0", "reconnect.delays.last")]
    [InlineData(nameof(SettingsDraft.MaxDelaySeconds), "0", "timing.positive")]
    [InlineData(nameof(SettingsDraft.JitterPercent), "150", "reconnect.jitter")]
    [InlineData(nameof(SettingsDraft.HealthHost), "", "health.host")]
    [InlineData(nameof(SettingsDraft.HealthPort), "0", "port.range")]
    [InlineData(nameof(SettingsDraft.HealthInterval), "0", "timing.positive")]
    [InlineData(nameof(SettingsDraft.FailureThreshold), "0", "health.threshold")]
    [InlineData(nameof(SettingsDraft.MinimumOutageSeconds), "-1", "notify.minoutage")]
    [InlineData(nameof(SettingsDraft.ProfileName), "  ", "profile.name.empty")]
    public void ValidationFailuresAreAttachedToTheEditedField(string property, string text, string code)
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        typeof(SettingsDraft).GetProperty(property)!.SetValue(d, text);

        var r = Build(d, profile);

        Assert.Contains(r.Errors, e => e.Field == property && e.Issue.Code == code);
    }

    [Fact]
    public void RetentionAndDatabasePathAreValidated()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.RetentionDays = -5;
        d.DatabasePath = Path.Combine("nowhere", "x.db");
        var r = d.Build(profile, null, _ => true, _ => false);
        Assert.Contains(r.Errors, e => e.Field == nameof(SettingsDraft.RetentionDays));
        Assert.Contains(r.Errors, e => e.Field == nameof(SettingsDraft.DatabasePath));
    }

    [Fact]
    public void HealthSettingsAreNotValidatedWhenHealthChecksAreOff()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.HealthEnabled = false;
        d.HealthHost = "";
        d.HealthInterval = "0";
        Assert.True(Build(d, profile).IsValid);
    }

    [Fact]
    public void NonLoopbackBindIsAWarningNotAnError()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.BindAddress = "0.0.0.0";
        var r = Build(d, profile);
        Assert.True(r.IsValid);
        var warning = Assert.Single(r.Warnings);
        Assert.Equal((nameof(SettingsDraft.BindAddress), "bind.nonloopback"), (warning.Field, warning.Issue.Code));
    }

    [Fact]
    public void DuplicateProfileNamesAreRejectedAgainstTheOtherProfiles()
    {
        var (config, profile) = Defaults();
        var repo = new ProfileRepository(config);
        repo.Add("Work");
        var d = SettingsDraft.From(config, profile);
        d.ProfileName = "work";
        Assert.Contains(Build(d, profile, repo).Errors, e => e.Field == nameof(SettingsDraft.ProfileName) && e.Issue.Code == "profile.name.duplicate");

        d.ProfileName = "Home"; // its own name is fine
        Assert.True(Build(d, profile, repo).IsValid);
    }

    [Fact]
    public void ExplicitMissingExecutableIsFlaggedOnTheExecutableField()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.SshExecutable = @"C:\nope\ssh.exe";
        var r = d.Build(profile, null, _ => false, _ => true);
        Assert.Contains(r.Errors, e => e.Field == nameof(SettingsDraft.SshExecutable) && e.Issue.Code == "ssh.notfound");
    }

    [Fact]
    public void UnknownLanguageFallsBackToAuto()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.Language = "xx";
        Assert.Equal("auto", Build(d, profile).Interface.Language);
    }

    [Fact]
    public void BuildNeverMutatesTheOriginalProfile()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.Host = "other";
        d.Port = "1";
        Build(d, profile);
        Assert.Equal("blindicide", profile.Host);
        Assert.Equal(10080, profile.Port);
    }

    [Fact]
    public void EveryDraftPropertyIsInitialisedFromTheConfiguration()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        Assert.Equal("Home", d.ProfileName);
        Assert.Equal("10080", d.Port);
        Assert.Equal("0, 1, 2, 5, 10, 15", d.ReconnectDelays);
        Assert.Equal("example.com", d.HealthHost);
        Assert.Equal(90, d.RetentionDays);
        Assert.Contains(d.RetentionDays, SettingsDraft.RetentionChoices);
        Assert.Equal(new[] { 30, 90, 180, 365, 0 }, SettingsDraft.RetentionChoices);
    }

    [Fact]
    public void ErrorsAreNotDuplicatedWhenSeveralValidatorsReportTheSameThing()
    {
        var (config, profile) = Defaults();
        var d = SettingsDraft.From(config, profile);
        d.ProfileName = "";
        var r = Build(d, profile, new ProfileRepository(config));
        Assert.Single(r.Errors, e => e.Field == nameof(SettingsDraft.ProfileName));
    }
}

public class SshCommandPreviewTests
{
    private static Profile P() => new() { Host = "blindicide", Port = 10080, TrafficMode = TrafficMode.Off };

    [Fact]
    public void MatchesTheSpecificationExampleWhenTheBridgeIsOff()
    {
        Assert.Equal(
            "ssh -4 -N -D 127.0.0.1:10080 -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes blindicide",
            SshCommandPreview.Build(P(), redactTarget: false));
    }

    [Fact]
    public void BridgeModeShowsAPlaceholderInsteadOfAFakePort()
    {
        var p = P();
        p.TrafficMode = TrafficMode.Bridge;
        var text = SshCommandPreview.Build(p, redactTarget: false);
        Assert.Contains("-D 127.0.0.1:<internal-port>", text);
        Assert.DoesNotContain("10080", text);
    }

    [Fact]
    public void TargetCanBeRedacted()
    {
        var text = SshCommandPreview.Build(P(), redactTarget: true);
        Assert.EndsWith(" <ssh-target>", text);
        Assert.DoesNotContain("blindicide", text);
    }

    [Fact]
    public void UsesTheResolvedOrConfiguredExecutable()
    {
        Assert.StartsWith(@"""C:\Program Files\OpenSSH\ssh.exe"" ", SshCommandPreview.Build(P(), false, @"C:\Program Files\OpenSSH\ssh.exe"));
        var p = P();
        p.SshExecutable = "myssh";
        Assert.StartsWith("myssh ", SshCommandPreview.Build(p, false));
    }

    [Fact]
    public void SecretLookingOptionValuesAreRedacted()
    {
        var p = P();
        p.AdditionalArguments = new List<string> { "-o", "ProxyPassword=hunter2", "-o", "Compression=yes", "--token=abc123", "-i", @"C:\keys\id" };
        var text = SshCommandPreview.Build(p, redactTarget: false);

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abc123", text);
        Assert.Contains("ProxyPassword=***", text);
        Assert.Contains("--token=***", text);
        Assert.Contains("Compression=yes", text);
    }

    [Fact]
    public void KeepAliveOptionsAreNotMistakenForSecrets()
    {
        var text = SshCommandPreview.Build(P(), false);
        Assert.Contains("ServerAliveInterval=30", text);
        Assert.Contains("ServerAliveCountMax=3", text);
    }
}

public class DiagnosticsReportTests
{
    private static DiagnosticsInfo Info() => new()
    {
        AppVersion = "1.2.3",
        OsVersion = "Microsoft Windows 10.0.22631",
        RuntimeVersion = ".NET 8.0.1",
        ConfiguredSsh = @"C:\Users\alice\tools\ssh.exe",
        ResolvedSshPath = @"C:\Users\alice\tools\ssh.exe",
        SshVersion = "OpenSSH_for_Windows_9.5p1",
        ProfileName = "Secret server",
        SshTarget = "private.example.org",
        SshPid = 4242,
        State = ConnectionState.Connected,
        SocksEndpoint = "127.0.0.1:10080",
        SessionStartUtc = new DateTimeOffset(2026, 6, 1, 12, 30, 0, TimeSpan.Zero),
        ReconnectCount = 2,
        TrafficMode = "Bridge",
        DatabasePath = @"C:\Users\alice\AppData\Local\constantproxy\constantproxy.db",
        LogPath = @"C:\Users\alice\AppData\Local\constantproxy\logs",
        ConfigPath = @"C:\Users\alice\AppData\Local\constantproxy\config.json",
        Language = "en",
        LastFailure = "ssh.exited",
        SshCommand = "ssh -N -D 127.0.0.1:<internal-port> <ssh-target>",
    };

    [Fact]
    public void ContainsEveryItemRequiredBySection65()
    {
        var text = DiagnosticsReport.Format(Info(), includePrivateDetails: true);
        foreach (var expected in new[]
                 {
                     "Application version: 1.2.3", "OS version: Microsoft Windows 10.0.22631", ".NET version: .NET 8.0.1",
                     "Configured ssh: C:\\Users\\alice\\tools\\ssh.exe", "SSH version: OpenSSH_for_Windows_9.5p1",
                     "Current profile: Secret server", "Current SSH PID: 4242", "Connection state: Connected",
                     "SOCKS endpoint: 127.0.0.1:10080", "Session start (UTC): 2026-06-01 12:30:00", "Reconnect count: 2",
                     "Database: C:\\Users\\alice\\AppData\\Local\\constantproxy\\constantproxy.db", "Log folder:",
                 })
        {
            Assert.Contains(expected, text);
        }
    }

    [Fact]
    public void PrivateDetailsAreHiddenByDefault()
    {
        var text = DiagnosticsReport.Format(Info(), includePrivateDetails: false);
        foreach (var secret in new[] { "private.example.org", "Secret server", "alice" })
        {
            Assert.DoesNotContain(secret, text);
        }

        Assert.Contains("Current profile: (hidden)", text);
        Assert.Contains("Private details", text);
        Assert.Contains("Application version: 1.2.3", text); // technical facts stay
        Assert.Contains("SSH version: OpenSSH_for_Windows_9.5p1", text);
    }

    [Fact]
    public void MissingValuesAreShownAsDashesNotBlank()
    {
        var text = DiagnosticsReport.Format(new DiagnosticsInfo(), includePrivateDetails: true);
        Assert.Contains("Current SSH PID: none", text);
        Assert.Contains("Resolved ssh path: not found", text);
        Assert.Contains("Configured ssh: (automatic detection)", text);
        Assert.Contains("Session start (UTC): -", text);
        Assert.DoesNotContain(": \r\n", text);
    }

    [Fact]
    public void ReportDoesNotDependOnTheUiLanguage()
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Contains("Application version:", DiagnosticsReport.Format(Info(), false));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }
}

public class SshVersionProbeTests
{
    [Theory]
    [InlineData("OpenSSH_9.5p1, OpenSSL 3.0.12\r\n", "OpenSSH_9.5p1, OpenSSL 3.0.12")]
    [InlineData("\n\n  OpenSSH_for_Windows_9.5p1  \nmore\n", "OpenSSH_for_Windows_9.5p1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParsesTheFirstNonEmptyLine(string? text, string? expected) =>
        Assert.Equal(expected, SshVersionProbe.FirstLine(text));

    [Fact]
    public async Task ReadsTheVersionFromStderrOfARealProgram()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var script = dir.File("fake-ssh");
        File.WriteAllText(script, "#!/bin/sh\necho 'OpenSSH_fake_1.0, test' 1>&2\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal("OpenSSH_fake_1.0, test", await SshVersionProbe.QueryAsync(script, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task MissingProgramYieldsNull() =>
        Assert.Null(await SshVersionProbe.QueryAsync(Path.Combine(Path.GetTempPath(), "no-such-ssh-" + Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(2)));

    [Fact]
    public async Task HungProgramIsAbandonedAfterTheTimeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var script = dir.File("slow-ssh");
        File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var started = DateTime.UtcNow;
        Assert.Null(await SshVersionProbe.QueryAsync(script, TimeSpan.FromMilliseconds(400)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
