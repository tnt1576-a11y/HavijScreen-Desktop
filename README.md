# HavijScreen

<img src="app/assets/carrot.png" width="88" alt="HavijScreen carrot">

A native Windows app for sharing gameplay with friends. Share a short room code, approve their request, and stream directly between your PCs. Application capture includes that app's audio without sharing the rest of your PC.

[Download the Windows preview](https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/download/v0.3.0/HavijScreen-0.3.0-win-x64.zip) · [Release notes](https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/tag/v0.3.0)

## Run on Windows

Extract the entire ZIP and run **HavijScreen.exe**. Keep its files and `runtime` folder together. No installation, administrator access, WebView2, browser, Python or separate .NET installation is required.

Use **64-bit Windows 11** with a supported NVIDIA, AMD or Intel GPU and current graphics drivers. Sending requires a hardware H.264 encoder; receiving requires D3D11 H.264 decoding. HavijScreen reports unavailable hardware instead of silently using CPU encoding. Application audio requires Windows build 20348 or newer.

Both people must use **0.3.0 or newer**. Native rooms cannot join the older WebView2 version. This is an unsigned development preview.

## Share with a friend

1. Choose your display name.
2. The host selects **Invite someone → Create room code** and sends the code privately.
3. Their friend selects **Join a room**, enters the code and chooses **Request to join**. Letter case, spaces and dashes do not matter.
4. The host chooses **Accept** or **Decline** in the room sidebar. There is no reply code to send back.
5. Either person selects **Start sharing**, chooses an **Application** or **Entire screen**, and selects audio in the same picker.
6. Their friend chooses **Watch stream**. Playback starts muted; choose **Enable sound** to listen.

Rooms support four people. The host approves each person and can remove participants. Pending requests can wait up to ten minutes without opening a peer connection. **Disable room code** stops invitations while existing streams continue. Leaving as host closes the room. Room codes are not saved between sessions.

**Stop sharing** ends capture. **Ctrl+Shift+S** stops sharing globally when the shortcut is available; an in-app shortcut remains available if Windows cannot register it.

## Application and screen audio

**Application → Share app audio** captures the selected application's Windows process and its child processes. Other applications' sounds are excluded. Separate windows belonging to the same application or process tree may share audio; for example, browser tabs are not independent audio sources. Protected audio and some applications may not support capture.

**Entire screen → Share all system audio** includes sounds from other apps and is off by default. It requires an explicit selection. If application audio fails, HavijScreen stops the share and reports the failure. It never substitutes whole-PC audio. You can choose the source again with audio unchecked to share video only.

No microphone or camera access is requested. Windows may show its capture border around the selected source. Protected, minimized or exclusive-fullscreen windows may not capture correctly; borderless window mode can help.

## Quality and game performance

| Preset | Resolution | Frame rate | Target video upload per viewer |
| --- | --- | --- | --- |
| Efficient | 720p | 30 fps | 2.5 Mbps |
| Balanced | 1080p | 30 fps | 5 Mbps |
| Fluid | 1080p | 60 fps | 8 Mbps |
| Crisp | 1440p | 60 fps | 12 Mbps |

Start with Efficient for a demanding game. Actual frame rate depends on the source, GPU and network. Choose a lower preset if the connection cannot carry the target bitrate; this preview uses fixed quality targets rather than automatically changing quality.

Capture, conversion and encoding use native Windows and GPU APIs. One hardware encode is shared among all viewers; each additional viewer adds upload and connection work. Three viewers at the Fluid preset can need roughly 24 Mbps of video upload, plus audio and network overhead.

Capture and encoding start only when someone watches and stop when the last viewer stops watching. There is no local preview consuming rendering work. Minimizing HavijScreen pauses incoming streams and resumes them when restored; outgoing sharing continues. The status area shows upload, encoder and viewer count.

The UI uses native Windows controls. The media engine uses Windows Graphics Capture, D3D11, WASAPI process loopback, hardware H.264 and Opus through GStreamer. Raw captured video stays in the GPU capture/convert/encode path; the app distributes the encoded packets. Download size is larger than the older web version because the native runtimes are included.

Capture, encoding and playback still consume resources. GPU contention, drivers and game settings affect FPS. HavijScreen has not been benchmarked across games or certified by anti-cheat vendors, and cannot promise zero FPS loss or universal compatibility. It does not inject DLLs into games, hook game graphics APIs, read game memory, install drivers or add an in-game overlay.

## Connection and privacy

Short room codes use the public [PeerJS Cloud service](https://peerjs.com/server/getting-started) at `0.peerjs.com` over secure WebSockets for connection setup. Screen and audio travel directly between participants. Setup messages are encrypted with AES-GCM before sending; approved guests use separate P-256 ECDH-derived keys. Media and control traffic use WebRTC's encrypted transports.

The service sees IP addresses, timing and opaque routing identifiers. It receives a derived room identifier, not the 80-bit room code, and cannot read encrypted setup messages without the code or connection keys. Share codes privately and approve only expected people. Display names do not prove identity. Connected peers can see each other's network addresses, and viewers can use other tools to record what they receive.

**Public STUN discovery for internet connections** contacts `stun.cloudflare.com:3478` to find direct network routes. STUN does not carry media. Turn it off when creating a room if you only need a shared LAN or an independently configured VPN. Room-code setup still contacts PeerJS Cloud. There is no manual-code pairing in this native preview.

Public service availability is outside HavijScreen's control. If it disconnects after joining, existing streams continue. Reopen **Invite someone → Reconnect room code** to accept new requests.

Some networks block direct connections, including certain carrier-grade NAT and enterprise networks. This preview has no TURN relay. HavijScreen does not change firewall, router, VPN, driver or security settings. Windows may ask you to allow the app through the firewall.

The app does not save screen recordings, audio, room histories, codes or pairing keys. Name, quality and STUN preferences are stored in `%LOCALAPPDATA%\HavijScreen\Native\preferences.json`. The native runtime also stores a local plugin registry. Copying a code uses the Windows clipboard, which may have history or synchronization enabled.

## Build

On Windows x64, with PowerShell and internet access:

```powershell
.\scripts\prepare-native-sdk.ps1
.\scripts\prepare-native.ps1
.\scripts\prepare-native-notices.ps1
.\scripts\build-native.ps1
```

The scripts fetch a pinned, checksum-verified .NET SDK and official native media libraries into `vendor`, without installing them system-wide. The native app source is in `native`. The self-contained portable build is written to `dist/native-0.3.0/HavijScreen`, with a ZIP in `dist`. Build into an empty output directory. Source archives, build recipes and third-party notices accompany the release; see [THIRD-PARTY.md](native/THIRD-PARTY.md).

`npm run build` runs the native build script. The previous HTML/WebView2 implementation remains in `app` and `desktop`; `npm run build:legacy` builds it separately. Its browser preview and tests remain available for that implementation.
