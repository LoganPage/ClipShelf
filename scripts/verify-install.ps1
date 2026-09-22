[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory,

    [string]$ExpectedVersion,

    [string]$InstalledDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\ClipShelf'),

    [string]$DataDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClipShelf'),

    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'

function Get-ClipOptionalFileState([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Exists = $false; Hash = $null }
    }

    return [pscustomobject]@{
        Exists = $true
        Hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Assert-ClipOptionalFileUnchanged([string]$Path, $Before, [string]$Label) {
    $after = Get-ClipOptionalFileState $Path
    if ($after.Exists -ne $Before.Exists -or $after.Hash -ne $Before.Hash) {
        throw "$Label changed during installation."
    }
}

$build = [IO.Path]::GetFullPath($BuildDirectory)
$installed = [IO.Path]::GetFullPath($InstalledDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$builtExe = Join-Path $build 'ClipShelf.exe'
$installedExe = Join-Path $installed 'ClipShelf.exe'
$installer = Join-Path $build 'InstallUpdate.ps1'

foreach ($required in @($builtExe, $installer, $installedExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file is missing: $required"
    }
}

$builtVersion = (Get-Item -LiteralPath $builtExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = $builtVersion
} elseif ($builtVersion -ne $ExpectedVersion) {
    throw "Build version '$builtVersion' does not match expected version '$ExpectedVersion'."
}

if ($build.Equals($installed, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildDirectory must be separate from InstalledDirectory.'
}

$historyPath = Join-Path $data 'history.json'
$settingsPath = Join-Path $data 'settings.json'
$historyBefore = Get-ClipOptionalFileState $historyPath
$settingsBefore = Get-ClipOptionalFileState $settingsPath
$localData = [Environment]::GetFolderPath('LocalApplicationData')
$stage = Join-Path (Join-Path $localData 'ClipShelf-Updates') ('stage-' + [Guid]::NewGuid().ToString('N'))
$verificationStarted = Get-Date

New-Item -ItemType Directory -Path (Join-Path $stage 'payload') -Force | Out-Null
Copy-Item -LiteralPath $build -Destination (Join-Path $stage 'payload\ClipShelf') -Recurse

$runningBefore = @(Get-Process ClipShelf -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })
try {
    if ($runningBefore.Count -gt 0) {
        Start-Process -FilePath $installedExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
        foreach ($process in $runningBefore) {
            if (-not $process.WaitForExit(15000)) {
                throw 'ClipShelf did not finish its normal shutdown within 15 seconds.'
            }
        }
    }

    if (Get-Process ClipShelf -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }) {
        throw 'ClipShelf is still running from the installation directory.'
    }

    $waitProcessId = if ($runningBefore.Count -gt 0) { $runningBefore[0].Id } else { 2147483647 }
    $windowsPowerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $helper = Start-Process -FilePath $windowsPowerShell -ArgumentList @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $installer + '"'),
        '-Stage', ('"' + $stage + '"'),
        '-Target', ('"' + $installed + '"'),
        '-WaitProcessId', $waitProcessId
    ) -PassThru -WindowStyle Hidden

    if (-not $helper.WaitForExit(60000)) {
        throw 'Installer did not finish within 60 seconds.'
    }
    if ($helper.ExitCode -ne 0) {
        throw "Installer exited with code $($helper.ExitCode)."
    }

    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
    if ($installedVersion -ne $ExpectedVersion) {
        throw "Installed version '$installedVersion' does not match '$ExpectedVersion'."
    }

    foreach ($name in @('ClipShelf.exe', 'ClipShelf.dll', 'InstallUpdate.ps1')) {
        $builtHash = (Get-FileHash -LiteralPath (Join-Path $build $name) -Algorithm SHA256).Hash
        $installedHash = (Get-FileHash -LiteralPath (Join-Path $installed $name) -Algorithm SHA256).Hash
        if ($builtHash -ne $installedHash) {
            throw "Installed hash mismatch: $name"
        }
    }

    Assert-ClipOptionalFileUnchanged $historyPath $historyBefore 'History'
    Assert-ClipOptionalFileUnchanged $settingsPath $settingsBefore 'Settings'

    $started = Get-Process ClipShelf -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $installedExe } |
        Select-Object -First 1
    if (-not $started -or -not $started.WaitForInputIdle(15000)) {
        throw 'Installed ClipShelf did not reach an interactive startup state.'
    }

    $backupRoot = Join-Path $localData 'ClipShelf-backups'
    $backup = if (Test-Path -LiteralPath $backupRoot -PathType Container) {
        Get-ChildItem -LiteralPath $backupRoot -Directory -Filter 'update-*' |
            Where-Object { $_.LastWriteTime -ge $verificationStarted.AddSeconds(-2) } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }

    $result = [pscustomobject]@{
        version = $installedVersion
        processId = $started.Id
        responding = $started.Responding
        historyUnchanged = $true
        settingsUnchanged = $true
        backup = if ($backup) { $backup.FullName } else { $null }
        stageCleaned = -not (Test-Path -LiteralPath $stage)
        verifiedAt = (Get-Date).ToString('o')
    }

    $json = $result | ConvertTo-Json
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $output = [IO.Path]::GetFullPath($ResultPath)
        $parent = Split-Path $output
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Set-Content -LiteralPath $output -Value $json -Encoding UTF8
    }
    $json
} catch {
    if (-not (Get-Process ClipShelf -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe })) {
        Start-Process -FilePath $installedExe -WindowStyle Hidden
    }
    throw
}
