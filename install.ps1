<#
.SYNOPSIS
Installs or updates ToolDock for the current Windows user.
.DESCRIPTION
Downloads a ToolDock release, verifies its SHA-256 checksum, stages the complete
bin directory, then replaces the installed programs. Registers the Starter and
Updater scheduled tasks, adds bin to the user PATH, and runs the first update.
The generated update.ps1 downloads the current installer from GitHub and passes
the settings saved in config.json.
Requires Windows x64 and the .NET 10 Runtime. Supports Windows PowerShell 5.1
and PowerShell 7 (pwsh). Administrator privileges are not required.
.PARAMETER Repository
Public GitHub repository containing ToolDock releases, in owner/repository form.
Required for the first installation; later read from config.json if omitted.
.PARAMETER CatalogUrl
Absolute HTTPS URL of the managed-package catalog (tools.json). Required for the
first installation; later read from config.json if omitted.
.PARAMETER Version
Exact ToolDock release tag. Omit to select the latest release. This does not pin
the versions of managed packages.
.PARAMETER UpdateIntervalMinutes
Package update interval in minutes, from 1 to 1440. Defaults to the saved value
when updating, or 1440 (24 hours) for the first installation.
.PARAMETER InstallRoot
Installation directory. Defaults to ~/.tooldock. Reuse the same
directory when updating; update.ps1 defaults to its own directory. This parameter
does not migrate an existing installation.
.EXAMPLE
.\install.ps1 -Repository 'vvladz/ToolDock' -CatalogUrl 'https://example.org/tools.json'
Installs the latest ToolDock release using your hosted catalog URL.
.EXAMPLE
.\install.ps1 -Repository 'vvladz/ToolDock' -CatalogUrl 'https://example.org/tools.json' -InstallRoot 'D:\My Tools\ToolDock' -UpdateIntervalMinutes 15
Uses a custom directory and checks managed packages every 15 minutes.
.EXAMPLE
& "$HOME\.tooldock\update.ps1"
Updates ToolDock using the saved installation settings.
.NOTES
Run Get-Help .\install.ps1 -Full for all parameters and examples.
Rerun with the same parameters after an interrupted installation to finish it.
The installer registers one task pair per user; multiple scheduled installations
are not supported. Open a new terminal after installation to refresh PATH.
#>
[CmdletBinding()]
param(
    [string] $Repository,

    [ValidateNotNullOrEmpty()]
    [string] $CatalogUrl,

    [string] $Version,

    [ValidateRange(1, 1440)]
    [int] $UpdateIntervalMinutes = 1440,

    [ValidateNotNullOrEmpty()]
    [string] $InstallRoot = (Join-Path $HOME '.tooldock')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-ToolDockUpdaterTrigger([int] $IntervalMinutes) {
    New-ScheduledTaskTrigger `
        -Once `
        -At ((Get-Date).AddMinutes($IntervalMinutes)) `
        -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
}

function Remove-InstallDirectory([string] $Path, [string] $Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup path is outside the installation root: $resolved"
    }
    if (-not (Test-Path -LiteralPath $resolved)) { return }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to traverse $resolved" }
    foreach ($child in Get-ChildItem -LiteralPath $resolved -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to traverse $($child.FullName)" }
        if ($child.PSIsContainer) { Remove-InstallDirectory $child.FullName $Root }
        else { Remove-Item -LiteralPath $child.FullName -Force }
    }
    Remove-Item -LiteralPath $resolved -Force
}

function Put-PathEntryFirst([string] $Value, [string] $Entry) {
    $otherEntries = @($Value -split ';' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not [string]::Equals($_.TrimEnd('\'), $Entry.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
    })
    return (@($Entry) + $otherEntries) -join ';'
}

function Stage-ToolDockBin([string] $PackageBin, [string] $Root) {
    $next = Join-Path $Root 'bin.next'
    Remove-InstallDirectory $next $Root
    New-Item -ItemType Directory -Path $next -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $PackageBin -Force) {
        Copy-Item -LiteralPath $file.FullName -Destination $next -Recurse -Force
    }
    $previousBin = Join-Path $Root 'bin'
    if (-not (Test-Path -LiteralPath $previousBin)) { $previousBin = Join-Path $Root 'bin.previous' }
    if (Test-Path -LiteralPath $previousBin) {
        foreach ($shim in Get-ChildItem -LiteralPath $previousBin -Filter '*.cmd' -File) {
            if ($shim.Name -ine 'tdctl.cmd') { Copy-Item -LiteralPath $shim.FullName -Destination $next -Force }
        }
    }
}

function Switch-ToolDockBin([string] $Root) {
    $live = Join-Path $Root 'bin'
    $next = Join-Path $Root 'bin.next'
    $previous = Join-Path $Root 'bin.previous'
    foreach ($name in @('ToolDock.Starter.exe', 'ToolDock.Updater.exe', 'ToolDock.Client.exe', 'tdctl.cmd')) {
        if (-not (Test-Path -LiteralPath (Join-Path $next $name) -PathType Leaf)) {
            throw "Staged bin is missing $name."
        }
    }
    if (Test-Path -LiteralPath $live) {
        Remove-InstallDirectory $previous $Root
        Move-Item -LiteralPath $live -Destination $previous
    }
    Move-Item -LiteralPath $next -Destination $live
}

function Publish-UpdateScript([string] $Root) {
    $script = @'
# Generated by ToolDock install.ps1.
[CmdletBinding()]
param(
    [string] $Repository,
    [string] $CatalogUrl,
    [string] $Version,
    [ValidateRange(1, 1440)]
    [int] $UpdateIntervalMinutes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
if ($null -eq $config -or $config -isnot [pscustomobject]) {
    throw 'ToolDock config.json must be a JSON object.'
}
$effectiveRepository = if ($PSBoundParameters.ContainsKey('Repository')) { $Repository } else { $config.repository }
$effectiveCatalogUrl = if ($PSBoundParameters.ContainsKey('CatalogUrl')) { $CatalogUrl } else { $config.catalogUrl }
$effectiveInterval = if ($PSBoundParameters.ContainsKey('UpdateIntervalMinutes')) {
    $UpdateIntervalMinutes
} else { $config.updateIntervalMinutes }
if ([string]::IsNullOrWhiteSpace($effectiveRepository) -or
    $effectiveRepository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    $effectiveRepository.Contains('..')) {
    throw 'Repository must be a public GitHub owner/repository name.'
}
$installerUri = "https://raw.githubusercontent.com/$effectiveRepository/HEAD/install.ps1"
$installer = Invoke-RestMethod -Uri $installerUri
& ([scriptblock]::Create($installer)) `
    -Repository $effectiveRepository `
    -CatalogUrl $effectiveCatalogUrl `
    -UpdateIntervalMinutes $effectiveInterval `
    -InstallRoot $PSScriptRoot `
    -Version $Version
'@ + [Environment]::NewLine
    $destination = Join-Path $Root 'update.ps1'
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $current = [IO.File]::ReadAllText($destination)
        if ($current -ceq $script) { return }
        if (-not ($current.StartsWith('# Generated by ToolDock install.ps1.') -or
                  $current.Contains('install.previous.ps1'))) {
            Write-Warning 'Existing update.ps1 is not a ToolDock launcher and was preserved.'
            return
        }
    } elseif (Test-Path -LiteralPath $destination) {
        throw "Cannot install update.ps1 because it is not a file: $destination"
    }
    $staged = Join-Path $Root ('.update-script-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    $backup = Join-Path $Root 'update.previous.ps1'
    try {
        [IO.File]::WriteAllText($staged, $script, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $destination) {
            if (Test-Path -LiteralPath $backup) {
                if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
                    throw "Cannot replace update.ps1 because its backup is not a file: $backup"
                }
                Remove-Item -LiteralPath $backup -Force
            }
            Move-Item -LiteralPath $destination -Destination $backup
            try { Move-Item -LiteralPath $staged -Destination $destination }
            catch {
                Move-Item -LiteralPath $backup -Destination $destination
                throw
            }
        } else {
            Move-Item -LiteralPath $staged -Destination $destination
        }
    }
    finally {
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    }
    if (Test-Path -LiteralPath $backup -PathType Leaf) {
        try { Remove-Item -LiteralPath $backup -Force }
        catch { Write-Warning "Old update script cleanup deferred: $($_.Exception.Message)" }
    }
}
$installPath = [IO.Path]::GetFullPath($InstallRoot)
$configPath = Join-Path $installPath 'config.json'
$settings = if (Test-Path -LiteralPath $configPath) {
    Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
if ($null -eq $settings -or $settings -isnot [pscustomobject]) {
    throw "Configuration must be a JSON object: $configPath"
}
if (-not $PSBoundParameters.ContainsKey('Repository') -and $settings.PSObject.Properties['repository']) {
    $Repository = $settings.repository
}
if (-not $PSBoundParameters.ContainsKey('CatalogUrl') -and $settings.PSObject.Properties['catalogUrl']) {
    $CatalogUrl = $settings.catalogUrl
}
if (-not $PSBoundParameters.ContainsKey('UpdateIntervalMinutes') -and $settings.PSObject.Properties['updateIntervalMinutes']) {
    $UpdateIntervalMinutes = $settings.updateIntervalMinutes
}
if ([string]::IsNullOrWhiteSpace($Repository) -or
    $Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    $Repository.Contains('..')) {
    throw 'Repository must be a public GitHub owner/repository name.'
}
if ($UpdateIntervalMinutes -lt 1 -or $UpdateIntervalMinutes -gt 1440) {
    throw 'UpdateIntervalMinutes must be between 1 and 1440.'
}

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'ToolDock can only be installed on Windows.'
}

$dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw 'ToolDock requires the .NET 10 Runtime (x64): https://dotnet.microsoft.com/download/dotnet/10.0'
}

$installedRuntimes = @(& $dotnet.Source --list-runtimes 2>$null)
if ($LASTEXITCODE -ne 0 -or -not ($installedRuntimes -match '^Microsoft\.NETCore\.App 10\.')) {
    throw 'ToolDock requires the .NET 10 Runtime (x64): https://dotnet.microsoft.com/download/dotnet/10.0'
}

$catalogUri = $null
if (-not [Uri]::TryCreate($CatalogUrl, [UriKind]::Absolute, [ref] $catalogUri) -or
    $catalogUri.Scheme -ne [Uri]::UriSchemeHttps) {
    throw 'CatalogUrl must be an absolute HTTPS URL.'
}

$binPath = Join-Path $installPath 'bin'
$assetName = 'ToolDock-win-x64.zip'
$checksumAssetName = "$assetName.sha256"
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'ToolDock-Installer'
    'X-GitHub-Api-Version' = '2022-11-28'
}

$releaseUri = if ([string]::IsNullOrWhiteSpace($Version)) {
    "https://api.github.com/repos/$Repository/releases/latest"
} else {
    "https://api.github.com/repos/$Repository/releases/tags/$([Uri]::EscapeDataString($Version))"
}

Write-Host "Resolving ToolDock release from $Repository..."
$release = Invoke-RestMethod -Uri $releaseUri -Headers $headers
$matchingAssets = @($release.assets | Where-Object { $_.name -ceq $assetName })
if ($matchingAssets.Count -ne 1) {
    throw "Release '$($release.tag_name)' does not contain $assetName."
}
$asset = $matchingAssets[0]
$matchingChecksums = @($release.assets | Where-Object { $_.name -ceq $checksumAssetName })
if ($matchingChecksums.Count -ne 1) {
    throw "Release '$($release.tag_name)' does not contain $checksumAssetName."
}
$checksumAsset = $matchingChecksums[0]

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("ToolDock-install-" + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryRoot $assetName
$checksumPath = Join-Path $temporaryRoot $checksumAssetName
$packagePath = Join-Path $temporaryRoot 'package'

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $archivePath
    Invoke-WebRequest -Uri $checksumAsset.browser_download_url -Headers $headers -OutFile $checksumPath
    $expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
    if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Release checksum file $checksumAssetName is invalid."
    }
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 verification failed for $assetName."
    }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $packagePath

    $packageBin = Join-Path $packagePath 'bin'
    $starterSource = Join-Path $packageBin 'ToolDock.Starter.exe'
    $updaterSource = Join-Path $packageBin 'ToolDock.Updater.exe'
    $clientSource = Join-Path $packageBin 'ToolDock.Client.exe'
    $clientShim = Join-Path $packageBin 'tdctl.cmd'
    if (-not (Test-Path -LiteralPath $starterSource -PathType Leaf) -or
        -not (Test-Path -LiteralPath $updaterSource -PathType Leaf) -or
        -not (Test-Path -LiteralPath $clientSource -PathType Leaf) -or
        -not (Test-Path -LiteralPath $clientShim -PathType Leaf)) {
        throw 'Release package is missing a ToolDock executable or tdctl.cmd.'
    }
    # Finish every release copy before stopping the installed programs.
    Stage-ToolDockBin $packageBin $installPath

    $starterTaskName = 'ToolDock Starter'
    $updaterTaskName = 'ToolDock Updater'
    Get-ScheduledTask -TaskName $starterTaskName, $updaterTaskName -ErrorAction SilentlyContinue |
        Stop-ScheduledTask -ErrorAction SilentlyContinue

    $managedProcesses = @(
        [pscustomobject]@{ Name = 'starter'; File = 'starter.exe' }
        [pscustomobject]@{ Name = 'updater'; File = 'updater.exe' }
        [pscustomobject]@{ Name = 'ToolDock.Starter'; File = 'ToolDock.Starter.exe' }
        [pscustomobject]@{ Name = 'ToolDock.Updater'; File = 'ToolDock.Updater.exe' }
        [pscustomobject]@{ Name = 'ToolDock.Client'; File = 'ToolDock.Client.exe' }
    )
    foreach ($managedProcess in $managedProcesses) {
        $existingExecutable = Join-Path $binPath $managedProcess.File
        Get-Process -Name $managedProcess.Name -ErrorAction SilentlyContinue | Where-Object {
            try { [string]::Equals($_.Path, $existingExecutable, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
        } | Stop-Process -Force
    }

    Switch-ToolDockBin $installPath
    New-Item -ItemType Directory -Path (Join-Path $installPath 'tools') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'state') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'logs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installPath 'temp') -Force | Out-Null
    $settings | Add-Member -NotePropertyName catalogUrl -NotePropertyValue $CatalogUrl -Force
    $settings | Add-Member -NotePropertyName repository -NotePropertyValue $Repository -Force
    $settings | Add-Member -NotePropertyName updateIntervalMinutes -NotePropertyValue $UpdateIntervalMinutes -Force
    $config = $settings | ConvertTo-Json -Depth 10
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $installPath 'config.json'), $config + [Environment]::NewLine, $utf8NoBom)
    Publish-UpdateScript $installPath

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $newUserPath = Put-PathEntryFirst $userPath $binPath
    if ($newUserPath -ne $userPath) {
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    }
    $env:Path = Put-PathEntryFirst $env:Path $binPath

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited

    $starterAction = New-ScheduledTaskAction -Execute (Join-Path $binPath 'ToolDock.Starter.exe')
    $starterTrigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
    $starterSettings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask `
        -TaskName $starterTaskName `
        -Action $starterAction `
        -Trigger $starterTrigger `
        -Principal $principal `
        -Settings $starterSettings `
        -Description 'Starts ToolDock process supervision in the current user session.' `
        -Force | Out-Null

    $updaterAction = New-ScheduledTaskAction -Execute (Join-Path $binPath 'ToolDock.Updater.exe')
    $updaterTrigger = New-ToolDockUpdaterTrigger $UpdateIntervalMinutes
    $updaterSettings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask `
        -TaskName $updaterTaskName `
        -Action $updaterAction `
        -Trigger $updaterTrigger `
        -Principal $principal `
        -Settings $updaterSettings `
        -Description 'Checks GitHub Releases and HTTPS packages and installs ToolDock-managed tools.' `
        -Force | Out-Null

    Start-ScheduledTask -TaskName $starterTaskName
    $firstUpdate = Start-Process -FilePath (Join-Path $binPath 'ToolDock.Updater.exe') -WindowStyle Hidden -Wait -PassThru
    if ($firstUpdate.ExitCode -ne 0) {
        Write-Warning "Initial update returned exit code $($firstUpdate.ExitCode). See $installPath\logs\ToolDock.Updater.log."
    }

    Write-Host "ToolDock $($release.tag_name) installed in $installPath."
    Write-Host 'Open a new terminal to use managed tool commands from PATH.'
    try { Remove-InstallDirectory (Join-Path $installPath 'bin.previous') $installPath }
    catch { Write-Warning "Old bin cleanup deferred: $($_.Exception.Message)" }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
