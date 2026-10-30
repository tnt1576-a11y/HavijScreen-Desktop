import { VERSION, MAX_PEERS, CODE_TTL, randomId, encodeInvite, decodeInvite, encodeReply, decodeReply, validName, validSDP, deferCandidates } from './protocol.mjs';

// Room owns direct peer connections. Initial SDP arrives through manual codes
// or Lobby; subsequent mesh negotiation uses the host's data channels.
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
  async createInvite(name, stun, prepared = null) {
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
      prepared?.({ session, peerId: id });
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
      // Gather a reply without starting outbound ICE checks. The host may need
      // minutes to receive it; that human waiting time is not a network failure.
      const deferred = deferCandidates(data.sdp);
      peer.pendingCandidates = deferred.candidates; peer.waitingForHost = true;
      await peer.pc.setRemoteDescription(deferred.description);
      this.prepareTransceivers(peer);
      await peer.pc.setLocalDescription(await peer.pc.createAnswer());
      await gather(peer.pc);
      this.deadline(peer, data.expires, 'The invitation expired before you connected. Ask for a new invitation.');
      return await encodeReply({ v: VERSION, type: 'answer', room: data.room, session: data.session, peer: this.id, name: this.name, sdp: peer.pc.localDescription.toJSON() }, data.secret);
    } catch (error) { this.dropPeer(peer.id); Object.assign(this, original); throw error; }
  }
  async connectToHost() {
    const peer = this.peers.get(this.hostId);
    if (!peer || this.isHost) throw new Error('Create a reply before connecting.');
    if (peer.established || !peer.waitingForHost) return;
    if (peer.channel?.readyState === 'closed' || peer.pc.connectionState === 'closed') throw new Error('This connection is no longer available. Ask for a new invitation.');
    peer.waitingForHost = false;
    this.deadline(peer, Date.now() + 60000, 'Could not establish a direct connection. These networks may need a trusted LAN/VPN or a TURN relay. No relay is configured.');
    for (const candidate of peer.pendingCandidates) await peer.pc.addIceCandidate(candidate);
    await peer.pc.addIceCandidate(null); peer.pendingCandidates = [];
  }
  async acceptReply(code, automatic = false) {
    const session = String(code).trim().split('.')[1];
    const offer = this.offers.get(session);
    if (!offer || offer.expires <= Date.now()) { this.expireOffers(); throw new Error('That invitation has expired or has already been used.'); }
    const reply = await decodeReply(code, offer.secret, session);
    if (reply.peer !== offer.peer.id || reply.room !== this.roomId) throw new Error('This reply is for a different room.');
    offer.peer.name = reply.name;
    await offer.peer.pc.setRemoteDescription(reply.sdp);
    this.offers.delete(session);
    if (!offer.peer.established) this.deadline(offer.peer, Date.now() + 60000, automatic
      ? 'Could not establish a direct connection to your friend. These networks may need a trusted LAN/VPN or a TURN relay. No relay is configured.'
      : 'Your friend did not connect. Ask them to click Connect after sending their reply, or try a fresh invitation.');
  }
  makePeer(id, name, initiator) {
    if (this.peers.has(id)) throw new Error('This person is already connected.');
    const pc = new RTCPeerConnection(this.configuration());
    const peer = { id, name, pc, channel: null, stream: new MediaStream(), watching: false, subscribed: false, sharing: false, info: null, timer: null, departed: false, established: false, waitingForHost: false, pendingCandidates: [] };
    this.peers.set(id, peer);
    pc.ontrack = event => {
      if (!peer.stream.getTrackById(event.track.id)) peer.stream.addTrack(event.track);
      this.emit('track', peer);
    };
    pc.onconnectionstatechange = () => {
      this.emit('state', peer);
      if (pc.connectionState === 'failed' && !peer.waitingForHost) this.failPeer(peer, 'Could not establish a direct connection. These networks may need a trusted LAN/VPN or a TURN relay. No relay is configured.');
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
      peer.established = true; peer.waitingForHost = false; peer.pendingCandidates = []; clearTimeout(peer.timer);
      this.send(peer, { type: 'hello', name: this.name, sharing: this.sharing, info: this.mediaInfo });
      this.emit('peer', peer);
      if (this.isHost) this.updateMesh(peer.id);
    };
    channel.onclose = () => { if (!peer.waitingForHost) this.failPeer(peer, 'The connection failed before joining. Ask for a fresh invitation and try again.'); };
    channel.onmessage = event => {
      if (typeof event.data !== 'string' || event.data.length > 160000) return;
      try { const message = JSON.parse(event.data); Promise.resolve(this.handle(peer, message)).catch(error => this.emit('notice', error.message)); }
      catch { this.emit('notice', 'A peer sent an invalid control message.'); }
    };
  }
  deadline(peer, expires, message) {
    clearTimeout(peer.timer);
    peer.timer = setTimeout(() => { if (!peer.established && !peer.departed) this.failPeer(peer, message); }, Math.max(0, expires - Date.now()));
  }
  failPeer(peer, message) {
    if (peer.departed) return;
    const established = peer.established;
    this.dropPeer(peer.id, established);
    // A pending host connection is not a room that has disconnected. Keep
    // existing host-side participants and capture when one invitation fails.
    if (!established) {
      if (peer.id === this.hostId && !this.closed) { this.leave(); this.emit('ended', message); }
      else this.emit('notice', message);
    }
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
    clearTimeout(peer.timer); peer.pendingCandidates = [];
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
