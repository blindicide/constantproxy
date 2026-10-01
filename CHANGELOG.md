# Changelog

All notable changes to constantproxy are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [0.8.0] - 2026-10-01

### Added
- Settings dialog with all sections (Connection, Reconnect, Monitoring, Analytics, Interface, Notifications, Advanced), live per-field validation with tooltips, an error summary and OK disabled until everything is valid; nothing is written until OK.
- Diagnostics window: application, OS and .NET versions, configured and resolved ssh, OpenSSH version (looked up off the UI thread), profile, SSH PID, state, SOCKS endpoint, session start, reconnect count, database and log locations, last failure; "Copy diagnostics" with SSH target, profile name and local paths hidden unless explicitly included.
- Read-only preview of the generated SSH command (in Settings and Diagnostics) that shows a placeholder for the bridge's internal port and redacts secret-looking values.
- First-run setup limited to target, port and ssh location, with automatic OpenSSH detection and a browse button.
- About dialog with version, license, repository link and description.
- Tooltips explaining the non-obvious settings; keyboard access keys throughout.
- Guard test that no user-visible text is hardcoded in XAML.

### Changed
- The main window no longer carries an inline settings form; it keeps the profile bar, status, traffic, graph and the Connect, Disconnect, Reconnect now and Settings buttons.
- Validation messages are attached to the field that caused them (reconnect and monitoring fields are reported individually).

## [0.7.0] - 2026-10-01

### Added
- Multiple connection profiles: add, clone, rename (edit the name and save) and delete, with unique names (case-insensitive), at least one profile always present, and each profile keeping fully independent settings.
- Profile selector in the main window and a "Profile" submenu in the tray menu with a check mark on the active profile; switching is only possible while no tunnel is running, and unsaved edits are kept when switching.
- Independent analytics per profile: sessions, traffic, availability and statistics follow the selected profile; profile names are stored with the history and included in CSV/JSON session exports (`profile_name`).
- New profiles get the next unused SOCKS port; clones copy every setting and are inserted next to their source.
- Duplicate profile ids in a hand-edited configuration file are repaired on load.

## [0.6.0] - 2026-10-01

### Added
- Complete English and Russian localization (184 strings) in embedded JSON resource files covering buttons, labels, menus, tray, dialogs, errors, notifications, settings, tooltips, units and statistics; Russian plural rules for durations.
- Language detection: Russian when the Windows UI language is Russian, English otherwise, with a manual Automatic / English / Русский override in the settings that takes effect immediately without a restart.
- Localized failure messages by failure code (including the wording from the specification, for example "Порт 10080 уже используется.") with the technical details kept untranslated in the expandable Details section.
- Locale-aware number formatting and unit labels (for example `1,50 КБ/с`).
- Resource tests: every English key has a Russian counterpart, placeholders and access keys match, nothing is left untranslated, plural groups are complete, and every key referenced from C# or XAML, every failure code and every validation code exists.

### Changed
- All user-visible text moved out of views and view models into the resource files; the XAML uses a `{loc:Loc key}` markup extension that refreshes on language change.
- Failure messages no longer embed the exit code; it is carried separately and shown in the Details section.

## [0.5.0] - 2026-10-01

### Added
- Notification-area icon with a context menu (Open, Connect, Disconnect, Reconnect, Settings, Exit) whose enabled items follow the connection state; per-state icons differ in shape as well as colour.
- Optional hide-to-tray on close and on minimize; exiting is always explicit (tray menu).
- Single-instance behaviour: launching constantproxy again brings the running instance to the front (acknowledged activation over a per-user named pipe).
- "Start with Windows" (per-user Run key, only its own value is touched, self-repairs if the executable moves) and an independent "Connect automatically when constantproxy starts" setting; "Start minimized" setting.
- Windows notifications with anti-spam: outages shorter than the configured minimum (default 10 s) stay silent, "restored" is only sent when the loss was announced and reports the downtime, intentional disconnects never notify.
- Graceful shutdown on exit and when Windows ends the session; a Windows job object makes sure only the ssh processes constantproxy started are terminated if constantproxy itself dies.
- Application icon (`assets/constantproxy.ico`, reproducible via `scripts/make_icon.py`).

## [0.4.0] - 2026-10-01

### Added
- SQLite analytics store (`Microsoft.Data.Sqlite`) with `profiles`, `sessions`, `state_intervals`, `traffic_samples` (one-minute aggregates), `connection_events`, `health_checks` and `settings` tables; timestamps are stored as UTC.
- Forward-only migration engine tracked by `PRAGMA user_version`: each step runs in a transaction, an existing database is backed up before an upgrade, and a database from a newer version is refused instead of damaged.
- Analytics recorder: a single background worker records sessions, state intervals, structured connection events, health checks and traffic once a minute, so neither the UI nor the supervisor ever waits on the database; storage failures are logged and contained.
- Availability analytics: uptime percentage per session, day, 7 days and 30 days; intentional disconnects and the very first connect are not counted as outages, reconnecting, failed and degraded time are.
- Aggregates: traffic today / this week / this month, total runtime and connected time, reconnect and failure counts, longest continuous connection.
- Retention policy (30 days, 90 days, 180 days, 1 year or forever; default 90 days) with automatic pruning; raw health checks are kept for 7 days only.
- Crash recovery: sessions left open by a crash are closed and marked as interrupted on the next launch; a damaged database is set aside and replaced.
- CSV (UTF-8 with BOM, formula-safe) and JSON export of sessions, traffic and connection events.
- Statistics tab with summary, history graphs (traffic, latency, availability over 1 hour to 30 days), export and "Open data folder".

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
