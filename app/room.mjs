import { VERSION, MAX_PEERS, CODE_TTL, randomId, encodeInvite, decodeInvite, encodeReply, decodeReply, validName, validSDP } from './protocol.mjs';

// A room has no HTTP or WebSocket service. Initial SDP is exchanged by the users;
// subsequent mesh negotiation travels over the host's authenticated data channels.
export class Room extends EventTarget {
  constructor() {
    super();
    this.id = randomId(); this.roomId = randomId(); this.hostId = this.id;
    this.name = 'Player'; this.stun = true; this.peers = new Map(); this.offers = new Map();
    this.stream = null; this.sharing = false; this.mediaInfo = null; this.closed = false;
  }
  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
  get isHost() { return this.hostId === this.id; }
  get connected() { return [...this.peers.values()].filter(p => p.channel?.readyState === 'open'); }
  get viewers() { return this.connected.filter(p => p.watching); }
  configuration() { return { iceServers: this.stun ? [{ urls: 'stun:stun.cloudflare.com:3478' }] : [], bundlePolicy: 'max-bundle' }; }
  async createInvite(name, stun) {
    if (!this.isHost) throw new Error('Only the room host can invite someone.');
    this.expireOffers();
    if (this.peers.size >= MAX_PEERS - 1) throw new Error('This room supports four people. Cancel an unused invite to free a place.');
    if (this.peers.size && stun !== this.stun) throw new Error('Keep the same network discovery setting for everyone in this room.');
    this.setName(name); this.stun = stun;
    const id = randomId(), session = randomId(), secret = randomId(32);
    const peer = this.makePeer(id, '', true);
    const offer = { peer, session, secret, expires: Date.now() + CODE_TTL };
    this.offers.set(session, offer);
    try {
      await peer.pc.setLocalDescription(await peer.pc.createOffer());
      await gather(peer.pc);
      return { session, code: await encodeInvite({ v: VERSION, type: 'invite', room: this.roomId, host: this.id, peer: id, session, secret, name: this.name, expires: offer.expires, stun, sdp: peer.pc.localDescription.toJSON() }) };
    } catch (error) { this.cancelInvite(session); throw error; }
  }
  setName(name) {
    if (!validName(name)) throw new Error('Choose a name with 1–32 characters.');
    this.name = name.trim();
  }
  cancelInvite(session) {
    const offer = this.offers.get(session);
    if (offer) { this.dropPeer(offer.peer.id); this.offers.delete(session); }
  }
  expireOffers() { for (const [session, offer] of this.offers) if (offer.expires < Date.now()) this.cancelInvite(session); }
  async acceptInvite(code, name) {
    if (this.peers.size || this.offers.size) throw new Error('Leave your current room before joining another.');
    const data = await decodeInvite(code);
    const original = { id: this.id, roomId: this.roomId, hostId: this.hostId, stun: this.stun };
    this.setName(name); this.id = data.peer; this.roomId = data.room; this.hostId = data.host; this.stun = data.stun;
    const peer = this.makePeer(data.host, data.name, false);
    try {
      await peer.pc.setRemoteDescription(data.sdp);
      this.prepareTransceivers(peer);
      await peer.pc.setLocalDescription(await peer.pc.createAnswer());
      await gather(peer.pc);
      return await encodeReply({ v: VERSION, type: 'answer', room: data.room, session: data.session, peer: this.id, name: this.name, sdp: peer.pc.localDescription.toJSON() }, data.secret);
    } catch (error) { this.dropPeer(peer.id); Object.assign(this, original); throw error; }
  }
  async acceptReply(code) {
    const session = String(code).trim().split('.')[1];
    const offer = this.offers.get(session);
    if (!offer || offer.expires <= Date.now()) { this.expireOffers(); throw new Error('That invitation has expired or has already been used.'); }
    const reply = await decodeReply(code, offer.secret, session);
    if (reply.peer !== offer.peer.id || reply.room !== this.roomId) throw new Error('This reply is for a different room.');
    offer.peer.name = reply.name;
    await offer.peer.pc.setRemoteDescription(reply.sdp);
    this.offers.delete(session);
  }
  makePeer(id, name, initiator) {
    if (this.peers.has(id)) throw new Error('This person is already connected.');
    const pc = new RTCPeerConnection(this.configuration());
    const peer = { id, name, pc, channel: null, stream: new MediaStream(), watching: false, subscribed: false, sharing: false, info: null, timer: null, departed: false };
    this.peers.set(id, peer);
    pc.ontrack = event => {
      if (!peer.stream.getTrackById(event.track.id)) peer.stream.addTrack(event.track);
      this.emit('track', peer);
    };
    pc.onconnectionstatechange = () => {
      this.emit('state', peer);
      if (pc.connectionState === 'failed') { this.emit('notice', `Could not connect to ${peer.name || 'your friend'}. Your routers may require a TURN relay or a trusted LAN/VPN.`); this.dropPeer(id, true); }
    };
    pc.ondatachannel = event => this.bindChannel(peer, event.channel);
    if (initiator) {
      pc.addTransceiver('video', { direction: 'sendrecv' });
      pc.addTransceiver('audio', { direction: 'sendrecv' });
      this.prepareTransceivers(peer);
      this.bindChannel(peer, pc.createDataChannel('drift-control', { ordered: true }));
    }
    return peer;
  }
  prepareTransceivers(peer) {
    for (const transceiver of peer.pc.getTransceivers()) {
      transceiver.direction = 'sendrecv';
      if (transceiver.receiver.track.kind !== 'video' || !transceiver.setCodecPreferences) continue;
      const codecs = RTCRtpSender.getCapabilities('video')?.codecs || [];
      // Prefer interoperable H.264; Chromium chooses the available encoder. Never
      // claim that a preferred codec guarantees hardware acceleration.
      transceiver.setCodecPreferences([...codecs.filter(c => /h264/i.test(c.mimeType)), ...codecs.filter(c => !/h264/i.test(c.mimeType))]);
    }
  }
  bindChannel(peer, channel) {
    peer.channel = channel;
    channel.onopen = () => {
      this.send(peer, { type: 'hello', name: this.name, sharing: this.sharing, info: this.mediaInfo });
      this.emit('peer', peer);
      if (this.isHost) this.updateMesh(peer.id);
    };
    channel.onclose = () => this.dropPeer(peer.id, true);
    channel.onmessage = event => {
      if (typeof event.data !== 'string' || event.data.length > 160000) return;
      try { const message = JSON.parse(event.data); Promise.resolve(this.handle(peer, message)).catch(error => this.emit('notice', error.message)); }
      catch { this.emit('notice', 'A peer sent an invalid control message.'); }
    };
  }
  send(peer, data) {
    if (peer?.channel?.readyState !== 'open') return false;
    if (peer.channel.bufferedAmount > 512000) return false;
    peer.channel.send(JSON.stringify(data)); return true;
  }
  broadcast(data) { for (const peer of this.connected) this.send(peer, data); }
  async handle(peer, message) {
    if (!message || typeof message.type !== 'string') return;
    if (message.type === 'hello') {
      if (!validName(message.name)) return;
      peer.name = message.name; peer.sharing = message.sharing === true; peer.info = cleanInfo(message.info);
      this.emit('peer', peer);
    } else if (message.type === 'sharing') {
      peer.sharing = message.active === true; peer.info = cleanInfo(message.info);
      if (!peer.sharing) { peer.subscribed = false; this.emit('unwatch', peer); }
      this.emit('peer', peer);
    } else if (message.type === 'watch') {
      peer.watching = message.active === true && this.sharing;
      await this.attachMedia(peer); this.emit('viewers', this.viewers.length);
    } else if (message.type === 'mesh-plan' && peer.id === this.hostId && !this.isHost) {
      if (!Array.isArray(message.people) || message.people.length > MAX_PEERS - 1) return;
      for (const person of message.people) {
        if (!/^[\w-]{22}$/.test(person.id) || !validName(person.name) || person.id === this.id || this.peers.has(person.id)) continue;
        if (this.peers.size >= MAX_PEERS - 1) break;
        if (this.id < person.id) await this.startMesh(person);
      }
    } else if (message.type === 'relay' && this.isHost) {
      const target = this.peers.get(message.to);
      if (!target || target.id === peer.id || typeof message.body !== 'object') return;
      this.send(target, { type: 'signal', from: peer.id, name: peer.name, body: message.body });
    } else if (message.type === 'signal' && peer.id === this.hostId && !this.isHost) {
      if (!/^[\w-]{22}$/.test(message.from) || message.from === this.id || message.from === this.hostId || !validName(message.name)) return;
      await this.receiveMesh(message.from, message.name, message.body);
    } else if (message.type === 'removed' && peer.id === this.hostId && !this.isHost) {
      if (message.id === this.id) { this.leave(); this.emit('ended', 'The host removed you from the room.'); }
      else this.dropPeer(message.id);
    } else if (message.type === 'room-ended' && peer.id === this.hostId && !this.isHost) {
      this.leave(); this.emit('ended', 'The host ended the room.');
    }
  }
  updateMesh() {
    if (!this.isHost) return;
    const people = this.connected.map(p => ({ id: p.id, name: p.name || 'Player' }));
    for (const peer of this.connected) this.send(peer, { type: 'mesh-plan', people });
  }
  relay(to, body) { this.send(this.peers.get(this.hostId), { type: 'relay', to, body }); }
  async startMesh(person) {
    const peer = this.makePeer(person.id, person.name, true);
    try {
      await peer.pc.setLocalDescription(await peer.pc.createOffer()); await gather(peer.pc);
      this.relay(peer.id, { type: 'offer', sdp: peer.pc.localDescription.toJSON() });
    } catch (error) { this.dropPeer(peer.id); throw error; }
  }
  async receiveMesh(id, name, message) {
    if (message?.type === 'offer' && validSDP(message.sdp, 'offer')) {
      if (this.peers.has(id) || this.peers.size >= MAX_PEERS - 1 || id > this.id) return;
      const peer = this.makePeer(id, name, false);
      try {
        await peer.pc.setRemoteDescription(message.sdp); this.prepareTransceivers(peer);
        await peer.pc.setLocalDescription(await peer.pc.createAnswer()); await gather(peer.pc);
        this.relay(id, { type: 'answer', sdp: peer.pc.localDescription.toJSON() });
      } catch (error) { this.dropPeer(id); throw error; }
    } else if (message?.type === 'answer' && validSDP(message.sdp, 'answer')) {
      const peer = this.peers.get(id);
      if (peer && peer.pc.signalingState === 'have-local-offer') await peer.pc.setRemoteDescription(message.sdp);
    }
  }
  async setMedia(stream, info = null) {
    this.stream = stream; this.sharing = !!stream; this.mediaInfo = info;
    for (const peer of this.connected) {
      if (!stream) peer.watching = false;
      await this.attachMedia(peer);
    }
    this.broadcast({ type: 'sharing', active: this.sharing, info });
    this.emit('viewers', this.viewers.length);
  }
  async attachMedia(peer) {
    for (const transceiver of peer.pc.getTransceivers()) {
      const kind = transceiver.receiver.track.kind;
      const track = peer.watching ? this.stream?.getTracks().find(t => t.kind === kind && t.readyState === 'live') || null : null;
      await transceiver.sender.replaceTrack(track);
    }
  }
  watch(id, active) {
    const peer = this.peers.get(id);
    if (!peer || (active && !peer.sharing)) return;
    peer.subscribed = active;
    this.send(peer, { type: 'watch', active });
    this.emit(active ? 'track' : 'unwatch', peer);
    this.emit('peer', peer);
  }
  remove(id) {
    if (!this.isHost) return;
    this.broadcast({ type: 'removed', id }); this.dropPeer(id, true);
  }
  dropPeer(id, notify = false) {
    const peer = this.peers.get(id);
    if (!peer || peer.departed) return;
    peer.departed = true; peer.channel && (peer.channel.onclose = null);
    peer.pc.onconnectionstatechange = null; peer.pc.ontrack = null; peer.pc.ondatachannel = null;
    peer.pc.close(); peer.stream.getTracks().forEach(t => t.stop()); this.peers.delete(id);
    for (const [session, offer] of this.offers) if (offer.peer.id === id) this.offers.delete(session);
    this.emit('departed', peer); this.emit('viewers', this.viewers.length);
    if (notify && this.isHost) this.broadcast({ type: 'removed', id });
    if (notify && id === this.hostId && !this.closed) { this.leave(); this.emit('ended', 'The host disconnected. The room has closed.'); }
  }
  leave() {
    this.closed = true;
    if (this.isHost) this.broadcast({ type: 'room-ended' });
    for (const id of [...this.peers.keys()]) this.dropPeer(id);
    this.offers.clear(); this.stream?.getTracks().forEach(t => t.stop()); this.stream = null; this.sharing = false;
  }
}
function cleanInfo(info) {
  if (!info || typeof info !== 'object') return null;
  return { width: Number(info.width) || 0, height: Number(info.height) || 0, fps: Number(info.fps) || 0, audio: info.audio === true };
}
export function gather(pc, timeout = 12000) {
  if (pc.iceGatheringState === 'complete') return Promise.resolve();
  return new Promise((resolve, reject) => {
    const finish = () => { clearTimeout(timer); pc.removeEventListener('icegatheringstatechange', change); pc.removeEventListener('connectionstatechange', closed); };
    const change = () => { if (pc.iceGatheringState === 'complete') { finish(); resolve(); } };
    const closed = () => { if (pc.connectionState === 'closed') { finish(); reject(new Error('Pairing was cancelled.')); } };
    const timer = setTimeout(() => { finish(); if (pc.localDescription?.sdp.includes('a=candidate:')) resolve(); else reject(new Error('No network candidates found. Check your connection and try again.')); }, timeout);
    pc.addEventListener('icegatheringstatechange', change); pc.addEventListener('connectionstatechange', closed);
  });
}
