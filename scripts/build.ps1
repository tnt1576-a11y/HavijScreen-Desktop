param([string]$OutputDirectory = 'dist\Drift')
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
$source = Join-Path $projectRoot 'desktop\Program.cs'
$manifest = Join-Path $projectRoot 'desktop\app.manifest'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$output\Drift.exe" "/win32manifest:$manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$coreDll" "/reference:$formsDll" $source
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Copy-Item -LiteralPath $coreDll,$formsDll -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $sdkPath 'runtimes\win-x64\native\WebView2Loader.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'app') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $sdkPath 'LICENSE.txt') -Destination (Join-Path $output 'WebView2-LICENSE.txt') -Force
$zipPath = Join-Path $projectRoot 'dist\Drift-0.1.0-win-x64.zip'
Compress-Archive -Path $output -DestinationPath $zipPath -Force
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-List
Write-Host "Built $zipPath"
