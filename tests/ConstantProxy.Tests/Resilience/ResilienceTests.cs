namespace ConstantProxy.Tests.Resilience;

public class ConfigMigrationTests
{
    [Fact]
    public void FileWithoutAVersionIsMigratedBackedUpAndRewritten()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "profiles": [ { "name": "Home", "host": "h", "port": 4000 } ] }""");

        var result = new ConfigurationService(path).Load();

        Assert.Equal(ConfigLoadStatus.Migrated, result.Status);
        Assert.Equal(0, result.FileVersion);
        Assert.Equal(path + ".v0.bak", result.BackupPath);
        Assert.Contains("\"host\": \"h\"", File.ReadAllText(result.BackupPath!)); // the original is untouched
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Equal(4000, result.Config.ActiveProfile.Port);
        Assert.Contains($"\"schemaVersion\": {AppConfig.CurrentSchemaVersion}", File.ReadAllText(path));

        // The rewritten file is current: loading again is a plain load.
        Assert.Equal(ConfigLoadStatus.Loaded, new ConfigurationService(path).Load().Status);
    }

    [Fact]
    public void CurrentVersionFilesAreLeftAlone()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var service = new ConfigurationService(path);
        service.Save(AppConfig.CreateDefault());
        var before = File.GetLastWriteTimeUtc(path);

        var result = service.Load();

        Assert.Equal(ConfigLoadStatus.Loaded, result.Status);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak"));
    }

    [Fact]
    public void FileFromANewerVersionIsKeptAsABackupAndLoadedBestEffort()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var future = AppConfig.CurrentSchemaVersion + 5;
        File.WriteAllText(path, $$"""{ "schemaVersion": {{future}}, "shinyNewSetting": true, "profiles": [ { "host": "h" } ] }""");

        var result = new ConfigurationService(path).Load();

        Assert.Equal(ConfigLoadStatus.LoadedFromNewerVersion, result.Status);
        Assert.Equal(future, result.FileVersion);
        Assert.Equal(path + $".v{future}.bak", result.BackupPath);
        Assert.Contains("shinyNewSetting", File.ReadAllText(result.BackupPath!));
        Assert.Equal("h", result.Config.ActiveProfile.Host);
        Assert.Equal(AppConfig.CurrentSchemaVersion, result.Config.SchemaVersion);
        Assert.Contains("shinyNewSetting", File.ReadAllText(path)); // the original stays until the user saves
    }

    [Theory]
    [InlineData("""{ "schemaVersion": "two" }""", 0)]
    [InlineData("""{ "schemaVersion": -3 }""", 0)]
    [InlineData("""{ "schemaVersion": 1.5 }""", 0)]
    [InlineData("""{ "SchemaVersion": 1 }""", 1)]
    [InlineData("""{ }""", 0)]
    [InlineData("[1]", 0)]
    public void SchemaVersionIsReadDefensively(string json, int expected) =>
        Assert.Equal(expected, ConfigurationService.ReadSchemaVersion(json));

    [Fact]
    public void MigratorAppliesConsecutiveStepsInOrder()
    {
        var order = new List<string>();
        var steps = new[]
        {
            new ConfigMigration(0, 1, "one", _ => order.Add("0->1")),
            new ConfigMigration(1, 2, "two", c => { order.Add("1->2"); c.LogVerbosity = LogVerbosity.Verbose; }),
            new ConfigMigration(2, 3, "three", _ => order.Add("2->3")),
        };
        var config = AppConfig.CreateDefault();

        var outcome = ConfigMigrator.Migrate(config, 1, steps);

        Assert.Equal(new[] { "1->2", "2->3" }, order);
        Assert.Equal(new[] { "two", "three" }, outcome.Applied);
        Assert.Equal((1, 3), (outcome.FromVersion, outcome.ToVersion));
        Assert.Equal(3, config.SchemaVersion);
        Assert.Equal(LogVerbosity.Verbose, config.LogVerbosity);
    }

    [Fact]
    public void AMissingStepIsReportedInsteadOfSilentlySkipped()
    {
        var steps = new[] { new ConfigMigration(0, 1, "one", _ => { }), new ConfigMigration(2, 3, "three", _ => { }) };
        Assert.Throws<InvalidOperationException>(() => ConfigMigrator.Migrate(AppConfig.CreateDefault(), 0, steps));
    }

    [Fact]
    public void ShippedStepsCoverEveryVersionUpToTheCurrentOne()
    {
        var versions = ConfigMigrator.Steps.OrderBy(s => s.From).ToList();
        for (var i = 0; i < versions.Count; i++)
        {
            Assert.Equal(i, versions[i].From);
            Assert.Equal(i + 1, versions[i].To);
        }

        Assert.Equal(AppConfig.CurrentSchemaVersion, versions[^1].To);
    }

    [Fact]
    public void AlreadyCurrentConfigsNeedNoSteps()
    {
        var outcome = ConfigMigrator.Migrate(AppConfig.CreateDefault(), AppConfig.CurrentSchemaVersion);
        Assert.Empty(outcome.Applied);
    }
}

public class CrashReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void WritesAReportWithVersionPlatformAndException()
    {
        using var dir = new TempDir();
        Exception ex;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        var path = CrashReport.Write(dir.Path, ex, "1.2.3", Now);

        Assert.NotNull(path);
        Assert.Equal("crash-20260601T123456789Z.txt", Path.GetFileName(path));
        var text = File.ReadAllText(path!);
        Assert.Contains("Version: 1.2.3", text);
        Assert.Contains("Time (UTC): 2026-06-01 12:34:56", text);
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Contains("OS:", text);
        Assert.Contains(".NET:", text);
    }

    [Fact]
    public void CreatesTheDirectoryAndKeepsOnlyTheTenNewestReports()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "logs", "nested");
        for (var i = 0; i < 14; i++)
        {
            Assert.NotNull(CrashReport.Write(folder, new Exception("x" + i), "1.0.0", Now.AddSeconds(i)));
        }

        var files = Directory.GetFiles(folder, "crash-*.txt").Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(10, files.Count);
        Assert.Equal("crash-20260601T123500789Z.txt", files[0]); // the four oldest are gone
    }

    [Fact]
    public void NeverThrowsWhenTheFolderCannotBeUsed()
    {
        using var dir = new TempDir();
        var blocker = dir.File("blocker");
        File.WriteAllText(blocker, "a file where a directory is needed");
        Assert.Null(CrashReport.Write(Path.Combine(blocker, "sub"), new Exception("x"), "1.0.0", Now));
    }

    [Fact]
    public void ReportContainsNoConfigurationValues()
    {
        var text = CrashReport.Format(new Exception("oops"), "1.0.0", Now, "test");
        Assert.DoesNotContain("blindicide", text);
        Assert.Contains("Source: test", text);
    }
}

public class ChildProcessRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeInspector : IProcessInspector
    {
        public Dictionary<int, ProcessSnapshot> Running { get; } = new();

        public List<int> Killed { get; } = new();

        public ProcessSnapshot? Inspect(int pid) => Running.TryGetValue(pid, out var s) ? s : null;

        public void Kill(int pid)
        {
            Killed.Add(pid);
            Running.Remove(pid);
        }
    }

    private static ChildProcessRegistry Make(TempDir dir, FakeInspector inspector) => new(dir.File("children.json"), inspector);

    [Fact]
    public void RegisteredProcessesSurviveInTheFileAndCanBeUnregistered()
    {
        using var dir = new TempDir();
        var registry = Make(dir, new FakeInspector());
        registry.Register(new RegisteredProcess(100, Start, "ssh"));
        registry.Register(new RegisteredProcess(200, Start, "ssh"));

        Assert.Equal(new[] { 100, 200 }, Make(dir, new FakeInspector()).Load().Select(p => p.Pid)); // a new instance reads the same file
        registry.Unregister(100);
        Assert.Equal(new[] { 200 }, registry.Load().Select(p => p.Pid));
        registry.Unregister(999); // unknown: harmless
    }

    [Fact]
    public void RegisteringAPidAgainReplacesTheOldEntry()
    {
        using var dir = new TempDir();
        var registry = Make(dir, new FakeInspector());
        registry.Register(new RegisteredProcess(100, Start, "ssh"));
        registry.Register(new RegisteredProcess(100, Start.AddHours(1), "ssh"));
        Assert.Equal(Start.AddHours(1), Assert.Single(registry.Load()).StartTimeUtc);
    }

    [Fact]
    public void AProvablyOurLeftoverIsEnded()
    {
        using var dir = new TempDir();
        var inspector = new FakeInspector();
        inspector.Running[100] = new ProcessSnapshot("ssh", Start.AddMilliseconds(800)); // within tolerance
        var registry = Make(dir, inspector);
        registry.Register(new RegisteredProcess(100, Start, "ssh"));

        Assert.Equal(1, registry.CleanupOrphans());
        Assert.Equal(new[] { 100 }, inspector.Killed);
        Assert.Empty(registry.Load());
    }

    [Fact]
    public void ARecycledPidWithADifferentStartTimeIsNeverTouched()
    {
        using var dir = new TempDir();
        var inspector = new FakeInspector();
        inspector.Running[100] = new ProcessSnapshot("ssh", Start.AddHours(5)); // a different ssh that got the same PID
        var registry = Make(dir, inspector);
        registry.Register(new RegisteredProcess(100, Start, "ssh"));

        Assert.Equal(0, registry.CleanupOrphans());
        Assert.Empty(inspector.Killed);
        Assert.Empty(registry.Load()); // the stale record is forgotten
    }

    [Fact]
    public void ADifferentProgramWithTheSameStartTimeIsNeverTouched()
    {
        using var dir = new TempDir();
        var inspector = new FakeInspector();
        inspector.Running[100] = new ProcessSnapshot("notepad", Start);
        var registry = Make(dir, inspector);
        registry.Register(new RegisteredProcess(100, Start, "ssh"));

        Assert.Equal(0, registry.CleanupOrphans());
        Assert.Empty(inspector.Killed);
    }

    [Fact]
    public void AnUnrelatedSshSessionThatWasNeverRegisteredIsNeverTouched()
    {
        using var dir = new TempDir();
        var inspector = new FakeInspector();
        inspector.Running[555] = new ProcessSnapshot("ssh", Start); // the user's own session
        var registry = Make(dir, inspector);
        registry.Register(new RegisteredProcess(100, Start, "ssh")); // ours, already gone

        Assert.Equal(0, registry.CleanupOrphans());
        Assert.Empty(inspector.Killed);
        Assert.True(inspector.Running.ContainsKey(555));
    }

    [Fact]
    public void ProcessNameComparisonIgnoresCase()
    {
        using var dir = new TempDir();
        var inspector = new FakeInspector();
        inspector.Running[7] = new ProcessSnapshot("SSH", Start);
        var registry = Make(dir, inspector);
        registry.Register(new RegisteredProcess(7, Start, "ssh"));
        Assert.Equal(1, registry.CleanupOrphans());
    }

    [Fact]
    public void ACorruptRegistryFileIsTreatedAsEmpty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("children.json"), "{ not json");
        var registry = Make(dir, new FakeInspector());
        Assert.Empty(registry.Load());
        Assert.Equal(0, registry.CleanupOrphans());
        registry.Register(new RegisteredProcess(1, Start, "ssh")); // and it recovers on the next write
        Assert.Single(registry.Load());
    }

    [Fact]
    public async Task TheLauncherRegistersAndUnregistersRealChildren()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var registry = new ChildProcessRegistry(dir.File("children.json"));
        var launcher = new SshProcessLauncher(registry: registry);

        using var process = launcher.Start(new SshLaunchSpec("sleep", new[] { "60" }));
        var entry = Assert.Single(registry.Load());
        Assert.Equal(process.Pid, entry.Pid);
        Assert.Equal("sleep", entry.ProcessName);

        process.Kill();
        await process.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await TestWait.Until(() => registry.Load().Count == 0, what: "unregistered after exit");
    }

    [Fact]
    public void TheRealInspectorSeesItselfAndIgnoresMissingProcesses()
    {
        var inspector = new SystemProcessInspector();
        var me = inspector.Inspect(Environment.ProcessId);
        Assert.NotNull(me);
        Assert.True(me!.StartTimeUtc < DateTimeOffset.UtcNow);
        Assert.Null(inspector.Inspect(int.MaxValue));
        inspector.Kill(int.MaxValue); // must not throw
    }
}

/// <summary>
/// Randomised lifecycle torture test: whatever order the user and ssh act in, there is never more than one ssh process
/// alive for the connection (SPEC §10, §64) and everything is stopped at the end.
/// </summary>
public class LifecycleStressTests
{
    private sealed class StrictLauncher : ISshProcessLauncher
    {
        private readonly FakeClock clock;
        private readonly object gate = new();

        public StrictLauncher(FakeClock clock) => this.clock = clock;

        public List<FakeProcess> All { get; } = new();

        public List<string> Violations { get; } = new();

        public ISshProcess Start(SshLaunchSpec spec)
        {
            lock (gate)
            {
                var alive = All.Count(p => !p.HasExited);
                if (alive > 0)
                {
                    Violations.Add($"started a new ssh while {alive} was still running");
                }

                var process = new FakeProcess(clock.UtcNow);
                All.Add(process);
                return process;
            }
        }

        public int Alive
        {
            get { lock (gate) { return All.Count(p => !p.HasExited); } }
        }

        public FakeProcess? NewestAlive
        {
            get { lock (gate) { return All.LastOrDefault(p => !p.HasExited); } }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RandomOperationOrdersNeverLeaveTwoSshProcessesAlive(int seed)
    {
        var random = new Random(seed);
        var clock = new FakeClock();
        var launcher = new StrictLauncher(clock);
        var verifier = new ScriptedVerifier();
        var manager = new ConnectionManager(launcher, verifier, clock, random: () => 0.5);
        var profile = new Profile { Host = "h" };

        for (var i = 0; i < 250; i++)
        {
            switch (random.Next(7))
            {
                case 0:
                case 1:
                    _ = manager.ConnectAsync(profile);
                    break;
                case 2:
                    _ = manager.DisconnectAsync();
                    break;
                case 3:
                    _ = manager.ReconnectNowAsync();
                    break;
                case 4:
                    launcher.NewestAlive?.Exit(random.Next(0, 256));
                    break;
                case 5:
                    verifier.Outcome = random.Next(3) switch { 0 => StartupOutcome.Ready, 1 => StartupOutcome.TimedOut, _ => StartupOutcome.ProcessExited };
                    break;
                default:
                    await Task.Yield();
                    break;
            }

            if (i % 25 == 0)
            {
                await Task.Delay(1);
            }
        }

        verifier.Outcome = StartupOutcome.Ready;
        await manager.DisconnectAsync();
        // A command that was still queued when we disconnected may legitimately start one last connection; stop that too.
        await manager.DisconnectAsync();
        await TestWait.Until(() => manager.State is ConnectionState.Disconnected or ConnectionState.Failed, what: "settled state");
        await manager.DisconnectAsync();

        Assert.Empty(launcher.Violations);
        Assert.Equal(0, launcher.Alive);
        Assert.Equal(ConnectionState.Disconnected, manager.State);
    }

    [Fact]
    public async Task ManyQuickConnectDisconnectCyclesAreClean()
    {
        var clock = new FakeClock();
        var launcher = new StrictLauncher(clock);
        var manager = new ConnectionManager(launcher, new ScriptedVerifier(), clock);
        var profile = new Profile { Host = "h" };

        for (var i = 0; i < 100; i++)
        {
            await manager.ConnectAsync(profile);
            await manager.DisconnectAsync();
        }

        Assert.Empty(launcher.Violations);
        Assert.Equal(0, launcher.Alive);
        Assert.Equal(ConnectionState.Disconnected, manager.State);
    }

    [Fact]
    public async Task SshDyingRepeatedlyNeverRunsTwoAtOnce()
    {
        var clock = new FakeClock();
        var launcher = new StrictLauncher(clock);
        var manager = new ConnectionManager(launcher, new ScriptedVerifier(), clock, random: () => 0.5);
        await manager.ConnectAsync(new Profile { Host = "h" });

        for (var i = 0; i < 60; i++)
        {
            await TestWait.Until(() => manager.State == ConnectionState.Connected && launcher.NewestAlive is not null, what: "connected again");
            launcher.NewestAlive!.Exit(255);
        }

        await manager.DisconnectAsync();
        Assert.Empty(launcher.Violations);
        Assert.Equal(0, launcher.Alive);
        Assert.True(launcher.All.Count >= 60); // the last restart may or may not have begun before the disconnect
    }
}
