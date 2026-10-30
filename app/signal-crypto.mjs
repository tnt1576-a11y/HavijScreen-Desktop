import { fromBase64, toBase64, randomId } from './protocol.mjs';

const ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
const encoder = new TextEncoder();
const salt = encoder.encode('havijscreen-setup-v1');
export function roomCode() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), b => ALPHABET[b & 31]).join('').match(/.{4}/g).join('-');
}
export function normalizeRoomCode(code) {
  const raw = String(code).toUpperCase().replace(/[\s-]/g, '');
  if (raw.length !== 16 || [...raw].some(c => !ALPHABET.includes(c))) throw new Error('Enter the 16-character room code, such as ABCD-EFGH-JKLM-NPQR.');
  return raw;
}
async function derive(bytes, info) {
  const material = await crypto.subtle.importKey('raw', bytes, 'HKDF', false, ['deriveKey']);
  return crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: encoder.encode(info) }, material, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
export async function codeKeys(code) {
  const raw = normalizeRoomCode(code);
  const digest = await crypto.subtle.digest('SHA-256', encoder.encode('havijscreen-room-v1:' + raw));
  const route = 'hs4-room-' + Array.from(new Uint8Array(digest).slice(0, 20), b => b.toString(16).padStart(2, '0')).join('');
  return { route, key: await derive(encoder.encode(raw), 'room'), code: raw.match(/.{4}/g).join('-') };
}
export function guestRoute() {
  return 'hs4-guest-' + Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('');
}
export async function ephemeralKey() {
  const pair = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  return { privateKey: pair.privateKey, publicKey: toBase64(new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey))) };
}
export async function connectionKey(privateKey, publicKey, request) {
  if (typeof publicKey !== 'string' || publicKey.length !== 87) throw new Error('Invalid connection key.');
  const remote = await crypto.subtle.importKey('raw', fromBase64(publicKey), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const shared = await crypto.subtle.deriveBits({ name: 'ECDH', public: remote }, privateKey, 256);
  return derive(shared, 'connection:' + request);
}
export async function seal(key, src, dst, body) {
  const plaintext = encoder.encode(JSON.stringify({ ...body, nonce: randomId(), at: Date.now() }));
  if (plaintext.length > 130000) throw new Error('Connection message is too large.');
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: encoder.encode('havijscreen-setup-v1|' + src + '|' + dst) }, key, plaintext);
  return { hs: 1, iv: toBase64(iv), data: toBase64(new Uint8Array(data)) };
}
export async function unseal(key, src, dst, envelope, now = Date.now()) {
  if (envelope?.hs !== 1 || typeof envelope.iv !== 'string' || envelope.iv.length !== 16 || typeof envelope.data !== 'string' || envelope.data.length > 174000) throw new Error('Invalid connection message.');
  const plaintext = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: fromBase64(envelope.iv), additionalData: encoder.encode('havijscreen-setup-v1|' + src + '|' + dst) }, key, fromBase64(envelope.data));
  const message = JSON.parse(new TextDecoder().decode(plaintext));
  if (!message || typeof message.type !== 'string' || !/^[\w-]{22}$/.test(message.nonce) || !Number.isFinite(message.at) || Math.abs(now - message.at) > 120000) throw new Error('Stale connection message.');
  return message;
}
