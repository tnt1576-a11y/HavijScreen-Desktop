Set-StrictMode -Version Latest

function Get-FrameMetrics {
    param([Parameter(Mandatory)][double[]]$Milliseconds)
    $values = @($Milliseconds | Where-Object { $_ -gt 0 -and -not [double]::IsNaN($_) -and -not [double]::IsInfinity($_) } | Sort-Object)
    if ($values.Count -lt 120) { throw 'Too few valid frames. Record at least 10 seconds of gameplay.' }
    $sum = ($values | Measure-Object -Sum).Sum
    # Timed capture loses the interval before the first present, so a requested
    # ten-second run can legitimately contain slightly less than ten seconds.
    if ($sum -lt 9000) { throw 'Capture is shorter than nine seconds. Record at least 10 seconds of gameplay.' }
    $slowCount = [Math]::Max(1,[int][Math]::Ceiling($values.Count * 0.01))
    $slowMean = ($values[($values.Count-$slowCount)..($values.Count-1)] | Measure-Object -Average).Average
    [pscustomobject][ordered]@{
        Frames = $values.Count
        DurationSeconds = [Math]::Round($sum / 1000,3)
        AverageFps = [Math]::Round(1000 * $values.Count / $sum,3)
        OnePercentLowFps = [Math]::Round(1000 / $slowMean,3)
        P99FrameMs = [Math]::Round($values[[int][Math]::Ceiling($values.Count * 0.99)-1],3)
        MaxFrameMs = [Math]::Round($values[-1],3)
        FramesOver33Ms = @($values | Where-Object { $_ -gt (1000 / 30) }).Count
        FramesOver50Ms = @($values | Where-Object { $_ -gt 50 }).Count
    }
}

function Read-PresentMonFrames {
    param([Parameter(Mandatory)][string]$CsvPath,[Parameter(Mandatory)][int]$GameProcessId)
    $rows = @(Import-Csv -LiteralPath $CsvPath)
    if ($rows.Count -eq 0) { throw 'PresentMon recorded no frames.' }
    foreach ($column in @('ProcessID','SwapChainAddress','MsBetweenPresents')) {
        if ($column -notin $rows[0].PSObject.Properties.Name) { throw "Missing $column. Use the PresentMon console with --v1_metrics." }
    }
    # Keep one render surface; combining swapchains inflates the measured FPS.
    $groups = @($rows | Where-Object { $_.ProcessID -eq [string]$GameProcessId } | Group-Object SwapChainAddress | Sort-Object Count -Descending)
    if ($groups.Count -eq 0) { throw 'No frames matched the selected game process.' }
    $selected = $groups[0]
    $times = [System.Collections.Generic.List[double]]::new()
    foreach ($row in $selected.Group) {
        $value = 0.0
        if ([double]::TryParse($row.MsBetweenPresents,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value)) { $times.Add($value) }
    }
    [pscustomobject]@{
        SwapChain = $selected.Name
        OtherSwapChains = $groups.Count-1
        Metrics = Get-FrameMetrics -Milliseconds $times.ToArray()
    }
}

function Get-Median {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { throw 'No measurements to summarize.' }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle-1]+$sorted[$middle])/2
}
