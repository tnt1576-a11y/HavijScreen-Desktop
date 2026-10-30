import test from 'node:test';
import assert from 'node:assert/strict';
import { Lobby } from '../app/lobby.mjs';

class TestRoom extends EventTarget {
  peers = new Map(); connected = []; isHost = true; name = 'Host'; offers = new Map(); stun = false; offerCalls = 0;
  setName(name) { this.name = name; }
  async createInvite() { this.offerCalls++; throw new Error('This fixture must not create a real peer'); }
  cancelInvite() {}
}
function setup() {
  const routes = new Map(), frames = [];
  class Socket extends EventTarget {
    constructor(id) { super(); this.id = id; this.opened = false; }
    async connect() { this.opened = true; routes.set(this.id, this); }
    send(dst, envelope) { frames.push({ src: this.id, dst, envelope }); queueMicrotask(() => routes.get(dst)?.dispatchEvent(new CustomEvent('message', { detail: { src: this.id, envelope } }))); }
    close() { this.opened = false; if (routes.get(this.id) === this) routes.delete(this.id); }
  }
  const hostRoom = new TestRoom(), guestRoom = new TestRoom();
  const host = new Lobby(hostRoom, id => new Socket(id)), guest = new Lobby(guestRoom, id => new Socket(id));
  const failures = []; guest.addEventListener('failure', e => failures.push(e.detail));
  return { host, guest, hostRoom, frames, failures, routes };
}
async function until(predicate) { for (let i = 0; i < 100; i++) { if (predicate()) return; await new Promise(r => setTimeout(r, 2)); } assert.fail('Fixture did not settle'); }

test('requesting and waiting never creates a peer; replay cannot duplicate a request', async t => {
  const s = setup(); t.after(() => { s.host.stop(); s.guest.stop(); });
  await s.host.host('Host', false); await s.guest.join(s.host.code, 'Guest');
  await until(() => s.host.requests.size === 1 && s.guest.guest.acknowledged);
  assert.equal(s.hostRoom.offerCalls, 0); assert.equal(s.guest.state, 'waiting');
  const request = s.frames[0]; await s.host.receive({ src: request.src, envelope: request.envelope });
  assert.equal(s.host.requests.size, 1); assert.equal(s.hostRoom.offerCalls, 0);
  assert.ok(!JSON.stringify(s.frames).includes('Guest'));
});

test('declining, cancellation and expiry release requests without creating a peer', async t => {
  for (const action of ['decline', 'cancel', 'expire']) {
    const s = setup(); t.after(() => { s.host.stop(); s.guest.stop(); });
    await s.host.host('Host', false); await s.guest.join(s.host.code, 'Guest');
    await until(() => s.host.requests.size === 1 && s.guest.guest.acknowledged);
    const record = [...s.host.requests.values()][0];
    if (action === 'decline') await s.host.reject(record.route);
    if (action === 'cancel') s.guest.stop();
    if (action === 'expire') { record.expires = Date.now() - 1; s.host.expire(); }
    await until(() => s.host.requests.size === 0);
    if (action !== 'cancel') { await until(() => s.failures.length === 1); assert.match(s.failures[0], /declined|expired/); }
    assert.equal(s.hostRoom.offerCalls, 0);
  }
});

test('capacity is checked before admitting or negotiating a guest', async t => {
  const s = setup(); t.after(() => { s.host.stop(); s.guest.stop(); });
  await s.host.host('Host', false);
  for (let i = 0; i < 3; i++) s.hostRoom.peers.set(String(i), {});
  await s.guest.join(s.host.code, 'Guest'); await until(() => s.failures.length === 1);
  assert.match(s.failures[0], /full/); assert.equal(s.host.requests.size, 0); assert.equal(s.hostRoom.offerCalls, 0);
});

test('unauthenticated service traffic cannot create a join request', async t => {
  const s = setup(); t.after(() => { s.host.stop(); s.guest.stop(); });
  await s.host.host('Host', false);
  await s.host.receive({ src: 'hs4-guest-' + '0'.repeat(32), envelope: { hs: 1, iv: 'A'.repeat(16), data: 'A'.repeat(50) } });
  assert.equal(s.host.requests.size, 0); assert.equal(s.hostRoom.offerCalls, 0);
});
