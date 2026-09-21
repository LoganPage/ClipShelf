param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$clipRoot = 'D:\CodexData\.codex\.chatgpt-projects\g-p-6a9fe2de64a481918d135da8bd52978c\windows'
if ((Get-Item -LiteralPath $clipRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Root must be a physical directory.' }
$clipCandidates = @()
foreach ($clipName in @('dist','dist-1.2.0','dist-1.2.1','dist-1.2.2','dist-1.2.3','dist-1.2.4','scroll-baseline')) {
    $clipPath = Join-Path $clipRoot $clipName
    if (Test-Path -LiteralPath $clipPath) { $clipCandidates += Get-Item -LiteralPath $clipPath }
}
$clipCandidates += Get-ChildItem -LiteralPath (Join-Path $clipRoot 'public') -Directory | Where-Object { $_.Name -match '^(ClipShelf-Windows-v|runtime-v)\d+\.\d+\.\d+$' -and $_.Name -notin @('ClipShelf-Windows-v1.2.4','runtime-v1.2.4') }
$clipCandidates += Get-ChildItem -LiteralPath $clipRoot -File | Where-Object { $_.Name -match '^ClipShelf-Windows.*-x64\.zip$|^ClipShelf-Windows-x64\.zip$' -and $_.Name -notin @('ClipShelf-Windows-v1.2.4-public-x64.zip','ClipShelf-Windows-v1.1.1-public-x64.zip') }
$clipCandidates += Get-ChildItem -LiteralPath $clipRoot -Directory -Recurse -Force | Where-Object { $_.Name -in @('bin','obj') }
$clipTargets = [Collections.Generic.List[string]]::new()
foreach ($clipCandidate in ($clipCandidates | Sort-Object { $_.FullName.Length })) {
    $clipPath = [IO.Path]::GetFullPath($clipCandidate.FullName)
    if (!$clipPath.StartsWith($clipRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Target outside Windows project.' }
    if (@($clipTargets | Where-Object { $clipPath -eq $_ -or $clipPath.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) { continue }
    if ($clipCandidate.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked target rejected.' }
    if ($clipCandidate.PSIsContainer -and @(Get-ChildItem -LiteralPath $clipPath -Recurse -Force -Attributes ReparsePoint).Count) { throw 'Nested link rejected.' }
    $clipTargets.Add($clipPath)
}
$clipBytes = 0L
foreach ($clipPath in $clipTargets) {
    $clipItem = Get-Item -LiteralPath $clipPath
    if ($clipItem.PSIsContainer) { $clipBytes += (Get-ChildItem -LiteralPath $clipPath -File -Recurse -Force | Measure-Object Length -Sum).Sum } else { $clipBytes += $clipItem.Length }
}
if ($Apply) {
    foreach ($clipPath in $clipTargets) { Remove-Item -LiteralPath $clipPath -Recurse -Force }
}
[pscustomobject]@{ Applied=[bool]$Apply; GiB=[math]::Round($clipBytes/1GB,2); Targets=$clipTargets.ToArray() } | ConvertTo-Json -Depth 3
