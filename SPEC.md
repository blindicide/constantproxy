# constantproxy

## 1. Project overview

`constantproxy` is a lightweight Windows desktop application for creating, supervising, monitoring, and automatically restoring an SSH SOCKS proxy connection.

The primary use case is a user who normally starts an OpenSSH dynamic forward manually with a command similar to:

```text
ssh -4 -N -D 127.0.0.1:10080 -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes blindicide
```

`constantproxy` shall replace the need to manually run and supervise this command.

The application is **not** an SSH implementation, VPN client, packet-filtering system, or general-purpose proxy suite. Windows OpenSSH remains responsible for SSH transport, authentication, configuration, encryption, forwarding, host verification, keys, and SSH-specific behavior.

`constantproxy` is responsible for:

- launching `ssh.exe`;
- selecting the local SOCKS port;
- monitoring the child process;
- detecting tunnel failure;
- automatically reconnecting;
- collecting traffic statistics;
- collecting connection statistics;
- storing historical analytics;
- presenting connection state through a compact Windows GUI;
- providing logs;
- optionally running in the system tray;
- optionally starting with Windows.

The repository will be public.

The project name is:

```text
constantproxy
```

The executable should normally be:

```text
constantproxy.exe
```

---

# 2. Core design principles

The project must remain small, understandable, auditable, and predictable.

The following principles are mandatory.

## 2.1 OpenSSH does the SSH work

Do not implement SSH protocol logic.

Do not bundle a custom SSH implementation unless explicitly approved in a future version.

Use the OpenSSH client installed on Windows.

Default executable:

```text
ssh.exe
```

The user must be able to override its path.

---

## 2.2 No hardcoded deployment values

The application must contain **no environment-specific hardcoded values**.

In particular, the following must not be hardcoded:

- `blindicide`;
- port `10080`;
- SSH executable location;
- SSH username;
- server hostname;
- SSH alias;
- SSH options;
- health-check destination;
- retry timing;
- language;
- database path;
- logging level;
- graph retention period;
- notification behavior.

Defaults may exist, but all operational values must be configurable.

The repository must therefore work for users other than the original developer.

---

## 2.3 No SSH credentials stored by constantproxy

`constantproxy` must not manage or store SSH passwords.

It must not copy private keys into its own data directory.

It must not introduce its own credential format.

Authentication should continue to work through standard OpenSSH facilities such as:

```text
%USERPROFILE%\.ssh\config
%USERPROFILE%\.ssh\id_ed25519
ssh-agent
```

For example:

```text
Host blindicide
    HostName example.org
    User example
    IdentityFile ~/.ssh/id_ed25519
```

Then `constantproxy` may simply launch:

```text
ssh ... blindicide
```

This keeps authentication outside the application's responsibility.

---

# 3. Target platform

Primary supported OS:

```text
Windows 10 64-bit
Windows 11 64-bit
```

Primary architecture:

```text
x86-64
```

ARM64 support may be considered later but is not required for `v1.0`.

The initial application should be implemented using:

```text
C#
.NET
WPF
```

Use a currently supported .NET release appropriate at development time.

Avoid Electron.

Avoid embedding a browser merely to create the application UI.

The normal installed/running application should have modest memory and CPU usage.

When the tunnel is idle, `constantproxy` itself should consume effectively negligible CPU.

---

# 4. Repository requirements

The project will be developed in a **public GitHub repository**.

Suggested repository layout:

```text
constantproxy/
├── .github/
│   └── workflows/
│       ├── build.yml
│       └── release.yml
│
├── src/
│   ├── ConstantProxy.App/
│   ├── ConstantProxy.Core/
│   └── ConstantProxy.Infrastructure/
│
├── tests/
│   └── ConstantProxy.Tests/
│
├── assets/
│   └── ...
│
├── docs/
│   ├── ARCHITECTURE.md
│   ├── CONFIGURATION.md
│   ├── DEVELOPMENT.md
│   └── RELEASES.md
│
├── LICENSE
├── README.md
└── CHANGELOG.md
```

Exact internal structure may differ if there is a good architectural reason.

The repository must not contain:

- private SSH keys;
- real passwords;
- authentication tokens;
- personal configuration;
- private server addresses unless intentionally supplied as harmless examples;
- generated runtime databases;
- user logs.

A proper `.gitignore` is mandatory.

---

# 5. Basic connection model

The user configures an SSH target.

The simplest target is an OpenSSH host alias:

```text
blindicide
```

The application launches approximately:

```text
ssh.exe \
    -4 \
    -N \
    -D 127.0.0.1:<PORT> \
    -o ServerAliveInterval=<VALUE> \
    -o ServerAliveCountMax=<VALUE> \
    -o ExitOnForwardFailure=yes \
    <HOST>
```

Example:

```text
ssh -4 -N -D 127.0.0.1:10080 -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes blindicide
```

The exact argument list must be generated safely by the application.

Do not construct a shell command and pass it through `cmd.exe`.

Launch `ssh.exe` directly with a proper process argument list.

This prevents quoting bugs and removes an unnecessary command interpreter.

---

# 6. Configurable SSH parameters

At minimum expose:

```text
SSH target / alias
Local SOCKS address
Local SOCKS port
SSH executable
IPv4 / automatic mode
ServerAliveInterval
ServerAliveCountMax
ExitOnForwardFailure
Additional SSH arguments
```

Defaults may be:

```text
Bind address:
127.0.0.1

Port:
10080

ServerAliveInterval:
30

ServerAliveCountMax:
3

ExitOnForwardFailure:
true
```

But these are defaults only.

The SSH target must not default permanently to `blindicide` in application code.

The user can enter it through configuration.

---

# 7. Profiles

By `v1.0`, support multiple connection profiles.

Example:

```text
Home server
University VPS
Test tunnel
Work proxy
```

Each profile contains independent settings.

Example data:

```text
Name: Home server
Host: blindicide
BindAddress: 127.0.0.1
Port: 10080
IPv4Only: true
ServerAliveInterval: 30
ServerAliveCountMax: 3
ExitOnForwardFailure: true
```

Only one profile needs to be active at a time in the initial implementation.

Running multiple simultaneous tunnels can be a future feature.

---

# 8. Connection state machine

The application must have explicit connection states rather than a single boolean.

Minimum state set:

```text
Disconnected
Starting
Connecting
Connected
Degraded
Reconnecting
Stopping
Failed
```

The UI must visually represent these states.

Possible transitions:

```text
Disconnected
    ↓
Starting
    ↓
Connecting
    ↓
Connected
    ↓
Degraded
    ↓
Reconnecting
    ↓
Connecting
```

Manual stop:

```text
Connected
    ↓
Stopping
    ↓
Disconnected
```

Permanent/configuration failure:

```text
Connecting
    ↓
Failed
```

---

# 9. SSH process supervision

The application must start SSH as a child process.

Store at minimum:

```text
Process ID
Start time
Exit time
Exit code
Profile
Reconnect attempt
```

The application must detect when the SSH process terminates.

If termination was unexpected and automatic reconnect is enabled, begin reconnection automatically.

Do not depend solely upon parsing terminal text to determine whether the process exists.

Use actual process state.

---

# 10. Automatic reconnect

Automatic reconnection is one of the primary purposes of the application.

If `ssh.exe` unexpectedly terminates:

```text
Connected
→ Reconnecting
→ Starting
→ Connecting
```

Initial reconnect may be immediate.

Repeated failures must use backoff.

Suggested default sequence:

```text
0 seconds
1 second
2 seconds
5 seconds
10 seconds
15 seconds
15 seconds
15 seconds
...
```

Values must be configurable.

The maximum delay must be configurable.

The backoff must reset once the connection has remained healthy for a configurable period.

Suggested default:

```text
30 seconds
```

The reconnect subsystem must prevent rapid uncontrolled process spawning.

There must never be several accidental `ssh.exe` instances created for the same profile by the reconnect loop.

Use synchronization/locking around connection lifecycle operations.

---

# 11. Manual reconnection

Provide:

```text
Reconnect now
```

When selected:

1. stop the current SSH process cleanly;
2. wait briefly for termination;
3. force terminate only if necessary;
4. start a fresh SSH process;
5. reset the connection state appropriately.

Manual reconnect must not increase the failure backoff counter.

---

# 12. Port conflict detection

Before starting SSH, verify whether the configured SOCKS endpoint can be used.

For example:

```text
127.0.0.1:10080
```

If the port is already occupied, show a meaningful error.

Example:

```text
Port 10080 is already in use.
```

Russian:

```text
Порт 10080 уже используется.
```

Do not blindly enter a reconnect loop when the failure is a persistent local configuration problem.

---

# 13. SSH startup verification

The application must not declare the tunnel connected merely because `ssh.exe` exists.

At minimum, wait until the configured SOCKS listener becomes available.

Check:

```text
127.0.0.1:<configured port>
```

Once the listener is confirmed, the state can become:

```text
Connected
```

A configurable startup timeout must exist.

Example default:

```text
15 seconds
```

If the SSH process exits during this period, record the exit normally.

---

# 14. SOCKS health checking

Process existence alone does not guarantee a usable proxy.

Implement an optional health-check subsystem.

It should periodically attempt an outbound connection through the SOCKS proxy.

The health-check destination must be configurable.

Do not hardcode a private project-controlled server.

A health check must have:

```text
enabled
interval
timeout
destination host
destination port
failure threshold
```

Example:

```text
Interval: 10 s
Timeout: 5 s
Failure threshold: 3
```

A single failed health check should generally not destroy the tunnel immediately.

After the configured threshold is reached:

```text
Connected
→ Degraded
```

Depending on user configuration, persistent failure can trigger:

```text
Degraded
→ Reconnecting
```

---

# 15. Traffic monitoring

`constantproxy` must provide upload/download telemetry for the SSH tunnel.

Required metrics:

```text
Current download rate
Current upload rate
Session downloaded bytes
Session uploaded bytes
Peak download rate
Peak upload rate
Average download rate
Average upload rate
```

Display rates using sensible units:

```text
B/s
KB/s
MB/s
GB/s
```

Display totals using:

```text
KB
MB
GB
TB
```

Use consistent binary or decimal units throughout the program.

Document whichever convention is selected.

---

# 16. Traffic measurement architecture

Traffic monitoring must be implemented as a replaceable backend/interface rather than tightly coupled to the UI.

Conceptually:

```text
ITrafficMonitor
    Start(...)
    Stop(...)
    CurrentUpload
    CurrentDownload
    TotalUpload
    TotalDownload
```

The preferred implementation should attribute network traffic to the supervised SSH connection/process.

Do not use total system network-interface traffic, because unrelated browser/download/game traffic would corrupt proxy statistics.

If accurate per-process network accounting proves unreliable on supported Windows versions without elevated privileges, implement a second supported telemetry approach rather than silently showing incorrect values.

Possible implementations include:

```text
Windows network event telemetry
```

or, if necessary:

```text
an application-managed local forwarding layer used solely for accurate byte accounting
```

The telemetry implementation must be isolated behind an abstraction so that it can be replaced without rewriting the application.

Incorrect analytics are worse than absent analytics.

If telemetry cannot be collected, display:

```text
Unavailable
```

rather than fabricated values.

Windows exposes TCP/IP tracing through ETW and supports associating network events with processes, so ETW is a reasonable backend to investigate for this purpose.

---

# 17. Sampling

Traffic data should normally be sampled at:

```text
1 second
```

The sampling interval may later become configurable.

Maintain enough data in memory to render short-term graphs efficiently.

Do not write one database transaction every second forever if that causes unnecessary disk activity.

Aggregate long-term historical points.

For example:

```text
1-second samples:
recent session only

1-minute aggregates:
historical storage
```

---

# 18. Analytics

The application must provide useful historical statistics.

Minimum session statistics:

```text
Session start
Session end
Duration
Connected duration
Disconnected duration
Downloaded bytes
Uploaded bytes
Average download
Average upload
Peak download
Peak upload
Reconnect count
Health-check failures
```

Additional aggregate statistics:

```text
Total traffic today
Total traffic this week
Total traffic this month
Total runtime
Total connected time
Overall reconnect count
Longest continuous connection
Number of connection failures
```

---

# 19. Availability analytics

Calculate:

```text
uptime percentage
```

for relevant periods.

Example:

```text
Session uptime: 99.82%
Today: 98.91%
7 days: 99.31%
```

Do not count time during which the user intentionally disconnected the proxy as an outage.

Distinguish:

```text
User disconnected
Connection failure
Reconnecting
Application stopped
```

---

# 20. Historical charts

Provide graphs for:

```text
Download rate
Upload rate
Connection availability
Latency / health-check response time
```

Useful ranges:

```text
1 minute
5 minutes
1 hour
Session
24 hours
7 days
30 days
```

Not every period needs to exist in the earliest versions.

Graphs should remain lightweight.

Avoid adding a huge browser/charting framework solely for drawing simple timeseries.

---

# 21. Local database

Use SQLite for persistent analytics.

The database may contain:

```text
Profiles
Sessions
Traffic aggregates
Connection events
Health checks
Application metadata
```

Example logical tables:

```text
profiles
sessions
traffic_samples
connection_events
health_checks
settings
```

Migration support is required.

The application must be able to upgrade an older database when schemas change.

Do not require the user to delete application data after each update.

---

# 22. Application configuration

Separate:

```text
configuration
```

from:

```text
analytics database
```

Configuration may use JSON or another simple local format.

Example concept:

```json
{
  "language": "en",
  "startMinimized": false,
  "profiles": []
}
```

Configuration must be validated before use.

Invalid configuration must not crash the program.

---

# 23. Data location

Application data should use an appropriate per-user Windows directory.

Do not write runtime data beside the executable by default.

Portable mode may be added later.

The data directory may contain:

```text
config
database
logs
```

A UI action should exist:

```text
Open data folder
```

---

# 24. Logging

Implement structured application logging.

Log events such as:

```text
Application started
SSH process started
SSH PID assigned
SOCKS port available
Connection established
Health check failed
Connection degraded
SSH process exited
Reconnect scheduled
Reconnect started
Reconnect succeeded
User disconnected
Application shutting down
```

Log levels:

```text
Debug
Information
Warning
Error
```

User-selectable normal levels may simply be:

```text
Normal
Verbose
```

---

# 25. SSH output capture

Capture:

```text
stdout
stderr
```

from `ssh.exe` when useful.

OpenSSH commonly writes diagnostic messages to stderr, so both streams must be handled correctly.

Do not allow pipe buffers to fill and deadlock the SSH child process.

SSH output should be accessible from:

```text
Logs → SSH
```

Potentially sensitive values should not be unnecessarily written to logs.

---

# 26. Main window

The main window should be compact.

Conceptual layout:

```text
┌──────────────────────────────────────────────┐
│ constantproxy                               │
│                                              │
│ Profile:  [ Home server               ▼ ]   │
│                                              │
│ ● Connected                         02:14:37 │
│                                              │
│ SOCKS   127.0.0.1:10080                     │
│                                              │
│ ↓ 1.82 MB/s          ↑ 214 KB/s             │
│ ↓ 3.74 GB            ↑ 618 MB               │
│                                              │
│ Latency 24 ms        Reconnects 1            │
│                                              │
│     traffic graph                            │
│                                              │
│ [ Disconnect ] [ Reconnect ] [ Settings ]   │
└──────────────────────────────────────────────┘
```

This mockup is illustrative, not a pixel-perfect mandate.

The interface should prioritize:

```text
connection status
current traffic
session duration
reconnect information
```

Do not clutter the primary view with obscure SSH settings.

---

# 27. Connection colors

The interface may distinguish states visually.

For example:

```text
Connected     green
Connecting    blue
Degraded      yellow/orange
Reconnecting  orange
Failed        red
Disconnected  gray
```

Do not rely exclusively on color.

Always include text and/or icons.

---

# 28. Settings window

Sections:

```text
Connection
Reconnect
Monitoring
Analytics
Interface
Notifications
Advanced
```

## Connection

```text
Profile name
SSH target
SOCKS address
SOCKS port
SSH executable
IPv4-only
ServerAliveInterval
ServerAliveCountMax
```

## Reconnect

```text
Automatic reconnect
Initial retry delay
Maximum retry delay
Healthy reset period
```

## Monitoring

```text
Health checking
Health-check target
Interval
Timeout
Failure threshold
```

## Analytics

```text
Store history
Retention duration
Database location
```

## Interface

```text
Language
Start minimized
Close to tray
```

## Notifications

```text
Notify on failure
Notify on recovery
Minimum outage duration before notification
```

## Advanced

```text
Additional SSH arguments
Logging verbosity
```

---

# 29. English and Russian localization

The complete UI must support:

```text
English
Русский
```

Do not hardcode user-visible text directly into view code.

Use localization resources.

For example:

```text
Resources.en.resx
Resources.ru.resx
```

or an equivalent structured localization system.

All normal UI elements must be translatable:

```text
buttons
labels
menus
dialogs
errors
notifications
status names
settings
tooltips
```

---

# 30. Language behavior

On first launch:

1. inspect Windows UI language;
2. use Russian when appropriate;
3. otherwise use English.

The user must be able to override this manually.

Changing language should ideally update the UI immediately.

If that creates disproportionate complexity, restarting the application after changing language is acceptable before `v1.0`.

---

# 31. System tray

The application should support minimizing to the Windows notification area.

Tray menu:

```text
Open constantproxy
Connect
Disconnect
Reconnect
Profile >
Settings
Exit
```

The tray icon should communicate broad connection state where practical.

Closing the main window may:

```text
minimize to tray
```

if enabled.

Actual termination must remain available through:

```text
Exit
```

---

# 32. Windows startup

Optional setting:

```text
Start constantproxy with Windows
```

Optional companion setting:

```text
Connect automatically after startup
```

These must be separate.

A user may want the GUI to launch without automatically opening the tunnel.

---

# 33. Notifications

Use normal Windows notifications where practical.

Examples:

```text
Proxy connection lost.
Reconnecting...
```

and:

```text
Proxy connection restored.
Downtime: 18 seconds.
```

To avoid spam, short transient disconnects should not necessarily produce notifications.

Support a configurable threshold.

Example:

```text
Notify only if outage lasts longer than 10 seconds.
```

---

# 34. Error handling

Errors must be understandable.

Bad:

```text
System.ComponentModel.Win32Exception: 2
```

Good:

```text
OpenSSH could not be found.
Check the configured ssh.exe path.
```

Russian:

```text
OpenSSH не найден.
Проверьте путь к ssh.exe в настройках.
```

Relevant technical details may appear in an expandable section:

```text
Details
```

---

# 35. Common errors that must be handled

At minimum:

```text
ssh.exe missing
invalid SSH executable
invalid port
port already occupied
unknown SSH host
DNS failure
authentication failure
host key verification failure
network unreachable
remote server unreachable
SOCKS forward cannot be established
unexpected SSH process termination
health-check timeout
corrupted configuration
database error
```

Do not automatically reconnect forever for obvious configuration errors such as:

```text
invalid executable path
invalid arguments
invalid local port
```

---

# 36. Safety around processes

When `constantproxy` exits intentionally, it should terminate only SSH processes that it started.

Never search for and kill every process named:

```text
ssh.exe
```

The user may have unrelated SSH sessions.

Track child process IDs explicitly.

---

# 37. Graceful shutdown

On disconnect:

1. mark the shutdown as intentional;
2. disable reconnect;
3. request child termination;
4. wait for a short timeout;
5. force terminate only if required;
6. finalize the analytics session;
7. update UI state.

The reconnect loop must never mistake intentional shutdown for connection failure.

---

# 38. Single-instance behavior

By default, only one `constantproxy` GUI instance should run for a user.

Launching it again should preferably focus/open the existing instance.

This prevents accidental duplicate tunnels and port conflicts.

A future multi-instance mode is unnecessary for initial releases.

---

# 39. Analytics privacy

Analytics are local only.

There must be no telemetry sent to the developer.

No account.

No cloud service.

No automatic uploading of:

```text
SSH hostnames
traffic history
connection logs
IP addresses
usage data
```

The README must state this clearly.

---

# 40. Updates

Automatic self-update is not required for `v1.0`.

The application may optionally provide:

```text
Check for updates
```

using public GitHub Releases.

Do not silently replace binaries.

---

# 41. Versioning

Use Semantic Versioning.

Format:

```text
MAJOR.MINOR.PATCH
```

Examples:

```text
0.1.0
0.2.0
0.4.1
1.0.0
```

Git tags:

```text
v0.1.0
v0.2.0
v1.0.0
```

The application must display its version under:

```text
About
```

Version information should preferably derive from build/release metadata rather than being duplicated manually throughout the source tree.

---

# 42. Development releases

Suggested roadmap follows.

## v0.1.0 — Foundation

Goal:

> Make the tunnel reliable.

Implement:

```text
basic WPF application
English UI
single profile
SSH executable configuration
SSH target configuration
port configuration
start connection
stop connection
process supervision
automatic reconnect
reconnect backoff
basic logs
connection status
session timer
basic configuration persistence
```

Acceptance:

A user can configure a host and port, click Connect, and `constantproxy` reliably supervises the OpenSSH process.

---

# 43. v0.2.0 — Monitoring

Goal:

> Know whether the tunnel is actually alive.

Add:

```text
SOCKS listener detection
health checks
Degraded state
health-check latency
failure threshold
manual Reconnect button
better error classification
port conflict detection
```

Acceptance:

The application can distinguish:

```text
SSH process running
```

from:

```text
usable SOCKS connection
```

---

# 44. v0.3.0 — Traffic telemetry

Goal:

> See what the proxy is doing.

Add:

```text
upload speed
download speed
uploaded bytes
downloaded bytes
averages
peaks
short-term traffic graph
traffic-monitor abstraction
```

Acceptance:

Proxy-specific traffic can be measured without counting unrelated system network traffic.

---

# 45. v0.4.0 — Historical analytics

Goal:

> Turn connection supervision into useful statistics.

Add:

```text
SQLite
session history
traffic history
reconnect history
availability statistics
daily totals
weekly totals
historical graphs
retention policy
```

---

# 46. v0.5.0 — Desktop integration

Add:

```text
system tray
Windows startup
start minimized
automatic connect
notifications
notification anti-spam
single-instance handling
```

---

# 47. v0.6.0 — Localization

Add complete:

```text
English
Russian
```

localization.

All application strings must move into resource files by this release.

---

# 48. v0.7.0 — Profiles

Add:

```text
multiple saved profiles
profile selection
profile cloning
profile rename
profile deletion
independent analytics per profile
```

Only one active connection is required.

---

# 49. v0.8.0 — UX polish

Focus on:

```text
layout
error dialogs
graph readability
tooltips
keyboard navigation
high-DPI behavior
dark/light theme compatibility
accessibility
settings validation
```

No major architectural features should be introduced during this phase.

---

# 50. v0.9.0 — Release candidate

Feature freeze.

Focus only on:

```text
bug fixes
reconnect edge cases
process cleanup
database migration
configuration migration
packaging
localization completeness
crash handling
documentation
```

---

# 51. v1.0.0 — Stable

`v1.0.0` is the first stable public release.

It must provide:

```text
reliable SSH supervision
automatic reconnect
configurable connection profiles
traffic monitoring
historical analytics
English/Russian UI
system tray
Windows startup integration
GitHub-built executable
documentation
clean upgrade path
```

Reliability is more important than adding additional features.

---

# 52. Post-1.0 possibilities

These are explicitly outside initial scope:

```text
simultaneous profiles
HTTP CONNECT proxy mode
SSH jump-host editor
portable mode
ARM64
PAC generation
browser proxy switching
system proxy integration
advanced ETW diagnostics
remote management
plugin system
```

Do not implement these early merely because they seem interesting.

---

# 53. GitHub Actions

GitHub Actions is mandatory.

All public releases must be reproducibly built by GitHub Actions.

A developer's manually compiled `.exe` must not be the canonical release artifact.

At minimum create:

```text
.github/workflows/build.yml
.github/workflows/release.yml
```

---

# 54. Continuous build workflow

On:

```text
push
pull_request
```

perform appropriate automated checks such as:

```text
restore
compile
unit tests
```

The build must fail if compilation fails.

Use a Windows GitHub Actions runner where Windows-specific build tooling is required.

Do not depend on the development server having a graphical Windows environment.

---

# 55. Release workflow

On a version tag matching:

```text
v*
```

GitHub Actions should:

1. check out the repository;
2. restore dependencies;
3. build Release configuration;
4. publish the Windows application;
5. package the result;
6. calculate hashes;
7. upload release artifacts;
8. create or populate the corresponding GitHub Release.

Expected artifacts should include at least:

```text
constantproxy-<version>-win-x64.zip
```

and preferably:

```text
constantproxy-<version>-win-x64.exe
```

where technically appropriate.

---

# 56. Packaging strategy

The easiest initial distribution is acceptable:

```text
ZIP containing the application
```

A single-file self-contained executable is desirable if technically reasonable and reliable.

Do not sacrifice application correctness simply to obtain a literally single physical file.

An installer may be added later.

---

# 57. Checksums

Release workflow should generate a SHA-256 checksum file.

Example:

```text
constantproxy-0.5.0-win-x64.zip
constantproxy-0.5.0-win-x64.zip.sha256
```

or:

```text
SHA256SUMS.txt
```

---

# 58. Release changelog

Maintain:

```text
CHANGELOG.md
```

Each release should document:

```text
Added
Changed
Fixed
Removed
```

Do not generate meaningless commit dumps as user-facing release notes.

---

# 59. README

README must explain:

```text
What constantproxy is
What it is not
Requirements
Installation
Quick start
OpenSSH prerequisites
SSH config example
How reconnect works
How analytics work
Data storage
Privacy
Building from source
Release downloads
License
```

Example quick start:

```text
1. Configure an SSH host in ~/.ssh/config.
2. Launch constantproxy.
3. Enter the SSH alias.
4. Select a SOCKS port.
5. Click Connect.
6. Configure the desired application to use socks5://127.0.0.1:<port>.
```

---

# 60. ARCHITECTURE.md

Document major components.

Conceptually:

```text
┌─────────────────────┐
│       WPF UI        │
└──────────┬──────────┘
           │
┌──────────▼──────────┐
│ Connection Manager  │
└──────┬────────┬─────┘
       │        │
       │        └───────────────┐
       │                        │
┌──────▼───────┐      ┌────────▼────────┐
│ SSH Supervisor│      │ Health Monitor  │
└──────┬───────┘      └─────────────────┘
       │
       │
┌──────▼────────┐
│   ssh.exe     │
└───────────────┘

┌──────────────────┐
│ Traffic Monitor  │
└────────┬─────────┘
         │
┌────────▼─────────┐
│ Analytics Store  │
│     SQLite       │
└──────────────────┘
```

UI code must not directly manage SSH processes.

---

# 61. Separation of concerns

Preferred logical services:

```text
ConnectionManager
SshProcessSupervisor
ReconnectPolicy
HealthCheckService
TrafficMonitor
AnalyticsService
ConfigurationService
LocalizationService
NotificationService
LoggingService
```

Names may differ.

Responsibilities must remain separated.

---

# 62. Threading

Do not block the WPF UI thread with:

```text
process waits
network operations
database operations
health checks
retry delays
```

Use asynchronous operations and cancellation tokens appropriately.

All long-running systems must terminate predictably during application shutdown.

---

# 63. Cancellation

Connection operations must be cancellable.

For example, if the application is currently waiting:

```text
15 seconds before reconnect
```

and the user presses:

```text
Disconnect
```

the reconnect delay must terminate immediately.

The user must never need to wait for a retry timer before the application obeys a stop request.

---

# 64. Race-condition protection

Pay particular attention to:

```text
Connect pressed twice
Disconnect during Connecting
Reconnect during Connecting
Application exit during reconnect delay
SSH exits while manual disconnect is occurring
Profile changed while connection active
```

Connection lifecycle should have one authoritative controller.

Avoid scattered booleans such as:

```text
isConnecting
isDisconnecting
shouldReconnect
forceStop
manualShutdown
```

without a coherent state machine.

---

# 65. Diagnostics

Provide a diagnostics view containing:

```text
Application version
OS version
.NET version
Configured ssh.exe
SSH version
Current profile
Current SSH PID
Connection state
SOCKS endpoint
Session start
Reconnect count
Database location
Log location
```

Provide:

```text
Copy diagnostics
```

Sensitive/private material should be omitted where practical.

Do not include private key contents.

---

# 66. Export

By `v1.0`, allow users to export analytics in a simple format.

At minimum:

```text
CSV
```

Possible exports:

```text
sessions.csv
traffic.csv
connection-events.csv
```

JSON may also be provided.

---

# 67. UI responsiveness

The interface must remain responsive during:

```text
connecting
failed SSH authentication
network outage
reconnecting
analytics loading
database cleanup
```

A broken network connection must never freeze the GUI.

---

# 68. Accessibility

Use native controls where practical.

Support:

```text
keyboard navigation
screen scaling
reasonable contrast
text labels in addition to colors
Windows DPI scaling
```

Do not build the entire interface from custom-painted controls unless necessary.

---

# 69. Themes

The application should work acceptably under both Windows light and dark environments.

A full custom theming system is not necessary for initial versions.

Correctness and readability matter more than elaborate visual styling.

---

# 70. No server-side visual testing

Development may occur on a remote/server environment that cannot launch or visually inspect the Windows GUI.

The coding agent must **not claim that it visually tested the application on the development server**.

Do not attempt fake GUI validation through screenshots of an environment incapable of running the target application.

The development server should be used for:

```text
source editing
repository operations
static analysis
non-visual automated tests
documentation
```

Windows-specific compilation may be delegated to GitHub Actions where necessary.

The final graphical/runtime verification is performed on an actual Windows machine.

The agent should design the UI from the specification and normal WPF practices rather than pretending to have visually inspected it.

---

# 71. Automated testing

Automated testing should focus on logic that can be tested reliably without requiring a live production SSH server.

Good candidates:

```text
ReconnectPolicy
state transitions
configuration parsing
configuration migration
database calculations
traffic rate calculations
analytics aggregation
formatting
localization resource completeness
argument generation
```

Do not make CI dependent on the availability of the developer's private SSH server.

Never put private SSH credentials into GitHub Actions.

---

# 72. SSH argument tests

Argument generation deserves dedicated tests.

Given:

```text
Host = blindicide
Port = 10080
IPv4 = true
ServerAliveInterval = 30
ServerAliveCountMax = 3
```

the argument model should represent:

```text
-4
-N
-D
127.0.0.1:10080
-o
ServerAliveInterval=30
-o
ServerAliveCountMax=3
-o
ExitOnForwardFailure=yes
blindicide
```

The program should pass these as separate process arguments.

---

# 73. Configuration validation

Validate:

```text
port range: 1–65535
non-empty SSH target
existing executable where explicit path is supplied
positive timing values
valid bind address
valid retention values
```

Invalid values should be caught in the UI before attempting connection.

---

# 74. Security assumptions

The SOCKS proxy should bind to:

```text
127.0.0.1
```

by default.

Do not expose the SOCKS server to:

```text
0.0.0.0
```

by default.

If the user deliberately changes the bind address to a non-loopback interface, display a warning that other hosts may be able to reach the local SOCKS endpoint depending on firewall/network configuration.

Do not silently override the user's choice.

---

# 75. Command display

Provide a read-only field or diagnostics option:

```text
Show generated SSH command
```

Example:

```text
ssh -4 -N -D 127.0.0.1:10080 ...
```

Sensitive values should be redacted if any future configuration option could contain them.

This feature is useful for troubleshooting.

---

# 76. External SSH configuration

`constantproxy` should cooperate with ordinary OpenSSH configuration.

Do not attempt to parse and reproduce every possible `.ssh/config` feature.

If the user selects:

```text
blindicide
```

then OpenSSH itself should resolve whatever `Host blindicide` means.

This preserves compatibility with:

```text
HostName
User
Port
IdentityFile
ProxyJump
ProxyCommand
CertificateFile
Match
Include
```

without constantproxy having to understand those features.

---

# 77. Failure classification

Where practical, classify failures into broad categories.

For example:

```text
Local configuration
Authentication
Host verification
Network
Remote connection
Forwarding
Unknown
```

Do not depend entirely on localized OpenSSH error strings.

Use exit state, listener state, process behavior, and stderr information together.

Unknown failures should remain possible.

Never invent certainty.

---

# 78. Analytics event model

Connection events should record structured event types such as:

```text
ApplicationStarted
ConnectionRequested
SshStarted
SocksReady
Connected
HealthCheckFailed
Degraded
SshExited
ReconnectScheduled
ReconnectAttempt
ReconnectSucceeded
ManualDisconnect
ApplicationExit
```

This makes future analytics much easier than attempting to reconstruct state entirely from text logs.

---

# 79. Time handling

Store timestamps internally in UTC.

Convert to local time for display.

This prevents historical analytics from breaking across:

```text
DST changes
timezone changes
travel
```

---

# 80. Database retention

Allow historical analytics retention to be controlled.

Possible options:

```text
30 days
90 days
180 days
1 year
Forever
```

Do not let high-frequency raw samples grow without bound.

Aggregate or delete old high-resolution points.

---

# 81. Performance objective

`constantproxy` should be boring.

While connected and idle:

```text
near-zero CPU activity
small memory footprint
minimal disk writes
no continuous polling loops running unnecessarily fast
```

A 1 Hz UI statistics refresh is sufficient.

There is absolutely no reason for this application to render at 144 FPS.

---

# 82. Crash recovery

If `constantproxy` itself crashes:

- database corruption should be avoided through normal transactional behavior;
- the next launch should be able to detect an unfinished analytics session;
- mark such a session as interrupted if necessary.

Do not automatically kill arbitrary lingering SSH processes from earlier unrelated sessions.

Any orphan cleanup mechanism must be conservative and identify only processes unquestionably created by constantproxy.

---

# 83. First-run experience

On first launch, show a minimal setup flow.

Example:

```text
SSH target:
[ blindicide                     ]

SOCKS port:
[ 10080 ]

SSH executable:
[ Automatic detection            ]

[ Connect ]
```

Advanced settings remain available separately.

Do not present twenty configuration options before the user can establish their first connection.

---

# 84. OpenSSH detection

Try to find the system OpenSSH client automatically.

If:

```text
ssh.exe
```

is accessible through PATH, use it.

Otherwise allow the user to browse for it.

Do not download or install OpenSSH without explicit user action.

---

# 85. About page

Display:

```text
constantproxy
Version
License
GitHub repository
```

and a concise description:

```text
A lightweight Windows supervisor and analytics interface for SSH SOCKS proxies.
```

---

# 86. License

Because the repository is public, include an explicit open-source license.

Unless another license is intentionally selected, MIT is a reasonable default for this project.

Do not leave the repository legally ambiguous by omitting a license accidentally.

---

# 87. Coding standards

Prefer:

```text
clear code
small classes
explicit state
dependency injection where useful
nullable reference types
async/await
cancellation tokens
structured logging
```

Avoid:

```text
massive god classes
static global state
UI code containing networking logic
busy loops
Thread.Sleep in asynchronous workflows
swallowed exceptions
catch (Exception) with no logging
```

---

# 88. Dependency policy

Keep dependencies minimal.

Every external dependency must solve an actual problem.

Do not add a framework merely because it is fashionable.

Particularly avoid turning a small utility into a 40-package dependency tree without justification.

Prefer built-in .NET functionality where it is adequate.

---

# 89. Definition of success

The project succeeds when the following user experience works reliably:

1. The user installs/downloads `constantproxy`.
2. The user already has a valid OpenSSH configuration.
3. The user chooses:

```text
Host: blindicide
Port: 10080
```

4. The user clicks:

```text
Connect
```

5. `constantproxy` launches the equivalent of:

```text
ssh -4 -N -D 127.0.0.1:10080 -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes blindicide
```

6. The UI reports when the SOCKS tunnel is available.
7. Applications can use:

```text
socks5://127.0.0.1:10080
```

8. `constantproxy` displays current upload/download traffic.
9. Network interruption kills or invalidates the SSH session.
10. `constantproxy` notices.
11. Reconnection begins automatically.
12. The tunnel returns without user intervention.
13. The application records the outage and reconnect.
14. The user can later see how much traffic passed through the tunnel and how reliable the connection was.

That is the central product.

Everything else is secondary.

---

# 90. Scope guard

The coding agent must actively resist unnecessary scope expansion.

`constantproxy` is **not** supposed to become:

```text
a VPN
an SSH client replacement
a packet capture suite
a firewall
a proxy-chain designer
a network scanner
a remote server manager
a browser
an AI assistant
```

Its job is extremely specific:

```text
Start the SSH SOCKS tunnel.
Keep it alive.
Tell the user whether it is alive.
Measure what it is doing.
Reconnect when it dies.
```

If those five things work exceptionally well, the project is successful.
