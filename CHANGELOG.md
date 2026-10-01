# Changelog

All notable changes to constantproxy are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

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
