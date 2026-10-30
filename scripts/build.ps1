param([string]$OutputDirectory = 'dist\HavijScreen')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkVersion = '1.0.4314.35'
$sdkPath = Join-Path $projectRoot 'vendor\webview2'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler is missing. Enable .NET Framework 4.8 on Windows.' }
if (-not (Test-Path -LiteralPath (Join-Path $sdkPath 'lib\net462\Microsoft.Web.WebView2.Core.dll'))) {
  New-Item -ItemType Directory -Path (Join-Path $projectRoot 'vendor') -Force | Out-Null
  $archive = Join-Path $projectRoot 'vendor\webview2.zip'
  Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$sdkVersion/microsoft.web.webview2.$sdkVersion.nupkg" -OutFile $archive
  Expand-Archive -LiteralPath $archive -DestinationPath $sdkPath -Force
}
$output = Join-Path $projectRoot $OutputDirectory
New-Item -ItemType Directory -Path $output -Force | Out-Null
$coreDll = Join-Path $sdkPath 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$formsDll = Join-Path $sdkPath 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
$audioVersion = '2.2.1'
foreach ($part in @('core','wasapi')) {
  $audioFolder = Join-Path $projectRoot "vendor\naudio-$part"
  if (-not (Test-Path -LiteralPath "$audioFolder\lib\netstandard2.0\NAudio.$part.dll")) {
    $audioArchive = Join-Path $projectRoot "vendor\naudio-$part.zip"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/naudio.$part/$audioVersion/naudio.$part.$audioVersion.nupkg" -OutFile $audioArchive
    Expand-Archive -LiteralPath $audioArchive -DestinationPath $audioFolder -Force
  }
}
$audioCore = Join-Path $projectRoot 'vendor\naudio-core\lib\netstandard2.0\NAudio.Core.dll'
$audioWasapi = Join-Path $projectRoot 'vendor\naudio-wasapi\lib\netstandard2.0\NAudio.Wasapi.dll'
$netstandardRef = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\netstandard') -Recurse -Filter netstandard.dll -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $netstandardRef) { throw 'The .NET Framework 4.8 netstandard facade is missing. Repair .NET Framework 4.8 before building.' }
$audioLicense = Join-Path $projectRoot 'vendor\NAudio-LICENSE.txt'
if (-not (Test-Path -LiteralPath $audioLicense)) { Invoke-WebRequest -Uri "https://raw.githubusercontent.com/naudio/NAudio/v$audioVersion/license.txt" -OutFile $audioLicense }
$source = Join-Path $projectRoot 'desktop\Program.cs'
$audioSource = Join-Path $projectRoot 'desktop\LoopbackAudio.cs'
$startupSource = Join-Path $projectRoot 'desktop\StartupSurface.cs'
$appIcon = Join-Path $projectRoot 'desktop\havijscreen.ico'
$carrot = Join-Path $projectRoot 'app\assets\carrot.png'
$manifest = Join-Path $projectRoot 'desktop\app.manifest'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$output\HavijScreen.exe" "/win32manifest:$manifest" "/win32icon:$appIcon" "/resource:$carrot,HavijScreen.Carrot.png" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$netstandardRef" "/reference:$audioCore" "/reference:$audioWasapi" "/reference:$coreDll" "/reference:$formsDll" $source $audioSource $startupSource
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Copy-Item -LiteralPath $coreDll,$formsDll -Destination $output -Force
Copy-Item -LiteralPath $audioCore,$audioWasapi,$audioLicense -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $sdkPath 'runtimes\win-x64\native\WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'app') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $sdkPath 'LICENSE.txt') -Destination (Join-Path $output 'WebView2-LICENSE.txt') -Force
$version = '0.2.0'
$zipPath = Join-Path $projectRoot "dist\HavijScreen-$version-win-x64.zip"
Compress-Archive -Path $output -DestinationPath $zipPath -Force
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-List
Write-Host "Built $zipPath"
