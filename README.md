# constantproxy

A lightweight Windows supervisor and analytics interface for SSH SOCKS proxies.

`constantproxy` starts `ssh -D`, keeps it alive, tells you whether the tunnel is really usable, measures what it carries,
and reconnects when it dies — so you no longer have to run and babysit a command like

```text
ssh -4 -N -D 127.0.0.1:10080 -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes my-tunnel
```

by hand.

## What it is — and what it is not

**It is** a small .NET / WPF desktop application that

- launches the Windows OpenSSH client (`ssh.exe`) directly with an explicit argument list (no `cmd.exe`, no shell);
- supervises the process, detects failure and reconnects with a configurable back-off;
- verifies that the SOCKS listener is up and, optionally, that traffic really passes through it (health checks);
- shows connection state, latency, upload/download rates, totals, peaks and a live graph;
- stores local history (sessions, traffic, outages, uptime) in SQLite and exports it as CSV/JSON;
- lives in the notification area, can start with Windows, and notifies you about meaningful outages;
- supports several profiles (one active tunnel at a time) and is fully available in English and Russian.

**It is not** an SSH implementation, a VPN, a firewall, a proxy suite or a system-wide proxy switcher. OpenSSH does all
SSH work — transport, authentication, keys, host verification, `~/.ssh/config`. constantproxy never sees, stores or
copies passwords or private keys.

## Requirements

- Windows 10 or Windows 11, 64-bit.
- The OpenSSH client (`ssh.exe`). It is included with Windows ("Optional features → OpenSSH Client") or installed by Git for Windows.
- An SSH host you can already reach with `ssh <host>`, ideally without an interactive prompt (key or agent authentication).

No .NET installation is needed: release builds are self-contained.

## Installation

1. Download `constantproxy-<version>-win-x64.zip` from the **Releases** page of this repository.
2. Verify it (optional but recommended, see [Release downloads](#release-downloads)).
3. Extract the ZIP anywhere (for example `C:\Tools\constantproxy`) and run `constantproxy.exe`.

The executable is not code-signed, so Windows SmartScreen may show a warning the first time. A single-file
`constantproxy-<version>-win-x64.exe` is published as well; if it fails to start, use the ZIP.

## Quick start

1. Configure an SSH host in `%USERPROFILE%\.ssh\config` (see below).
2. Start `constantproxy`. On first launch a short setup window appears.
3. Enter the SSH alias (or host name) as the **SSH target**.
4. Choose a **SOCKS port** (default `10080`).
5. Leave **SSH executable** empty to auto-detect `ssh.exe`, then press **Connect**.
6. Point your application at `socks5://127.0.0.1:<port>` (use `socks5h` / "proxy DNS" if the program supports it).

When the tunnel is up the main window shows **Connected**, the session timer, latency and live traffic.

## OpenSSH prerequisites

constantproxy hands the target to OpenSSH unchanged, so everything OpenSSH understands works: `User`, `Port`,
`IdentityFile`, `ProxyJump`, `ProxyCommand`, `CertificateFile`, `Include`, `Match`, … Example `%USERPROFILE%\.ssh\config`:

```text
Host my-tunnel
    HostName example.org
    User alice
    IdentityFile ~/.ssh/id_ed25519
```

With that, the SSH target in constantproxy is simply `my-tunnel`. Make sure the key is usable without a prompt
(`ssh-agent`, or an unencrypted key you accept the risk of). constantproxy runs `ssh` without a console, so it cannot
answer passphrase or host-key questions: connect once manually (`ssh my-tunnel`) to accept the host key. Enable
**Never wait for input (BatchMode)** in *Settings → Advanced* if you prefer ssh to fail immediately instead of waiting.

## How reconnect works

- The tunnel is `Disconnected → Starting → Connecting → Connected`, with `Degraded`, `Reconnecting`, `Stopping` and `Failed`
  as further explicit states shown with text, a symbol and a colour.
- **Connected** is only declared once the SOCKS listener really accepts connections (startup timeout: 15 s by default).
- If `ssh.exe` exits unexpectedly, constantproxy reconnects after **0, 1, 2, 5, 10, 15, 15, 15 … seconds** (all configurable,
  optional jitter). The counter resets after the tunnel stayed healthy for 30 s.
- **Reconnect now** restarts ssh at once, skips any pending wait and does not count as a failure.
- **Disconnect** is never mistaken for a failure and cancels any retry delay immediately.
- Persistent local problems are *not* retried forever: a missing `ssh.exe`, invalid arguments, a busy local port, an
  authentication failure or a changed host key put the tunnel into `Failed` with an explanation.
- Optional **health checks** open a SOCKS connection to a host you choose (default `example.com:443`) every 10 s. After 3
  consecutive failures the state becomes `Degraded`; optionally persistent failure triggers a reconnect.
- Exactly one ssh process runs per tunnel. On exit constantproxy ends only the process it started — never other `ssh.exe`
  instances — and a Windows job object ends it too if constantproxy itself crashes.

## Traffic measurement

Traffic is counted exactly where it passes: constantproxy listens on your SOCKS port and relays each connection to ssh's
internal loopback listener, counting the bytes in both directions (**Settings → Advanced → Traffic measurement**). Unrelated
system traffic is never included, no administrator rights are needed, and if measurement is off the UI says
**Unavailable** instead of showing invented numbers.

Counts are the proxied application payload (including SOCKS handshakes), not SSH protocol overhead. Units are **binary**:
1 KB = 1024 B, 1 MB = 1024 KB, and so on; rates are shown as B/s, KB/s, MB/s, GB/s and totals as B, KB, MB, GB, TB.

## How analytics work

Everything is stored **locally** in a SQLite database (`constantproxy.db`):

- **sessions** (start, end, why it ended, connected time, reconnects, failures, health failures, bytes, average and peak rates);
- **state intervals** (connected, degraded, reconnecting, failed, user-disconnected) used for uptime;
- **one-minute traffic aggregates** with health-check counts and latency (never one write per second);
- **structured connection events** (`Connected`, `HealthCheckFailed`, `Degraded`, `SshExited`, `ReconnectAttempt`, …);
- raw health checks for 7 days.

**Uptime** is the share of measured time the tunnel was connected and healthy. Time you intentionally disconnected, and
the very first connect of a session, are *not* outages; reconnecting, failed and degraded time are. When nothing was
measured, the UI shows a dash instead of a made-up 100 %.

The **Statistics** tab shows uptime (session / today / 7 days / 30 days), traffic (today / this week / this month),
runtime, reconnects, failures, the longest continuous connection, and history graphs for traffic, latency and
availability. Retention is configurable (30, 90, 180 days, 1 year or forever; default 90 days). **Export…** writes
`sessions`, `traffic` and `connection-events` as CSV (UTF-8, formula-safe) or JSON. A crash is detected on the next start
and the unfinished session is marked *interrupted*. Each profile has its own history.

## Data storage

All data lives in a per-user folder (never beside the executable), by default `%LOCALAPPDATA%\constantproxy\`:

| File | Content |
| --- | --- |
| `config.json` | settings and profiles (see [docs/CONFIGURATION.md](docs/CONFIGURATION.md)) |
| `constantproxy.db` | analytics database (location configurable) |
| `logs\constantproxy.log` | application and SSH log (rolled at 2 MB) |
| `logs\crash-*.txt` | crash reports, if any |
| `children.json` | ssh processes started by constantproxy (used for crash cleanup) |

Use **Statistics → Open data folder** to open it. Set the environment variable `CONSTANTPROXY_DATA_DIR` to use another folder.

## Privacy

constantproxy is **local only**. There is no telemetry, no account, no cloud service and no automatic upload of
hostnames, traffic history, logs, IP addresses or usage data. The only network activity is the `ssh` connection you
configured and the optional health-check connection (through your own tunnel, to the host you chose). *Diagnostics →
Copy diagnostics* hides your SSH target, profile name and local paths unless you tick the box to include them.

## Command line

```text
constantproxy.exe --version     prints "constantproxy <version>" and exits
```

(`--startup` is passed by the Windows startup entry and has no other effect.)

## Troubleshooting

| You see | Meaning and fix |
| --- | --- |
| *OpenSSH could not be found.* | Install the Windows OpenSSH Client or set the path to `ssh.exe` in *Settings → Connection*. |
| *Port 10080 is already in use.* | Another program (or another tunnel) uses the port. Pick a different SOCKS port. |
| *SSH authentication failed.* | Check your key or agent with `ssh <host>` in a terminal. constantproxy cannot type passwords. |
| *The server's host key could not be verified.* | Check `known_hosts`; connect once manually to accept a new host. |
| *The SSH host name could not be resolved.* | Typo in the alias, or no network. constantproxy keeps retrying while the network is down. |
| **Degraded** | The tunnel process runs but health checks fail. Check the health-check target in *Settings → Monitoring*. |
| Traffic shows **Unavailable** | Traffic measurement is off (*Settings → Advanced*). |

*Settings → Advanced* shows the exact generated ssh command; *Diagnostics* produces a report you can paste into a bug report.

## Building from source

Requirements: the .NET 8 SDK. The WPF application builds on Windows; the platform-neutral libraries and all unit tests also
build and run on Linux or macOS.

```powershell
dotnet restore
dotnet build -c Release
dotnet test tests/ConstantProxy.Tests -c Release
dotnet publish src/ConstantProxy.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtraction=true
```

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Release downloads

Releases are built by GitHub Actions from version tags (`v*`), never from a developer's machine:
`constantproxy-<version>-win-x64.zip`, `constantproxy-<version>-win-x64.exe` and `SHA256SUMS.txt`. Verify a download:

```powershell
(Get-FileHash .\constantproxy-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLower()   # compare with SHA256SUMS.txt
```

Release notes live in [CHANGELOG.md](CHANGELOG.md); the process is described in [docs/RELEASES.md](docs/RELEASES.md).

## Project status

Version 1.0 was developed on a headless Linux machine. All platform-neutral logic — state machine, supervision,
reconnect back-off, argument generation, health evaluation, SOCKS probing, traffic accounting, analytics, migrations,
retention, export, localization, validation, notification filtering — is covered by automated tests, and the Windows
application compiles and publishes. **The WPF user interface and Windows-specific integration (tray, notifications,
startup entry, job object) could not be run or visually inspected during development**; they are built from the
specification and normal WPF practice and are verified on real Windows machines. Please report anything that looks or
behaves wrong.

## License

[MIT](LICENSE).
