# Configuration

All operational values are configurable; the application contains no environment-specific hardcoded values. Defaults exist
only for convenience. The SSH target has **no** default — it must be supplied by the user.

Most settings are edited in **Settings** inside the application. The file can also be edited by hand while constantproxy is
not running (property names are matched case-insensitively; `//` comments and trailing commas are accepted).

## Location

`%LOCALAPPDATA%\constantproxy\config.json` — or, if the environment variable `CONSTANTPROXY_DATA_DIR` is set, `config.json`
inside that folder. The file is written atomically (temporary file + rename).

## Robustness

- **Unreadable or invalid JSON** never crashes the program. The file is renamed to `config.json.corrupt-<timestamp>`, defaults
  are used and you are told where the old file went.
- **Missing values** get their defaults; null sections are repaired; duplicate profile ids are made unique.
- **Invalid values** (for example port `99999`) are loaded as written but the profile cannot connect until fixed; the settings
  dialog points at the field.
- **Older files** (no `schemaVersion`, or an older one) are upgraded by `ConfigMigrator`; the previous file is kept as
  `config.json.v<old>.bak` and the upgraded file is written back.
- **Newer files** (a later `schemaVersion`) are loaded as far as understood; the original is kept as `config.json.v<N>.bak`
  and you are warned that unknown settings are dropped on the next save.

## Example

```json
{
  "schemaVersion": 1,
  "activeProfileId": "0c6a5b2e-6a8e-4c0e-9d7e-2f2c1d7a1111",
  "profiles": [
    {
      "id": "0c6a5b2e-6a8e-4c0e-9d7e-2f2c1d7a1111",
      "name": "Home server",
      "host": "my-tunnel",
      "bindAddress": "127.0.0.1",
      "port": 10080,
      "sshExecutable": "",
      "ipv4Only": true,
      "serverAliveInterval": 30,
      "serverAliveCountMax": 3,
      "exitOnForwardFailure": true,
      "batchMode": false,
      "additionalArguments": [],
      "startupTimeoutSeconds": 15,
      "reconnect": {
        "enabled": true,
        "delaysSeconds": [0, 1, 2, 5, 10, 15],
        "maxDelaySeconds": 15,
        "healthyResetSeconds": 30,
        "jitterPercent": 0
      },
      "monitoring": {
        "enabled": true,
        "intervalSeconds": 10,
        "timeoutSeconds": 5,
        "targetHost": "example.com",
        "targetPort": 443,
        "failureThreshold": 3,
        "reconnectOnFailure": false,
        "reconnectAfterFailures": 6
      },
      "trafficMode": "bridge"
    }
  ],
  "logVerbosity": "normal",
  "analytics": { "storeHistory": true, "retentionDays": 90, "databasePath": "" },
  "interface": {
    "language": "auto",
    "startMinimized": false,
    "closeToTray": true,
    "minimizeToTray": true,
    "startWithWindows": false,
    "connectOnLaunch": false
  },
  "notifications": { "notifyOnFailure": true, "notifyOnRecovery": true, "minimumOutageSeconds": 10 }
}
```

## Reference

### Profile

| Field | Default | Meaning and validation |
| --- | --- | --- |
| `name` | `Default` | Unique (case-insensitive), 1–64 characters, no control characters. |
| `host` | *(empty)* | OpenSSH host alias or host name; required to connect; no whitespace, must not start with `-`. |
| `bindAddress` | `127.0.0.1` | Valid IP address. A non-loopback address is allowed but produces a warning (other hosts may reach the proxy). |
| `port` | `10080` | 1–65535. |
| `sshExecutable` | *(empty)* | Empty = automatic detection (`PATH`, then `%SystemRoot%\System32\OpenSSH`). An explicit path must exist. |
| `ipv4Only` | `true` | Adds `-4`. |
| `serverAliveInterval` / `serverAliveCountMax` | `30` / `3` | Positive integers; passed as `-o ServerAliveInterval=…`, `-o ServerAliveCountMax=…`. |
| `exitOnForwardFailure` | `true` | Adds `-o ExitOnForwardFailure=yes`. |
| `batchMode` | `false` | Adds `-o BatchMode=yes` (never prompt). |
| `additionalArguments` | `[]` | Extra ssh arguments, one process argument per entry, inserted before the target. |
| `startupTimeoutSeconds` | `15` | How long to wait for the SOCKS listener; ≥ 1. |
| `trafficMode` | `bridge` | `bridge` counts proxied bytes through a local relay; `off` runs ssh on the configured port directly (traffic shown as unavailable). |

With `trafficMode: "bridge"` ssh listens on a loopback port chosen per attempt and constantproxy listens on `port`. The generated
command is shown in *Settings → Advanced* and *Diagnostics*.

### Reconnect (per profile)

| Field | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Reconnect automatically after an unexpected exit. |
| `delaysSeconds` | `[0,1,2,5,10,15]` | Wait before the Nth consecutive retry; the last value repeats. Non-negative, the last value ≥ 1 (prevents a respawn storm). |
| `maxDelaySeconds` | `15` | Upper bound for every delay; ≥ 1. |
| `healthyResetSeconds` | `30` | A connection that stayed up this long resets the counter; ≥ 1. |
| `jitterPercent` | `0` | Random spread applied to non-zero delays; 0–100. |

### Monitoring (per profile)

| Field | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Health checks through the proxy. |
| `targetHost` / `targetPort` | `example.com` / `443` | Destination of the SOCKS `CONNECT` probe; choose something you trust to be reachable. |
| `intervalSeconds` / `timeoutSeconds` | `10` / `5` | Check cadence and per-check timeout; ≥ 1. |
| `failureThreshold` | `3` | Consecutive failures before `Degraded`; ≥ 1. |
| `reconnectOnFailure` | `false` | Restart ssh when failures persist. |
| `reconnectAfterFailures` | `6` | Failures at which that restart happens; must be ≥ `failureThreshold`. |

### Application

| Field | Default | Meaning |
| --- | --- | --- |
| `logVerbosity` | `normal` | `normal` or `verbose` (adds debug output such as ssh's stdout/stderr lines). |
| `analytics.storeHistory` | `true` | Keep history; off disables every database write. |
| `analytics.retentionDays` | `90` | `30`, `90`, `180`, `365` or `0` (forever). Raw health checks are always pruned after 7 days. |
| `analytics.databasePath` | *(empty)* | Empty = `constantproxy.db` in the data folder. Applies after a restart. |
| `interface.language` | `auto` | `auto` (Windows UI language: Russian if Russian, otherwise English), `en`, `ru`. Applies immediately. |
| `interface.startMinimized` | `false` | Start hidden in the notification area. |
| `interface.closeToTray` / `minimizeToTray` | `true` / `true` | Closing / minimizing hides the window instead of exiting. Exit stays available in the tray menu. |
| `interface.startWithWindows` | `false` | Per-user `Run` registry entry (`"…\constantproxy.exe" --startup`). |
| `interface.connectOnLaunch` | `false` | Connect the active profile when the app starts. Independent of `startWithWindows`. |
| `notifications.notifyOnFailure` / `notifyOnRecovery` | `true` / `true` | Which notifications to show. |
| `notifications.minimumOutageSeconds` | `10` | An outage must last this long (0–86400) before the user is told. |

## Database schema versions

The SQLite database carries its own version (`PRAGMA user_version`). Upgrades run automatically at start-up, each step in a
transaction, after a backup (`constantproxy.db.pre-v<N>.bak`). A database written by a newer version is refused (history is
unavailable) rather than modified. A damaged database is renamed to `constantproxy.db.corrupt-<timestamp>` and replaced.
