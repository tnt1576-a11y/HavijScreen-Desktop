param([string]$Directory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\game-benchmarks'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'frame-metrics.ps1')
$runs = @(Get-ChildItem -LiteralPath $Directory -Filter '*.run.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
if ($runs.Count -eq 0) { throw 'No completed benchmark runs found.' }
# Do not manufacture an apples-to-apples comparison across different scenes,
# settings, capture durations, or recorder binaries. Use a directory per setup.
foreach ($field in @('Schema','GameExe','Scene','GameSettings','StreamSettings','Source','StreamAudio','RequestedSeconds','PresentMonSha256')) {
    if (@($runs | ForEach-Object { $_.$field } | Sort-Object -Unique).Count -ne 1) { throw "Runs differ in $field. Compare a single test setup per directory." }
}
if (@($runs | Where-Object { $_.Schema -ne 1 -or $_.Condition -notin @('Baseline','HavijScreen','Discord') }).Count) { throw 'Unsupported benchmark result format.' }
$baselineRuns = @($runs | Where-Object Condition -eq 'Baseline')
if ($baselineRuns.Count -eq 0) { throw 'Record a Baseline run first.' }
$baseline = Get-Median @($baselineRuns | ForEach-Object { $_.Metrics.AverageFps })
$summary = @(foreach ($condition in @('Baseline','HavijScreen','Discord')) {
    $items = @($runs | Where-Object Condition -eq $condition)
    if ($items.Count -eq 0) { continue }
    $average = Get-Median @($items | ForEach-Object { $_.Metrics.AverageFps })
    [pscustomobject][ordered]@{
        Condition = $condition; Runs = $items.Count
        AverageFps = [Math]::Round($average,2)
        AverageFpsMin = ($items | ForEach-Object { $_.Metrics.AverageFps } | Measure-Object -Minimum).Minimum
        AverageFpsMax = ($items | ForEach-Object { $_.Metrics.AverageFps } | Measure-Object -Maximum).Maximum
        OnePercentLowFps = [Math]::Round((Get-Median @($items | ForEach-Object { $_.Metrics.OnePercentLowFps })),2)
        P99FrameMs = [Math]::Round((Get-Median @($items | ForEach-Object { $_.Metrics.P99FrameMs })),2)
        FramesOver50Ms = Get-Median @($items | ForEach-Object { $_.Metrics.FramesOver50Ms })
        AverageFpsLossPercent = [Math]::Round((1-$average/$baseline)*100,2)
    }
})
$summary | Format-Table -AutoSize
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'comparison.json') -Encoding UTF8
if ($summary.Count -lt 3 -or @($summary | Where-Object Runs -lt 3).Count) { Write-Warning 'Comparison is preliminary: collect at least three runs for each condition.' }
Write-Host 'Results use medians across runs. Inspect the FPS range and viewer quality before drawing a conclusion.'
