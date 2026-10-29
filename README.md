# Drift

A small Windows desktop app for sharing gameplay with a friend. Create an invitation, exchange a reply, and stream directly between your PCs. No account or hosted room server required.

## Run on Windows

Extract **Drift-0.1.0-win-x64.zip**, keep its files together, and run **Drift.exe**. Requires 64-bit Windows 10/11, .NET Framework 4.8, and the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). No administrator permission is needed to run Drift. The portable app uses the system WebView2 runtime rather than shipping a separate Chromium installation.

This is an unsigned development preview. It has not been certified by an anti-cheat vendor or benchmarked across gaming systems.

## Connect to a friend

1. Both people open Drift and choose a display name.
2. The host chooses **Invite someone**, then **Create invitation**. Copy the invitation and send it through a private, trusted channel.
3. The friend chooses **Join a friend's room**, pastes the invitation, and creates a reply. They send that reply back and keep Drift open.
4. The host pastes the reply and chooses **Accept reply & connect**.
5. Either person chooses **Start sharing** and selects a monitor or application in the capture picker. The other chooses **Watch stream** beside their name. Choose **Enable sound** on the player to hear shared audio.

Invitations are single-use, valid for ten minutes, and contain connection details. Keep them private. Each extra person needs their own new invitation. Rooms support up to four people. The host coordinates new connections and can remove participants. All media uses direct peer connections. Leaving as host closes the room.

## Quality, audio and performance

- **Efficient:** up to 720p, 30 fps, 2.5 Mbps video per viewer. Start here when playing a demanding game.
- **Balanced:** up to 1080p, 30 fps, 5 Mbps per viewer.
- **Fluid:** up to 1080p, 60 fps, 8 Mbps per viewer.
- **Crisp:** up to 1440p, 60 fps, 12 Mbps per viewer.

Actual resolution and frame rate depend on the source and runtime. The total video upload budget is divided among current viewers and also constrained by the selected quality preset. Captured audio and protocol overhead add to that budget. Bandwidth may undershoot the limit as WebRTC adapts to the network. Each additional viewer can add encoding work as well as upload use. Capture is shared across viewers; encoding is managed by WebRTC per connection.

No video is sent to a participant until they choose to watch. Local preview defaults to off. Minimize Drift to pause local video playback and ask WebView2 to reduce memory use while the outgoing stream continues. The upload and round-trip figures come from WebRTC statistics; round trip is network RTT, not total glass-to-glass video latency. Encoder information is shown only if reported by the runtime. H.264 is preferred, but hardware encoding is not guaranteed. The small download does not imply a tiny memory footprint: WebView2 runs multiple processes, and memory use depends on the runtime, GPU, capture picker and active streams.

Enable **Share audio** before starting, then enable audio in the capture picker if offered. The available sources depend on Windows and WebView2. A selected application's video may be accompanied by whole-system audio; check the picker before sharing. Drift reports when no audio track is provided. It never requests microphone or camera access. Changing **Share audio** during a stream mutes or unmutes an existing capture track; restart capture if the source did not originally include audio.

Choose **Stop sharing** at any time. **Ctrl+Shift+S** stops capture globally when Windows permits Drift to register that shortcut; the app displays whether it is available. Closing Drift releases capture and disconnects peers.

## Networks

Drift has no signaling server, account service, media server or recording server. The initial invitation/reply exchange carries the SDP and gathered ICE candidates. Subsequent group pairing travels through the host's encrypted WebRTC control channels.

**Use public STUN discovery** is enabled by default and contacts `stun.cloudflare.com:3478` to discover routes through your router. STUN carries discovery traffic, never your video. Turn it off for LAN-only connections. All participants should use fresh invitations when network conditions change.

Direct connections are not possible on every network. Symmetric NAT, carrier-grade NAT, enterprise firewalls and blocked UDP may prevent pairing. This version does not include a TURN relay. A trusted shared LAN or an independently configured VPN can provide another direct route. Drift does not change VPN, router, firewall, or Windows security settings. Windows may request firewall access for WebView2.

## Privacy and games

Media and control channels use WebRTC's encrypted transports. Reply codes are authenticated with a one-time AES-GCM secret from the invitation. Pairing authenticity depends on exchanging the invitation privately with the intended person. Connected peers can see each other's network addresses. The host is trusted to admit and remove participants. Anyone watching can use separate recording tools.

Drift uses the runtime's capture picker outside the game process. It does not inject DLLs, hook graphics APIs, read game memory, install drivers or show an in-game overlay. Capture and encoding still consume resources. Anti-cheat rules and game capture restrictions vary; the app cannot promise universal compatibility or zero FPS loss. Protected or minimized windows may not capture correctly. Borderless window mode often provides a more usable capture source.

No video, pairing codes, room histories or credentials are persisted by the app. Only the name and stream preferences are stored locally in WebView2's app profile under `%LOCALAPPDATA%\Drift`. The WebView2 runtime manages its own local caches and updates.

## Develop

The UI and WebRTC engine are vanilla HTML/CSS/JavaScript, with no production npm dependencies. The native host is C# WinForms and .NET Framework 4.8 with the official WebView2 SDK.

```powershell
npm start             # Loopback-only browser preview on http://127.0.0.1:4317
npm test              # Protocol, authentication, expiry and rate-limit tests
npm run build         # Compile and zip the Windows desktop app
```

The build downloads the pinned Microsoft WebView2 SDK to `vendor/` if necessary and compiles using the Windows .NET Framework compiler. It produces `dist/Drift/Drift.exe` and `dist/Drift-0.1.0-win-x64.zip`. No Node.js process or local server is required to run the desktop app.

The browser integration suite (`npm run test:e2e`) requires Playwright. Install it locally with `npm install --no-save playwright`, then `npx playwright install chromium`, or set `DRIFT_PLAYWRIGHT_PATH` to an existing Playwright package and `DRIFT_BROWSER_PATH` to a Chromium executable. It checks direct pairing, three-person mesh connectivity, synthetic video/audio transmission, subscriptions, quality limits, stopping and removal. Synthetic streams do not capture or send the user's desktop.
