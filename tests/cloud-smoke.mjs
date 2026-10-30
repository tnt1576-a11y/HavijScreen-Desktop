import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.DRIFT_PLAYWRIGHT_PATH || 'playwright');
const server = spawn(process.execPath, ['scripts/serve.mjs'], { env: { ...process.env, PORT: '4319' }, stdio: 'pipe' });
await new Promise((resolve, reject) => { server.stdout.once('data', resolve); server.once('error', reject); });
let browser;
try {
  browser = await chromium.launch({ headless: true, ...(process.env.DRIFT_BROWSER_PATH ? { executablePath: process.env.DRIFT_BROWSER_PATH } : {}), args: ['--no-sandbox'] });
  const page = await browser.newPage(); await page.goto('http://127.0.0.1:4319');
  const result = await page.evaluate(async () => {
    const { Room } = await import('/room.mjs'), { Lobby } = await import('/lobby.mjs');
    const hostRoom = new Room(), guestRoom = new Room(), host = new Lobby(hostRoom), guest = new Lobby(guestRoom);
    const until = async predicate => { for (let i = 0; i < 600; i++) { if (predicate()) return; await new Promise(r => setTimeout(r, 50)); } throw new Error('Cloud setup timed out'); };
    try {
      const code = await host.host('Synthetic host', false); await guest.join(code, 'Synthetic guest');
      await until(() => host.requests.size === 1);
      const beforeApproval = { host: hostRoom.peers.size, guest: guestRoom.peers.size };
      await host.approve([...host.requests.keys()][0]);
      await until(() => hostRoom.connected.length === 1 && guestRoom.connected.length === 1);
      const guestSocketClosed = guest.socket?.opened === false;
      host.stop(); await new Promise(r => setTimeout(r, 600));
      return { beforeApproval, hostConnected: hostRoom.connected.length, guestConnected: guestRoom.connected.length, guestSocketClosed, noCapture: !hostRoom.stream && !guestRoom.stream };
    } finally { guest.stop(); host.stop(); guestRoom.leave(); hostRoom.leave(); }
  });
  assert.deepEqual(result, { beforeApproval: { host: 0, guest: 0 }, hostConnected: 1, guestConnected: 1, guestSocketClosed: true, noCapture: true });
  await writeFile('artifacts/cloud-smoke-results.json', JSON.stringify(result, null, 2));
  console.log('PASS real PeerJS Cloud: encrypted request, host approval, direct WebRTC, setup socket cleanup; no screen/audio captured');
} finally { await browser?.close(); server.kill(); }
