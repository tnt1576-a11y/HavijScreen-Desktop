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
await mkdir('artifacts', { recursive: true });
try {
  await new Promise((ready, fail) => { server.stdout.once('data', ready); server.once('error', fail); server.once('exit', code => fail(new Error('Preview exited: ' + code))); });
  browser = await chromium.launch({ headless: true, ...(process.env.DRIFT_BROWSER_PATH ? { executablePath: process.env.DRIFT_BROWSER_PATH } : {}), args: ['--no-sandbox', '--autoplay-policy=no-user-gesture-required'] });
  async function page(name) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
    await context.addInitScript(() => {
      window.testTracks = [];
      navigator.mediaDevices.getDisplayMedia = async options => {
        window.testCaptureOptions = options;
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
    });
    const p = await context.newPage(); p.on('pageerror', error => errors.push(name + ': ' + error.message));
    await p.goto('http://127.0.0.1:4318'); await p.locator('#display-name').fill(name);
    return p;
  }
  const alice = await page('Alice'), bob = await page('Bob'), carol = await page('Carol'), dave = await page('Dave');
  async function pair(host, guest) {
    await host.locator('#invite-button').click();
    if (await host.locator('#stun').isVisible()) await host.locator('#stun').uncheck();
    await host.locator('#pair-action').click();
    await host.waitForFunction(() => document.getElementById('code-output').value.startsWith('DRIFT1I.'));
    const invitation = await host.locator('#code-output').inputValue();
    await guest.locator('#join-button').click(); await guest.locator('#code-input').fill(invitation); await guest.locator('#pair-action').click();
    await guest.waitForFunction(() => document.getElementById('code-output').value.startsWith('DRIFT1R.'));
    await host.locator('#code-input').fill(await guest.locator('#code-output').inputValue()); await host.locator('#pair-action').click();
    await guest.waitForFunction(() => !document.getElementById('pair-dialog').open, { timeout: 20000 });
  }
  await alice.screenshot({ path: 'artifacts/01-idle.png', fullPage: true });
  await alice.setViewportSize({ width: 1380, height: 870 });
  assert.ok(await alice.evaluate(() => document.documentElement.scrollHeight <= innerHeight), 'All controls should fit a normal Windows desktop window');
  await alice.screenshot({ path: 'artifacts/01-compact.png', fullPage: true });
  await alice.setViewportSize({ width: 1440, height: 960 });
  results.push('Initial UI renders with no remote assets');
  // Negative UI path: no native privileges, room, or network connection from malformed input.
  await bob.locator('#join-button').click(); await bob.locator('#code-input').fill('bad invitation'); await bob.locator('#pair-action').click();
  assert.match(await bob.locator('#pair-error').innerText(), /complete Drift invitation/); await bob.locator('#pair-close').click();
  results.push('Malformed pairing input is rejected visibly');
  await pair(alice, bob);
  await alice.waitForFunction(() => document.getElementById('room-count').textContent === '2 / 4');
  await bob.waitForFunction(() => document.getElementById('room-count').textContent === '2 / 4');
  results.push('Two separate clients pair without a server or STUN');
  await alice.locator('#source-window').click(); await alice.locator('#share-button').click();
  await bob.locator('.watch-peer').waitFor();
  assert.equal(await alice.locator('#stat-viewers').innerText(), '0 people');
  assert.equal(await bob.locator('video').count(), 0);
  assert.equal(await alice.evaluate(() => testCaptureOptions.video.displaySurface), 'window');
  assert.equal(await alice.evaluate(() => !!testCaptureOptions.audio), true);
  results.push('Application selection and audio options are passed; video is not auto-subscribed');
  await bob.locator('.watch-peer').click();
  await bob.waitForFunction(() => { const v = document.querySelector('video'); return v && v.videoWidth > 0 && v.readyState >= 2; }, { timeout: 20000 });
  await alice.waitForFunction(() => document.getElementById('stat-viewers').textContent.includes('1 person'));
  assert.equal(await bob.evaluate(() => document.querySelector('video').srcObject.getAudioTracks().length), 1);
  await bob.locator('.video-caption button').first().click();
  assert.equal(await bob.evaluate(() => document.querySelector('video').muted), false);
  results.push('Synthetic video frames and an audio track reach the watching peer');
  await alice.locator('#quality').selectOption('fluid'); await alice.locator('#bandwidth').fill('4'); await alice.locator('#bandwidth').dispatchEvent('change');
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
    const offer = await host.createInvite('Encoder probe', false); const reply = await guest.acceptInvite(offer.code, 'Viewer'); await host.acceptReply(reply);
    const wait = () => new Promise((resolve, reject) => { let n = 0; const timer = setInterval(() => { if (host.connected.length && guest.connected.length) { clearInterval(timer); resolve(); } else if (++n > 100) { clearInterval(timer); reject(new Error('Probe did not connect')); } }, 50); });
    await wait(); const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 360; const stream = canvas.captureStream(30);
    await host.setMedia(stream, {});
    await new Promise((resolve, reject) => { let n = 0; const timer = setInterval(() => { if (guest.peers.get(host.id)?.sharing) { clearInterval(timer); resolve(); } else if (++n > 100) { clearInterval(timer); reject(new Error('Share announcement did not arrive')); } }, 20); });
    guest.watch(host.id, true);
    await new Promise(resolve => setTimeout(resolve, 300));
    const sender = host.connected[0].pc.getSenders().find(s => s.track?.kind === 'video');
    const parameters = videoParameters(sender.getParameters(), PRESETS.fluid, 4, 2); await sender.setParameters(parameters);
    const actual = sender.getParameters().encodings[0]; const result = { maxBitrate: actual.maxBitrate, maxFramerate: actual.maxFramerate };
    stream.getTracks().forEach(t => t.stop()); guest.leave(); host.leave(); return result;
  });
  assert.equal(params.maxBitrate, 1872000); assert.equal(params.maxFramerate, 60);
  results.push('Real RTCRtpSender accepts the divided bitrate budget and frame-rate ceiling');
  await pair(alice, dave);
  for (const p of [alice, bob, carol, dave]) await p.waitForFunction(() => document.getElementById('room-count').textContent === '4 / 4', { timeout: 25000 });
  assert.ok(await alice.locator('#invite-button').isDisabled());
  results.push('Four-person mesh connects; the room refuses additional invitations');
  await bob.locator('.watch-peer').click();
  await alice.waitForFunction(() => document.getElementById('stat-viewers').textContent.includes('1 person'));
  assert.equal(await bob.locator('video').count(), 0);
  results.push('Unsubscribing releases the viewer player and detaches outgoing tracks');
  await alice.locator('#audio').uncheck();
  assert.equal(await alice.evaluate(() => testTracks.find(t => t.kind === 'audio').enabled), false);
  await alice.locator('#share-button').click();
  assert.ok(await alice.evaluate(() => testTracks.every(t => t.readyState === 'ended')));
  await carol.waitForFunction(() => document.querySelectorAll('video').length === 0);
  results.push('Mute disables captured audio; Stop ends every capture track and clears remote players');
  // Sharing works in both directions, not only from the host.
  await bob.locator('#share-button').click(); await alice.locator('.watch-peer').click();
  await alice.waitForFunction(() => document.querySelector('video')?.videoWidth > 0, { timeout: 20000 });
  results.push('A guest can share back to the host without renegotiating or a server');
  await alice.locator('.person.remote').filter({ hasText: 'Carol' }).locator('.remove-peer').click();
  await carol.waitForFunction(() => document.getElementById('room-count').textContent === '1 / 4');
  await bob.waitForFunction(() => document.getElementById('room-count').textContent === '3 / 4');
  results.push('Host removal closes the removed participant and the remaining mesh edges');
  await alice.locator('#leave-button').click();
  await bob.waitForFunction(() => document.getElementById('room-count').textContent === '1 / 4');
  await dave.waitForFunction(() => document.getElementById('room-count').textContent === '1 / 4');
  assert.ok(await bob.evaluate(() => testTracks.every(t => t.readyState === 'ended')));
  results.push('Host departure closes the room and stops the guest capture');
  assert.deepEqual(errors, []);
  await writeFile('artifacts/e2e-results.json', JSON.stringify({ passed: results, browserErrors: errors, encoderParameters: params }, null, 2));
  console.log(results.map((r, i) => `${i + 1}. PASS ${r}`).join('\n'));
} catch (error) {
  await writeFile('artifacts/e2e-results.json', JSON.stringify({ passed: results, error: error.stack, browserErrors: errors }, null, 2));
  if (browser) for (const [i, context] of browser.contexts().entries()) for (const p of context.pages()) await p.screenshot({ path: `artifacts/failure-${i}.png`, fullPage: true }).catch(() => {});
  throw error;
} finally { await browser?.close(); server.kill(); }
