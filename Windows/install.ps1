param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$clipSource = Join-Path $PSScriptRoot 'ClipShelf'
if (-not (Test-Path -LiteralPath (Join-Path $clipSource 'ClipShelf.exe'))) { $clipSource = Join-Path $PSScriptRoot 'dist\ClipShelf' }
if (-not (Test-Path -LiteralPath (Join-Path $clipSource 'ClipShelf.exe'))) { throw 'ClipShelf.exe was not found beside this installer.' }
$clipDestination = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\ClipShelf'
$clipExe = Join-Path $clipDestination 'ClipShelf.exe'
if (Get-Process ClipShelf -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $clipExe }) { throw 'Please exit ClipShelf from its tray menu before updating.' }
New-Item -ItemType Directory -Path $clipDestination -Force | Out-Null
Copy-Item -Path (Join-Path $clipSource '*') -Destination $clipDestination -Recurse -Force
$clipShell = New-Object -ComObject WScript.Shell
foreach ($clipShortcutFolder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $clipShortcut = $clipShell.CreateShortcut((Join-Path $clipShortcutFolder 'ClipShelf.lnk'))
    $clipShortcut.TargetPath = $clipExe
    $clipShortcut.WorkingDirectory = $clipDestination
    $clipShortcut.IconLocation = Join-Path $clipDestination 'Assets\ClipShelf.ico'
    $clipShortcut.Description = 'ClipShelf - Clipboard history for Windows'
    $clipShortcut.Save()
}
Write-Output "Installed: $clipExe"
if (-not $NoLaunch) { Start-Process -FilePath $clipExe }
