import test from 'node:test';
import assert from 'node:assert/strict';
import { roomCode, normalizeRoomCode, codeKeys, ephemeralKey, connectionKey, seal, unseal } from '../app/signal-crypto.mjs';

test('80-bit room codes are compact, case-insensitive and do not expose their secret in the route', async () => {
  const codes = new Set(Array.from({ length: 100 }, roomCode)); assert.equal(codes.size, 100);
  const code = [...codes][0]; assert.match(code, /^[A-Z2-9]{4}(?:-[A-Z2-9]{4}){3}$/);
  const a = await codeKeys(code), b = await codeKeys(code.toLowerCase().replaceAll('-', ' '));
  assert.equal(a.route, b.route); assert.ok(!a.route.includes(normalizeRoomCode(code)));
  for (const invalid of ['', '1234', '0000-OOOO-IIII-1111', code + 'X']) assert.throws(() => normalizeRoomCode(invalid), /16-character/);
});

test('setup messages authenticate both routing identities and expire', async () => {
  const { key } = await codeKeys(roomCode()), other = await codeKeys(roomCode());
  const envelope = await seal(key, 'host', 'guest', { type: 'request', name: 'Alice' });
  assert.ok(!JSON.stringify(envelope).includes('Alice'));
  assert.equal((await unseal(key, 'host', 'guest', envelope)).name, 'Alice');
  await assert.rejects(unseal(other.key, 'host', 'guest', envelope));
  await assert.rejects(unseal(key, 'attacker', 'guest', envelope));
  await assert.rejects(unseal(key, 'host', 'other', envelope));
  await assert.rejects(unseal(key, 'host', 'guest', envelope, Date.now() + 121000), /Stale/);
  await assert.rejects(unseal(key, 'host', 'guest', { ...envelope, data: (envelope.data[0] === 'A' ? 'B' : 'A') + envelope.data.slice(1) }));
  await assert.rejects(unseal(key, 'host', 'guest', { ...envelope, data: 'x'.repeat(174001) }));
});

test('each approved guest has a separate ECDH key that the shared room key cannot decrypt', async () => {
  const host = await ephemeralKey(), guest = await ephemeralKey(), other = await ephemeralKey();
  const a = await connectionKey(host.privateKey, guest.publicKey, 'request-a');
  const b = await connectionKey(guest.privateKey, host.publicKey, 'request-a');
  const wrong = await connectionKey(other.privateKey, host.publicKey, 'request-a');
  const envelope = await seal(a, 'host', 'guest', { type: 'offer', code: 'private-sdp' });
  assert.equal((await unseal(b, 'host', 'guest', envelope)).code, 'private-sdp');
  await assert.rejects(unseal(wrong, 'host', 'guest', envelope));
  await assert.rejects(unseal((await codeKeys(roomCode())).key, 'host', 'guest', envelope));
  const differentRequest = await connectionKey(guest.privateKey, host.publicKey, 'request-b');
  await assert.rejects(unseal(differentRequest, 'host', 'guest', envelope));
  await assert.rejects(connectionKey(host.privateKey, 'bad-key', 'request-a'));
});
