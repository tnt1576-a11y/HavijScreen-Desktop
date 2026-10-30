# Third-party software

HavijScreen uses unmodified, dynamically linked GStreamer 1.28.8 libraries and selected plugins from the official Windows x64 packages. They can be replaced with ABI-compatible builds. HavijScreen imposes no restriction on modifying these libraries or reverse engineering the application to debug those modifications.

GStreamer and GLib are provided under LGPL-2.0-or-later; libnice is available under LGPL-2.1-or-later or MPL-1.1. The media runtime also includes libffi (MIT), PCRE2 (BSD), zlib (Zlib), proxy-libintl (LGPL), OpenSSL (Apache-2.0), Opus (BSD), ORC (BSD), libSRTP (BSD), and bundled usrsctp (BSD). Selected Windows GPU plugins use the corresponding vendor's installed graphics driver. The application does not include the GPL codec plugin bundles, FFmpeg, Python, WebView2, or a browser runtime.

Exact copyright and license texts are included in `runtime/licenses`. The .NET runtime and Windows Forms license and third-party notices are in `licenses/dotnet`. Microsoft Visual C++ runtime terms are included in `runtime/licenses/Microsoft-Visual-C-Runtime.html`.

The matching source archives, upstream Cerbero build recipes and patches, checksums, and download locations are provided alongside the binary at:

https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/download/v0.3.0/HavijScreen-0.3.0-media-sources.zip

`runtime/packages.json` identifies the exact upstream binary packages and checksums. `runtime/licenses/sources.json` identifies the source archives. The source bundle contains the Cerbero 1.28.8 build system with its platform configurations and patches; see its README for rebuilding the upstream libraries. These notices and third-party licenses apply to their respective components.

Upstream projects:

- GStreamer and Cerbero: https://gstreamer.freedesktop.org/ and https://github.com/GStreamer/cerbero/tree/1.28.8
- .NET and Windows Forms: https://github.com/dotnet/runtime and https://github.com/dotnet/winforms
- Microsoft Visual C++ runtime: https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/
