$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $projectRoot 'vendor\dotnet'
$archive = Join-Path $projectRoot 'vendor\native-downloads\dotnet-sdk-10.0.403.zip'
if (Test-Path -LiteralPath (Join-Path $destination 'sdk\10.0.403\dotnet.dll')) { Write-Output 'Pinned .NET SDK already available'; exit 0 }
$meta = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$sdk = @($meta.releases | ForEach-Object { $_.sdk } | Where-Object { $_.version -eq '10.0.403' })[0]
$asset = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') })[0]
if (-not $asset) { throw 'Pinned .NET SDK not found in official release metadata' }
New-Item -ItemType Directory -Path (Split-Path -Parent $archive),$destination -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $asset.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $asset.hash.ToLowerInvariant()) { throw '.NET SDK checksum mismatch' }
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
[ordered]@{version=$sdk.version;url=$asset.url;sha512=$asset.hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'source.json') -Encoding utf8
Write-Output 'Pinned .NET SDK ready'
