param([string]$OutputDirectory = 'dist\native-0.3.0\HavijScreen', [switch]$SkipZip)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'prepare-native-sdk.ps1')
if (-not (Test-Path (Join-Path $projectRoot 'vendor\native-runtime\packages.json'))) { & (Join-Path $PSScriptRoot 'prepare-native.ps1') }
$runtime = Join-Path $projectRoot 'vendor\native-runtime'
$output = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $output.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside the project.' }
if ((Test-Path $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) { throw 'Use an empty output directory to avoid stale files.' }
$env:DOTNET_ROOT = Join-Path $projectRoot 'vendor\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'vendor\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'vendor\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& (Join-Path $env:DOTNET_ROOT 'dotnet.exe') publish (Join-Path $projectRoot 'native\HavijScreen.Native.csproj') -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed.' }

# Copy only the media plugins in use. Follow their PE import tables to include
# native dependencies, rather than shipping unrelated codecs and cloud plugins.
function Get-PeImports([string]$Path) {
  $bytes = [IO.File]::ReadAllBytes($Path)
  $pe = [BitConverter]::ToInt32($bytes,0x3c)
  if ([BitConverter]::ToInt32($bytes,$pe) -ne 0x4550) { throw "Invalid PE: $Path" }
  $sections = [BitConverter]::ToUInt16($bytes,$pe+6)
  $optional = $pe+24
  if ([BitConverter]::ToUInt16($bytes,$optional) -ne 0x20b) { throw "Expected x64 PE: $Path" }
  $sectionTable = $optional+[BitConverter]::ToUInt16($bytes,$pe+20)
  function Offset([uint32]$Rva) {
    for ($index=0; $index -lt $sections; $index++) {
      $item=$sectionTable+40*$index
      $address=[BitConverter]::ToUInt32($bytes,$item+12)
      $size=[Math]::Max([BitConverter]::ToUInt32($bytes,$item+8),[BitConverter]::ToUInt32($bytes,$item+16))
      if ($Rva -ge $address -and $Rva -lt $address+$size) { return [int]($Rva-$address+[BitConverter]::ToUInt32($bytes,$item+20)) }
    }
    throw "Invalid import RVA in $Path"
  }
  function ImportName([uint32]$Rva) {
    $start=Offset $Rva; $end=$start
    while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
    [Text.Encoding]::ASCII.GetString($bytes,$start,$end-$start)
  }
  $rva=[BitConverter]::ToUInt32($bytes,$optional+120)
  if ($rva) {
    $offset=Offset $rva
    while (($name=[BitConverter]::ToUInt32($bytes,$offset+12)) -ne 0) { ImportName $name; $offset+=20 }
  }
  $rva=[BitConverter]::ToUInt32($bytes,$optional+216)
  if ($rva) {
    $offset=Offset $rva
    while (($name=[BitConverter]::ToUInt32($bytes,$offset+4)) -ne 0) {
      if ([BitConverter]::ToUInt32($bytes,$offset) -ne 1) { throw "Unsupported absolute delay import: $Path" }
      ImportName $name; $offset+=32
    }
  }
}
$native = Join-Path $output 'runtime'
$bin = Join-Path $native 'bin'; $plugins = Join-Path $native 'lib\gstreamer-1.0'
$scanner = Join-Path $native 'libexec\gstreamer-1.0'
New-Item -ItemType Directory -Path $bin,$plugins,$scanner -Force | Out-Null
$pluginNames = @('coreelements','app','d3d11','nvcodec','amfcodec','qsv','videoconvertscale','videoparsersbad','typefindfunctions','wasapi2','audioconvert','audioresample','opus','rtp','rtpmanager','webrtc','nice','dtls','srtp','sctp','volume','videotestsrc','audiotestsrc')
$pending = [Collections.Generic.Queue[string]]::new()
foreach ($name in $pluginNames) {
  $path = Join-Path $runtime "lib\gstreamer-1.0\gst$name.dll"
  if (-not (Test-Path -LiteralPath $path)) { throw "Missing native plugin: $name" }
  Copy-Item -LiteralPath $path -Destination $plugins
  $pending.Enqueue($path)
}
$scanSource=Join-Path $runtime 'libexec\gstreamer-1.0\gst-plugin-scanner.exe'
Copy-Item -LiteralPath $scanSource -Destination $scanner
$pending.Enqueue($scanSource)
foreach ($name in @('gstreamer-1.0-0.dll','gobject-2.0-0.dll','glib-2.0-0.dll','gstapp-1.0-0.dll','gstwebrtc-1.0-0.dll','gstsdp-1.0-0.dll','gstvideo-1.0-0.dll','gstwebrtcnice-1.0-0.dll')) { $pending.Enqueue((Join-Path $runtime "bin\$name")) }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count) {
  $path=$pending.Dequeue()
  if (-not $seen.Add($path)) { continue }
  if ((Split-Path -Parent $path) -eq (Join-Path $runtime 'bin')) { Copy-Item -LiteralPath $path -Destination $bin }
  foreach ($name in (Get-PeImports $path)) {
    $dependency=Join-Path $runtime "bin\$name"
    if (Test-Path -LiteralPath $dependency) { $pending.Enqueue($dependency) }
  }
}
Copy-Item -LiteralPath (Join-Path $runtime 'packages.json') -Destination $native
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output
if (-not (Test-Path (Join-Path $runtime 'licenses'))) { throw 'Run scripts/prepare-native-notices.ps1 before packaging.' }
Copy-Item -LiteralPath (Join-Path $runtime 'licenses') -Destination $native -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'native\THIRD-PARTY.md') -Destination $output
$dotnetNotices=Join-Path $output 'licenses\dotnet'
New-Item -ItemType Directory -Path $dotnetNotices -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.win-x64\10.0.14\LICENSE.TXT') -Destination $dotnetNotices
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.win-x64\10.0.14\THIRD-PARTY-NOTICES.TXT') -Destination $dotnetNotices
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'microsoft.windowsdesktop.app.runtime.win-x64\10.0.14\LICENSE') -Destination (Join-Path $dotnetNotices 'WindowsDesktop-LICENSE.txt')
$files = Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
  [ordered]@{path=$_.FullName.Substring($output.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$files | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'files.json') -Encoding utf8
if (-not $SkipZip) {
  $zip=Join-Path $projectRoot 'dist\HavijScreen-0.3.0-win-x64.zip'
  Compress-Archive -LiteralPath $output -DestinationPath $zip -Force
  Get-FileHash -LiteralPath $zip -Algorithm SHA256
}
Write-Output "Native app: $output"
