param([switch]$SelfContained, [switch]$Test)
$ErrorActionPreference = 'Stop'
$clipRoot = $PSScriptRoot
$clipSdk = Join-Path (Split-Path $clipRoot) '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $clipSdk)) { $clipSdk = 'dotnet' }
$clipProject = Join-Path $clipRoot 'ClipShelf\ClipShelf.csproj'
& (Join-Path $clipRoot 'generate-icons.ps1')
if ($SelfContained) {
    & $clipSdk publish $clipProject -c Release -r win-x64 --self-contained true -o (Join-Path $clipRoot 'dist\ClipShelf') --nologo
} else {
    & $clipSdk publish $clipProject -c Release --self-contained false -o (Join-Path $clipRoot 'dist\ClipShelf') --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'ClipShelf build failed.' }
if ($Test) {
    $clipReport = Join-Path $clipRoot 'artifacts\self-test-results.json'
    $clipProcess = Start-Process -FilePath (Join-Path $clipRoot 'dist\ClipShelf\ClipShelf.exe') -ArgumentList @('--self-test', '--test-report', ('"' + $clipReport + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($clipProcess.ExitCode -ne 0) { throw "Self-test failed. See $clipReport" }
}
