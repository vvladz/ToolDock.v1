[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sentinel = 'STOP_AFTER_RELEASE_ASSET_SELECTION'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-installer-test-" + [Guid]::NewGuid().ToString('N'))
$expectedDefaultRoot = [IO.Path]::GetFullPath((Join-Path $HOME '.tooldock'))
$verifyDefaultRoot = $false
$verifySavedSettings = $false
$verifyDefaultInterval = $true
$republishDuringDownload = $false
$expectedReleaseUri = 'https://api.github.com/repos/example/ToolDock/releases/latest'
$expectedInstallerUri = 'https://raw.githubusercontent.com/example/ToolDock/HEAD/install.ps1'
$expectedCatalogUrl = 'https://example.org/saved.json'
$expectedInterval = 17

function Invoke-RestMethod {
    param([string] $Uri)
    if ($Uri -like 'https://raw.githubusercontent.com/*/HEAD/install.ps1') {
        if ($Uri -cne $expectedInstallerUri) { throw "Wrong installer source: $Uri" }
        if ($republishDuringDownload) { Publish-UpdateScript $installRoot }
        return [IO.File]::ReadAllText($installer)
    }
    if ($verifySavedSettings -and $Uri -cne $expectedReleaseUri) {
        throw "Saved repository was not used: $Uri"
    }
    return [pscustomobject]@{
        tag_name = 'v-test'
        assets = @(
            [pscustomobject]@{
                name = 'ToolDock-win-x64.zip'
                browser_download_url = 'https://example.org/ToolDock-win-x64.zip'
            }
            [pscustomobject]@{
                name = 'ToolDock-win-x64.zip.sha256'
                browser_download_url = 'https://example.org/ToolDock-win-x64.zip.sha256'
            }
        )
    }
}

function Invoke-WebRequest {
    if ($verifyDefaultInterval -and $UpdateIntervalMinutes -ne 1440) {
        throw 'A new installation must check packages every 24 hours.'
    }
    if ($verifyDefaultRoot -and -not [string]::Equals($installPath, $expectedDefaultRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer chose $installPath instead of $expectedDefaultRoot."
    }
    if ($verifySavedSettings -and
        (-not [string]::Equals($installPath, $installRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $CatalogUrl -cne $expectedCatalogUrl -or $UpdateIntervalMinutes -ne $expectedInterval)) {
        throw 'Installed update.ps1 did not use its root and saved settings.'
    }
    throw $sentinel
}

try {
    $help = Get-Help $installer -Full | Out-String
    foreach ($expected in @('Installs or updates ToolDock', 'Repository', 'CatalogUrl', 'Version', 'UpdateIntervalMinutes', 'InstallRoot')) {
        if (-not $help.Contains($expected)) { throw "Installer help is missing: $expected" }
    }
    try {
        & $installer `
            -Repository 'example/ToolDock' `
            -CatalogUrl 'https://example.org/tools.json' `
            -InstallRoot $installRoot
        throw 'Installer unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) {
            throw
        }
    }
    $verifyDefaultInterval = $false

    $verifyDefaultRoot = $true
    try {
        & $installer -Repository 'example/ToolDock' -CatalogUrl 'https://example.org/tools.json'
        throw 'Installer unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) {
            throw
        }
    }
    $verifyDefaultRoot = $false

    # Exercise installer functions without changing scheduled tasks or user PATH.
    $parseErrors = $null
    $tokens = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Installer has PowerShell syntax errors.' }
    $definitions = $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -in @('New-ToolDockUpdaterTrigger', 'Remove-InstallDirectory', 'Put-PathEntryFirst', 'Stage-ToolDockBin', 'Switch-ToolDockBin', 'Publish-UpdateScript')
    }, $false)
    foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    foreach ($interval in @(1440, 15)) {
        $before = Get-Date
        $trigger = @(New-ToolDockUpdaterTrigger $interval)
        $after = Get-Date
        if ($trigger.Count -ne 1 -or $trigger[0].CimClass.CimClassName -ne 'MSFT_TaskTimeTrigger' -or
            [Xml.XmlConvert]::ToTimeSpan($trigger[0].Repetition.Interval).TotalMinutes -ne $interval) {
            throw 'Updater must have one repeating time trigger with the requested interval.'
        }
        $firstRun = [DateTime]::Parse($trigger[0].StartBoundary)
        if ($firstRun -lt $before.AddMinutes($interval).AddSeconds(-1) -or $firstRun -gt $after.AddMinutes($interval)) {
            throw 'Updater should wait a full interval after the initial installation update.'
        }
    }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    $configPath = Join-Path $installRoot 'config.json'
    [IO.File]::WriteAllText($configPath,
        '{"catalogUrl":"https://example.org/saved.json","repository":"example/ToolDock","updateIntervalMinutes":17}')
    $updateScript = Join-Path $installRoot 'update.ps1'
    Publish-UpdateScript $installRoot
    if (-not (Test-Path -LiteralPath $updateScript -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $installRoot 'install.ps1'))) {
        throw 'Installer did not generate a standalone update.ps1.'
    }
    $generatedLauncher = [IO.File]::ReadAllText($updateScript)

    $verifySavedSettings = $true
    try {
        & $updateScript
        throw 'Generated update.ps1 unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) { throw }
    }

    # The launcher reads config.json on every run and supports explicit overrides.
    [IO.File]::WriteAllText($configPath,
        '{"catalogUrl":"https://example.org/changed.json","repository":"example/ToolDock","updateIntervalMinutes":23}')
    $expectedCatalogUrl = 'https://example.org/changed.json'
    $expectedInterval = 23
    $expectedReleaseUri = 'https://api.github.com/repos/example/ToolDock/releases/tags/v-selected'
    try {
        & $updateScript -Version v-selected
        throw 'Generated update.ps1 unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) { throw }
    }
    $expectedInstallerUri = 'https://raw.githubusercontent.com/alternate/ToolDock/HEAD/install.ps1'
    $expectedReleaseUri = 'https://api.github.com/repos/alternate/ToolDock/releases/latest'
    $expectedCatalogUrl = 'https://example.org/override.json'
    $expectedInterval = 31
    try {
        & $updateScript -Repository 'alternate/ToolDock' -CatalogUrl $expectedCatalogUrl -UpdateIntervalMinutes $expectedInterval
        throw 'Generated update.ps1 unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) { throw }
    }
    $verifySavedSettings = $false
    $expectedInstallerUri = 'https://raw.githubusercontent.com/example/ToolDock/HEAD/install.ps1'
    [IO.File]::WriteAllText($updateScript, $generatedLauncher + '# old launcher' + [Environment]::NewLine)
    $republishDuringDownload = $true
    try {
        & $updateScript
        throw 'Generated update.ps1 unexpectedly continued past the download boundary.'
    }
    catch {
        if ($_.Exception.Message -ne $sentinel) { throw }
    }
    $republishDuringDownload = $false
    if ([IO.File]::ReadAllText($updateScript) -cne $generatedLauncher -or
        (Test-Path -LiteralPath (Join-Path $installRoot 'update.previous.ps1'))) {
        throw 'Running update.ps1 was not replaced cleanly.'
    }
    [IO.File]::WriteAllText($updateScript, '# custom launcher')
    Publish-UpdateScript $installRoot 3>$null
    if ([IO.File]::ReadAllText($updateScript) -cne '# custom launcher') {
        throw 'Installer replaced an unrelated launcher.'
    }
    [IO.File]::WriteAllText($updateScript, '# legacy launcher using install.previous.ps1')
    Publish-UpdateScript $installRoot
    if ([IO.File]::ReadAllText($updateScript) -cne $generatedLauncher) {
        throw 'Installer did not replace the legacy launcher.'
    }
    $orderedPath = Put-PathEntryFirst 'C:\Previous\bin;C:\New\bin;C:\Other;C:\NEW\bin\' 'C:\New\bin'
    if ($orderedPath -cne 'C:\New\bin;C:\Previous\bin;C:\Other' -or
        (Put-PathEntryFirst $orderedPath 'C:\New\bin') -cne $orderedPath) {
        throw 'Installer did not prioritize the selected bin directory without duplicates.'
    }
    $releaseBin = Join-Path $installRoot 'release'
    $liveBin = Join-Path $installRoot 'bin'
    New-Item -ItemType Directory -Path $releaseBin, $liveBin -Force | Out-Null
    $coreFiles = @('ToolDock.Starter.exe', 'ToolDock.Updater.exe', 'ToolDock.Client.exe', 'tdctl.cmd')
    foreach ($name in $coreFiles) {
        [IO.File]::WriteAllText((Join-Path $releaseBin $name), 'new')
        [IO.File]::WriteAllText((Join-Path $liveBin $name), 'old')
    }
    [IO.File]::WriteAllText((Join-Path $liveBin 'managed.cmd'), 'managed command')
    Stage-ToolDockBin $releaseBin $installRoot
    foreach ($name in $coreFiles) {
        if ([IO.File]::ReadAllText((Join-Path $liveBin $name)) -ne 'old') { throw 'Staging modified live bin.' }
    }
    Switch-ToolDockBin $installRoot
    foreach ($name in $coreFiles) {
        if ([IO.File]::ReadAllText((Join-Path $liveBin $name)) -ne 'new') { throw 'Switched bin contains mixed versions.' }
    }
    if ([IO.File]::ReadAllText((Join-Path $liveBin 'managed.cmd')) -ne 'managed command') { throw 'Managed shim was lost.' }

    $held = [IO.File]::Open((Join-Path $releaseBin 'ToolDock.Client.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $failedCopy = $false
        try { Stage-ToolDockBin $releaseBin $installRoot } catch { $failedCopy = $true }
        if (-not $failedCopy) { throw 'Test did not inject the expected copy failure.' }
        foreach ($name in $coreFiles) {
            if ([IO.File]::ReadAllText((Join-Path $liveBin $name)) -ne 'new') { throw 'Failed copy modified live bin.' }
        }
    }
    finally { $held.Dispose() }

    # Simulate interruption after moving bin aside. Rerunning must finish forward installation.
    Stage-ToolDockBin $releaseBin $installRoot
    Remove-InstallDirectory (Join-Path $installRoot 'bin.previous') $installRoot
    Move-Item -LiteralPath $liveBin -Destination (Join-Path $installRoot 'bin.previous')
    Stage-ToolDockBin $releaseBin $installRoot
    Switch-ToolDockBin $installRoot
    if (-not (Test-Path -LiteralPath (Join-Path $liveBin 'managed.cmd'))) { throw 'Interrupted install lost managed shim.' }
    Stage-ToolDockBin $releaseBin $installRoot
    Remove-Item -LiteralPath (Join-Path $installRoot 'bin.next\ToolDock.Updater.exe')
    $rejected = $false
    try { Switch-ToolDockBin $installRoot } catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText((Join-Path $liveBin 'ToolDock.Updater.exe')) -ne 'new') {
        throw 'Incomplete staging replaced live bin.'
    }
    "PowerShell $($PSVersionTable.PSVersion) installer help, staging, recovery, and generated update launcher: OK"
}
finally {
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
}
