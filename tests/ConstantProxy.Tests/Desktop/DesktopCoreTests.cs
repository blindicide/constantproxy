namespace ConstantProxy.Tests.Desktop;

public class TrayMenuStateTests
{
    [Theory]
    [InlineData(ConnectionState.Disconnected, true, false, false, TrayIconKind.Disconnected)]
    [InlineData(ConnectionState.Starting, false, true, false, TrayIconKind.Connecting)]
    [InlineData(ConnectionState.Connecting, false, true, true, TrayIconKind.Connecting)]
    [InlineData(ConnectionState.Connected, false, true, true, TrayIconKind.Connected)]
    [InlineData(ConnectionState.Degraded, false, true, true, TrayIconKind.Degraded)]
    [InlineData(ConnectionState.Reconnecting, false, true, true, TrayIconKind.Reconnecting)]
    [InlineData(ConnectionState.Stopping, false, false, false, TrayIconKind.Reconnecting)]
    [InlineData(ConnectionState.Failed, true, true, false, TrayIconKind.Failed)]
    public void MenuEnablementFollowsTheState(ConnectionState state, bool connect, bool disconnect, bool reconnect, TrayIconKind icon)
    {
        var menu = TrayMenuState.For(state);
        Assert.Equal((connect, disconnect, reconnect, icon), (menu.CanConnect, menu.CanDisconnect, menu.CanReconnect, menu.Icon));
    }

    [Fact]
    public void ConnectAndDisconnectAreNeverBothEnabledExceptWhenFailed()
    {
        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            var m = TrayMenuState.For(state);
            if (state != ConnectionState.Failed)
            {
                Assert.False(m.CanConnect && m.CanDisconnect, state.ToString());
            }
        }
    }

    [Fact]
    public void EveryStateMapsToAnIcon() =>
        Assert.All(Enum.GetValues<ConnectionState>(), s => Assert.True(Enum.IsDefined(TrayMenuState.For(s).Icon)));

    [Fact]
    public void TooltipIsTruncatedToTheWindowsLimitWithAnEllipsis()
    {
        Assert.Equal("short", TrayMenuState.FitTooltip("short"));
        var exact = new string('a', TrayMenuState.MaxTooltipLength);
        Assert.Equal(exact, TrayMenuState.FitTooltip(exact));
        var fitted = TrayMenuState.FitTooltip(exact + "b");
        Assert.Equal(TrayMenuState.MaxTooltipLength, fitted.Length);
        Assert.EndsWith("…", fitted);
    }
}

public class NotificationFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static StateChange Change(ConnectionState from, ConnectionState to, double seconds, FailureInfo? failure = null) =>
        new(from, to, T0.AddSeconds(seconds), failure);

    private static (NotificationFilter Filter, NotificationConfig Config) Make(Action<NotificationConfig>? tweak = null)
    {
        var config = new NotificationConfig { MinimumOutageSeconds = 10 };
        tweak?.Invoke(config);
        return (new NotificationFilter(() => config), config);
    }

    [Fact]
    public void ShortOutageProducesNoNotificationAtAll()
    {
        var (filter, _) = Make();
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0)));
        Assert.Equal(T0.AddSeconds(10), filter.NextCheckAt);
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, 4)));
        Assert.Null(filter.NextCheckAt);
        Assert.Null(filter.Evaluate(T0.AddSeconds(60)));
    }

    [Fact]
    public void LongOutageNotifiesOnceAfterTheThresholdAndRecoveryReportsDowntime()
    {
        var (filter, _) = Make();
        filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0));

        Assert.Null(filter.Evaluate(T0.AddSeconds(9)));
        var lost = filter.Evaluate(T0.AddSeconds(10));
        Assert.Equal(NotificationKind.ConnectionLost, lost!.Kind);
        Assert.Null(filter.Evaluate(T0.AddSeconds(20))); // only once
        Assert.Null(filter.NextCheckAt);

        var restored = Assert.Single(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, 18)));
        Assert.Equal(NotificationKind.ConnectionRestored, restored.Kind);
        Assert.Equal(TimeSpan.FromSeconds(18), restored.Downtime);
    }

    [Fact]
    public void ReconnectCyclesInsideOneOutageDoNotResetTheClock()
    {
        var (filter, _) = Make();
        filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0));
        filter.OnStateChanged(Change(ConnectionState.Reconnecting, ConnectionState.Starting, 1));
        filter.OnStateChanged(Change(ConnectionState.Starting, ConnectionState.Connecting, 1));
        filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Reconnecting, 5));
        Assert.Equal(T0.AddSeconds(10), filter.NextCheckAt);
    }

    [Fact]
    public void ZeroThresholdNotifiesImmediately()
    {
        var (filter, _) = Make(c => c.MinimumOutageSeconds = 0);
        var immediate = Assert.Single(filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0)));
        Assert.Equal(NotificationKind.ConnectionLost, immediate.Kind);
        Assert.Null(filter.NextCheckAt);
    }

    [Fact]
    public void RecoveryIsSilentWhenTheLossWasNeverAnnounced()
    {
        var (filter, _) = Make();
        filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0));
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, 9)));
    }

    [Fact]
    public void RecoveryNotificationCanBeDisabled()
    {
        var (filter, _) = Make(c => c.NotifyOnRecovery = false);
        filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0));
        filter.Evaluate(T0.AddSeconds(10));
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, 30)));
    }

    [Fact]
    public void FailureNotificationsCanBeDisabledEntirely()
    {
        var (filter, _) = Make(c => c.NotifyOnFailure = false);
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0)));
        Assert.Null(filter.NextCheckAt);
        Assert.Null(filter.Evaluate(T0.AddSeconds(100)));
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Reconnecting, ConnectionState.Failed, 20)));
    }

    [Fact]
    public void FailedNotifiesImmediatelyWithTheFailureAndEndsTheOutage()
    {
        var (filter, _) = Make();
        var failure = new FailureInfo(FailureCategory.Authentication, "ssh.auth", "denied", false);
        var n = Assert.Single(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Failed, 3, failure)));
        Assert.Equal(NotificationKind.ConnectionFailed, n.Kind);
        Assert.Same(failure, n.Failure);
        Assert.False(filter.InOutage);

        // The user reconnects later: that is not a "restored" event.
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, 60)));
    }

    [Fact]
    public void IntentionalDisconnectNeverNotifiesAndClearsTheOutage()
    {
        var (filter, _) = Make();
        filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0));
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Reconnecting, ConnectionState.Stopping, 2)));
        Assert.False(filter.InOutage);
        Assert.Null(filter.Evaluate(T0.AddSeconds(100)));
    }

    [Fact]
    public void DegradedAloneIsNotAnOutage()
    {
        var (filter, _) = Make();
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Degraded, 0)));
        Assert.False(filter.InOutage);
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Degraded, ConnectionState.Connected, 5)));
    }

    [Fact]
    public void ReconnectFromDegradedStartsAnOutage()
    {
        var (filter, _) = Make();
        filter.OnStateChanged(Change(ConnectionState.Degraded, ConnectionState.Reconnecting, 0));
        Assert.True(filter.InOutage);
    }

    [Fact]
    public void FirstConnectionRetriesBeforeEverConnectingAreNotOutages()
    {
        var (filter, _) = Make();
        Assert.Empty(filter.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Reconnecting, 0)));
        Assert.False(filter.InOutage);
    }

    [Fact]
    public void SettingsAreReadLiveSoChangesApplyImmediately()
    {
        var (filter, config) = Make();
        filter.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, 0));
        config.MinimumOutageSeconds = 30;
        Assert.Equal(T0.AddSeconds(30), filter.NextCheckAt);
    }
}

public class NotificationServiceTests
{
    private static StateChange Change(ConnectionState from, ConnectionState to, DateTimeOffset at) => new(from, to, at, null);

    [Fact]
    public async Task LongOutageNotifiesAfterTheDelayThenRecoveryFollows()
    {
        var clock = new FakeClock { BlockDelays = true };
        var config = new NotificationConfig { MinimumOutageSeconds = 10 };
        var sent = new List<AppNotification>();
        using var service = new NotificationService(() => config, clock, n => { lock (sent) { sent.Add(n); } });

        service.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, clock.UtcNow));
        await TestWait.Until(() => clock.RecordedDelays.Count == 1);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.RecordedDelays[0]);
        Assert.Empty(sent);

        clock.Advance(TimeSpan.FromSeconds(10));
        clock.ReleaseDelays();
        await TestWait.Until(() => { lock (sent) { return sent.Count == 1; } });
        Assert.Equal(NotificationKind.ConnectionLost, sent[0].Kind);

        clock.Advance(TimeSpan.FromSeconds(8));
        service.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, clock.UtcNow));
        Assert.Equal(2, sent.Count);
        Assert.Equal(TimeSpan.FromSeconds(18), sent[1].Downtime);
    }

    [Fact]
    public async Task RecoveryBeforeTheThresholdCancelsTheWaitAndStaysSilent()
    {
        var clock = new FakeClock { BlockDelays = true };
        var config = new NotificationConfig { MinimumOutageSeconds = 10 };
        var sent = new List<AppNotification>();
        using var service = new NotificationService(() => config, clock, sent.Add);

        service.OnStateChanged(Change(ConnectionState.Connected, ConnectionState.Reconnecting, clock.UtcNow));
        await TestWait.Until(() => clock.RecordedDelays.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(3));
        service.OnStateChanged(Change(ConnectionState.Connecting, ConnectionState.Connected, clock.UtcNow));
        clock.Advance(TimeSpan.FromSeconds(30));
        clock.ReleaseDelays();
        await Task.Delay(50);

        Assert.Empty(sent);
    }

    [Fact]
    public void FailedStateNotifiesWithoutWaiting()
    {
        var clock = new FakeClock();
        var sent = new List<AppNotification>();
        using var service = new NotificationService(() => new NotificationConfig(), clock, sent.Add);
        service.OnStateChanged(new StateChange(ConnectionState.Connecting, ConnectionState.Failed, clock.UtcNow, new FailureInfo(FailureCategory.Authentication, "ssh.auth", "m", false)));
        Assert.Equal(NotificationKind.ConnectionFailed, Assert.Single(sent).Kind);
    }

    [Fact]
    public async Task AttachedToARealManagerAnOutageIsReported()
    {
        var clock = new FakeClock { BlockWhen = d => d == TimeSpan.FromSeconds(10) };
        var launcher = new FakeLauncher(clock);
        var manager = new ConnectionManager(launcher, new ScriptedVerifier(), clock);
        var sent = new List<AppNotification>();
        var config = new NotificationConfig { MinimumOutageSeconds = 10 };
        using var service = new NotificationService(() => config, clock, n => { lock (sent) { sent.Add(n); } });
        service.Attach(manager);

        await manager.ConnectAsync(new Profile { Host = "h" });
        await TestWait.Until(() => manager.State == ConnectionState.Connected);
        clock.Advance(TimeSpan.FromSeconds(100));
        launcher.Last!.Exit(255);
        await TestWait.Until(() => launcher.Processes.Count == 2 && manager.State == ConnectionState.Connected);
        // reconnected instantly (0 s delay): brief blip, so nothing was announced
        Assert.Empty(sent);
        await manager.DisposeAsync();
    }
}

public class StartupManagerTests
{
    private sealed class FakeRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetValue(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public void SetValue(string name, string value) => Values[name] = value;

        public void DeleteValue(string name) => Values.Remove(name);
    }

    private const string Exe = @"C:\Program Files\constantproxy\constantproxy.exe";

    [Fact]
    public void EnableWritesAQuotedCommandWithTheStartupFlag()
    {
        var registry = new FakeRegistry();
        var manager = new StartupManager(registry, Exe);
        manager.Apply(true);

        Assert.Equal("\"C:\\Program Files\\constantproxy\\constantproxy.exe\" --startup", registry.Values["constantproxy"]);
        Assert.True(manager.IsEnabled);
        Assert.False(manager.NeedsRepair);
    }

    [Fact]
    public void DisableRemovesOnlyItsOwnValue()
    {
        var registry = new FakeRegistry();
        registry.Values["SomethingElse"] = "keep";
        var manager = new StartupManager(registry, Exe);
        manager.Apply(true);
        manager.Apply(false);

        Assert.False(manager.IsEnabled);
        Assert.Equal("keep", registry.Values["SomethingElse"]);
        manager.Apply(false); // idempotent
    }

    [Fact]
    public void ApplyingTheSameStateTwiceDoesNotRewrite()
    {
        var registry = new CountingRegistry();
        var manager = new StartupManager(registry, Exe);
        manager.Apply(true);
        manager.Apply(true);
        Assert.Equal(1, registry.Writes);
    }

    [Fact]
    public void AMovedExecutableIsDetectedAndRepaired()
    {
        var registry = new FakeRegistry();
        new StartupManager(registry, @"C:\Old\constantproxy.exe").Apply(true);
        var current = new StartupManager(registry, @"D:\New\constantproxy.exe");

        Assert.True(current.NeedsRepair);
        Assert.True(current.RepairIfNeeded());
        Assert.Contains(@"D:\New\constantproxy.exe", registry.Values["constantproxy"]);
        Assert.False(current.RepairIfNeeded());
    }

    [Fact]
    public void RepairNeverCreatesAnEntryTheUserDidNotAskFor()
    {
        var registry = new FakeRegistry();
        var manager = new StartupManager(registry, Exe);
        Assert.False(manager.RepairIfNeeded());
        Assert.Empty(registry.Values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\bad\"path\\x.exe")]
    public void UnusableExecutablePathsAreRefused(string path) =>
        Assert.Throws<InvalidOperationException>(() => new StartupManager(new FakeRegistry(), path).Apply(true));

    private sealed class CountingRegistry : IStartupRegistry
    {
        private string? value;

        public int Writes { get; private set; }

        public string? GetValue(string name) => value;

        public void SetValue(string name, string v)
        {
            value = v;
            Writes++;
        }

        public void DeleteValue(string name) => value = null;
    }
}

public class DesktopConfigTests
{
    [Fact]
    public void DefaultsKeepTheTrayAndNotificationsReasonable()
    {
        var c = new AppConfig();
        Assert.False(c.Interface.StartMinimized);
        Assert.True(c.Interface.CloseToTray);
        Assert.False(c.Interface.StartWithWindows);
        Assert.False(c.Interface.ConnectOnLaunch);
        Assert.True(c.Notifications.NotifyOnFailure);
        Assert.True(c.Notifications.NotifyOnRecovery);
        Assert.Equal(10, c.Notifications.MinimumOutageSeconds);
    }

    [Fact]
    public void StartWithWindowsAndConnectOnLaunchAreIndependent()
    {
        var c = new InterfaceConfig { StartWithWindows = true };
        Assert.False(c.ConnectOnLaunch);
        c = new InterfaceConfig { ConnectOnLaunch = true };
        Assert.False(c.StartWithWindows);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(86400, true)]
    [InlineData(-1, false)]
    [InlineData(86401, false)]
    public void MinimumOutageIsValidated(int seconds, bool ok) =>
        Assert.Equal(ok, ProfileValidator.ValidateNotifications(new NotificationConfig { MinimumOutageSeconds = seconds }).IsValid);

    [Fact]
    public void SettingsRoundTripAndNullSectionsAreRepaired()
    {
        using var dir = new TempDir();
        var service = new ConfigurationService(dir.File("config.json"));
        var config = AppConfig.CreateDefault();
        config.Interface = new InterfaceConfig { StartMinimized = true, CloseToTray = false, StartWithWindows = true, ConnectOnLaunch = true };
        config.Notifications = new NotificationConfig { NotifyOnFailure = false, NotifyOnRecovery = false, MinimumOutageSeconds = 45 };
        service.Save(config);

        var loaded = service.Load().Config;
        Assert.True(loaded.Interface.StartMinimized);
        Assert.False(loaded.Interface.CloseToTray);
        Assert.True(loaded.Interface.StartWithWindows);
        Assert.True(loaded.Interface.ConnectOnLaunch);
        Assert.False(loaded.Notifications.NotifyOnFailure);
        Assert.Equal(45, loaded.Notifications.MinimumOutageSeconds);

        File.WriteAllText(dir.File("c2.json"), """{ "interface": null, "notifications": null, "profiles": [ { "host": "x" } ] }""");
        var repaired = new ConfigurationService(dir.File("c2.json")).Load().Config;
        Assert.NotNull(repaired.Interface);
        Assert.NotNull(repaired.Notifications);
    }
}
