import { randomId } from './protocol.mjs';

// PeerServer's documented WebSocket transport. Its offer envelope requires an
// SDP field; the fixed placeholder contains no connection details. All real
// application messages (including SDP) are encrypted inside metadata.
export class SignalSocket extends EventTarget {
  constructor(id) { super(); this.id = id; this.token = randomId(24); this.socket = null; this.opened = false; this.pending = null; this.heartbeat = null; }
  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
  connect() {
    if (this.opened) return Promise.resolve();
    if (this.pending) return this.pending;
    this.pending = new Promise((resolve, reject) => {
      const ws = new WebSocket('wss://0.peerjs.com/peerjs?key=peerjs&id=' + encodeURIComponent(this.id) + '&token=' + encodeURIComponent(this.token) + '&version=1.5.5');
      this.socket = ws; let settled = false, count = 0, windowStart = Date.now();
      const fail = message => { if (!settled) { settled = true; clearTimeout(timeout); this.pending = null; reject(new Error(message)); } };
      const timeout = setTimeout(() => { fail('The connection service is unavailable. Check your internet connection and try again.'); ws.close(); }, 12000);
      ws.onmessage = event => {
        if (this.socket !== ws || typeof event.data !== 'string' || event.data.length > 190000) return;
        if (Date.now() - windowStart > 1000) { count = 0; windowStart = Date.now(); }
        if (++count > 32) return;
        let message; try { message = JSON.parse(event.data); } catch { return; }
        if (message.type === 'OPEN' && !settled) {
          settled = true; clearTimeout(timeout); this.pending = null; this.opened = true;
          this.heartbeat = setInterval(() => { if (ws.readyState === WebSocket.OPEN) ws.send('{"type":"HEARTBEAT"}'); }, 5000);
          resolve();
        } else if (['ERROR', 'ID-TAKEN'].includes(message.type)) {
          fail(message.type === 'ID-TAKEN' ? 'This room code is temporarily unavailable. Create a new code.' : 'The connection service rejected the request. Try again later.'); ws.close();
        } else if (this.opened && message.type === 'OFFER' && typeof message.src === 'string' && /^hs4-(room-[a-f0-9]{40}|guest-[a-f0-9]{32})$/.test(message.src) && message.dst === this.id && message.payload?.label === 'havijscreen-setup-v1') {
          this.emit('message', { src: message.src, envelope: message.payload.metadata });
        }
      };
      ws.onerror = () => { fail('The connection service is unavailable. Check your internet connection and try again.'); ws.close(); };
      ws.onclose = () => {
        if (this.socket !== ws) return;
        const wasOpen = this.opened; this.opened = false; clearInterval(this.heartbeat); this.socket = null;
        fail('The connection service disconnected. Please try again.');
        if (wasOpen) this.emit('offline');
      };
    });
    return this.pending;
  }
  send(dst, envelope) {
    if (!this.opened || this.socket?.readyState !== WebSocket.OPEN) throw new Error('The connection service is offline. Try again.');
    if (this.socket.bufferedAmount > 256000) throw new Error('The connection service is busy. Try again.');
    this.socket.send(JSON.stringify({ type: 'OFFER', dst, payload: { type: 'data', connectionId: 'hs-' + randomId(), label: 'havijscreen-setup-v1', reliable: true, serialization: 'json', sdp: { type: 'offer', sdp: 'v=0\r\n' }, metadata: envelope } }));
  }
  close() {
    clearInterval(this.heartbeat);
    // Keep close/error callbacks until a pending connect promise settles.
    this.opened = false; this.socket?.close();
  }
}
