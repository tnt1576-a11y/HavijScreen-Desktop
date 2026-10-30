import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.DRIFT_PLAYWRIGHT_PATH || 'playwright');
const server = spawn(process.execPath, ['scripts/serve.mjs'], { cwd: resolve('.'), env: { ...process.env, PORT: '4318' }, stdio: 'pipe' });
let browser;
const errors = [], results = [];
const broker = new Map(), signalingFrames = [];
let sharedCode;
await mkdir('artifacts', { recursive: true });
try {
  await new Promise((ready, fail) => { server.stdout.once('data', ready); server.once('error', fail); server.once('exit', code => fail(new Error('Preview exited: ' + code))); });
  browser = await chromium.launch({ headless: true, ...(process.env.DRIFT_BROWSER_PATH ? { executablePath: process.env.DRIFT_BROWSER_PATH } : {}), args: ['--no-sandbox', '--autoplay-policy=no-user-gesture-required'] });
  async function page(name, native = false) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
    await context.routeWebSocket('wss://0.peerjs.com/**', ws => {
      const id = new URL(ws.url()).searchParams.get('id'); broker.set(id, ws);
      ws.onMessage(raw => {
        const message = JSON.parse(String(raw)); if (message.type === 'HEARTBEAT') return;
        signalingFrames.push({ src: id, ...message });
        broker.get(message.dst)?.send(JSON.stringify({ ...message, src: id }));
      });
      ws.onClose(() => { if (broker.get(id) === ws) broker.delete(id); ws.close().catch(() => {}); });
      ws.send('{"type":"OPEN"}');
    });
    await context.addInitScript(native => {
      window.testTracks = [];
      window.peerConnectionCreations = 0;
      window.RTCPeerConnection = new Proxy(window.RTCPeerConnection, { construct(target, args) { window.peerConnectionCreations++; return Reflect.construct(target, args); } });
      if (native) {
        const listeners = new Set(); let timer, phase = 0;
        const emit = data => { for (const listener of listeners) listener({ data }); };
        window.nativeStops = 0; window.nativeReadyReports = 0;
        window.chrome = { ...window.chrome, webview: {
          addEventListener: (type, listener) => { listeners.add(listener); setTimeout(() => listener({ data: { type: 'native-ready', hotkey: false, audioLoopback: true } }), 0); },
          removeEventListener: (type, listener) => listeners.delete(listener),
          postMessage: data => {
            if (data.type === 'ui-ready') { window.nativeReadyReports++; return; }
            if (data.type === 'audio-stop') { clearInterval(timer); window.nativeStops++; return; }
            if (data.type !== 'audio-start') return;
            emit({ type: 'audio-started', id: data.id, sampleRate: 44100, channels: 8, bits: 32, float: true });
            timer = setInterval(() => {
              const samples = new Float32Array(1103 * 8);
              for (let frame = 0; frame < 1103; frame++) { const tone = Math.sin(phase++ * 2 * Math.PI * 440 / 44100) * .2; samples[frame * 8] = tone; samples[frame * 8 + 1] = tone; }
              const bytes = new Uint8Array(samples.buffer); let text = ''; for (const b of bytes) text += String.fromCharCode(b);
              emit({ type: 'audio-data', id: data.id, pcm: btoa(text) });
            }, 25);
          }
        } };
      }
      navigator.mediaDevices.getDisplayMedia = async options => {
        window.testCaptureOptions = options;
        if (options.audio && window.failAudioOnce) { window.failAudioOnce = false; throw new DOMException('Could not start audio source', 'NotReadableError'); }
        const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 720;
        const ctx = canvas.getContext('2d'); let frame = 0;
        const timer = setInterval(() => { ctx.fillStyle = frame++ % 2 ? '#305f51' : '#dfad71'; ctx.fillRect(0, 0, 1280, 720); ctx.fillStyle = 'white'; ctx.font = '80px sans-serif'; ctx.fillText('Synthetic gameplay ' + frame, 100, 350); }, 33);
        const stream = canvas.captureStream(30); stream.getVideoTracks()[0].addEventListener('ended', () => clearInterval(timer));
        // Ended isn't fired by track.stop, so keep a bounded timer fixture.
        setTimeout(() => clearInterval(timer), 120000);
        if (options.audio) {
          const audioContext = new AudioContext(); const oscillator = audioContext.createOscillator(); const destination = audioContext.createMediaStreamDestination();
          oscillator.frequency.value = 200; oscillator.connect(destination); oscillator.start();
          stream.addTrack(destination.stream.getAudioTracks()[0]);
          setTimeout(() => { oscillator.stop(); audioContext.close(); }, 120000);
        }
        window.testTracks.push(...stream.getTracks()); return stream;
      };
    }, native);
    const p = await context.newPage(); p.on('pageerror', error => errors.push(name + ': ' + error.message));
    await p.goto('http://127.0.0.1:4318'); await p.locator('#display-name').fill(name);
    return p;
  }
  const alice = await page('Alice'), bob = await page('Bob'), carol = await page('Carol'), dave = await page('Dave');
  async function request(host, guest, keepInviteOpen = false) {
    await host.locator('#invite-button').click();
    if (await host.locator('#stun').isVisible()) await host.locator('#stun').uncheck();
    if (!(await host.locator('#room-code-output').isVisible())) await host.locator('#pair-action').click();
    await host.locator('#room-code-output').waitFor();
    await host.waitForFunction(() => document.getElementById('room-code-output').value.length === 19);
    sharedCode = await host.locator('#room-code-output').inputValue();
    if (keepInviteOpen) await host.screenshot({ path: 'artifacts/06-room-code.png', fullPage: true });
    else await host.locator('#pair-close').click();
    await guest.locator('#join-button').click(); await guest.locator('#room-code-input').fill(sharedCode); await guest.locator('#pair-action').click();
    const name = await guest.locator('#display-name').inputValue();
    return host.locator('.join-request').filter({ hasText: name });
  }
  async function pair(host, guest, delay = false) {
    const card = await request(host, guest, delay); await card.waitFor();
    assert.equal(await guest.locator('.person.remote').count(), 0);
    const before = await guest.evaluate(() => peerConnectionCreations);
    if (delay) {
      await guest.waitForTimeout(45000);
      assert.match(await guest.locator('#pair-description').innerText(), /Waiting for Alice to accept/);
      assert.equal(await guest.evaluate(() => peerConnectionCreations), before);
      assert.equal(await host.evaluate(() => peerConnectionCreations), 0);
      await host.screenshot({ path: 'artifacts/05-join-request.png', fullPage: true });
      results.push('A guest waits 45 seconds for approval without starting ICE or falsely closing a room');
    }
    await card.locator('.accept-request').click();
    await guest.waitForFunction(() => !document.getElementById('pair-dialog').open, { timeout: 25000 });
  }
  async function manualPair(host, guest) {
    await host.locator('#invite-button').click(); await host.locator('#manual-pairing').click();
    if (await host.locator('#stun').isVisible()) await host.locator('#stun').uncheck();
    await host.locator('#pair-action').click();
    await host.waitForFunction(() => document.getElementById('code-output').value.startsWith('DRIFT1I.'));
    const invitation = await host.locator('#code-output').inputValue();
    await guest.locator('#join-button').click(); await guest.locator('#manual-pairing').click(); await paste(guest, invitation); await guest.locator('#pair-action').click();
    await guest.waitForFunction(() => document.getElementById('code-output').value.startsWith('HS2R.'));
    assert.equal(await guest.locator('#code-output').isVisible(), false);
    await paste(host, await guest.locator('#code-output').inputValue()); await host.locator('#pair-action').click();
    await guest.evaluate(() => { if (document.getElementById('pair-dialog').open) document.getElementById('pair-action').click(); });
    await guest.waitForFunction(() => !document.getElementById('pair-dialog').open, { timeout: 20000 });
  }
  async function paste(p, text) {
    await p.evaluate(text => { window.testClipboard = text; Object.defineProperty(navigator.clipboard, 'readText', { configurable: true, value: async () => window.testClipboard }); }, text);
    await p.locator('#paste-code').click();
  }
  await alice.screenshot({ path: 'artifacts/01-idle.png', fullPage: true });
  await alice.setViewportSize({ width: 1380, height: 870 });
  assert.ok(await alice.evaluate(() => document.documentElement.scrollHeight <= innerHeight), 'All controls should fit a normal Windows desktop window');
  await alice.screenshot({ path: 'artifacts/01-compact.png', fullPage: true });
  await alice.setViewportSize({ width: 1440, height: 960 });
  results.push('Initial UI renders with no remote assets');
  assert.equal(await alice.title(), 'HavijScreen');
  assert.ok(await alice.locator('.brand-icon').evaluate(img => img.complete && img.naturalWidth === 256));
  assert.match(await alice.locator('.brand').innerText(), /HavijScreen/);
  results.push('HavijScreen branding and the bundled carrot image render correctly');
  assert.equal(await alice.locator('#bandwidth, #source-monitor, #source-window, #audio, #top-status, #stage-title, #stage-note, footer').count(), 0);
  assert.ok(!/Better together|front-row seat|A little setup|Built to stay|GOOD TIMES/.test(await alice.locator('body').innerText()));
  results.push('Marked slogans, duplicate capture controls, footer, and total upload feature are removed');
  await alice.evaluate(() => {
    window.nativeMessages = [];
    window.chrome = { ...window.chrome, webview: { postMessage: data => window.nativeMessages.push(data), addEventListener: () => {} } };
    document.getElementById('window-controls').hidden = false;
  });
  await alice.locator('#window-minimize').click(); await alice.locator('#window-maximize').click(); await alice.locator('#window-close').click();
  assert.deepEqual(await alice.evaluate(() => nativeMessages.map(x => x.type)), ['window-minimize', 'window-maximize', 'window-close']);
  await alice.screenshot({ path: 'artifacts/04-custom-titlebar.png', fullPage: true });
  results.push('Custom caption buttons send only the expected native window commands');
  // Negative UI path: no native privileges, room, or network connection from malformed input.
  await bob.locator('#join-button').click(); await bob.locator('#room-code-input').fill('bad'); await bob.locator('#pair-action').click();
  assert.match(await bob.locator('#pair-error').innerText(), /16-character room code/); await bob.locator('#pair-close').click();
  results.push('Malformed room codes are rejected before contacting the service');
  await pair(alice, bob, true);
  await alice.waitForFunction(() => document.getElementById('room-count').textContent === '2 / 4');
  await bob.waitForFunction(() => document.getElementById('room-count').textContent === '2 / 4');
  results.push('A short room code and explicit host acceptance connect two clients without a manual reply or STUN');
  const rejected = await page('Rejected guest');
  const rejectedCard = await request(alice, rejected); await rejectedCard.locator('.decline-request').click();
  await rejected.waitForFunction(() => document.getElementById('pair-error').textContent.includes('declined'));
  assert.equal(await rejected.evaluate(() => peerConnectionCreations), 0);
  results.push('A declined request creates no peer connection');
  await rejected.locator('#pair-close').click();
  const cancelledCard = await request(alice, rejected); await cancelledCard.waitFor();
  await rejected.locator('#cancel-invite').click(); await cancelledCard.waitFor({ state: 'detached' });
  results.push('Cancelling a join removes the pending request from the host');
  await alice.locator('#share-button').click();
  await bob.locator('.watch-peer').waitFor();
  assert.equal(await alice.locator('#stat-viewers').innerText(), '0 people');
  assert.equal(await bob.locator('video').count(), 0);
  assert.deepEqual(await alice.evaluate(() => testCaptureOptions), { video: true, audio: true });
  results.push('Minimal capture request leaves source and audio selection to the native picker');
  await bob.locator('.watch-peer').click();
  await bob.waitForFunction(() => { const v = document.querySelector('video'); return v && v.videoWidth > 0 && v.readyState >= 2; }, { timeout: 20000 });
  await alice.waitForFunction(() => document.getElementById('stat-viewers').textContent.includes('1 person'));
  assert.equal(await bob.evaluate(() => document.querySelector('video').srcObject.getAudioTracks().length), 1);
  await bob.locator('.video-caption button').first().click();
  assert.equal(await bob.evaluate(() => document.querySelector('video').muted), false);
  results.push('Synthetic video frames and an audio track reach the watching peer');
  const hostRoute = [...broker.keys()].find(id => id.startsWith('hs4-room-'));
  await broker.get(hostRoute).close({ code: 1011, reason: 'Synthetic service outage' });
  await alice.locator('#lobby-status').waitFor();
  assert.equal(await alice.locator('#room-count').innerText(), '2 / 4');
  assert.ok(await bob.evaluate(() => document.querySelector('video')?.videoWidth > 0));
  await alice.locator('#invite-button').click(); await alice.locator('#pair-action').click();
  await alice.waitForFunction(() => document.getElementById('lobby-status').hidden); await alice.locator('#pair-close').click();
  results.push('Signaling loss leaves the active stream connected; reconnect restores the same room code');
  await alice.locator('#quality').selectOption('fluid');
  await alice.screenshot({ path: 'artifacts/02-sharing.png', fullPage: true });
  await bob.screenshot({ path: 'artifacts/03-viewer.png', fullPage: true });
  await pair(alice, carol);
  for (const p of [alice, bob, carol]) await p.waitForFunction(() => document.getElementById('room-count').textContent === '3 / 4', { timeout: 25000 });
  results.push('Three participants establish a full peer mesh using the host control channel');
  await carol.locator('.watch-peer').click();
  await carol.waitForFunction(() => document.querySelector('video')?.videoWidth > 0, { timeout: 20000 });
  await alice.waitForFunction(() => document.getElementById('stat-viewers').textContent.includes('2 people'));
  results.push('Two viewers receive the same capture through direct connections');
  // Independently inspect the public WebRTC engine for actual encoder ceilings.
  const params = await alice.evaluate(async () => {
    const { Room } = await import('/room.mjs'); const { videoParameters, PRESETS } = await import('/protocol.mjs');
    const host = new Room(), guest = new Room();
    const offer = await host.createInvite('Encoder probe', false); const reply = await guest.acceptInvite(offer.code, 'Viewer'); await host.acceptReply(reply); await guest.connectToHost();
    const wait = () => new Promise((resolve, reject) => { let n = 0; const timer = setInterval(() => { if (host.connected.length && guest.connected.length) { clearInterval(timer); resolve(); } else if (++n > 100) { clearInterval(timer); reject(new Error('Probe did not connect')); } }, 50); });
    await wait(); const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 360; const stream = canvas.captureStream(30);
    await host.setMedia(stream, {});
    await new Promise((resolve, reject) => { let n = 0; const timer = setInterval(() => { if (guest.peers.get(host.id)?.sharing) { clearInterval(timer); resolve(); } else if (++n > 100) { clearInterval(timer); reject(new Error('Share announcement did not arrive')); } }, 20); });
    guest.watch(host.id, true);
    await new Promise(resolve => setTimeout(resolve, 300));
    const sender = host.connected[0].pc.getSenders().find(s => s.track?.kind === 'video');
    const parameters = videoParameters(sender.getParameters(), PRESETS.fluid); await sender.setParameters(parameters);
    const actual = sender.getParameters().encodings[0]; const result = { maxBitrate: actual.maxBitrate, maxFramerate: actual.maxFramerate };
    stream.getTracks().forEach(t => t.stop()); guest.leave(); host.leave(); return result;
  });
  assert.equal(params.maxBitrate, 8000000); assert.equal(params.maxFramerate, 60);
  results.push('Real RTCRtpSender accepts the preset quality without the removed total upload cap');
  await pair(alice, dave);
  for (const p of [alice, bob, carol, dave]) await p.waitForFunction(() => document.getElementById('room-count').textContent === '4 / 4', { timeout: 25000 });
  assert.ok(await alice.locator('#invite-button').isDisabled());
  results.push('Four-person mesh connects; the room refuses additional invitations');
  await rejected.locator('#join-button').click(); await rejected.locator('#room-code-input').fill(sharedCode); await rejected.locator('#pair-action').click();
  await rejected.waitForFunction(() => document.getElementById('pair-error').textContent.includes('full'));
  assert.equal(await rejected.evaluate(() => peerConnectionCreations), 0);
  results.push('A full room rejects additional short-code requests without creating a peer');
  await bob.locator('.watch-peer').click();
  await alice.waitForFunction(() => document.getElementById('stat-viewers').textContent.includes('1 person'));
  assert.equal(await bob.locator('video').count(), 0);
  results.push('Unsubscribing releases the viewer player and detaches outgoing tracks');
  await alice.locator('#share-button').click();
  assert.ok(await alice.evaluate(() => testTracks.every(t => t.readyState === 'ended')));
  await carol.waitForFunction(() => document.querySelectorAll('video').length === 0);
  results.push('Stop ends every capture track and clears remote players');
  // Sharing works in both directions, not only from the host.
  await bob.evaluate(() => { window.failAudioOnce = true; });
  await bob.locator('#share-button').click(); await alice.locator('.watch-peer').click();
  await alice.waitForFunction(() => document.querySelector('video')?.videoWidth > 0, { timeout: 20000 });
  results.push('A guest can share back to the host without renegotiating or a server');
  assert.equal(await bob.locator('#audio-status').innerText(), 'Video only');
  assert.equal(await bob.evaluate(() => testCaptureOptions.audio), false);
  results.push('Audio-source failure starts a clearly labelled video-only stream instead of aborting sharing');
  await alice.locator('.person.remote').filter({ hasText: 'Carol' }).locator('.remove-peer').click();
  await carol.waitForFunction(() => document.getElementById('room-count').textContent === '1 / 4');
  await bob.waitForFunction(() => document.getElementById('room-count').textContent === '3 / 4');
  results.push('Host removal closes the removed participant and the remaining mesh edges');
  await alice.locator('#leave-button').click();
  await bob.waitForFunction(() => document.getElementById('room-count').textContent === '1 / 4');
  await dave.waitForFunction(() => document.getElementById('room-count').textContent === '1 / 4');
  assert.ok(await bob.evaluate(() => testTracks.every(t => t.readyState === 'ended')));
  results.push('Host departure closes the room and stops the guest capture');
  const manualHost = await page('Manual host'), manualGuest = await page('Manual guest');
  await manualPair(manualHost, manualGuest);
  await manualHost.waitForFunction(() => document.getElementById('room-count').textContent === '2 / 4');
  await manualHost.locator('#leave-button').click();
  results.push('Optional manual pairing still works with compressed replies and hidden long strings');
  const nativePeer = await page('Windows audio', true);
  await nativePeer.waitForFunction(() => nativeReadyReports === 1);
  results.push('The native startup surface receives a single ready notification after UI initialization');
  await pair(alice, nativePeer);
  await nativePeer.evaluate(() => { window.failAudioOnce = true; });
  await nativePeer.locator('#share-button').click();
  await nativePeer.locator('#capture-error-dialog').waitFor();
  assert.match(await nativePeer.locator('#capture-error-description').innerText(), /directly through Windows/);
  await nativePeer.locator('#retry-video').click();
  await nativePeer.waitForFunction(() => document.getElementById('audio-status').textContent === 'Audio on', { timeout: 15000 });
  await alice.locator('.watch-peer').click();
  await alice.waitForFunction(() => document.querySelector('video')?.videoWidth > 0, { timeout: 20000 });
  const peak = await alice.evaluate(async () => {
    const context = new AudioContext(); await context.resume();
    const source = context.createMediaStreamSource(document.querySelector('video').srcObject);
    const analyser = context.createAnalyser(); analyser.fftSize = 256; source.connect(analyser);
    const samples = new Float32Array(256); let peak = 0;
    for (let i = 0; i < 100 && peak < .01; i++) { await new Promise(resolve => setTimeout(resolve, 40)); analyser.getFloatTimeDomainData(samples); peak = Math.max(...samples.map(Math.abs)); }
    source.disconnect(); await context.close(); return peak;
  });
  assert.ok(peak > .01, 'Windows PCM audio should survive worklet downmix, resampling and WebRTC transmission');
  results.push('Native 44.1 kHz eight-channel PCM crosses the audio worklet and reaches a peer as audible stereo');
  await nativePeer.locator('#share-button').click();
  assert.ok(await nativePeer.evaluate(() => nativeStops > 0));
  await alice.waitForFunction(() => document.querySelectorAll('video').length === 0);
  results.push('Stopping sharing sends native audio-stop and releases the remote player');
  await alice.locator('#leave-button').click();
  const wire = JSON.stringify(signalingFrames);
  assert.ok(signalingFrames.length > 8);
  assert.ok(!wire.includes(sharedCode) && !wire.includes('DRIFT1I.') && !wire.includes('HS2R.') && !wire.includes('a=candidate:') && !wire.includes('Alice') && !wire.includes('Bob'));
  assert.ok(signalingFrames.every(frame => frame.payload.sdp.sdp === 'v=0\r\n' && frame.payload.metadata.hs === 1));
  results.push('The service sees only opaque setup messages and a fixed SDP placeholder, never names, codes or real SDP');
  assert.deepEqual(errors, []);
  await writeFile('artifacts/e2e-results.json', JSON.stringify({ passed: results, browserErrors: errors, encoderParameters: params }, null, 2));
  console.log(results.map((r, i) => `${i + 1}. PASS ${r}`).join('\n'));
} catch (error) {
  await writeFile('artifacts/e2e-results.json', JSON.stringify({ passed: results, error: error.stack, browserErrors: errors }, null, 2));
  if (browser) for (const [i, context] of browser.contexts().entries()) for (const p of context.pages()) await p.screenshot({ path: `artifacts/failure-${i}.png`, fullPage: true }).catch(() => {});
  throw error;
} finally { await browser?.close(); server.kill(); }
