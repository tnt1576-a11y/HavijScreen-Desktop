import { validName, randomId } from './protocol.mjs';
import { roomCode, codeKeys, guestRoute, ephemeralKey, connectionKey, seal, unseal } from './signal-crypto.mjs';
import { SignalSocket } from './signal-socket.mjs';

const WAIT = 10 * 60 * 1000;
export class Lobby extends EventTarget {
  constructor(room, socketFactory = id => new SignalSocket(id)) {
    super(); this.room = room; this.socketFactory = socketFactory; this.socket = null; this.keys = null; this.role = null;
    this.requests = new Map(); this.guest = null; this.seen = new Set(); this.stopped = false; this.state = 'idle'; this.timer = null; this.waitTimer = null;
    this.onPeer = () => this.connected(); room.addEventListener('peer', this.onPeer);
  }
  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
  status(state, message = '') { this.state = state; this.emit('state', { state, message }); }
  get code() { return this.role === 'host' ? this.keys?.code || '' : ''; }
  get joining() { return this.role === 'guest' && this.state !== 'connected'; }
  async openSocket(id) {
    if (!this.socket) {
      this.socket = this.socketFactory(id);
      this.socket.addEventListener('message', e => { this.receive(e.detail).catch(() => { /* Unauthenticated or obsolete messages never change the room. */ }); });
      this.socket.addEventListener('offline', () => {
        if (this.stopped) return;
        if (this.role === 'host') this.status('offline', 'Room code offline. Retry to accept new people. Current streams continue.');
        else if (this.state !== 'connected') this.failed('The connection service disconnected before joining. Try again.');
      });
    }
    await this.socket.connect();
    if (this.stopped) { this.socket.close(); throw new Error('Joining cancelled.'); }
  }
  async host(name, stun) {
    if (!this.room.isHost || this.role === 'guest') throw new Error('Only the host can invite people.');
    if (this.room.peers.size && stun !== this.room.stun) throw new Error('Keep the same network discovery setting for this room.');
    this.room.setName(name); this.room.stun = stun; this.role = 'host';
    this.keys ||= await codeKeys(roomCode());
    this.status('opening'); await this.openSocket(this.keys.route); this.status('hosting');
    if (!this.timer) this.timer = setInterval(() => this.expire(), 5000);
    return this.code;
  }
  async join(code, name) {
    if (this.room.peers.size || this.role) throw new Error('Leave your current room before joining another.');
    if (!validName(name)) throw new Error('Choose a name with 1–32 characters.');
    this.keys = await codeKeys(code); this.role = 'guest';
    this.guest = { id: randomId(), name: name.trim(), route: guestRoute(), pair: await ephemeralKey(), key: null, acknowledged: false, phase: 'request' };
    this.status('opening');
    await this.openSocket(this.guest.route);
    this.status('waiting', 'Request sent. Waiting for the host…');
    this.timer = setTimeout(() => this.failed('Room not found, or the host is offline. Check the code and try again.'), 18000);
    await this.send(this.keys.route, { type: 'request', request: this.guest.id, name: this.guest.name, publicKey: this.guest.pair.publicKey });
  }
  async send(dst, body, key = this.keys.key) {
    const envelope = await seal(key, this.socket.id, dst, body);
    if (this.stopped) return;
    this.socket.send(dst, envelope);
  }
  async receive({ src, envelope }) {
    if (this.stopped || !this.keys || !this.socket) return;
    const record = this.role === 'host' ? this.requests.get(src) : this.guest;
    if (this.role === 'guest' && src !== this.keys.route) return;
    if (this.role === 'host' && !src.startsWith('hs4-guest-')) return;
    let message, pairEncrypted = false;
    try { message = await unseal(this.keys.key, src, this.socket.id, envelope); }
    catch {
      if (!record?.key) return;
      message = await unseal(record.key, src, this.socket.id, envelope); pairEncrypted = true;
    }
    if (this.stopped || this.seen.has(message.nonce)) return;
    this.seen.add(message.nonce); if (this.seen.size > 256) this.seen.delete(this.seen.values().next().value);
    if (!/^[\w-]{22}$/.test(message.request)) return;
    if (this.role === 'host') await this.hostMessage(src, message, pairEncrypted);
    else await this.guestMessage(message, pairEncrypted);
  }
  changed() { this.emit('requests'); }
  async hostMessage(src, message, pairEncrypted) {
    let record = this.requests.get(src);
    if (message.type === 'request' && !pairEncrypted) {
      if (!validName(message.name) || typeof message.publicKey !== 'string' || !/^[\w-]{87}$/.test(message.publicKey)) return;
      if (record) return;
      if (this.room.peers.size >= 3 || this.requests.size >= 6) { await this.send(src, { type: 'declined', request: message.request, reason: 'The room is full or has too many pending requests. Try again later.' }); return; }
      record = { id: message.request, route: src, name: message.name.trim(), publicKey: message.publicKey, phase: 'pending', expires: Date.now() + WAIT, session: null, peerId: null };
      this.requests.set(src, record); this.changed();
      await this.send(src, { type: 'queued', request: record.id, name: this.room.name }); return;
    }
    if (!record || message.request !== record.id) return;
    if (message.type === 'cancel') { this.discard(record); return; }
    if (message.type === 'waiting-ping' && !pairEncrypted && record.phase === 'pending') { await this.send(src, { type: 'queued', request: record.id, name: this.room.name }); return; }
    if (message.type === 'ready' && pairEncrypted && record.phase === 'approved') {
      record.phase = 'preparing'; this.changed();
      try {
        const invite = await this.room.createInvite(this.room.name, this.room.stun, prepared => { record.session = prepared.session; record.peerId = prepared.peerId; });
        if (this.stopped || this.requests.get(src) !== record) { this.room.cancelInvite(invite.session); return; }
        record.session = invite.session; record.peerId = this.room.offers.get(invite.session)?.peer.id; record.phase = 'offer';
        await this.send(src, { type: 'offer', request: record.id, code: invite.code }, record.key); this.changed();
      } catch { await this.reject(record.route, 'Could not prepare this connection. Please try again.'); }
    } else if (message.type === 'answer' && pairEncrypted && record.phase === 'offer' && typeof message.code === 'string' && message.code.length < 120000) {
      record.phase = 'connecting';
      try {
        await this.room.acceptReply(message.code, true);
        if (!this.stopped && this.requests.get(src) === record) await this.send(src, { type: 'answer-ok', request: record.id }, record.key);
      } catch { await this.reject(record.route, 'The connection reply could not be accepted. Please try again.'); }
      this.changed();
    }
  }
  async approve(route) {
    const record = this.requests.get(route);
    if (!record || record.phase !== 'pending') return;
    const reserved = [...this.requests.values()].filter(r => r.phase !== 'pending' && !r.peerId).length;
    if (this.room.peers.size + reserved >= 3) { await this.reject(route, 'The room is full.'); return; }
    record.phase = 'approving'; record.expires = Date.now() + 90000; this.changed();
    try {
      const pair = await ephemeralKey();
      record.key = await connectionKey(pair.privateKey, record.publicKey, record.id);
      if (this.stopped || this.requests.get(route) !== record) return;
      record.phase = 'approved';
      await this.send(route, { type: 'approved', request: record.id, publicKey: pair.publicKey }); this.changed();
    } catch { await this.reject(route, 'Could not approve this request. Try again.'); }
  }
  async reject(route, reason = 'The host declined your request.') {
    const record = this.requests.get(route); if (!record) return;
    try { await this.send(route, { type: 'declined', request: record.id, reason }); } catch { /* An offline guest needs no notification. */ }
    this.discard(record);
  }
  discard(record) {
    this.requests.delete(record.route);
    if (record.session) this.room.cancelInvite(record.session);
    const peer = this.room.peers.get(record.peerId);
    if (peer && !peer.established) this.room.dropPeer(peer.id);
    record.key = null; this.changed();
  }
  async guestMessage(message, pairEncrypted) {
    const record = this.guest; if (!record || message.request !== record.id) return;
    if (message.type === 'declined' && !pairEncrypted) {
      const reasons = ['The host declined your request.', 'The room is full.', 'The room is full or has too many pending requests. Try again later.', 'Could not prepare this connection. Please try again.', 'The connection reply could not be accepted. Please try again.', 'Could not approve this request. Try again.', 'This join request expired. Try again.'];
      this.failed(reasons.includes(message.reason) ? message.reason : 'The host cancelled this request.');
    } else if (message.type === 'queued' && !pairEncrypted && record.phase === 'request') {
      record.lastResponse = Date.now();
      if (!record.acknowledged) {
        clearTimeout(this.timer); this.timer = setTimeout(() => this.failed('Your join request expired. Ask the host and try again.'), WAIT);
        this.waitTimer = setInterval(() => {
          if (Date.now() - record.lastResponse > 35000) { this.failed('The host stopped responding. Check with them and try again.'); return; }
          this.send(this.keys.route, { type: 'waiting-ping', request: record.id }).catch(() => this.failed('The connection service is offline. Try again.'));
        }, 10000);
        record.acknowledged = true; this.status('waiting', 'Waiting for ' + (validName(message.name) ? message.name.trim() : 'the host') + ' to accept…');
      }
    } else if (message.type === 'approved' && !pairEncrypted && record.phase === 'request') {
      record.phase = 'approved';
      record.key = await connectionKey(record.pair.privateKey, message.publicKey, record.id); record.pair = null;
      if (this.stopped) return;
      clearInterval(this.waitTimer); clearTimeout(this.timer); this.timer = setTimeout(() => this.failed('Could not finish connection setup. Please try again.'), 90000);
      this.status('connecting', 'Accepted. Connecting directly…');
      await this.send(this.keys.route, { type: 'ready', request: record.id }, record.key);
    } else if (message.type === 'offer' && pairEncrypted && record.phase === 'approved' && typeof message.code === 'string' && message.code.length < 120000) {
      record.phase = 'answering';
      let reply;
      try { reply = await this.room.acceptInvite(message.code, record.name); }
      catch { this.failed('Could not prepare a direct connection. Please try again.'); return; }
      if (this.stopped) return;
      record.phase = 'answer'; await this.send(this.keys.route, { type: 'answer', request: record.id, code: reply }, record.key);
    } else if (message.type === 'answer-ok' && pairEncrypted && record.phase === 'answer') {
      record.phase = 'connecting'; await this.room.connectToHost();
    }
  }
  connected() {
    if (this.stopped) return;
    if (this.role === 'host') {
      for (const record of [...this.requests.values()]) if (record.peerId && this.room.peers.get(record.peerId)?.established) { this.requests.delete(record.route); record.key = null; }
      this.changed();
    } else if (this.role === 'guest' && this.room.connected.some(p => p.id === this.room.hostId)) {
      clearInterval(this.waitTimer); clearTimeout(this.timer); this.status('connected'); this.socket?.close(); this.guest = null; this.keys = null; this.seen.clear();
    }
  }
  expire() { for (const record of this.requests.values()) if (record.expires <= Date.now()) this.reject(record.route, 'This join request expired. Try again.').catch(() => {}); }
  failed(message) { if (this.stopped || this.state === 'connected') return; this.status('failed', message); this.emit('failure', message); }
  stop() {
    if (this.stopped) return;
    // Flush cancellation while encryption completes, with a short upper bound.
    // Established media connections are independent of the setup service.
    const socket = this.socket, keys = this.keys;
    const messages = this.role === 'guest' && this.guest ? [{ dst: keys?.route, body: { type: 'cancel', request: this.guest.id } }]
      : [...this.requests.values()].map(r => ({ dst: r.route, body: { type: 'declined', request: r.id, reason: 'The host cancelled this request.' } }));
    this.stopped = true; clearTimeout(this.timer); clearInterval(this.timer); clearInterval(this.waitTimer);
    this.room.removeEventListener('peer', this.onPeer);
    if (socket?.opened && keys) {
      const closeTimer = setTimeout(() => socket.close(), 500);
      Promise.all(messages.map(async ({ dst, body }) => { const envelope = await seal(keys.key, socket.id, dst, body); socket.send(dst, envelope); }))
        .catch(() => {}).finally(() => { clearTimeout(closeTimer); socket.close(); });
    } else socket?.close();
    for (const record of [...this.requests.values()]) this.discard(record);
    this.requests.clear(); this.guest = null; this.keys = null; this.seen.clear();
  }
}
