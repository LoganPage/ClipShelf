param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'

# Export an explicit, reviewed allowlist to a NEW directory only. Never copy a
# directory recursively: local reports, screenshots and clipboard data are not
# publication inputs. This script does not create a repository or use a network.
$clipExportSource = [IO.Path]::GetFullPath($PSScriptRoot)
$clipExportDestination = [IO.Path]::GetFullPath($Destination)
if ($clipExportDestination -eq [IO.Path]::GetPathRoot($clipExportDestination)) { throw 'Choose a new, dedicated export directory, not a drive root.' }
foreach ($clipProtectedDirectory in @(
    (Join-Path (Split-Path $clipExportSource) 'sources'),
    (Join-Path (Split-Path $clipExportSource) 'mac-reference'),
    (Join-Path $clipExportSource 'ClipShelf')
)) {
    $clipProtectedPath = [IO.Path]::GetFullPath($clipProtectedDirectory)
    if ($clipExportDestination.Equals($clipProtectedPath, [StringComparison]::OrdinalIgnoreCase) -or
        $clipExportDestination.StartsWith($clipProtectedPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose an export directory outside the read-only references and working source tree.'
    }
}
if (Test-Path -LiteralPath $clipExportDestination) { throw 'The export destination must not already exist; no existing files are overwritten.' }

$clipPublicSourceFiles = @(
    'app.manifest',
    'App.xaml',
    'App.xaml.cs',
    'AppSelfTest.cs',
    'CachedHistoryRow.cs',
    'CacheCleanupService.cs',
    'CacheCleanupTests.cs',
    'MainWindow.Cleanup.cs',
    'ClipShelf.csproj',
    'CopyOnlyTests.cs',
    'DemoContent.cs',
    'DocxDocumentModel.cs',
    'DocxPaginator.cs',
    'DocxPreviewProvider.cs',
    'DragSelectionTests.cs',
    'FilePreviewTests.cs',
    'FileRecordTests.cs',
    'FluentMenus.xaml',
    'FluentPresentationTests.cs',
    'FocusCuePolicy.cs',
    'FocusCueTests.cs',
    'HistoryListBox.cs',
    'HistoryShortcutPolicy.cs',
    'IconAssetTests.cs',
    'LayoutRegressionTests.cs',
    'MainWindow.Commands.cs',
    'MainWindow.Focus.cs',
    'MainWindow.Tray.cs',
    'MainWindow.Updates.cs',
    'WindowsUpdateService.cs',
    'WindowsUpdateTests.cs',
    'InstallUpdate.ps1',
    'MainWindow.xaml',
    'MainWindow.xaml.cs',
    'NativePreviewBenchmark.cs',
    'NativePreviewTests.cs',
    'OoxmlPreviewProvider.cs',
    'OoxmlStyles.cs',
    'PdfPageRenderService.cs',
    'PptxPreviewProvider.cs',
    'PreviewCacheService.cs',
    'PreviewFormatRegistry.cs',
    'PreviewInteractionTests.cs',
    'PreviewProviders.cs',
    'PreviewRenderScheduler.cs',
    'PreviewScene.cs',
    'PreviewSession.cs',
    'PreviewWindow.cs',
    'QaRenderer.cs',
    'RecordIcons.xaml',
    'RecordTypeIcon.cs',
    'RoundedClipBorder.cs',
    'SafeOoxmlPackage.cs',
    'ScrollRenderingProbe.cs',
    'SettingsExperienceTests.cs',
    'SettingsPanel.cs',
    'SmoothScrollViewer.cs',
    'TextImagePreviewService.cs',
    'TextImagePreviewTests.cs',
    'ThemeManager.cs',
    'ThemeTransition.cs',
    'ThemeTransitionTests.cs',
    'ThirdPartyNotices.txt',
    'ThumbnailLoader.cs',
    'ToolTipPresentationTests.cs',
    'TrayInteractionTests.cs',
    'UiPerformanceTests.cs',
    'WheelScrollMotion.cs',
    'WindowAppearance.cs',
    'WindowsInteractionTests.cs',
    'Licenses\BouncyCastle.txt',
    'Licenses\DOC-Apache-2.0.txt',
    'Licenses\NPOI-Apache-2.0.txt',
    'Licenses\SharpZipLib-MIT.txt',
    'Licenses\SystemDrawing-LICENSE.txt',
    'Licenses\SystemDrawing-NOTICES.txt',
    'Models\AppSettings.cs',
    'Models\ClipItem.cs',
    'Models\SearchMatcher.cs',
    'Models\StorageTests.cs',
    'Services\HistoryStore.cs',
    'Services\NativeClipboard.cs',
    'Services\NativeMethods.cs',
    'Services\WindowsIntegration.cs',
    'Assets\AppIcon2.png',
    'Assets\ClipShelf.ico'
)
$clipPublicMap = [ordered]@{}
foreach ($clipPublicFile in $clipPublicSourceFiles) { $clipPublicMap['ClipShelf\' + $clipPublicFile] = 'ClipShelf\' + $clipPublicFile }
$clipPublicMap['build.ps1'] = 'build.ps1'
$clipPublicMap['generate-icons.ps1'] = 'generate-icons.ps1'
$clipPublicMap['install.ps1'] = 'install.ps1'
$clipPublicMap['PUBLIC-README-1.1.md'] = 'README.md'
$clipPublicMap['PUBLIC.gitignore'] = '.gitignore'
$clipPublicMap['LICENSE'] = 'LICENSE'

# Fail closed on recognizable private workstation paths or credential formats.
# Report only the file name, never matched contents. Ordinary CancellationToken
# and test-token variable names are intentionally not treated as credentials.
$clipPrivatePattern = '(?i)([A-Z]:[\\/](Users|Documents and Settings)[\\/][^\s"'']+|\.codex[\\/]|\.chatgpt-projects|github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{30,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)'
foreach ($clipPublicInput in $clipPublicMap.Keys) {
    $clipPublicInputPath = Join-Path $clipExportSource $clipPublicInput
    if (-not (Test-Path -LiteralPath $clipPublicInputPath -PathType Leaf)) { throw "Required public input is missing: $clipPublicInput" }
    if ([IO.Path]::GetExtension($clipPublicInputPath) -notin @('.png', '.ico') -and
        [regex]::IsMatch([IO.File]::ReadAllText($clipPublicInputPath), $clipPrivatePattern)) {
        throw "Privacy review required for: $clipPublicInput"
    }
}

function Copy-ClipPublicPng([string]$InputPath, [string]$OutputPath) {
    # Remove textual / EXIF metadata without decoding or changing a single image
    # pixel. Color profile and physical-resolution chunks remain byte-for-byte.
    $clipPngBytes = [IO.File]::ReadAllBytes($InputPath)
    if ($clipPngBytes.Length -lt 8 -or [BitConverter]::ToString($clipPngBytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'Invalid PNG asset.' }
    $clipPngOutput = [IO.MemoryStream]::new()
    try {
        $clipPngOutput.Write($clipPngBytes, 0, 8)
        $clipChunkOffset = 8
        while ($clipChunkOffset + 12 -le $clipPngBytes.Length) {
            $clipLengthBytes = [byte[]]$clipPngBytes[$clipChunkOffset..($clipChunkOffset + 3)]
            [Array]::Reverse($clipLengthBytes)
            $clipChunkLength = [BitConverter]::ToUInt32($clipLengthBytes, 0)
            $clipChunkTotal = 12L + $clipChunkLength
            if ($clipChunkTotal -gt [int]::MaxValue -or $clipChunkOffset + $clipChunkTotal -gt $clipPngBytes.Length) { throw 'Invalid PNG chunk length.' }
            $clipChunkType = [Text.Encoding]::ASCII.GetString($clipPngBytes, $clipChunkOffset + 4, 4)
            if ($clipChunkType -notin @('eXIf', 'tEXt', 'iTXt', 'zTXt')) {
                $clipPngOutput.Write($clipPngBytes, $clipChunkOffset, [int]$clipChunkTotal)
            }
            $clipChunkOffset += $clipChunkTotal
        }
        if ($clipChunkOffset -ne $clipPngBytes.Length) { throw 'Unexpected PNG trailing data.' }
        [IO.File]::WriteAllBytes($OutputPath, $clipPngOutput.ToArray())
    } finally { $clipPngOutput.Dispose() }
}

New-Item -ItemType Directory -Path $clipExportDestination | Out-Null
foreach ($clipPublicInput in $clipPublicMap.Keys) {
    $clipPublicInputPath = Join-Path $clipExportSource $clipPublicInput
    $clipPublicOutputPath = Join-Path $clipExportDestination $clipPublicMap[$clipPublicInput]
    New-Item -ItemType Directory -Path (Split-Path $clipPublicOutputPath) -Force | Out-Null
    if ([IO.Path]::GetExtension($clipPublicInputPath) -eq '.png') {
        Copy-ClipPublicPng $clipPublicInputPath $clipPublicOutputPath
    } else { Copy-Item -LiteralPath $clipPublicInputPath -Destination $clipPublicOutputPath }
}
Write-Output "Prepared $($clipPublicMap.Count) reviewed public files in: $clipExportDestination"
Write-Output 'No repository, network, installed application, or user clipboard data was changed.'
