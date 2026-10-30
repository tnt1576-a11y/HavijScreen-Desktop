$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sources = Join-Path $projectRoot 'vendor\native-sources'
$cerbero = Join-Path $sources 'cerbero-1.28.8'
$bundle = Join-Path $projectRoot 'vendor\media-source-bundle'
$notices = Join-Path $projectRoot 'vendor\native-runtime\licenses'
New-Item -ItemType Directory -Path $sources,$bundle,$notices -Force | Out-Null
$recipeArchive = Join-Path $sources 'cerbero-1.28.8.tar.gz'
if (-not (Test-Path $recipeArchive)) { Invoke-WebRequest 'https://codeload.github.com/GStreamer/cerbero/tar.gz/refs/tags/1.28.8' -OutFile $recipeArchive }
if ((Get-FileHash $recipeArchive -Algorithm SHA256).Hash -ne '50361646DBAB5C2CCD3FF5A4135FE1AAB78EBB8D0F00C21B778CD46AB8FAAF34') { throw 'Cerbero source checksum mismatch' }
if (-not (Test-Path $cerbero)) { & tar -xf $recipeArchive -C $sources; if ($LASTEXITCODE) { throw 'Source extraction failed' } }
Copy-Item -LiteralPath $recipeArchive -Destination $bundle -Force
Copy-Item -LiteralPath (Join-Path $cerbero 'data\licenses') -Destination (Join-Path $notices 'license-texts') -Recurse -Force
$items = @(
  @('gstreamer-1.0','https://gstreamer.freedesktop.org/src/gstreamer/gstreamer-1.28.8.tar.xz'),
  @('gst-plugins-base-1.0','https://gstreamer.freedesktop.org/src/gst-plugins-base/gst-plugins-base-1.28.8.tar.xz'),
  @('gst-plugins-good-1.0','https://gstreamer.freedesktop.org/src/gst-plugins-good/gst-plugins-good-1.28.8.tar.xz'),
  @('gst-plugins-bad-1.0','https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-1.28.8.tar.xz'),
  @('glib','https://download.gnome.org/sources/glib/2.82/glib-2.82.4.tar.xz'),
  @('libffi','https://gitlab.freedesktop.org/gstreamer/meson-ports/libffi/-/archive/meson-3.2.9999.5/libffi-meson-3.2.9999.5.tar.bz2'),
  @('pcre2','https://github.com/PhilipHazel/pcre2/releases/download/pcre2-10.42/pcre2-10.42.tar.bz2'),
  @('zlib','https://zlib.net/fossils/zlib-1.3.1.tar.gz'),
  @('proxy-libintl','https://github.com/frida/proxy-libintl/archive/refs/tags/0.5.tar.gz'),
  @('libnice','https://libnice.freedesktop.org/releases/libnice-0.1.24.tar.gz'),
  @('openssl','https://github.com/openssl/openssl/releases/download/openssl-3.5.0/openssl-3.5.0.tar.gz'),
  @('opus','https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz'),
  @('libsrtp','https://github.com/cisco/libsrtp/archive/v2.8.0.tar.gz'),
  @('orc','https://gstreamer.freedesktop.org/src/orc/orc-0.4.42.tar.xz'),
  @('directx-headers','https://github.com/microsoft/DirectX-Headers/archive/refs/tags/v1.611.0.tar.gz')
)
$records=@()
foreach ($item in $items) {
  $name=$item[0]; $url=$item[1]
  $recipe=Get-Content (Join-Path $cerbero "recipes\$name.recipe") -Raw
  $match=[regex]::Match($recipe,"tarball_checksum\s*=\s*'([a-f0-9]{64})'")
  if (-not $match.Success) { throw "Missing pinned checksum for $name" }
  $hash=$match.Groups[1].Value
  $archive=Join-Path $bundle ($name+'-'+($url.Split('/')[-1]))
  if (-not (Test-Path $archive) -or (Get-Item $archive).Length -eq 0) {
    & curl.exe --fail --silent --show-error --location --max-time 60 --retry 1 $url --output $archive
    if ($LASTEXITCODE) { throw "Source download failed: $name" }
  }
  if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
    # Cerbero uses a recipe-namespaced upstream mirror when a source host
    # serves a bot challenge or removes an older release.
    $mirror='https://gstreamer.freedesktop.org/src/mirror/'+$name+'/'+$url.Split('/')[-1]
    & curl.exe --fail --silent --show-error --location --max-time 60 --retry 1 $mirror --output $archive
    if ($LASTEXITCODE -or (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Source checksum mismatch: $name" }
    $url=$mirror
  }
  $entries=@(& tar -tf $archive)
  if ($LASTEXITCODE) { throw "Could not list $name source" }
  if ($entries | Where-Object { $_ -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))' }) { throw 'Unsafe source archive path' }
  $licenseFiles=@($entries | Where-Object { $_ -notmatch '/$' -and ($_ -match '(?i)(^|/)(copying[^/]*|licen[sc]e[^/]*|notice[^/]*|authors[^/]*)$' -or $_ -match '(?i)(^|/)licen[sc]es?/') })
  $target=Join-Path $notices $name
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  # Preserve the exact upstream copyright and license notices, including any
  # bundled third-party code. The complete archives accompany the release.
  if ($licenseFiles.Count) { & tar -xf $archive -C $target @licenseFiles; if ($LASTEXITCODE) { throw "Notice extraction failed: $name" } }
  $records += [ordered]@{name=$name;url=$url;sha256=$hash;archive=(Split-Path -Leaf $archive);recipe="cerbero-1.28.8/recipes/$name.recipe"}
}
$records | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $bundle 'sources.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $bundle 'sources.json') -Destination $notices -Force
$vcUrl='https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/'
Invoke-WebRequest $vcUrl -OutFile (Join-Path $notices 'Microsoft-Visual-C-Runtime.html')
Copy-Item -LiteralPath (Join-Path $projectRoot 'native\THIRD-PARTY.md') -Destination $bundle -Force
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'dist') -Force | Out-Null
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath (Join-Path $projectRoot 'dist\HavijScreen-0.3.0-media-sources.zip') -Force
Write-Output 'Native license notices and corresponding media sources prepared.'
