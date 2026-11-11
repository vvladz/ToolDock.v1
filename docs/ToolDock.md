# ToolDock architecture

ToolDock is a user-scoped Windows package updater and process supervisor. It installs GitHub release packages and direct HTTPS ZIPs or binaries, exposes their CLI commands, and runs selected background applications in the current interactive user session.

## Invariants

- Managed packages remain ordinary standalone ZIP archives and executables.
- Managed packages consume ordinary environment variables and do not know about ToolDock value storage or DPAPI.
- Source polling and installation stay outside managed tools.
- Only the starter owns supervised daemon processes and their Job Objects. The client owns interactive command launches; notification delivery owns its temporary handler process.
- Only one update operation per installation root may run in the user session at a time.
- ToolDock requires no administrator privileges, Windows Service, SSH, or inbound connection.
- Scheduled components never create console windows.
- CLI output and persistent diagnostic logging remain separate concerns.

## Executables

```text
ToolDock.Starter.exe   WinExe   long-running process supervisor
ToolDock.Updater.exe   WinExe   scheduled one-shot update host
ToolDock.Client.exe    Exe      interactive control host
tdctl.cmd                       short client shim
```

`ToolDock.Starter.exe` and `ToolDock.Updater.exe` are Windows-subsystem applications. Task Scheduler can launch them without creating or hiding a console window. They always write diagnostics to files.

`ToolDock.Client.exe` is a console-subsystem application. It writes results to stdout, errors to stderr, and never needs to attach to a parent console manually.

The three executables are framework-dependent single-file publications for `win-x64`. ToolDock assemblies and logging dependencies are bundled into each executable, while the shared .NET 10 runtime remains an installation prerequisite.

## Component boundaries

```text
                      GitHub Releases / HTTPS URLs
                                  │
                                  ▼
                    ┌─────────────────────────┐
Task Scheduler ────►│ ToolDock.Updater.exe    │
                    │                         │
tdctl update ──────►│ UpdateCoordinator       │
                    │ UpdateEngine            │
                    └────────────┬────────────┘
                                 │ install package
                                 │ switch junction
                                 │ save state
                                 ▼
                           Package storage
                                 │
                                 │ package-updated
                                 ▼
tdctl start/status ──pipe──► ToolDock.Starter.exe
                                 │
                     ┌───────────┼───────────┐
                     ▼           ▼           ▼
                   Job[a]      Job[b]      Job[c]

managed command shim ──────► ToolDock.Client.exe exec
                                  │
                                  ▼
                           command process
```

### Starter

The starter is stateful and exclusive. It:

- loads daemon definitions and version roots from the same installed-state snapshot;
- starts configured autostart daemons;
- owns the process and Job Object handles;
- starts, stops, restarts, and reports daemon status;
- captures stdout and stderr;
- accepts local commands through `ToolDock.Starter.v1`;
- reconciles running daemons after a package update.

The starter does not download, install, or version packages. Its singleton mutex is:

```text
Local\ToolDock.Starter
```

### Update coordinator and engine

`ToolDock.Updating` contains the shared update implementation. Both update hosts call the same synchronous coordinator entry point:

```text
UpdateCoordinator.Run(cancellationToken)
    acquire Local\ToolDock.Update
    run asynchronous UpdateEngine work
    release mutex on the acquiring thread
```

The synchronous boundary is intentional because a Windows mutex must be released by the thread that acquired it. The coordinator returns one of:

```text
Completed
CompletedWithErrors
AlreadyRunning
Cancelled
Failed
```

The engine:

1. downloads and validates the catalog;
2. resolves the latest GitHub release, or downloads and hashes the HTTPS source, for every enabled package;
3. extracts a changed ZIP or copies a standalone binary into a staging directory;
4. validates every declared executable;
5. moves the staged version into its final directory;
6. atomically publishes installed paths, active definitions, catalog variable snapshots, and pending reconciliation in `installed.json`;
7. repairs the derived catalog cache, `current` junctions, and generated command shims;
8. asks the starter to reconcile the updated package with a stable activation identifier;
9. persists reconciliation completion, emits a version-change event, and prunes unused older versions.

Before contacting the network, every run repairs derived files from committed state. Failed downloads and extraction never publish new definitions. Readers use the atomic installed-state snapshot; legacy state falls back to `catalog.json`. Source identity is stored with each installation and contributes to the version directory name. For GitHub it is derived from repository and asset, retaining compatibility with older state and version markers. For direct downloads it includes the URL, format, and installed filename. Junction creation uses the Windows reparse-point API directly, without a command shell.

URL versions use the full SHA-256 of the downloaded file. Every check downloads the source once, including when the content is unchanged; the same temporary file is used for hashing and staging. Identical content and source identity retain the existing activation. Failures remove temporary files and leave the committed package usable.

The installer defaults to a 1,440-minute repeating time trigger for all packages. It performs the initial update itself and schedules the next check one full interval later. The updater has no additional logon trigger. Existing saved intervals and explicit overrides are preserved. Manual `tdctl update` bypasses scheduling and checks immediately.

The scheduled updater and interactive client differ only at the host boundary:

| Host | Subsystem | Cancellation | Log providers |
|---|---|---|---|
| `ToolDock.Updater.exe` | WinExe | process lifetime | rotating `ToolDock.Updater.log` |
| `tdctl update` | Exe | Ctrl+C | rotating `ToolDock.Updater.log` and terminal |

### Client

The client provides:

- local help and installed-command discovery (`commands`), without a Starter connection;
- process control goes through the starter Named Pipe;
- `update` directly runs the shared update coordinator;
- `exec` resolves and runs a catalog command with its configured environment;
- `variable` and `secret` manage user-scoped values;
- `logs` reads rotating files with read/write/delete sharing and optionally follows them.

The client does not own supervised daemon processes. Interactive command processes remain attached to it, inherit its terminal streams, and return their exit code. Command execution diagnostics are written to `ToolDock.Client.log` without changing child stdout or stderr.

## Catalog model

The optional top-level `variables` object contains public catalog-wide variable overrides. The `tools` object maps package names to installation definitions:

```json
{
  "variables": {
    "service.server": "https://service.example"
  },
  "tools": {
    "package-name": {
      "repo": "owner/repository",
      "asset": "package-win-x64.zip",
      "enabled": true,
      "commands": {
        "command-name": {
          "executable": "relative/path/command.exe",
          "arguments": ["client"],
          "environment": {
            "MODE": "interactive",
            "SERVER": { "variable": "service.server" },
            "TOKEN": { "secret": "service.token" }
          }
        }
      },
      "daemons": {
        "daemon-name": {
          "executable": "relative/path/daemon.exe",
          "arguments": ["serve"],
          "autostart": true,
          "restartOnUpdate": true,
          "environment": {
            "MODE": "daemon",
            "TOKEN": { "secret": "service.token" }
          }
        }
      }
    }
  }
}
```

Each package must specify either `repo` and `asset`, or an absolute HTTPS `url`. URL packages default to ZIP; `format: "file"` requires a plain `fileName` for the downloaded binary. `format` and `fileName` apply only to URL sources. Local files and authenticated sources are not supported.

Each package must define at least one command or daemon. Either section may be omitted when unused. Package names, command names, and daemon names are case-insensitively unique within their respective namespaces. Command and daemon executable paths must remain under the installed package directory. Environment names use the conventional Windows identifier form and are case-insensitively unique within one process definition.

Command `arguments` are optional and precede arguments passed through `tdctl exec` or a generated shim. Both command and daemon arguments default to an empty list; each array entry is passed as one argument, including empty strings.

An environment value is exactly one of:

```text
literal string                 public value stored in the catalog
{ "variable": "name" }       user-scoped plaintext value reference
{ "secret": "name" }         user-scoped DPAPI-protected value reference
```

Variable references resolve the catalog's top-level `variables` object first, then the separate current-user variable store. Catalog variables therefore override user variables with the same name. Secrets use their own current-user store. All values are resolved immediately before process creation, and a missing reference fails the launch before any child process is created.

## Installation layout

```text
~/.tooldock/
├── bin\
│   ├── ToolDock.Starter.exe
│   ├── ToolDock.Updater.exe
│   ├── ToolDock.Client.exe
│   ├── tdctl.cmd
│   └── <managed-command>.cmd
├── tools\
│   └── <package>\
│       ├── <version>--<source-id>\
│       ├── .leases\
│       └── current\ -> <version>--<source-id>
├── state\
│   ├── catalog.json
│   ├── installed.json
│   ├── starter-reconciliations.json
│   └── update-failures.json
├── logs\
│   ├── ToolDock.Starter.log
│   ├── ToolDock.Updater.log
│   ├── ToolDock.Client.log
│   └── <daemon>.log
├── secrets\
│   └── secrets.dat
├── temp\
├── variables.json
├── config.json
└── update.ps1
```

Installed state records the package version directory, not a single executable. That permits one release package to provide multiple commands and daemons. State also records generated command names so obsolete shims can be removed safely.

The default root is `~/.tooldock`. With no `TOOLDOCK_HOME` override, an executable in `<root>\bin` discovers `<root>\config.json`. Every root uses a stable path hash in the pipe and singleton/update mutex names, so separate installations cannot collide.

The installer generates `update.ps1` at the root. It downloads the current `install.ps1` from the configured GitHub repository and runs it with settings from `config.json`. The release archive contains only the `bin` directory. A parameterless `update.ps1` updates the same installation.

Retention keeps the newest three marked version directories, the installed-state root, the `current` target, and any older version in use. Commands, notification handlers, and daemons hold shared version leases outside the version directory. Cleanup takes an exclusive lease before renaming an unused version to a private cleanup directory. It also checks matching running executable paths to protect a command whose client died. Inaccessible matching processes defer cleanup. Recursive deletion rejects reparse points and leaves the ownership marker until payload removal completes. Cleanup errors are warnings and are retried after later successful checks; unmarked legacy directories are preserved.

## Update and restart semantics

An updated command begins using the installed version recorded in ToolDock state on its next invocation. Existing command processes are not managed by ToolDock.

Variable and secret changes also take effect on the next process launch. ToolDock does not restart running daemons merely because a stored value changed.

For daemons, the engine sends:

```text
package-updated <package> installed|updated <activation-id>
```

The starter then applies these rules:

- first installation: start entries with `autostart: true`;
- package update: restart running entries with `restartOnUpdate: true`;
- stopped entries remain stopped;
- command-only packages require no process action.

The updater never stops or starts managed daemons directly. If the starter cannot reconcile an installed package, installation remains complete, the pending request stays in installed state, and the update result reports an error. The next run retries it before installing another release. Starter persists its remaining daemon actions and completion receipts; process activation IDs also prevent duplicate restarts when a receipt write or reply is lost. The legacy request without an activation ID remains accepted.

## Process containment

Each daemon is created suspended, assigned to a dedicated Windows Job Object, and then resumed. The Job Object uses kill-on-close semantics. `stop` and `restart` therefore terminate the complete process tree rather than only the root process.

Daemon arguments are encoded using Windows command-line quoting rules. The executable path is also passed separately as the `CreateProcess` application name.

The starter builds a Unicode child environment block from its inherited environment plus the daemon's configured overrides. The client applies the same resolver to interactive command processes. Neither component modifies the user's global Windows environment.

The starter captures stdout and stderr through inherited anonymous pipes. A separate observer records the main process exit immediately and once, independently of later stop/disposal calls. Unexpected exits schedule notification delivery outside supervisor locks; the last nonempty stderr line is retained in a bounded buffer. Requested stops and restarts do not emit crash notifications. Job Object shutdown still drains the output pumps.

## IPC and security

The starter listens on:

```text
ToolDock.Starter.v1
```

The Named Pipe ACL grants access only to the current Windows user and its effective owner SID. The protocol is line-oriented and intentionally local.

Supported public requests are:

```text
list
start <daemon>
stop <daemon>
restart <daemon>
status <daemon>
```

`package-updated` is an internal update-engine request.

## Logging

Application diagnostics use `Microsoft.Extensions.Logging` with a ToolDock rotating-file provider. Structured message templates are retained at call sites without adding a third-party logging dependency.

Managed daemon stdout/stderr is not application logging. It remains a raw transcript with `SYS`, `OUT`, and `ERR` markers so arbitrary child output is not interpreted as ToolDock diagnostic events.

Every catalog-configured environment override is logged at process launch:

```text
ENV_NAME: value
VAR_NAME: variable.name -> value
SEC_NAME: secret.name -> *******
```

Inherited environment entries are not logged. Secret plaintext is never passed to the logger. Child output is not redacted; a managed application remains responsible for not printing credentials.

## Value storage and DPAPI

`variables.json` and `secrets.dat` are versioned, atomically replaced files. A session-local named mutex serializes writes from concurrent client processes. Variable values are plaintext and may be read with `tdctl variable get`.

Each secret value is encrypted with Windows DPAPI using `DataProtectionScope.CurrentUser`. Secret names and ciphertext remain visible, but the plaintext is tied to the current Windows user. ToolDock exposes set, list, status, and remove operations, but no normal secret get/export operation. DPAPI protects stored data; it is not a security boundary against arbitrary malicious code already running as the same user.

Log files rotate at 10 MB with five archives. A mutex derived from the canonical log path serializes writes and rotation across processes. Each write opens and closes the file under that mutex, so a writer cannot retain a stale archive handle. Readers use read/write/delete sharing.

The update coordinator records one start and one final outcome after acquiring the update mutex. The final record includes status and counts, including cancellation and fatal failures. The terminal keeps its existing summary without printing a duplicate final record.

## Notification delivery

`notificationCommand` in `config.json` selects a command from the active installed catalog. `NotificationSender` resolves the executable and normal environment directly, sends the event type in argv and the UTF-8 message on stdin, and bounds execution with `notificationTimeoutSeconds` (default 10, range 1–60). It drains handler output and terminates a timed-out handler. Delivery errors are local warnings and never become another event.

Events are `daemon-crash`, `tool-update-success`, and `tool-update-failure`. Update success requires a version change and completed reconciliation. An attempt-local buffer supplies up to 20 lines / 4 KiB of failure context. A persisted failure fingerprint per package or run suppresses consecutive identical failures and is cleared by a successful check. No handler is configured by default.

## Task Scheduler

The installer creates two limited, interactive-user tasks:

- `ToolDock Starter`: at logon, no execution time limit;
- `ToolDock Updater`: at logon and then at the configured repetition interval.

These task names are fixed. Root-specific runtime IPC supports isolated tests and manual instances, but the installer supports one scheduled installation per user. Rerunning against another root replaces task registrations and does not migrate stored data.

The updater is a daemon in the deployment sense: it is a windowless background executable. It remains one-shot rather than resident; Task Scheduler owns its schedule.

## Build and release

The Windows workflow:

1. restores and builds the solution;
2. verifies installer help, staging, and recovery in Windows PowerShell 5.1 and PowerShell 7;
3. runs deterministic integration scenarios for package activation, reconciliation, retention, notifications, and concurrent clients;
4. publishes all three executables as framework-dependent single files and rejects managed sidecars;
5. tests updater mutex ownership and collision behavior;
6. tests client help, command discovery, output, and interactive updating;
7. runs an actual autostart, argument, output-capture, package-restart, and process-tree smoke cycle;
8. creates the release ZIP and SHA-256 file.

Local commands and coverage limits are documented in [testing.md](testing.md). The notification handler contract and delivery limits are in [notifications.md](notifications.md).
