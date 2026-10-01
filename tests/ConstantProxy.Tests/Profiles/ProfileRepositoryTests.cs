namespace ConstantProxy.Tests.Profiles;

public class ProfileRepositoryTests
{
    private static (ProfileRepository Repo, AppConfig Config) Make(params string[] names)
    {
        var config = new AppConfig();
        foreach (var n in names)
        {
            config.Profiles.Add(new Profile { Name = n, Host = n.ToLowerInvariant() });
        }

        config.Normalize();
        return (new ProfileRepository(config), config);
    }

    [Fact]
    public void ARepositoryAlwaysHasAtLeastOneProfile()
    {
        var repo = new ProfileRepository(new AppConfig());
        Assert.Single(repo.Profiles);
        Assert.Equal(repo.Profiles[0], repo.Active);
    }

    [Fact]
    public void AddCreatesADefaultProfileWithTheNextFreePortAndTheMachineSshPath()
    {
        var (repo, config) = Make("Home");
        config.Profiles[0].Port = 10080;
        config.Profiles[0].SshExecutable = @"C:\OpenSSH\ssh.exe";

        var added = repo.Add();

        Assert.Equal(2, repo.Profiles.Count);
        Assert.Equal("Profile 2", added.Name);
        Assert.Equal(10081, added.Port);
        Assert.Equal(string.Empty, added.Host);
        Assert.Equal(@"C:\OpenSSH\ssh.exe", added.SshExecutable);
        Assert.NotEqual(config.Profiles[0].Id, added.Id);
        Assert.Equal(config.Profiles[0].Id, repo.Active.Id); // adding never switches the active profile
    }

    [Fact]
    public void AddSkipsPortsAlreadyUsedByOtherProfiles()
    {
        var (repo, config) = Make("A", "B");
        config.Profiles[0].Port = 10080;
        config.Profiles[1].Port = 10081;
        Assert.Equal(10082, repo.Add().Port);
    }

    [Fact]
    public void AddWithANameValidatesIt()
    {
        var (repo, _) = Make("Home");
        Assert.Equal("Work", repo.Add("  Work  ").Name);
        Assert.Equal("profile.name.duplicate", Assert.Throws<ProfileException>(() => repo.Add("home")).Code);
        Assert.Equal("profile.name.empty", Assert.Throws<ProfileException>(() => repo.Add("  ")).Code);
    }

    [Fact]
    public void DefaultNamesNeverCollide()
    {
        var (repo, _) = Make("Profile 2", "Profile 3");
        Assert.Equal("Profile 4", repo.Add().Name);
    }

    [Fact]
    public void CloneCopiesEverythingExceptIdentityAndGetsAUniqueName()
    {
        var (repo, config) = Make("Home", "Work");
        var home = config.Profiles[0];
        home.Host = "blindicide";
        home.Port = 4000;
        home.IPv4Only = false;
        home.AdditionalArguments = new List<string> { "-o", "Compression=yes" };
        home.Reconnect.DelaysSeconds = new List<int> { 0, 9 };
        home.Monitoring.TargetHost = "probe.example";

        var copy = repo.Clone(home.Id);

        Assert.Equal("Home (copy)", copy.Name);
        Assert.NotEqual(home.Id, copy.Id);
        Assert.Equal((home.Host, home.Port, home.IPv4Only), (copy.Host, copy.Port, copy.IPv4Only));
        Assert.Equal(home.AdditionalArguments, copy.AdditionalArguments);
        Assert.Equal(new[] { 0, 9 }, copy.Reconnect.DelaysSeconds);
        Assert.Equal("probe.example", copy.Monitoring.TargetHost);
        Assert.Equal(new[] { "Home", "Home (copy)", "Work" }, repo.Profiles.Select(p => p.Name)); // inserted right after its source
    }

    [Fact]
    public void ClonedProfilesAreIndependentOfTheOriginal()
    {
        var (repo, config) = Make("Home");
        var copy = repo.Clone(config.Profiles[0].Id);
        copy.AdditionalArguments.Add("-v");
        copy.Reconnect.DelaysSeconds.Add(99);
        copy.Monitoring.TargetPort = 1;

        Assert.Empty(config.Profiles[0].AdditionalArguments);
        Assert.DoesNotContain(99, config.Profiles[0].Reconnect.DelaysSeconds);
        Assert.Equal(443, config.Profiles[0].Monitoring.TargetPort);
    }

    [Fact]
    public void RepeatedClonesGetNumberedNames()
    {
        var (repo, config) = Make("Home");
        var id = config.Profiles[0].Id;
        Assert.Equal("Home (copy)", repo.Clone(id).Name);
        Assert.Equal("Home (copy) (2)", repo.Clone(id).Name);
        Assert.Equal("Home (copy) (3)", repo.Clone(id).Name);
    }

    [Fact]
    public void CloneUsesALocalizedCopyFormat()
    {
        var (repo, config) = Make("Дом");
        Assert.Equal("Дом (копия)", repo.Clone(config.Profiles[0].Id, "{0} (копия)").Name);
    }

    [Fact]
    public void LongNamesStayWithinTheLimitWhenSuffixed()
    {
        var (repo, config) = Make(new string('x', ProfileRepository.MaxNameLength));
        var copy = repo.Clone(config.Profiles[0].Id);
        Assert.True(copy.Name.Length <= ProfileRepository.MaxNameLength);
        Assert.NotEqual(config.Profiles[0].Name, copy.Name);
    }

    [Fact]
    public void RenameChangesTheNameOnly()
    {
        var (repo, config) = Make("Home", "Work");
        var home = config.Profiles[0];
        var id = home.Id;
        repo.Rename(id, "  Home server ");
        Assert.Equal("Home server", home.Name);
        Assert.Equal(id, home.Id);
        Assert.Equal("home", home.Host);
    }

    [Fact]
    public void RenamingToTheSameNameOrADifferentCaseOfItselfIsAllowed()
    {
        var (repo, config) = Make("Home");
        repo.Rename(config.Profiles[0].Id, "Home");
        repo.Rename(config.Profiles[0].Id, "HOME");
        Assert.Equal("HOME", config.Profiles[0].Name);
    }

    [Theory]
    [InlineData("work", "profile.name.duplicate")]
    [InlineData("WORK", "profile.name.duplicate")]
    [InlineData("", "profile.name.empty")]
    [InlineData("   ", "profile.name.empty")]
    public void RenameRejectsBadNames(string name, string code)
    {
        var (repo, config) = Make("Home", "Work");
        Assert.Equal(code, Assert.Throws<ProfileException>(() => repo.Rename(config.Profiles[0].Id, name)).Code);
        Assert.Equal("Home", config.Profiles[0].Name);
    }

    [Fact]
    public void NamesWithControlCharactersOrExcessiveLengthAreRejected()
    {
        var (repo, config) = Make("Home");
        Assert.Equal("profile.name.invalid", Assert.Throws<ProfileException>(() => repo.Rename(config.Profiles[0].Id, "bad\nname")).Code);
        Assert.Equal("profile.name.invalid", Assert.Throws<ProfileException>(() => repo.Rename(config.Profiles[0].Id, new string('x', 65))).Code);
    }

    [Fact]
    public void DeleteRemovesTheProfileAndKeepsTheOthersIntact()
    {
        var (repo, config) = Make("A", "B", "C");
        var b = config.Profiles[1];
        repo.Delete(b.Id);
        Assert.Equal(new[] { "A", "C" }, repo.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void DeletingTheActiveProfileActivatesItsNeighbour()
    {
        var (repo, config) = Make("A", "B", "C");
        config.ActiveProfileId = config.Profiles[1].Id;
        repo.Delete(config.Profiles[1].Id);
        Assert.Equal("C", repo.Active.Name);

        config.ActiveProfileId = config.Profiles[1].Id; // C, the last one
        repo.Delete(config.Profiles[1].Id);
        Assert.Equal("A", repo.Active.Name);
    }

    [Fact]
    public void TheLastProfileCannotBeDeleted()
    {
        var (repo, config) = Make("Only");
        Assert.Equal("profile.last", Assert.Throws<ProfileException>(() => repo.Delete(config.Profiles[0].Id)).Code);
        Assert.Single(repo.Profiles);
    }

    [Fact]
    public void UnknownProfilesAreReported()
    {
        var (repo, _) = Make("A");
        var missing = Guid.NewGuid();
        Assert.Equal("profile.notfound", Assert.Throws<ProfileException>(() => repo.Delete(missing)).Code);
        Assert.Equal("profile.notfound", Assert.Throws<ProfileException>(() => repo.Rename(missing, "x")).Code);
        Assert.Equal("profile.notfound", Assert.Throws<ProfileException>(() => repo.Clone(missing)).Code);
        Assert.Equal("profile.notfound", Assert.Throws<ProfileException>(() => repo.SetActive(missing)).Code);
    }

    [Fact]
    public void SetActiveSwitchesTheSelection()
    {
        var (repo, config) = Make("A", "B");
        repo.SetActive(config.Profiles[1].Id);
        Assert.Equal("B", repo.Active.Name);
        Assert.Equal(config.Profiles[1].Id, config.ActiveProfileId);
    }

    [Fact]
    public void ProfileSettingsAreIndependentPerProfile()
    {
        var (repo, config) = Make("A", "B");
        config.Profiles[0].Port = 1111;
        config.Profiles[1].Port = 2222;
        config.Profiles[1].Reconnect.Enabled = false;
        repo.SetActive(config.Profiles[1].Id);
        Assert.Equal(2222, repo.Active.Port);
        Assert.False(repo.Active.Reconnect.Enabled);
        Assert.True(config.Profiles[0].Reconnect.Enabled);
    }

    [Fact]
    public void UniqueNameIgnoresTheProfileBeingRenamed()
    {
        var (repo, config) = Make("A");
        Assert.Equal("a", repo.UniqueName("a", config.Profiles[0]));
        Assert.Equal("a (2)", repo.UniqueName("a"));
    }

    [Fact]
    public void ValidateNameReportsTheSameCodesTheOperationsThrow()
    {
        var (repo, config) = Make("Home", "Work");
        Assert.True(repo.ValidateName("New", null).IsValid);
        Assert.Contains(repo.ValidateName("work", config.Profiles[0]).Errors, e => e.Code == "profile.name.duplicate");
        Assert.True(repo.ValidateName("Home", config.Profiles[0]).IsValid);
    }
}

public class ProfileConfigurationTests
{
    [Fact]
    public void DuplicateIdsInAFileAreRepairedOnLoad()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var id = Guid.NewGuid();
        File.WriteAllText(path, $$"""{ "profiles": [ { "id": "{{id}}", "name": "A", "host": "a" }, { "id": "{{id}}", "name": "B", "host": "b" } ] }""");

        var config = new ConfigurationService(path).Load().Config;

        Assert.Equal(2, config.Profiles.Select(p => p.Id).Distinct().Count());
        Assert.Equal(new[] { "A", "B" }, config.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void ManyProfilesAndTheActiveSelectionRoundTrip()
    {
        using var dir = new TempDir();
        var service = new ConfigurationService(dir.File("config.json"));
        var config = AppConfig.CreateDefault();
        var repo = new ProfileRepository(config);
        repo.Rename(repo.Active.Id, "Home server");
        repo.Active.Host = "blindicide";
        var second = repo.Add("University VPS");
        second.Host = "uni";
        second.Port = 4040;
        repo.SetActive(second.Id);
        service.Save(config);

        var loaded = service.Load().Config;
        var loadedRepo = new ProfileRepository(loaded);
        Assert.Equal(new[] { "Home server", "University VPS" }, loadedRepo.Profiles.Select(p => p.Name));
        Assert.Equal("University VPS", loadedRepo.Active.Name);
        Assert.Equal(4040, loadedRepo.Active.Port);
        Assert.Equal("blindicide", loadedRepo.Profiles[0].Host);
    }

    [Fact]
    public void DuplicateNamesAreFlaggedByTheListValidator()
    {
        var profiles = new[] { new Profile { Name = "A" }, new Profile { Name = "a" }, new Profile { Name = "B" } };
        Assert.Contains(ProfileValidator.ValidateProfileList(profiles).Errors, e => e.Code == "profile.name.duplicate");
        Assert.True(ProfileValidator.ValidateProfileList(new[] { new Profile { Name = "A" }, new Profile { Name = "B" } }).IsValid);
    }

    [Fact]
    public void TrayOnlyAllowsSwitchingProfilesWhenNoTunnelIsRunning()
    {
        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            var expected = state is ConnectionState.Disconnected or ConnectionState.Failed;
            Assert.Equal(expected, TrayMenuState.For(state).CanSwitchProfile);
        }
    }
}

public class PerProfileAnalyticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProfileNamesSurviveInTheDatabaseForExports()
    {
        using var dir = new TempDir();
        var store = SqliteAnalyticsStore.Open(dir.File("a.db")).Store;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.UpsertProfile(a, "Home", T0);
        store.UpsertProfile(b, "Work", T0);
        store.UpsertProfile(a, "Home server", T0.AddHours(1)); // renamed

        var names = store.GetProfileNames();
        Assert.Equal("Home server", names[a]);
        Assert.Equal("Work", names[b]);
    }

    [Fact]
    public async Task EachProfileGetsItsOwnSessionsAndTotals()
    {
        var clock = new FakeClock();
        var store = new RecordingStore();
        var manager = new ConnectionManager(new FakeLauncher(clock), new ScriptedVerifier(), clock);
        var recorder = new AnalyticsRecorder(store, clock);
        recorder.Attach(manager, null);

        var home = new Profile { Name = "Home", Host = "h1" };
        var work = new Profile { Name = "Work", Host = "h2", Port = 10081 };
        await manager.ConnectAsync(home);
        await TestWait.Until(() => manager.State == ConnectionState.Connected);
        await manager.DisconnectAsync();
        await manager.ConnectAsync(work);
        await TestWait.Until(() => manager.State == ConnectionState.Connected);
        await manager.DisconnectAsync();
        await recorder.FlushAsync();

        Assert.Equal(new[] { home.Id, work.Id }, store.Sessions.Select(s => s.ProfileId));
        Assert.Equal(new[] { "Home", "Work" }, store.Profiles.Select(p => p.Name));
        Assert.All(store.Events.Where(e => e.Type == "Connected"), e => Assert.NotNull(e.ProfileId));
        Assert.Equal(home.Id, store.Events.First(e => e.Type == "ConnectionRequested").ProfileId);
        await recorder.DisposeAsync();
    }
}
