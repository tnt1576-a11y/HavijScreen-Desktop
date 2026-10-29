export const VERSION = 1;
export const MAX_PEERS = 4;
export const CODE_TTL = 10 * 60 * 1000;
const MAX_CODE = 120000;
const MAX_EXPANDED = 180000;
export const PRESETS = Object.freeze({
  efficient: { label: 'Efficient', width: 1280, height: 720, fps: 30, bitrate: 2500000 },
  balanced: { label: 'Balanced', width: 1920, height: 1080, fps: 30, bitrate: 5000000 },
  fluid: { label: 'Fluid', width: 1920, height: 1080, fps: 60, bitrate: 8000000 },
  crisp: { label: 'Crisp', width: 2560, height: 1440, fps: 60, bitrate: 12000000 }
});
export function randomId(size = 16) { return toBase64(crypto.getRandomValues(new Uint8Array(size))); }
export function toBase64(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}
export function fromBase64(text) {
  if (!/^[A-Za-z0-9_-]+$/.test(text)) throw new Error('This code contains invalid characters.');
  return Uint8Array.from(atob(text.replaceAll('-', '+').replaceAll('_', '/')), c => c.charCodeAt(0));
}
async function readLimited(stream) {
  const reader = stream.getReader();
  const chunks = []; let total = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      total += value.byteLength;
      if (total > MAX_EXPANDED) throw new Error('This code is too large.');
      chunks.push(value);
    }
  } finally { await reader.cancel(); }
  const bytes = new Uint8Array(total); let offset = 0;
  for (const part of chunks) { bytes.set(part, offset); offset += part.length; }
  return bytes;
}
export async function encodeInvite(payload) {
  const input = new TextEncoder().encode(JSON.stringify(payload));
  const bytes = await readLimited(new Blob([input]).stream().pipeThrough(new CompressionStream('deflate')));
  return 'DRIFT1I.' + toBase64(bytes);
}
export async function decodeInvite(code, now = Date.now()) {
  code = String(code).trim();
  if (code.length > MAX_CODE || !code.startsWith('DRIFT1I.')) throw new Error('Paste a complete HavijScreen invitation code.');
  let data;
  try {
    const bytes = await readLimited(new Blob([fromBase64(code.slice(8))]).stream().pipeThrough(new DecompressionStream('deflate')));
    data = JSON.parse(new TextDecoder().decode(bytes));
  } catch { throw new Error('This invitation is damaged or too large.'); }
  if (data.v !== VERSION || data.type !== 'invite' || !isId(data.room) || !isId(data.host) || !isId(data.peer) || !isId(data.session)
    || !isId(data.secret, 43) || !validName(data.name) || !validSDP(data.sdp, 'offer') || typeof data.stun !== 'boolean') {
    throw new Error('This invitation is not valid.');
  }
  if (!Number.isFinite(data.expires) || data.expires <= now || data.expires > now + CODE_TTL + 30000) throw new Error('This invitation has expired. Ask for a new one.');
  return data;
}
function isId(value, length = 22) { return typeof value === 'string' && value.length === length && /^[\w-]+$/.test(value); }
export function validName(value) { return typeof value === 'string' && value.trim().length >= 1 && value.length <= 32 && !/[\x00-\x1f\x7f]/.test(value); }
export function validSDP(value, type) { return value && value.type === type && typeof value.sdp === 'string' && value.sdp.length < 100000 && value.sdp.startsWith('v=0'); }
async function keyFrom(secret) { return crypto.subtle.importKey('raw', fromBase64(secret), 'AES-GCM', false, ['encrypt', 'decrypt']); }
export async function encodeReply(payload, secret) {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: new TextEncoder().encode('drift-reply-v1:' + payload.session) }, await keyFrom(secret), new TextEncoder().encode(JSON.stringify(payload)));
  return `DRIFT1R.${payload.session}.${toBase64(iv)}.${toBase64(new Uint8Array(data))}`;
}
export async function decodeReply(code, secret, session) {
  code = String(code).trim();
  const parts = code.split('.');
  if (code.length > MAX_CODE || parts.length !== 4 || parts[0] !== 'DRIFT1R' || parts[1] !== session) throw new Error('This reply belongs to a different invitation.');
  try {
    const bytes = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: fromBase64(parts[2]), additionalData: new TextEncoder().encode('drift-reply-v1:' + session) }, await keyFrom(secret), fromBase64(parts[3]));
    const data = JSON.parse(new TextDecoder().decode(bytes));
    if (data.v !== VERSION || data.session !== session || !isId(data.peer) || !isId(data.room) || !validName(data.name) || !validSDP(data.sdp, 'answer')) throw new Error();
    return data;
  } catch { throw new Error('This reply could not be authenticated. Ask your friend to copy it again.'); }
}
export function videoParameters(parameters, preset) {
  if (!parameters.encodings?.length) return null;
  parameters.encodings[0].maxBitrate = preset.bitrate;
  parameters.encodings[0].maxFramerate = preset.fps;
  parameters.degradationPreference = 'maintain-framerate';
  return parameters;
}
