# Changelog

All notable changes to constantproxy are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-10-01

### Added
- `ITrafficMonitor` abstraction for replaceable traffic backends; unmeasurable traffic is shown as "Unavailable" rather than invented.
- Byte-counting bridge backend: constantproxy relays the SOCKS port to ssh's internal loopback listener and counts exactly the proxied bytes (no system-wide interface counters, no elevated privileges). Enabled by default, switchable per profile.
- Traffic statistics: current upload/download rates, session totals, peaks and averages, sampled once per second with a bounded in-memory history.
- Consistent binary unit formatting (1 KB = 1024 B) for rates (B/s to GB/s) and totals (B to TB).
- Lightweight in-app traffic graph (1 minute, 5 minutes, 1 hour ranges) with a text legend so series do not rely on colour alone.

### Changed
- ssh's `-D` listener moves to an internal ephemeral loopback port while the bridge is active; the configured port stays the user-facing SOCKS endpoint.

## [0.2.0] - 2026-10-01

### Added
- Port conflict detection before launching ssh ("Port 10080 is already in use."); a port still held during a reconnect is retried with backoff instead of failing permanently.
- SSH startup verification: the tunnel is only `Connected` once the SOCKS listener accepts connections, with a configurable startup timeout.
- SOCKS5 health checks through the proxy (configurable target, interval, timeout and failure threshold), latency measurement and the `Degraded` state with recovery.
- Optional reconnect when health checks keep failing.
- Failure classification (local configuration, authentication, host verification, network, remote connection, forwarding, unknown) combining stage, exit code and stderr; authentication, host-key and bind failures stop the retry loop.
- "Reconnect now" restarts ssh without increasing the backoff counter and skips a pending retry delay.
- Latency display and an expandable Details section for failures in the main window.

## [0.1.0] - 2026-10-01

### Added
- Solution structure: `ConstantProxy.Core`, `ConstantProxy.Infrastructure`, `ConstantProxy.App` (WPF) and an xUnit test project.
- Explicit connection state machine (Disconnected, Starting, Connecting, Connected, Degraded, Reconnecting, Stopping, Failed).
- `ConnectionManager`: single authoritative lifecycle controller with serialized commands, cancellable retry delays and intentional-stop handling.
- Direct `ssh.exe` launch with a discrete argument list (no `cmd.exe`), PID tracking, stdout/stderr capture without pipe deadlocks.
- Configurable reconnect backoff (default 0, 1, 2, 5, 10, 15 s) with healthy-period reset and optional jitter.
- JSON configuration with validation, atomic saves and recovery from corrupt files.
- Main window with status indicator, session timer, Connect / Disconnect / Reconnect controls, connection settings and a log view.
- Version derived from build metadata; `--version` command line switch.
