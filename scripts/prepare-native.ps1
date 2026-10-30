param([string]$Destination = 'vendor\native-runtime')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $projectRoot 'vendor\native-downloads'
$runtime = Join-Path $projectRoot $Destination
New-Item -ItemType Directory -Path $downloads,$runtime -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$records = @()
foreach ($name in @('gstreamer-libs','gstreamer-ext-runtime','gstreamer-plugins-libs','gstreamer-plugins','gstreamer-plugins-restricted','gstreamer-cli')) {
  $meta = Invoke-RestMethod -Uri "https://pypi.org/pypi/$name/1.28.8/json"
  $asset = @($meta.urls | Where-Object { $_.filename -match 'cp313-cp313t-win_amd64\.whl$' })
  if ($asset.Count -ne 1) { throw "Missing pinned native package: $name" }
  $asset = $asset[0]
  $archive = Join-Path $downloads ($name + '.whl')
  if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $asset.url -OutFile $archive }
  $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -ne $asset.digests.sha256) { throw "Checksum mismatch: $name" }
  $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
  try {
    foreach ($entry in $zip.Entries) {
      # Wheels carry the upstream native runtime under purelib/<package>.
      # Python extensions are not used by the application.
      if ($entry.FullName -match '\.dist-info/licenses/(.+)$') { $relative = 'share/licenses/' + $name + '/' + $Matches[1] }
      elseif ($entry.FullName -match '\.data/purelib/[^/]+/(.+)$') { $relative = $Matches[1] }
      else { continue }
      if ($relative -notmatch '^(bin|lib|libexec|share)/' -or $relative -match '\.py[cd]?$') { continue }
      $target = [IO.Path]::GetFullPath((Join-Path $runtime $relative))
      if (-not $target.StartsWith([IO.Path]::GetFullPath($runtime)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid archive path' }
      if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
      New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
    }
  } finally { $zip.Dispose() }
  $records += [ordered]@{name=$name;version='1.28.8';url=$asset.url;sha256=$hash}
}
$records | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runtime 'packages.json') -Encoding utf8
Write-Output "Native runtime ready: $runtime"
