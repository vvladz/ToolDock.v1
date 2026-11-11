# ToolDock

ToolDock installs Windows tool packages from GitHub Releases or direct HTTPS URLs, keeps them up to date, and supervises background applications in the current user session. Managed applications require no ToolDock-specific integration.

ToolDock works without administrator privileges, inbound connections, SSH, or Windows Services. It installs three executables:

- `ToolDock.Starter.exe`: a long-running, windowless process supervisor;
- `ToolDock.Updater.exe`: a windowless, one-shot updater launched by Task Scheduler;
- `ToolDock.Client.exe`: the interactive console client exposed through the `tdctl` shim.

## Requirements

- Windows 10/11 x64;
- public GitHub Releases for ToolDock; managed packages may also use direct public HTTPS downloads;
- [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0);
- Windows PowerShell 5.1 or PowerShell 7 (`pwsh`) on Windows;
- an interactive sign-in for the current user.

## Installation

Choose the HTTPS URL of your package catalog, then install the latest ToolDock release. Replace `https://example.org/tools.json` with your URL in every example:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/vvladz/ToolDock/master/install.ps1'))) `
    -Repository 'vvladz/ToolDock' `
    -CatalogUrl 'https://example.org/tools.json'
```

To inspect the script first:

```powershell
Invoke-WebRequest 'https://raw.githubusercontent.com/vvladz/ToolDock/master/install.ps1' -OutFile install.ps1
Get-Help .\install.ps1 -Full
.\install.ps1 -Repository 'vvladz/ToolDock' -CatalogUrl 'https://example.org/tools.json'
```

The script can also be launched explicitly with `pwsh -NoProfile -File .\install.ps1 ...` or `powershell.exe -NoProfile -File .\install.ps1 ...` using the same parameters.

| Parameter | Required / default | Meaning |
|---|---|---|
| `-Repository` | Required on first install | Public ToolDock release repository, in `owner/repository` form; saved for updates |
| `-CatalogUrl` | Required on first install | Absolute HTTPS URL of your JSON package catalog; saved for updates |
| `-Version` | Latest release | Exact ToolDock release tag; does not pin managed packages |
| `-InstallRoot` | `~/.tooldock` | Installation directory; installed `update.ps1` uses its own directory |
| `-UpdateIntervalMinutes` | Saved value, or `1440` (24 hours) on first install | Package check interval, from 1 to 1,440 minutes |

The installer:

- places ToolDock in `~/.tooldock`;
- puts `~/.tooldock/bin` first in the user `PATH`;
- creates the `ToolDock Starter` and `ToolDock Updater` scheduled tasks;
- starts the supervisor and performs the first update;
- verifies the release archive against its published SHA-256 file;
- generates `update.ps1` in `<InstallRoot>` for future ToolDock updates.

Run the installed launcher to update ToolDock itself. It downloads the current `install.ps1` from the configured GitHub repository and runs it with the repository, catalog URL, and interval saved in `config.json`, targeting its own installation directory:

```powershell
& "$HOME\.tooldock\update.ps1"
& "$HOME\.tooldock\update.ps1" -Version v1.2.3 # Select a specific release
```

For a custom root, run its `update.ps1` instead. You can also rerun `install.ps1` with the same root to recover an interrupted installation.

For an installation with the older `update.ps1` that uses a local `install.ps1`, run the current installer once from GitHub using the same installation root. It will replace the old launcher with the generated one. The saved settings in `config.json` supply the repository, catalog URL, and interval:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/vvladz/ToolDock/HEAD/install.ps1'))) `
    -InstallRoot "$HOME\.tooldock"
```

For a custom directory and a 15-minute interval:

```powershell
.\install.ps1 -Repository 'vvladz/ToolDock' `
    -CatalogUrl 'https://example.org/tools.json' `
    -InstallRoot 'D:\My Tools\ToolDock' -UpdateIntervalMinutes 15
```

The executables discover `config.json` beside their `bin` directory, including in a new terminal or sign-in session. `TOOLDOCK_HOME` takes precedence over this discovery. Separate roots have separate runtime pipes and mutexes, but the installer registers a single pair of scheduled tasks for the user. Installing to another root replaces those task registrations; it does not migrate existing packages, variables, or secrets. Use the same root for upgrades.

Self-installation prepares a complete `bin.next`, preserves command shims, stops ToolDock, and switches directories through `bin.previous`. If interrupted, rerun the installer to finish the installation. Existing notification settings are preserved.

### First use

Open a new terminal after installation so the updated `PATH` is available:

```powershell
tdctl --help
tdctl commands                 # Installed, enabled command names
tdctl list                     # Catalog daemon names; requires Starter
tdctl variable status          # Missing user variables referenced by the catalog
tdctl secret status            # Missing secrets referenced by the catalog
tdctl update                   # Check now and retry any pending activation
```

Set any required values before starting the affected command or daemon. For the FarShell command in the catalog below:

```powershell
tdctl secret set openai.api-key # Prompts with masked input
tdctl exec farshell -- --help   # Arguments after -- belong to FarShell
farshell --help                 # Equivalent generated command shim
tdctl status farshell           # Daemon status, not command status
```

An empty hosted catalog, `{ "tools": {} }`, is valid for installing ToolDock before adding packages. If initial autostart fails because a value is missing, set the value and run `tdctl update` again.

### Local configuration

The installer writes `<InstallRoot>\config.json`; [config.example.json](docs/config.example.json) shows the minimal format.

| Setting | Default | Meaning |
|---|---|---|
| `catalogUrl` | Required | HTTPS package catalog URL |
| `repository` | Written by installer | ToolDock release repository used by `update.ps1` |
| `updateIntervalMinutes` | Written by installer | Scheduled package check interval reused by `update.ps1` |
| `notificationCommand` | Omitted | Installed catalog command that receives events |
| `notificationTimeoutSeconds` | `10` | Handler timeout, from 1 to 60 seconds |

Edit this file to change the catalog or notification settings. They are read on each update or notification delivery. To change the schedule, rerun the installer with `-UpdateIntervalMinutes` or edit `updateIntervalMinutes` and run `update.ps1`.

New installations check all enabled packages every 24 hours, after the installer's initial update. Signing in starts the supervisor without an extra package check; Task Scheduler is configured to catch up on missed scheduled checks when available. `tdctl update` always checks immediately, regardless of the scheduled interval, and does not reset the schedule.

Upgrades preserve the saved interval. To switch an existing installation to daily checks when updating ToolDock:

```powershell
& "$HOME\.tooldock\update.ps1" -UpdateIntervalMinutes 1440
```

## Package catalog

A catalog entry describes one package and its command and daemon entry points. [tools.example.json](docs/tools.example.json) contains GitHub, URL ZIP, and single-file examples. Except for the ngrok download URL, repository names, URLs, asset names, executable paths, and service addresses are placeholders; replace them with your package's actual layout.

Package names identify installations. Command names identify terminal entry points. Daemon names identify supervised background processes. A command and a daemon can share a name:

```json
{
  "variables": {
    "farshell.server": "127.0.0.1:7777"
  },
  "tools": {
    "farshell": {
      "repo": "owner/farshell",
      "asset": "farshell-win-x64.zip",
      "enabled": true,
      "commands": {
        "farshell": {
          "executable": "farshell.exe",
          "environment": {
            "FARSHELL_SERVER": {
              "variable": "farshell.server"
            },
            "OPENAI_API_KEY": {
              "secret": "openai.api-key"
            }
          }
        }
      },
      "daemons": {
        "farshell": {
          "executable": "farshelld.exe",
          "arguments": [],
          "autostart": true,
          "restartOnUpdate": true,
          "environment": {
            "LOG_LEVEL": "info"
          }
        }
      }
    }
  }
}
```

One package may contain CLI commands, background daemons, or both. At least one command or daemon is required; an unused `commands` or `daemons` section may be omitted. The same executable may serve both roles; fixed arguments can select each mode:

```json
"commands": {
  "example": {
    "executable": "example.exe",
    "arguments": ["client"]
  }
},
"daemons": {
  "example": {
    "executable": "example.exe",
    "arguments": ["serve"],
    "autostart": true,
    "restartOnUpdate": true
  }
}
```

Fields:

| Field | Meaning |
|---|---|
| `variables` | Optional catalog-wide plaintext variables that override user variables with the same name |
| `repo` | GitHub repository in `owner/name` form; use with `asset`, mutually exclusive with `url` |
| `asset` | Exact ZIP asset name in the latest GitHub release |
| `url` | Alternative package source: absolute HTTPS download URL, without user info or a fragment |
| `format` | URL sources only: `zip` (default) or `file` for a standalone binary |
| `fileName` | Required for `format: "file"`: a plain installed filename, such as `helper.exe`; not a path |
| `enabled` | Whether ToolDock may update and start the package; defaults to `true` |
| `commands` | Optional globally unique command names mapped to command definitions |
| `daemons` | Optional globally unique supervised-daemon names and their launch settings |
| `executable` | Relative executable path inside the installed package; for single files, use `fileName` |
| `arguments` | Optional fixed command arguments (before invocation arguments) or daemon arguments; defaults to an empty list |
| `autostart` | Start the daemon after sign-in or its first installation; defaults to `false` |
| `restartOnUpdate` | Restart the daemon after its package changes, if it is currently running; defaults to `false` |
| `environment` | Optional process environment made from literals, variables, and secret references |

Each package must use either `repo` and `asset`, or `url`. For example, add this entry to `tools` to install ngrok from its standalone Windows ZIP:

```json
"ngrok": {
  "url": "https://bin.ngrok.com/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip",
  "commands": {
    "ngrok": { "executable": "ngrok.exe" }
  }
}
```

For an unarchived binary, set `format` to `file` and `fileName` to its installed name:

```json
"helper": {
  "url": "https://example.org/download/helper.exe",
  "format": "file",
  "fileName": "helper.exe",
  "commands": {
    "helper": { "executable": "helper.exe" }
  }
}
```

URL packages are downloaded on every scheduled or manual check. The SHA-256 of the downloaded bytes is their version, displayed as `sha256-<hash>`; no release tag or manually maintained version is needed. Identical bytes do not reinstall the package or restart its daemons. Changed bytes go through normal staging and activation. ZIP metadata changes also change the hash. This content hash detects changes; it is not a publisher-provided checksum. URL downloads currently have no conditional HTTP caching, authentication, local-file, or directory-source support.

Versions are installed side by side and exposed through a `current` directory junction:

```text
~/.tooldock/tools/farshell/
├── v1.0.0--<source-id>\
├── v1.1.0--<source-id>\
└── current\        directory junction → v1.1.0--<source-id>
```

Each command gets a shim in `~/.tooldock/bin`. The shim delegates execution to `ToolDock.Client.exe`, which resolves the installed executable and its environment, preserves the calling terminal streams, and returns the child exit code. Daemons are started from the exact installed-version directory recorded in ToolDock state.

Names are case-insensitive and must be unique within each namespace. ToolDock reserves the command names `tdctl`, `ToolDock.Client`, `ToolDock.Starter`, `ToolDock.Updater`, `starter`, and `updater`.

Removing or disabling a package removes its generated command shims on the next successful catalog refresh while preserving installed files and stored values. It does not stop an already running daemon; use `tdctl stop <daemon>` when that is intended. User-created or modified shims are preserved, and a conflicting command file causes package activation to fail. Changing a package's repository or asset installs the new source even if the release tag stays the same. Replacing an asset in place under the same repository, tag, and asset name is not detected; publish a new release tag for updates.

For URL packages, changing the URL, format, or installed filename also installs a distinct source, even when the downloaded bytes are identical. Existing GitHub catalogs and installed state remain supported.

Updates publish the installed version and its command/daemon definitions together. A failed download or extraction keeps the previous definitions usable. An interrupted activation is repaired on the next update. Failed Starter reconciliation remains pending and is retried without repeating an already applied restart.

After successful activation, ToolDock keeps the three most recently installed managed versions, plus any older active or running version. Busy versions and failed cleanup are retried on subsequent successful checks. Cleanup skips `current`, staging directories, reparse points, and legacy directories without a ToolDock version marker. Commands and daemons retain a lease on their version for their lifetime.

## Variables and secrets

Environment entries support three forms:

```json
{
  "environment": {
    "LOG_LEVEL": "info",
    "SERVICE_URL": {
      "variable": "service.url"
    },
    "ACCESS_TOKEN": {
      "secret": "service.token"
    }
  }
}
```

- literals live directly in process definitions in the public catalog;
- variables are catalog-wide plaintext values or user-scoped plaintext values in `~/.tooldock/variables.json`;
- secrets are user-scoped named values protected with Windows DPAPI `CurrentUser` in `~/.tooldock/secrets/secrets.dat`.

`{ "variable": "name" }` first resolves `name` from the catalog's top-level `variables` object, then falls back to the current user's variable store. A catalog value therefore overrides a user value with the same name. Variables and secrets are shared by all commands and daemons. They are not added to the global Windows environment; ToolDock injects them only into configured child processes. `tdctl variable status` describes the user store, so a user variable shadowed by the catalog is reported as unused.

Manage variables with:

```powershell
tdctl variable set service.url https://service.example
tdctl variable get service.url
tdctl variable list
tdctl variable status
tdctl variable remove service.url
```

Manage secrets with:

```powershell
tdctl secret set service.token
tdctl secret list
tdctl secret status
tdctl secret remove service.token
```

Secret input is masked and is never accepted as a command-line value. `tdctl secret set <name> --stdin` reads one line for controlled automation. There is no normal secret `get` operation. A missing variable or secret prevents process creation. Changes take effect the next time a command starts or a daemon is started or restarted.

## Control client

`tdctl help <command>` and `tdctl <command> --help` show details without requiring a running Starter or a configuration file.

| Command | Purpose |
|---|---|
| `tdctl commands` | List installed, enabled command names, sorted one per line; empty output when none are installed |
| `tdctl list` | Ask Starter for catalog daemon names, including disabled packages; response begins with `OK` |
| `tdctl start\|stop\|restart\|status <daemon>` | Control or inspect one daemon through Starter |
| `tdctl exec <command> -- [arguments...]` | Run a command in the calling directory with its configured environment and terminal streams |
| `tdctl update` | Update enabled managed packages now; rerun the installer to update ToolDock itself |
| `tdctl variable ...` / `tdctl secret ...` | Manage values for future process launches |
| `tdctl logs <target> [--lines <count>] [--follow]` | Read recent logs or wait for new lines |

```powershell
tdctl help exec
tdctl commands
tdctl list
tdctl status farshell
tdctl start farshell
tdctl restart farshell
tdctl stop farshell
tdctl update
tdctl variable status
tdctl secret status
```

Process commands are sent to `ToolDock.Starter.exe` over a current-user-only Named Pipe. `tdctl update` runs the same update coordinator as the scheduled updater. A cross-process named mutex prevents both update hosts from running concurrently.

`exec` requires the `--` separator even when there are no child arguments. It does not require Starter; a package activation during `update` does require Starter to acknowledge reconciliation. A successful status query returns exit code `0` even when the daemon is stopped.

Client exit codes are `0` for success, `1` for an operation failure, `2` for invalid usage, and `130` for Ctrl+C cancellation of `update` or `logs --follow`. `exec` returns the child exit code unchanged. `tdctl update` returns `1` when another update owns the lock; the scheduled updater treats that overlap as a successful no-op. Inspect `$LASTEXITCODE` in PowerShell scripts.

After a package update, the update engine notifies the starter. The starter alone owns process and Job Object handles, and restarts the running daemons from that package that have `restartOnUpdate` enabled. A stopped daemon remains stopped; an autostart daemon is started after its first installation.

## Logs

Logs are stored in `~/.tooldock/logs`:

- `ToolDock.Starter.log`: supervisor and Named Pipe diagnostics;
- `ToolDock.Updater.log`: scheduled and interactive update diagnostics;
- `ToolDock.Client.log`: managed-command execution and environment diagnostics;
- `<daemon>.log`: captured stdout, stderr, and lifecycle events.

View them through the client:

```powershell
tdctl logs ToolDock.Updater
tdctl logs ToolDock.Client --lines 50
tdctl logs farshell --lines 200
tdctl logs farshell --lines 0 --follow
```

The default is the last 100 lines. `--follow` (or `-f`) also waits for a log that does not exist yet. Press Ctrl+C to stop following.

Daemon diagnostics use `Microsoft.Extensions.Logging`. Scheduled updates write to `ToolDock.Updater.log`; `tdctl update` writes the same operation to `ToolDock.Updater.log` and the terminal. Process launches log every catalog-configured environment entry. Literals use `ENV_NAME: value`, variables use `ENV_NAME: variable.name -> value`, and secrets use `ENV_NAME: secret.name -> *******`. Inherited environment entries are not logged.

Logs rotate at 10 MB with five archives retained.

Concurrent commands share the client log safely, including rotation. Each update that acquires the update mutex writes one start and one final outcome with status and counts. Daemon logs record the main process's exit promptly, once per process, including its PID and exit code.

## Notifications

An optional catalog command can receive plain-text events. Add these settings to `config.json` alongside `catalogUrl`:

```json
{
  "catalogUrl": "https://example.org/tools.json",
  "notificationCommand": "tdlog",
  "notificationTimeoutSeconds": 10
}
```

The active catalog must expose `tdlog` as a regular package command. ToolDock resolves its installed executable directly, applies its configured environment, passes one event-type argument, and writes the complete UTF-8 message without a BOM to stdin. The command's normal shim remains available to other tools. Omit `notificationCommand` to disable delivery. The timeout defaults to 10 seconds and supports 1–60 seconds.

| Event | When it is sent |
|---|---|
| `daemon-crash` | An unexpected main-process exit, including exit code 0; contains daemon name, PID, exit code, and the latest nonempty stderr line, limited to 2,048 characters |
| `tool-update-success` | A version change after installation, state persistence, and Starter reconciliation succeed; contains package and old/new versions |
| `tool-update-failure` | A package or run-level failure; contains a concise error and at most 20 lines / 4 KiB of logs from the current attempt |

Requested stops, restarts, unchanged versions, and disabled packages do not generate success or crash events. Identical consecutive update failures are suppressed across scheduled checks until a successful check or a changed failure. Handler failures and timeouts are logged locally and do not alter update or daemon outcomes. Notification delivery does not enable automatic daemon restart.

See [Notification setup and handler contract](docs/notifications.md) for a complete catalog example, manual test, and delivery limitations.

## Troubleshooting and recovery

| Symptom | Action |
|---|---|
| `tdctl` is not recognized | Open a new terminal, or run `& "$HOME\.tooldock\bin\ToolDock.Client.exe" --help` (use your custom root if applicable) |
| `Starter is unavailable` | Check `Get-ScheduledTask -TaskName 'ToolDock Starter'`, run `Start-ScheduledTask -TaskName 'ToolDock Starter'`, then inspect `tdctl logs ToolDock.Starter` |
| Commands use another installation | Check `$env:TOOLDOCK_HOME` and `Get-Command tdctl`; the environment override wins over executable location |
| A variable or secret is missing | Use `tdctl variable status` / `tdctl secret status`, set the missing value, and retry the command or daemon start |
| `update` reports failed reconciliation | Inspect the updater and daemon logs, fix the launch problem, then rerun `tdctl update`; pending work is retained |
| `Update is already running` | Let the active scheduled or interactive update finish, then retry |
| A daemon exited | Inspect `tdctl logs <daemon>` and explicitly `tdctl start <daemon>` after fixing the cause; there is no automatic restart on exit |
| A user command conflicts with a shim | Rename or move your conflicting `.cmd` file if you want ToolDock to own that command name, then rerun `tdctl update` |
| Self-installation was interrupted | Rerun `install.ps1` with the same root and catalog parameters; it prepares and switches a complete `bin` directory |

Ordinary update recovery is forward completion: retry `tdctl update` rather than editing `state\installed.json`, deleting version directories, or switching `current` manually. An installation can be committed while daemon reconciliation is still pending; the error and retry state make that visible. There is no automatic rollback command.

## Build and release

Local builds require the .NET 10 SDK:

```powershell
dotnet build ToolDock.sln -c Release
dotnet run --project tests/ToolDock.IntegrationTests -c Release --no-build
```

See [Local validation](docs/testing.md) for both PowerShell installer checks, published-executable tests, and the full supervision smoke test. Tests use temporary installation roots and isolated pipes/mutexes.

See [ToolDock.md](docs/ToolDock.md) for the detailed architecture and invariants.
