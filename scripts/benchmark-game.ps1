param(
    [Parameter(Mandatory)][string]$PresentMon,
    [Parameter(Mandatory)][string]$GameExe,
    [Parameter(Mandatory)][ValidateSet('Baseline','HavijScreen','Discord')][string]$Condition,
    [Parameter(Mandatory)][string]$Scene,
    [Parameter(Mandatory)][string]$GameSettings,
    [string]$StreamSettings = '720p30',
    [ValidateSet('Application','Monitor')][string]$Source = 'Application',
    [ValidateSet('On','Off')][string]$StreamAudio = 'On',
    [ValidateRange(10,600)][int]$Seconds = 60,
    [ValidateRange(0,120)][int]$DelaySeconds = 10,
    [ValidateRange(1,100)][int]$Round = 1,
    [switch]$ViewerConfirmed,
    [string]$Notes = '',
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\game-benchmarks')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'frame-metrics.ps1')
if ($Condition -ne 'Baseline' -and -not $ViewerConfirmed) { throw 'Have one friend watch the stream on another PC, then add -ViewerConfirmed.' }
if (-not (Test-Path -LiteralPath $PresentMon -PathType Leaf)) { throw 'Supply the official PresentMon console EXE path. See docs/game-performance.md.' }
if ([IO.Path]::GetFileName($GameExe) -ne $GameExe -or -not $GameExe.EndsWith('.exe',[StringComparison]::OrdinalIgnoreCase)) { throw 'GameExe must be the game executable name, for example game.exe.' }
$gameProcesses = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($GameExe)) -ErrorAction SilentlyContinue)
if ($gameProcesses.Count -ne 1) { throw "Expected one running $GameExe process; found $($gameProcesses.Count). Open the game and use its executable name, not the launcher." }
$gameProcessId = $gameProcesses[0].Id
$presentMonPath = (Resolve-Path -LiteralPath $PresentMon).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
$runId = '{0}-{1}-r{2}-{3}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'),$Condition,$Round,[Guid]::NewGuid().ToString('N').Substring(0,6)
$csvPath = Join-Path $output ($runId+'.csv')
$info = [ordered]@{
    Schema = 1; RunId = $runId; Condition = $Condition; Round = $Round
    GameExe = $GameExe; GameProcessId = $gameProcessId; Scene = $Scene; GameSettings = $GameSettings
    StreamSettings = $StreamSettings; Source = $Source; StreamAudio = $StreamAudio
    RemoteViewers = $(if ($Condition -eq 'Baseline') { 0 } else { 1 })
    RequestedSeconds = $Seconds; Notes = $Notes; Csv = [IO.Path]::GetFileName($csvPath)
    PresentMonSha256 = (Get-FileHash -LiteralPath $presentMonPath -Algorithm SHA256).Hash
}
# No hooks, overlays, automatic elevation, service installation, or existing ETW
# session termination. A unique session cannot stop another recorder's capture.
$arguments = @('--process_id',"$gameProcessId",'--output_file',$csvPath,'--v1_metrics',
    '--delay',"$DelaySeconds",'--timed',"$Seconds",'--terminate_after_timed',
    '--session_name',('HavijBench-'+[Guid]::NewGuid().ToString('N')),
    '--no_console_stats','--no_track_input','--no_track_gpu','--no_track_display')
Write-Host "Recording $Condition round $Round in $DelaySeconds seconds for $Seconds seconds. Return to the game and repeat the same scene."
& $presentMonPath @arguments
if ($LASTEXITCODE -ne 0) { throw "PresentMon exited with code $LASTEXITCODE. Check its output; ETW access may require running this terminal as administrator. No result was counted." }
$frames = Read-PresentMonFrames -CsvPath $csvPath -GameProcessId $gameProcessId
if ($gameProcesses[0].HasExited -or $frames.Metrics.DurationSeconds -lt $Seconds*0.9) { throw 'The gameplay capture is incomplete. Repeat the run with the game presenting frames throughout the timed segment. No result was counted.' }
$info.SwapChain = $frames.SwapChain
$info.OtherSwapChains = $frames.OtherSwapChains
$info.Metrics = $frames.Metrics
$info | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output ($runId+'.run.json')) -Encoding UTF8
if ($frames.OtherSwapChains -gt 0) { Write-Warning 'Multiple render surfaces were recorded. The one with the most frames was used; verify it is the gameplay surface.' }
$frames.Metrics | Format-List
Write-Host "Saved locally: $csvPath"
