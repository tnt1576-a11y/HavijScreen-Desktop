$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\scripts\frame-metrics.ps1')
function Assert([bool]$Condition,[string]$Message) { if(-not $Condition) { throw $Message } }
$values = @((1..990 | ForEach-Object { 10.0 }) + (1..10 | ForEach-Object { 50.0 }))
$metrics = Get-FrameMetrics $values
Assert ($metrics.AverageFps -eq 96.154) 'Average FPS must be the inverse of mean frame time, not the mean of instantaneous FPS.'
Assert ($metrics.OnePercentLowFps -eq 20) '1% low must use the mean of the slowest 1% of frame intervals.'
Assert ($metrics.P99FrameMs -eq 10 -and $metrics.MaxFrameMs -eq 50) 'Nearest-rank percentile or maximum is wrong.'
Assert ($metrics.FramesOver33Ms -eq 10 -and $metrics.FramesOver50Ms -eq 0) 'Frame-spike counts are wrong.'
$filtered = Get-FrameMetrics ($values + @(0,-1,[double]::NaN,[double]::PositiveInfinity))
Assert ($filtered.Frames -eq 1000) 'Invalid intervals must not count as frames.'
$withStall = Get-FrameMetrics ($values + @(3000.0))
Assert ($withStall.MaxFrameMs -eq 3000 -and $withStall.AverageFps -lt 96) 'Long stalls must not be removed as outliers.'
Assert ((Get-Median @(10,20,50,60)) -eq 35) 'Even median failed.'
Assert ((Get-Median @(10,60,20)) -eq 20) 'Odd median failed.'
$failed=$false
try { Get-FrameMetrics @(1..120 | ForEach-Object { 1.0 }) | Out-Null } catch { $failed=$true }
Assert $failed 'Short captures must be rejected.'
$tenSeconds=Get-FrameMetrics @(1..999 | ForEach-Object { 10.0 })
Assert ($tenSeconds.DurationSeconds -eq 9.99) 'A complete timed 10-second capture must tolerate its missing first interval.'
$directory=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\benchmark-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$path=Join-Path $directory 'fixture.csv'
$rows=@(foreach($value in $values) { [pscustomobject]@{ ProcessID=123; SwapChainAddress='0x1'; MsBetweenPresents=$value.ToString([Globalization.CultureInfo]::InvariantCulture) } })
$rows+=@(1..100 | ForEach-Object { [pscustomobject]@{ ProcessID=123; SwapChainAddress='0x2'; MsBetweenPresents='20' } })
$rows+=@(1..1200 | ForEach-Object { [pscustomobject]@{ ProcessID=456; SwapChainAddress='0x3'; MsBetweenPresents='20' } })
$rows | Export-Csv -LiteralPath $path -NoTypeInformation
$oldCulture=[Threading.Thread]::CurrentThread.CurrentCulture
try {
    [Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('de-DE')
    $parsed=Read-PresentMonFrames -CsvPath $path -GameProcessId 123
    Assert ($parsed.SwapChain -eq '0x1' -and $parsed.OtherSwapChains -eq 1 -and $parsed.Metrics.Frames -eq 1000) 'Process/swapchain isolation failed.'
    Assert ($parsed.Metrics.OnePercentLowFps -eq 20) 'CSV parsing must be culture independent.'
} finally { [Threading.Thread]::CurrentThread.CurrentCulture=$oldCulture }
foreach($condition in @('Baseline','HavijScreen','Discord')) {
    foreach($round in 1..3) {
        [pscustomobject]@{ Schema=1; Condition=$condition; Round=$round; GameExe='fixture.exe'; Scene='fixture'; GameSettings='fixed'; StreamSettings='720p30'; Source='Application'; StreamAudio='On'; RequestedSeconds=60; PresentMonSha256='fixture'; Metrics=$metrics } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory "$condition-$round.run.json")
    }
}
& (Join-Path $PSScriptRoot '..\scripts\compare-game-benchmarks.ps1') -Directory $directory
$comparison=@(Get-Content (Join-Path $directory 'comparison.json') -Raw | ConvertFrom-Json)
Assert ($comparison.Count -eq 3 -and $comparison[1].AverageFpsLossPercent -eq 0) 'Comparison failed.'
$badPath=Join-Path $directory 'Discord-3.run.json'
$bad=Get-Content $badPath -Raw | ConvertFrom-Json
$bad.Scene='different scene'
$bad | ConvertTo-Json -Depth 4 | Set-Content $badPath
$failed=$false
try { & (Join-Path $PSScriptRoot '..\scripts\compare-game-benchmarks.ps1') -Directory $directory } catch { $failed=$true }
Assert $failed 'Mismatched scenes must not be compared.'
Write-Output 'Benchmark metrics and comparison checks passed (synthetic data only).'
