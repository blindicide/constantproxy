# Releases

constantproxy follows [Semantic Versioning](https://semver.org/). Public releases are built by GitHub Actions from version
tags; a manually compiled executable is never a release artifact.

## Artifacts

| File | Content |
| --- | --- |
| `constantproxy-<version>-win-x64.zip` | the self-contained single-file `constantproxy.exe` plus its native runtime components, `README.md`, `LICENSE`, `CHANGELOG.md` |
| `constantproxy-<version>-win-x64.exe` | the same executable on its own (it extracts native components to a temporary folder on first start) |
| `SHA256SUMS.txt` | SHA-256 of both files, in `sha256sum` format |

The ZIP is the canonical download; use it if the standalone exe misbehaves. The executable is **not code-signed**, so Windows
SmartScreen may warn on first launch. Verify a download:

```powershell
(Get-FileHash .\constantproxy-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLower()
# compare with the matching line in SHA256SUMS.txt
```

On Linux/macOS: `sha256sum -c SHA256SUMS.txt`.

## Making a release

1. Make sure `main` is green (`build.yml`) and the tests pass locally.
2. Set `<Version>` in `Directory.Build.props` to the new version.
3. Move the entries of the changelog into a new `## [X.Y.Z] - YYYY-MM-DD` section of `CHANGELOG.md`
   (**Added / Changed / Fixed / Removed**, written for users — not a commit dump).
4. Commit (`chore: release X.Y.Z`) and create an annotated tag: `git tag -a vX.Y.Z -m "vX.Y.Z"`.
5. Push the commit and the tag. `release.yml` then, on a Windows runner: verifies that the tag equals the version, restores,
   builds in Release, runs all tests, publishes `win-x64` (self-contained, single file), smoke-tests the executable
   (`--version` must print the tagged version), packages the ZIP, writes `SHA256SUMS.txt`, extracts the release notes from the
   changelog and creates the GitHub Release (marked *pre-release* for versions below 1.0.0).
6. Download the artifacts on a real Windows machine and run through the manual checklist below.

## Manual verification checklist (real Windows machine)

Automated tests cannot see the desktop, so before announcing a release:

- First launch shows the setup window; Connect with a real host works; the main window shows Connected, timer, latency, traffic.
- Kill the `ssh.exe` child in Task Manager → state goes Reconnecting → Connected again; the outage appears in Statistics.
- Disconnect during the retry delay is immediate; Exit leaves no `ssh.exe` that constantproxy started.
- Tray menu items follow the state; closing/minimizing hides to the tray; a second launch focuses the running instance.
- Start with Windows adds/removes the Run entry; "connect on launch" is independent.
- A 10-second-plus outage produces one "lost" and one "restored" notification; short blips produce none.
- Switch the language (English ⇄ Русский) — everything changes immediately; check dialogs and the tray menu.
- High-DPI and light/dark Windows themes remain readable; keyboard-only operation works (Tab order, access keys).
- Upgrading from the previous release keeps settings and history.

## Version history

| Version | Theme |
| --- | --- |
| 0.1.0 | Foundation: state machine, supervision, reconnect back-off, configuration, WPF skeleton |
| 0.2.0 | Monitoring: listener verification, SOCKS health checks, Degraded state, port conflicts, failure classification |
| 0.3.0 | Traffic telemetry: replaceable monitor, byte-counting bridge, rates, totals, peaks, graph |
| 0.4.0 | Historical analytics: SQLite, migrations, uptime, retention, export |
| 0.5.0 | Desktop integration: tray, startup, notifications, single instance, graceful shutdown |
| 0.6.0 | Localization: complete English and Russian |
| 0.7.0 | Profiles: multiple profiles, selector, clone/rename/delete, per-profile analytics |
| 0.8.0 | UX polish: settings dialog, diagnostics, command preview, first-run, About |
| 0.9.0 | Release candidate: configuration migration, crash handling, orphan cleanup, packaging, CI/CD, documentation |
| 1.0.0 | Stable |

## After 1.0

Explicitly out of scope for 1.0 and not to be added casually: simultaneous profiles, HTTP CONNECT mode, jump-host editor,
portable mode, ARM64, PAC generation, browser/system proxy switching, advanced ETW diagnostics, remote management, plugins.
