# HavijScreen

<img src="app/assets/carrot.png" width="88" alt="HavijScreen carrot">

A native Windows app for sharing gameplay with friends. Share a short room code, approve their request, and stream directly between your PCs. Application capture includes that app's audio without sharing the rest of your PC.

[Download the Windows preview](https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/download/v0.5.2/HavijScreen-0.5.2-win-x64.zip) · [Release notes](https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/tag/v0.5.2)

## Run on Windows

Extract the entire ZIP and run **HavijScreen.exe**. Keep its files and `runtime` folder together. No installation, administrator access, WebView2, browser, Python or separate .NET installation is required.

Use **64-bit Windows 11** with a supported NVIDIA, AMD or Intel GPU and current graphics drivers. Sending requires a hardware H.264 encoder; receiving requires D3D11 H.264 decoding. HavijScreen reports unavailable hardware instead of silently using CPU encoding. Application audio requires Windows build 20348 or newer.

Both people must use **0.3.0 or newer**. Native rooms cannot join the older WebView2 version. This is an unsigned development preview.

## Share with a friend

1. Choose your display name.
2. The host selects **Invite someone → Create room code** and sends the code privately.
3. Their friend selects **Join a room**, enters the code and chooses **Request to join**. Letter case, spaces and dashes do not matter.
4. The host chooses **Accept** or **Decline** in the room sidebar. There is no reply code to send back.
5. Either person selects **Share your screen** or **Start sharing**, opens **Applications** or **Entire Screen**, and clicks the thumbnail to share. Hovering or focusing a card shows **Share Screen**. Use the refresh button to update the list.
6. Their friend chooses **Watch stream**. Playback starts muted; choose **Enable sound** to listen. Choose **Fullscreen** to expand the video. Press **Esc**, **F11**, or **Exit fullscreen** to return; sound and stop-watching controls stay available.

Rooms support four people. The host approves each person and can remove participants. Pending requests can wait up to ten minutes without opening a peer connection. **Disable room code** stops invitations while existing streams continue. Leaving as host closes the room. Room codes are not saved between sessions.

The Applications picker omits Windows Input Experience, Windows Settings, and windows hidden by the Windows compositor, including suspended app placeholders. Ordinary app windows remain available regardless of their title.

**Stop sharing** ends capture. **Ctrl+Shift+S** stops sharing globally when the shortcut is available; an in-app shortcut remains available if Windows cannot register it.

## Application and screen audio

In the source picker's gear menu, **Mute stream audio** controls audio for the selected source. Application audio is on by default and captures the selected application's Windows process and its child processes. Other applications' sounds are excluded. Separate windows belonging to the same application or process tree may share audio; for example, browser tabs are not independent audio sources. Protected audio and some applications may not support capture.

**Entire Screen** starts muted. Unchecking **Mute stream audio** explicitly enables audio from all apps on the PC, as indicated in the menu. If application audio fails, HavijScreen stops the share and reports the failure. It never substitutes whole-PC audio. You can choose the source again with stream audio muted to share video only.

No microphone or camera access is requested. Application capture uses Windows Graphics Capture and may show Windows' yellow capture border. Entire Screen uses DXGI Desktop Duplication, which does not use that border. Enabling local preview starts capture even without a viewer. Protected, minimized or exclusive-fullscreen windows may not capture correctly; borderless window mode can help.

## Quality and game performance

| Preset | Resolution | Frame rate | Target video upload per viewer |
| --- | --- | --- | --- |
| Performance | 720p | 15–30 fps, adaptive | 2.5 Mbps |
| Efficient | 720p | 30 fps | 2.5 Mbps |
| Balanced | 1080p | 30 fps | 5 Mbps |
| Fluid | 1080p | 60 fps | 8 Mbps |
| Crisp | 1440p | 60 fps | 12 Mbps |

Start with **Performance** for a demanding game. It begins at 720p/30, reduces capture to 20 or 15 fps if sustained encoding output falls behind, and cautiously tries a higher rate after recovery. Audio and peer connections keep running during these changes. It watches its own stream cadence, not the game's FPS or GPU utilization; it cannot detect every case where sharing affects a game. New preferences default to Performance; existing quality choices are preserved. The other presets keep a fixed capture-rate target.

Both people should update to **0.5.2 or newer** for improved motion-video recovery. Damaged video waits for a fresh keyframe instead of displaying broken blocks; severe packet loss can briefly freeze the picture.

Actual frame rate depends on the source, GPU and network. HavijScreen also lowers video bitrate when viewers report packet loss or rising network delay, then gradually raises it as the connection recovers, up to the selected preset's target. Choose a lower preset if the stream remains difficult to watch.

The source picker's gear menu offers **Performance**, **High quality** (1440p, 60 fps; previously called Gaming), **Screenshare** (source resolution, 15 fps), and **Custom**. Custom supports source resolution, 480p, 720p, 1080p or 1440p at 15, 30 or 60 fps. **Advanced** controls cursor capture. The bottom-left summary shows the settings that will apply when you click a source.

Capture, conversion and encoding use native Windows and GPU APIs. One hardware encode is shared among all viewers; each additional viewer adds upload and connection work. Three viewers at the Fluid preset can need roughly 24 Mbps of video upload, plus audio and network overhead.

Because viewers share one encode, the weakest reported connection sets the video bitrate for everyone. Without usable connection feedback, the app keeps its current target rather than assuming the network has recovered.

**Local preview** is off each time the app opens. Enable it to see your own shared source, with its sound muted. Preview alone captures at 720p and at most 30 fps without starting an encoder or audio capture. With viewers, preview shares the existing GPU capture path and one hardware encoder; preview rendering stays capped at 30 fps independently of the outgoing stream. Leave preview off to save rendering work. Capture stops when neither a viewer nor local preview needs it.

Minimizing HavijScreen pauses local preview and incoming streams, then resumes them when restored; outgoing sharing continues. Toggling local preview does not restart an active outgoing encoder or audio capture. The status area shows upload, encoder and viewer count.

The UI uses native Windows controls. The media engine uses DXGI Desktop Duplication for monitors, Windows Graphics Capture for applications, D3D11, WASAPI process loopback, hardware H.264 and Opus through GStreamer. Raw captured video stays in the GPU capture/convert/encode path; the app distributes the encoded packets. Download size is larger than the older web version because the native runtimes are included.

Capture, encoding and playback still consume resources. GPU contention, drivers and game settings affect FPS. HavijScreen has not been benchmarked across games or certified by anti-cheat vendors, and cannot promise zero FPS loss or universal compatibility. It does not inject DLLs into games, hook game graphics APIs, read game memory, install drivers or add an in-game overlay.

To measure your own setup against sharing off or Discord, follow the [game performance comparison guide](docs/game-performance.md). Optional recording scripts save frame timings locally and compare average FPS, 1% lows and frame-time spikes. They do not run as part of normal sharing.

## Connection and privacy

Short room codes use the public [PeerJS Cloud service](https://peerjs.com/server/getting-started) at `0.peerjs.com` over secure WebSockets for connection setup. Screen and audio travel directly between participants. Setup messages are encrypted with AES-GCM before sending; approved guests use separate P-256 ECDH-derived keys. Media and control traffic use WebRTC's encrypted transports.

The service sees IP addresses, timing and opaque routing identifiers. It receives a derived room identifier, not the 80-bit room code, and cannot read encrypted setup messages without the code or connection keys. Share codes privately and approve only expected people. Display names do not prove identity. Connected peers can see each other's network addresses, and viewers can use other tools to record what they receive.

**Public STUN discovery for internet connections** contacts `stun.cloudflare.com:3478` to find direct network routes. STUN does not carry media. Turn it off when creating a room if you only need a shared LAN or an independently configured VPN. Room-code setup still contacts PeerJS Cloud. There is no manual-code pairing in this native preview.

Public service availability is outside HavijScreen's control. If it disconnects after joining, existing streams continue. Reopen **Invite someone → Reconnect room code** to accept new requests.

Some networks block direct connections, including certain carrier-grade NAT and enterprise networks. This preview has no TURN relay. HavijScreen does not change firewall, router, VPN, driver or security settings. Windows may ask you to allow the app through the firewall.

The app does not save screen recordings, audio, room histories, codes or pairing keys. Source thumbnails stay local, are not saved, and stop updating when the picker closes. Name, quality, cursor and STUN preferences are stored in `%LOCALAPPDATA%\HavijScreen\Native\preferences.json`. The native runtime also stores a local plugin registry. Copying a code uses the Windows clipboard, which may have history or synchronization enabled.

## Build

On Windows x64, with PowerShell and internet access:

```powershell
.\scripts\prepare-native-sdk.ps1
.\scripts\prepare-native.ps1
.\scripts\prepare-native-notices.ps1
.\scripts\build-native.ps1
```

The scripts fetch a pinned, checksum-verified .NET SDK and official native media libraries into `vendor`, without installing them system-wide. The native app source is in `native`. The self-contained portable build is written to `dist/native-0.5.2/HavijScreen`, with a ZIP in `dist`. Build into an empty output directory. Source archives, build recipes and third-party notices accompany the release; see [THIRD-PARTY.md](native/THIRD-PARTY.md).

`npm run build` runs the native build script. The previous HTML/WebView2 implementation remains in `app` and `desktop`; `npm run build:legacy` builds it separately. Its browser preview and tests remain available for that implementation.
