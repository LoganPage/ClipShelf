param([string]$Version)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$clipPackageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $clipPackageRoot 'ClipShelf\ClipShelf.csproj') -Raw)).Project.PropertyGroup.Version }
$clipDistribution = Join-Path $clipPackageRoot 'dist'
$clipBinary = Join-Path $clipDistribution 'ClipShelf\ClipShelf.exe'
if (-not (Test-Path -LiteralPath $clipBinary)) { throw 'Build the self-contained application first.' }
foreach ($clipDoc in @('README.md','PARITY.md','PERFORMANCE.md','LICENSE','install.ps1')) {
    Copy-Item -LiteralPath (Join-Path $clipPackageRoot $clipDoc) -Destination $clipDistribution -Force
}
function Write-ClipArchive([string]$Destination, [string]$Root, [IO.FileInfo[]]$Files) {
    $clipStream = [IO.File]::Open($Destination, [IO.FileMode]::Create)
    $clipArchive = [IO.Compression.ZipArchive]::new($clipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($clipFile in $Files) {
            $clipEntry = [IO.Path]::GetRelativePath($Root, $clipFile.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($clipArchive, $clipFile.FullName, $clipEntry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $clipArchive.Dispose(); $clipStream.Dispose() }
}
$clipRuntimeArchive = Join-Path $clipPackageRoot "ClipShelf-Windows-v$Version-x64.zip"
Write-ClipArchive $clipRuntimeArchive $clipDistribution @(Get-ChildItem -LiteralPath $clipDistribution -File -Recurse)
$clipSourceFiles = @()
foreach ($clipFolder in @('ClipShelf','benchmarks')) {
    $clipSourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $clipPackageRoot $clipFolder) -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
}
$clipSourceFiles += @(Get-ChildItem -LiteralPath $clipPackageRoot -File | Where-Object { $_.Extension -in '.md','.ps1' -or $_.Name -in 'LICENSE','.gitignore' })
foreach ($clipReport in @('storage-baseline.json','storage-deferred.json','storage-tests.json','search-baseline.json','search-optimized.json','search-regression-results.json','ui-performance.json','self-test-v1.0.1.json')) {
    $clipSourceFiles += Get-Item -LiteralPath (Join-Path $clipPackageRoot "artifacts\$clipReport")
}
$clipSourceArchive = Join-Path $clipPackageRoot "artifacts\ClipShelf-Windows-v$Version-source.zip"
foreach ($clipOptionalReport in @('ui-performance-v1.0.2.json','self-test-v1.0.2.json','layout-v1.0.2\layout-regression-results.json','interaction-v1.0.3.json','ui-performance-v1.0.3.json','self-test-v1.0.3.json','storage-undo-tests-1.0.3.json','layout-v1.0.3\layout-regression-results.json','presentation-v1.0.4\presentation-results.json','interaction-v1.0.4.json','ui-performance-v1.0.4.json','layout-v1.0.4\layout-regression-results.json','scroll-render-v1.0.4.json','display-timing-v1.0.4.json','self-test-v1.0.4.json','presentation-v1.0.5\presentation-results.json','interaction-v1.0.5.json','ui-performance-v1.0.5.json','layout-v1.0.5\layout-regression-results.json','self-test-v1.0.5.json')) {
    $clipReportPath = Join-Path $clipPackageRoot "artifacts\$clipOptionalReport"
    if (Test-Path -LiteralPath $clipReportPath) { $clipSourceFiles += Get-Item -LiteralPath $clipReportPath }
}
foreach ($clipNewReport in @('paste-safety-v1.0.6.json','tray-v1.0.6\tray-results.json','tray-popup-v1.0.6\tray-popup-results.json','interaction-v1.0.6.json','ui-performance-v1.0.6.json','layout-v1.0.6\layout-regression-results.json','presentation-v1.0.6\presentation-results.json','self-test-v1.0.6.json','native-ui-v1.0.6-results.json')) {
    $clipNewReportPath = Join-Path $clipPackageRoot "artifacts\$clipNewReport"
    if (Test-Path -LiteralPath $clipNewReportPath) { $clipSourceFiles += Get-Item -LiteralPath $clipNewReportPath }
}
foreach ($clipDragReport in @('drag-v1.0.7\drag-selection-results.json','drag-ui-before-v1.0.7\drag-ui-events.json','drag-ui-after-v1.0.7\drag-ui-events.json','interaction-v1.0.7.json','ui-performance-v1.0.7.json','layout-v1.0.7\layout-regression-results.json','presentation-v1.0.7\presentation-results.json','paste-safety-v1.0.7.json','tray-v1.0.7\tray-results.json','tray-popup-v1.0.7\tray-popup-results.json','self-test-v1.0.7.json')) {
    $clipDragReportPath = Join-Path $clipPackageRoot "artifacts\$clipDragReport"
    if (Test-Path -LiteralPath $clipDragReportPath) { $clipSourceFiles += Get-Item -LiteralPath $clipDragReportPath }
}
foreach ($clipFocusReport in @('focus-v1.0.8\focus-results.json','focus-ui-v1.0.8\focus-ui-events.json','ui-performance-v1.0.8.json','interaction-v1.0.8.json','layout-v1.0.8\layout-regression-results.json','presentation-v1.0.8\presentation-results.json','drag-v1.0.8\drag-selection-results.json','paste-safety-v1.0.8.json','tray-v1.0.8\tray-results.json','self-test-v1.0.8.json')) {
    $clipFocusReportPath = Join-Path $clipPackageRoot "artifacts\$clipFocusReport"
    if (Test-Path -LiteralPath $clipFocusReportPath) { $clipSourceFiles += Get-Item -LiteralPath $clipFocusReportPath }
}
Write-ClipArchive $clipSourceArchive $clipPackageRoot $clipSourceFiles
Get-Item -LiteralPath $clipRuntimeArchive,$clipSourceArchive | Select-Object FullName,Length
