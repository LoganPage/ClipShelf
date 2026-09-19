param([Parameter(Mandatory=$true)][string]$Stage,[Parameter(Mandatory=$true)][string]$Target,[Parameter(Mandatory=$true)][int]$WaitProcessId)
$ErrorActionPreference = 'Stop'
function Get-ClipUpdateHash([string]$Path) {
    $clipStream = [IO.File]::OpenRead($Path)
    $clipAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($clipAlgorithm.ComputeHash($clipStream)) }
    finally { $clipAlgorithm.Dispose(); $clipStream.Dispose() }
}
$clipLocal = [Environment]::GetFolderPath('LocalApplicationData')
$clipUpdateRoot = Join-Path $clipLocal 'ClipShelf-Updates'
$clipExpectedTarget = Join-Path $clipLocal 'Programs\ClipShelf'
$clipStagePath = [IO.Path]::GetFullPath($Stage).TrimEnd('\')
$clipTargetPath = [IO.Path]::GetFullPath($Target).TrimEnd('\')
if ($clipTargetPath -ne $clipExpectedTarget -or (Split-Path $clipStagePath) -ne $clipUpdateRoot -or (Split-Path $clipStagePath -Leaf) -notmatch '^stage-[0-9a-f]{32}$') { throw 'Invalid update paths.' }
$clipSource = Join-Path $clipStagePath 'payload\ClipShelf'
if (!(Test-Path -LiteralPath (Join-Path $clipSource 'ClipShelf.exe'))) { throw 'Incomplete update.' }
# Reject links/junctions before any recursive copy or cleanup.
foreach ($clipRoot in @($clipStagePath, $clipTargetPath, (Join-Path $clipLocal 'ClipShelf'))) {
    if (Test-Path -LiteralPath $clipRoot) {
        if ((Get-Item -LiteralPath $clipRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse root is not allowed.' }
        if (Get-ChildItem -LiteralPath $clipRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Reparse entry is not allowed.' }
    }
}
'ready' | Set-Content -LiteralPath (Join-Path $clipStagePath 'ready') -Encoding ASCII
$clipProcess = Get-Process -Id $WaitProcessId -ErrorAction SilentlyContinue
if ($clipProcess -and !$clipProcess.WaitForExit(90000)) { exit 2 } # Never kill the app or overwrite unsaved history.
$clipBackup = Join-Path (Join-Path $clipLocal 'ClipShelf-backups') ('update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$clipBackupDone = $false
$clipCopyStarted = $false
$clipInstallMutex = $null
$clipOwnsMutex = $false
try {
    $clipCreated = $false
    $clipInstallMutex = [Threading.Mutex]::new($true, 'Local\ClipShelf.Windows.Default', [ref]$clipCreated)
    $clipOwnsMutex = $clipCreated
    if (!$clipOwnsMutex) { throw 'Another application instance is active.' }
    New-Item -ItemType Directory -Path $clipBackup | Out-Null
    $clipData = Join-Path $clipLocal 'ClipShelf'
    if (Test-Path -LiteralPath $clipData) { Copy-Item -LiteralPath $clipData -Destination (Join-Path $clipBackup 'ClipShelf') -Recurse }
    Copy-Item -LiteralPath $clipTargetPath -Destination (Join-Path $clipBackup 'previous-application') -Recurse
    $clipBackupDone = $true
    $clipCopyStarted = $true
    Get-ChildItem -LiteralPath $clipSource | Copy-Item -Destination $clipTargetPath -Recurse -Force
    foreach ($clipFile in Get-ChildItem -LiteralPath $clipSource -Recurse -File) {
        $clipInstalled = Join-Path $clipTargetPath $clipFile.FullName.Substring($clipSource.Length+1)
        if ((Get-ClipUpdateHash $clipFile.FullName) -ne (Get-ClipUpdateHash $clipInstalled)) { throw 'Installed hash mismatch.' }
    }
    $clipErrorFile = Join-Path $clipUpdateRoot 'last-error.txt'
    if (Test-Path -LiteralPath $clipErrorFile) { Remove-Item -LiteralPath $clipErrorFile -Force }
    $clipInstallMutex.ReleaseMutex(); $clipOwnsMutex = $false
    $clipInstallMutex.Dispose(); $clipInstallMutex = $null
    Start-Process -FilePath (Join-Path $clipTargetPath 'ClipShelf.exe') -WindowStyle Hidden
} catch {
    $clipFailure = 'Line=' + $_.InvocationInfo.ScriptLineNumber + '; Type=' + $_.Exception.GetType().FullName + '; ' + $_.Exception.Message
    if ($clipBackupDone -and $clipCopyStarted) { Get-ChildItem -LiteralPath (Join-Path $clipBackup 'previous-application') | Copy-Item -Destination $clipTargetPath -Recurse -Force }
    New-Item -ItemType Directory -Path $clipUpdateRoot -Force | Out-Null
    $clipFailure | Set-Content -LiteralPath (Join-Path $clipUpdateRoot 'installer-error.log') -Encoding UTF8
    'Update failed. The previous application was retained or restored. Backups are in ClipShelf-backups.' | Set-Content -LiteralPath (Join-Path $clipUpdateRoot 'last-error.txt') -Encoding UTF8
    if ($clipOwnsMutex) { $clipInstallMutex.ReleaseMutex(); $clipOwnsMutex = $false }
    if ($clipInstallMutex) { $clipInstallMutex.Dispose(); $clipInstallMutex = $null }
    Start-Process -FilePath (Join-Path $clipTargetPath 'ClipShelf.exe') -WindowStyle Hidden
    exit 1
} finally {
    if ($clipOwnsMutex) { $clipInstallMutex.ReleaseMutex() }
    if ($clipInstallMutex) { $clipInstallMutex.Dispose() }
}
# Cleanup failure must never roll back a successfully restarted application.
# Only this validated, uniquely-created staging directory is removed. Backups remain recoverable.
try { Remove-Item -LiteralPath $clipStagePath -Recurse -Force } catch { }
