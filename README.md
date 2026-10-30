# HavijScreen

<img src="app/assets/carrot.png" width="88" alt="HavijScreen carrot">

A small Windows desktop app for sharing gameplay with friends. Share a short room code, approve their join request, and stream directly between your PCs. No account or server of your own is required.

[Download the Windows preview](https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/download/v0.2.0/HavijScreen-0.2.0-win-x64.zip) · [Release notes](https://github.com/tnt1576-a11y/HavijScreen-Desktop/releases/tag/v0.2.0)

## Run on Windows

Extract **HavijScreen-0.2.0-win-x64.zip**, keep its files together, and run **HavijScreen.exe**. Requires 64-bit Windows 10/11, .NET Framework 4.8, and the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). No administrator permission is needed to run HavijScreen. The portable app uses the system WebView2 runtime rather than shipping a separate Chromium installation. Both people should use the current version.

This is an unsigned development preview. It has not been certified by an anti-cheat vendor or benchmarked across gaming systems.

## Connect to a friend

1. Both people open HavijScreen and choose a display name.
2. The host chooses **Invite someone**, then **Create room code**. Send the code privately to your friend.
3. The friend chooses **Join a friend's room**, enters the 16-character code, and chooses **Request to join**. Spaces, dashes and letter case do not matter.
4. The host sees the person's name in the room sidebar and chooses **Accept** or **Decline**. Connection details are exchanged automatically after approval; there is no reply to send back.
5. Either person chooses **Start sharing** and selects a monitor or application in the capture picker. The other chooses **Watch stream** beside their name. Choose **Enable sound** on the player to hear shared audio.

The same room code can invite up to three friends. Keep it private and only approve people you expect. Pending requests can wait up to ten minutes without starting a peer connection. Codes stay in memory while the host keeps them enabled; they are not saved between app sessions. **Disable room code** stops new requests without ending existing streams. Rooms support up to four people. The host can remove participants; leaving as host closes the room.

For pairing without the public connection service, choose **Use manual codes instead** on both PCs. The host creates an invitation, the friend pastes it and sends the generated reply back, and the host accepts that reply. The friend then chooses **Connect** after the host accepts. Copy/Paste buttons hide the long strings; **View code** and **Paste manually** remain available. Manual invitations are single-use and valid for ten minutes. Use a fresh invitation for each friend.

## Quality, audio and performance

- **Efficient:** up to 720p, 30 fps, 2.5 Mbps video per viewer. Start here when playing a demanding game.
- **Balanced:** up to 1080p, 30 fps, 5 Mbps per viewer.
- **Fluid:** up to 1080p, 60 fps, 8 Mbps per viewer.
- **Crisp:** up to 1440p, 60 fps, 12 Mbps per viewer.

Actual resolution and frame rate depend on the source and runtime. Each viewer gets a direct connection using the selected quality preset. WebRTC adapts to network conditions. Each additional viewer can add encoding work and upload use. Capture is shared across viewers; encoding is managed by WebRTC per connection.

No video is sent to a participant until they choose to watch. Local preview defaults to off. Minimize HavijScreen to pause local video playback and ask WebView2 to reduce memory use while the outgoing stream continues. The upload and round-trip figures come from WebRTC statistics; round trip is network RTT, not total glass-to-glass video latency. Encoder information is shown only if reported by the runtime. H.264 is preferred, but hardware encoding is not guaranteed. The small download does not imply a tiny memory footprint: WebView2 runs multiple processes, and memory use depends on the runtime, GPU, capture picker and active streams.

Select the monitor or application and **Share with system audio** in the capture picker. These choices are made in one place. Available audio sources depend on Windows and WebView2. System audio can include sounds from other applications. The stream banner shows **Audio on** or **Video only**. HavijScreen never requests microphone or camera access.

If the browser cannot start system audio, HavijScreen offers **Use Windows audio capture?** Choose **Choose source & share with audio**, then select the screen or window once more. HavijScreen captures the default playback device through Windows WASAPI and converts multichannel audio to stereo for sharing. This fallback includes all audio played on the default output device. It stops when sharing stops and requires no audio driver or device-setting changes. Cancelling either picker cancels sharing.

The title bar matches the app. Drag it to move the window, double-click it to maximize or restore, or use its minimize, maximize and close buttons.

The carrot appears in the app header, executable and taskbar. A native startup view appears immediately while the Windows web runtime loads, then switches to the ready interface.

Choose **Stop sharing** at any time. **Ctrl+Shift+S** stops capture globally when Windows permits HavijScreen to register that shortcut; the app displays whether it is available. Closing HavijScreen releases capture and disconnects peers.

## Networks

Short room codes use the free public [PeerJS Cloud signaling service](https://peerjs.com/server/getting-started) at `0.peerjs.com` over secure WebSockets. It delivers encrypted join requests and connection details; it does not carry screen or audio streams. HavijScreen encrypts these messages before sending them, and each approved guest gets a separate connection key. The service can see network addresses, timing and opaque routing identifiers. No account, media server or recording server is used. Subsequent group pairing travels through the host's encrypted WebRTC control channels.

Public service availability is outside HavijScreen's control. If it disconnects after a room has connected, existing streams continue. Reopen **Invite someone** and choose **Reconnect room code** to accept new requests. Manual codes remain available without this service.

**Use public STUN discovery** is enabled by default and contacts `stun.cloudflare.com:3478` to discover routes through your router. STUN carries discovery traffic, never your video. Turn it off for LAN-only media. Short room codes still contact PeerJS Cloud when STUN is off; choose manual codes as well to pair without a public setup service. Rejoin with a fresh connection when network conditions change.

Direct connections are not possible on every network. Symmetric NAT, carrier-grade NAT, enterprise firewalls and blocked UDP may prevent pairing. This version does not include a TURN relay. A trusted shared LAN or an independently configured VPN can provide another direct route. HavijScreen does not change VPN, router, firewall, or Windows security settings. Windows may request firewall access for WebView2.

## Privacy and games

Media and control channels use WebRTC's encrypted transports. Room codes contain 80 random bits; the public service receives a derived identifier rather than the code. Setup messages use AES-GCM, and approved connections use separate ECDH-derived keys. Manual replies are authenticated with a one-time secret from their invitation. Pairing authenticity depends on sharing codes privately and approving the intended person; display names alone do not prove identity. Connected peers can see each other's network addresses. The host is trusted to admit and remove participants. Anyone watching can use separate recording tools.

HavijScreen uses the runtime's capture picker outside the game process. It does not inject DLLs, hook graphics APIs, read game memory, install drivers or show an in-game overlay. Capture and encoding still consume resources. Anti-cheat rules and game capture restrictions vary; the app cannot promise universal compatibility or zero FPS loss. Protected or minimized windows may not capture correctly. Borderless window mode often provides a more usable capture source.

No video, room codes, pairing keys, room histories or credentials are persisted by the app. Only the name and stream preferences are stored locally in WebView2's app profile under `%LOCALAPPDATA%\HavijScreen`. The WebView2 runtime manages its own local caches and updates. Copy/Paste actions use the system clipboard, which may have history or synchronization enabled in Windows.

## Develop

The UI and WebRTC engine are vanilla HTML/CSS/JavaScript, with no production npm dependencies. The native host is C# WinForms and .NET Framework 4.8 with the official WebView2 SDK. Windows audio fallback uses [NAudio](https://github.com/naudio/NAudio), with its MIT license included in the package.

```powershell
npm start             # Loopback-only browser preview on http://127.0.0.1:4317
npm test              # Pairing protocol and capture recovery tests
npm run build         # Compile and zip the Windows desktop app
```

The build downloads the pinned Microsoft WebView2 SDK and NAudio dependencies to `vendor/` if necessary and compiles using the Windows .NET Framework compiler. It produces `dist/HavijScreen/HavijScreen.exe` and `dist/HavijScreen-0.2.0-win-x64.zip`. No Node.js process or local server is required to run the desktop app.

The browser integration suite (`npm run test:e2e`) requires Playwright. Install it locally with `npm install --no-save playwright`, then `npx playwright install chromium`, or set `DRIFT_PLAYWRIGHT_PATH` to an existing Playwright package and `DRIFT_BROWSER_PATH` to a Chromium executable. It uses an isolated mock signaling service and synthetic media. `node tests/cloud-smoke.mjs` separately checks encrypted setup against the real public service without capturing screen or audio.
