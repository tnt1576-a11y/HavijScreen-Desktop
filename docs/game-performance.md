# Compare game performance

Use the same repeatable gameplay route or built-in benchmark for **sharing off**, **HavijScreen**, and **Discord**. A quiet scene in a private match works when a built-in benchmark is unavailable. Warm up the game first, then keep graphics, resolution, frame cap, display mode, upscaling and frame generation unchanged. Avoid menus, loading screens and shader compilation during the recorded segment.

For a matched first comparison, use HavijScreen **Efficient (720p/30)** and Discord **720p/30**, sharing the same application with audio on and one friend actively watching on another PC. Leave HavijScreen's local preview off, and keep its window minimized during the gameplay segment. Do not run the two outgoing streams together. Keep the same background apps open in each condition, with sharing disabled for the baseline.

Repeat each condition at least three times. Rotate the order to reduce warm-up and temperature bias: baseline/HavijScreen/Discord, Discord/baseline/HavijScreen, HavijScreen/Discord/baseline. Ask the viewer to note motion smoothness, text clarity, freezes and delay. A local receiver on the gaming PC adds decoding work and is not a fair substitute.

Then repeat with **Performance** mode in a separate results directory. It starts at 720p/30 and can reduce capture to 20 or 15 fps when it falls behind. Record any reduction in the notes: fewer streamed frames can lower work but are a different viewing experience. The app's status strip shows the current capture-rate target, not the game's FPS.

## Record frame timings

Download the x64 **PresentMon console executable** from the [official PresentMon releases](https://github.com/GameTechDev/PresentMon/releases). Use a console version supporting `--v1_metrics` and the [documented capture flags](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md). The desktop overlay/service package is not needed. This recorder uses Windows event tracing; the script does not install services, inject into the game, add an overlay or change game/security settings. ETW permissions may require an administrator terminal. Anti-cheat policies still depend on the game.

Run these commands from the extracted HavijScreen folder or source checkout, replacing the executable name, scene and settings with your own. The script records only the selected process, starts after a 10-second delay and stops after 60 seconds. Use `-Seconds` and `-DelaySeconds` to match your scene. Identify the actual game executable in Task Manager rather than its launcher.

```powershell
$test = @{
    PresentMon = 'C:\Tools\PresentMon-x64.exe'
    GameExe = 'game.exe'
    Scene = 'Same 60-second route in a private match'
    GameSettings = '2560x1440, same graphics preset and FPS cap, frame generation off'
    StreamSettings = '720p30'
    Round = 1
}
.\scripts\benchmark-game.ps1 @test -Condition Baseline
# Start HavijScreen sharing and have a friend watch before this run:
.\scripts\benchmark-game.ps1 @test -Condition HavijScreen -ViewerConfirmed
# Stop HavijScreen sharing, then start Discord sharing with the same viewer:
.\scripts\benchmark-game.ps1 @test -Condition Discord -ViewerConfirmed
```

Repeat with `Round = 2` and `Round = 3`. `-ViewerConfirmed` records your confirmation; the tool cannot inspect Discord or prove that the remote viewer is watching. For monitor sharing or muted audio, use `-Source Monitor` or `-StreamAudio Off` consistently. Use `-Notes` for stream quality observations, app versions, driver version and anomalies. Use a separate `-OutputDirectory` for each scene or settings combination.

Results stay in `artifacts/game-benchmarks` by default. The folder is ignored by Git. Raw CSVs contain process/render timing data, not screen images or audio. Nothing is uploaded automatically. To compare:

```powershell
.\scripts\compare-game-benchmarks.ps1
```

## Read the results

- **Average FPS:** 1000 divided by the mean interval between application presents.
- **1% low:** 1000 divided by the mean of the slowest 1% of those intervals, rounded up to a whole number of frames. This is not the reciprocal of the 99th percentile.
- **P99 frame time:** nearest-rank 99th-percentile interval in milliseconds. Lower is better.
- **Frames over 50 ms:** long-frame count. Compare equal durations.
- **FPS loss:** percentage change from the median baseline average FPS. A negative value means the measured run was faster; it may be normal variation.

The comparison uses medians of individual runs and includes their average-FPS range. It refuses mixed scenes, settings, durations or recorder binaries. For multiple render surfaces, the recorder chooses the swapchain with the most frames and warns; inspect the CSV if that surface could be a menu or auxiliary window. Long stalls are retained, not discarded as outliers. These are application-present timings, not a measurement of stream FPS, end-to-end latency or every displayed/generated frame.

If the difference is within run-to-run variation, collect more runs. Keep viewer quality alongside game FPS: a stream that freezes can make the game benchmark look better without being useful. HavijScreen has not yet established Discord-equivalent gameplay performance.
