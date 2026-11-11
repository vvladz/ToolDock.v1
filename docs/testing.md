# Local validation

Run from the repository root on Windows x64 with the .NET 10 SDK, Windows PowerShell 5.1, and PowerShell 7 installed. The normal build and publish steps restore the repository's existing dependencies.

## Build and deterministic integration tests

```powershell
dotnet build ToolDock.sln -c Release
dotnet run --project tests/ToolDock.IntegrationTests -c Release --no-build
powershell.exe -NoProfile -File tests/install-script.ps1 -InstallerPath install.ps1
pwsh -NoProfile -File tests/install-script.ps1 -InstallerPath install.ps1
```

The integration runner exits nonzero on failure. It covers native junctions, concurrent log rotation, successful installation, source changes, interrupted activation, shim ownership, durable reconciliation, version retention, notifications, daemon exits, custom-root discovery, and overlapping clients. URL scenarios cover ZIPs, standalone files, repeated immediate checks, changed content at a stable URL, failed downloads and extraction, source switches, and compatibility with pre-URL GitHub state and markers. Package HTTP responses are supplied by an in-process test source.

To rerun a scenario, pass part of its printed name:

```powershell
dotnet run --project tests/ToolDock.IntegrationTests -c Release --no-build -- "notification command"
```

Installer tests exercise release asset selection, help, complete staging, copy failure, directory switching, interrupted-install recovery, and the generated `update.ps1` with saved settings. They also check the 24-hour default and create unregistered time-trigger objects to verify the requested interval and next-check delay. Downloads are stubbed; they do not register scheduled tasks, edit the user `PATH`, or replace an installed ToolDock.

## Test published executables

Run the following block in PowerShell 7:

```powershell
foreach ($project in @('Starter', 'Updater', 'Client')) {
    dotnet publish "src/ToolDock.$project/ToolDock.$project.csproj" `
        -c Release -r win-x64 --self-contained false -p:DebugType=None `
        -o "artifacts/verified/$project"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
dotnet publish tests/ToolDock.SmokeTool/ToolDock.SmokeTool.csproj `
    -c Release -o artifacts/verified/smoke-tool
if ($LASTEXITCODE -ne 0) { throw 'Smoke tool publish failed.' }

pwsh -NoProfile -File tests/updater.ps1 `
    -UpdaterPath artifacts/verified/Updater/ToolDock.Updater.exe
pwsh -NoProfile -File tests/client.ps1 `
    -ClientPath artifacts/verified/Client/ToolDock.Client.exe
pwsh -NoProfile -File tests/smoke.ps1 `
    -StarterPath artifacts/verified/Starter/ToolDock.Starter.exe `
    -ClientPath artifacts/verified/Client/ToolDock.Client.exe `
    -ToolDirectory artifacts/verified/smoke-tool
```

Check each script's exit code before continuing. The client check includes help, usage errors, command discovery, logs, and interactive update failure. The smoke test exercises actual process-tree containment, autostart, package restart, variable/secret resolution, and command streams and exit codes.

Tests use unique temporary roots, pipes, and mutexes. They launch their own child processes and clean up those test instances. A sandbox must allow current-user Named Pipes and process creation; an IPC access denial is an environment failure, not a successful test.

## CI and installation coverage

[The Windows workflow](../.github/workflows/build-release.yml) runs these checks, tests the installer in both shells, rejects managed DLL/JSON sidecars in the three product publish directories, and checks the packaged archive size. Pull requests and manual runs upload artifacts; pushes to `master` also publish the release.

Full installation against a live GitHub release, scheduled-task registration, and persistence of the user `PATH` are outside these automated tests. Validate those in a disposable Windows user profile when checking installer deployment end to end. Runtime discovery of a custom installation directory is covered by fresh Client, Updater, and Starter processes in the integration suite.
