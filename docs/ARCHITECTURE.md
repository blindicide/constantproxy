# Architecture

constantproxy has one job: **start the SSH SOCKS tunnel, keep it alive, tell the user whether it is alive, measure what it
carries, and reconnect when it dies.** The code is organised so each of those responsibilities is separate, small and testable.

```text
┌──────────────────────────────────────────────────────────┐
│  ConstantProxy.App   (net8.0-windows, WPF)               │
│  views, view models, tray, notifications, single instance │
└────────────────────────────┬─────────────────────────────┘
                             │  (UI code never starts processes or opens sockets)
┌────────────────────────────▼─────────────────────────────┐
│  ConnectionManager   — the one authoritative controller   │
│  state machine · reconnect policy · health evaluation     │
└──────┬──────────────┬───────────────┬───────────────┬────┘
       │              │               │               │
┌──────▼──────┐ ┌─────▼──────┐ ┌──────▼───────┐ ┌─────▼────────────┐
│ ssh process │ │ SOCKS      │ │ port probe   │ │ ITrafficMonitor  │
│ launcher    │ │ listener & │ │ (TcpPort-    │ │ (bridge counts   │
│ (ssh.exe)   │ │ health     │ │  Probe)      │ │  proxied bytes)  │
└─────────────┘ │ probe      │ └──────────────┘ └─────┬────────────┘
                └────────────┘                         │ cumulative totals
                                         ┌─────────────▼────────────┐
 ConnectionManager events ─────────────► │ AnalyticsRecorder         │
 + traffic samples                        │  (single worker thread)   │
                                          └─────────────┬────────────┘
                                                        ▼
                                          SQLite (SqliteAnalyticsStore)
```

## Projects

| Project | Target | Contents |
| --- | --- | --- |
| `ConstantProxy.Core` | `net8.0` | Pure domain logic with no I/O of its own: models, state machine, `ConnectionManager`, reconnect policy, SSH argument generation, failure classification, health evaluation, traffic statistics, analytics math, notification filtering, localization, settings validation, formatting. Depends on nothing but the BCL. |
| `ConstantProxy.Infrastructure` | `net8.0` | Everything that touches the machine: process launching, TCP/SOCKS probes, the traffic bridge, SQLite store and migrations, JSON configuration, logging, single-instance guard, job object, crash reports, orphan registry. Only external package: `Microsoft.Data.Sqlite`. |
| `ConstantProxy.App` | `net8.0-windows` | WPF views and view models, notification-area icon, windows service (dialogs), Windows startup registry entry. No business rules. |
| `ConstantProxy.Tests` | `net8.0` | xUnit tests for Core and Infrastructure; no GUI dependency. |

Everything the manager needs from the outside world is an interface in Core — `ISshProcessLauncher`, `IStartupVerifier`,
`IPortProbe`, `ISocksProbe`, `ITrafficMonitor`, `IClock`, `IAppLog`, `IAnalyticsStore` — so the supervision logic is tested
with fakes and a deterministic clock, and real implementations are tested separately against loopback sockets and real
child processes (`sleep`, `ping`, `sh`).

## The service map

| Role | Implementation |
| --- | --- |
| ConnectionManager | `Core/Connection/ConnectionManager` |
| SshProcessSupervisor | `ConnectionManager` (lifecycle) + `Infrastructure/Ssh/SshProcessLauncher`, `LocalSshProcess` |
| ReconnectPolicy | `Core/Connection/ReconnectPolicy` |
| HealthCheckService | `Core/Connection/HealthCheckEvaluator` + `Infrastructure/Network/Socks5Probe` |
| TrafficMonitor | `Core/Traffic/ITrafficMonitor`; `Infrastructure/Traffic/BridgeTrafficMonitor`; `TrafficStatistics`, `TrafficSamplingService` |
| AnalyticsService | `Core/Analytics/AnalyticsRecorder`, `AnalyticsSummaryService`; `Infrastructure/Analytics/SqliteAnalyticsStore` |
| ConfigurationService | `Infrastructure/Config/ConfigurationService`, `Core/Models/ConfigMigrator`, `ProfileRepository` |
| LocalizationService | `Core/Localization/LocalizationService` (embedded JSON string tables) |
| NotificationService | `Core/Desktop/NotificationService`, `NotificationFilter` |
| LoggingService | `Infrastructure/Logging/AppLog` |

## Connection lifecycle

States are explicit and the only place they live is `ConnectionStateMachine`, whose transition table is covered by a test
for all 64 pairs:

```text
Disconnected → Starting → Connecting → Connected ⇄ Degraded
                  ▲            │            │          │
                  │            ▼            ▼          ▼
              Reconnecting ◄───┴────────────┴──────────┘      Failed  (permanent problems)
                                                                │
 Connected/Degraded/Connecting/... → Stopping → Disconnected    └→ Starting (user presses Connect) / Disconnected
```

`ConnectionManager` is a **single authoritative controller**:

- Public commands (`ConnectAsync`, `DisconnectAsync`, `ReconnectNowAsync`) are serialized by one `SemaphoreSlim`, so
  "Connect pressed twice", "Disconnect during Connecting" and "Reconnect during Connecting" cannot interleave.
- Each connection has exactly one supervision task. It owns the ssh process, runs startup verification, monitors process
  exit / health probes / manual reconnect / cancellation, and decides between *retry*, *fail permanently* and *stop*.
- There are no scattered booleans: intentional stop is cancellation of the run's `CancellationToken`, a manual reconnect
  is a one-shot signal, everything else is the state machine.
- A new ssh is only started after the previous one was stopped (polite stop, short grace period, then kill of that PID's
  tree). A randomised stress test asserts that two ssh processes are never alive at once.
- The profile is **cloned** at connect time; editing the active profile while connected affects the next connection only.

### Startup verification and health

`ListenerStartupVerifier` polls the listener (default every 250 ms, never a busy loop) and also watches the process, so an
early exit is reported at once instead of waiting for the timeout. Health checks are a one-shot SOCKS5 `CONNECT` through the
local endpoint (`Socks5Probe`), evaluated by `HealthCheckEvaluator` (consecutive-failure threshold, recovery, optional
reconnect threshold, rolling latency).

### Failure classification

`FailureClassifier` combines the lifecycle stage, exit code and ssh's stderr tail into a category (local configuration,
authentication, host verification, network, remote connection, forwarding, unknown). Text patterns are only one input;
unknown is always allowed and no certainty is invented. Codes (`ssh.auth`, `port.inuse`, …) are stable and double as
localization keys. Authentication, host-key, bind and local-configuration failures are non-retryable; network and
remote failures retry with back-off.

## Traffic accounting

Per-process accounting through ETW needs elevation or is unreliable, and system-wide interface counters would include
unrelated traffic, so the default backend is an **application-managed bridge** (`BridgeTrafficMonitor`): ssh listens on a
loopback port chosen per attempt (`-D 127.0.0.1:<internal>`), constantproxy listens on the user's SOCKS port, and each
connection is relayed verbatim in both directions with byte counters. The relay never inspects or alters the stream, handles
half-close, and bounds how long a connection may linger after the remote side finished. Everything is behind
`ITrafficMonitor`; with `NullTrafficMonitor` (or traffic mode "off") the UI reports *Unavailable*.
`TrafficStatistics` derives rates, session totals, peaks, averages and a bounded history from cumulative totals sampled once
a second by `TrafficSamplingService`.

## Analytics pipeline

`AnalyticsRecorder` subscribes to the manager's state changes, events and health results and to the traffic sampler. Handlers
only enqueue work; a **single background worker** owns all session bookkeeping and database writes. The UI and the supervisor
never wait for the database, and a failing database is logged and contained. Once a minute it flushes one-minute traffic
aggregates, buffered health checks and session progress; at the end of a session it writes the final summary.

`SqliteAnalyticsStore` opens a short-lived connection per operation (no pooling, WAL, `busy_timeout`), stores UTC unix
milliseconds, and is upgraded by a forward-only migration runner tracked with `PRAGMA user_version` (each step in its own
transaction, a backup before upgrading, refusal to open a database from a newer version). `AvailabilityCalculator` and
`TimeWindows` do the uptime and calendar math (local-time day/week/month boundaries converted to UTC, DST-safe).

## Desktop integration

- **Tray**: `TrayMenuState` (pure) decides enabled items and the icon kind; `TrayController` applies it to a WinForms
  `NotifyIcon` (the only use of WinForms). Icons are drawn at runtime; each state has its own shape as well as colour.
- **Single instance**: a named mutex plus a per-user named pipe; a second launch asks the first to show itself and waits for
  an acknowledgement.
- **Notifications**: `NotificationFilter` keeps brief outages silent (default 10 s), sends "restored" only after a "lost",
  and never notifies for intentional disconnects.
- **Shutdown and cleanup**: exit stops the tunnel and flushes analytics. A Windows job object (kill-on-close) ends ssh if
  constantproxy dies; `ChildProcessRegistry` records started processes (PID + start time + name) so a later run can end only
  provable leftovers.
- **Crash handling**: unhandled exceptions are logged and written to `logs\crash-*.txt` (no configuration inside).

## Localization

All user-visible text lives in `Localization/Strings.en.json` and `Strings.ru.json` (embedded resources). Codes from the lower
layers (state names, failure codes, validation codes) map to keys by convention (`state.Connected`, `failure.port.inuse`,
`validation.port.range`); Russian plurals follow CLDR rules. XAML uses `{loc:Loc key}`, which refreshes on language change.
Tests enforce key parity, placeholder parity, access-key parity, that nothing is left untranslated, that every referenced key
exists, and that no XAML contains hardcoded visible text.

## Threading and cancellation

No process wait, network call, database write, health check or retry delay runs on the UI thread. Long-running work uses
`async`/`await` with cancellation tokens, a retry delay is a cancellable `IClock.Delay`, and every loop ends predictably
on shutdown. The UI refreshes at 1 Hz at most; timers only run while a session or the visible Statistics tab needs them.
