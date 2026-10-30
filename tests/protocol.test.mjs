import test from 'node:test';
import assert from 'node:assert/strict';
import { CODE_TTL, PRESETS, randomId, encodeInvite, decodeInvite, encodeReply, decodeReply, videoParameters, validName, deferCandidates, fromBase64, toBase64 } from '../app/protocol.mjs';
function invitation(overrides = {}) {
  return { v: 1, type: 'invite', room: randomId(), host: randomId(), peer: randomId(), session: randomId(), secret: randomId(32), name: 'Alice', expires: Date.now() + CODE_TTL, stun: false, sdp: { type: 'offer', sdp: 'v=0\r\na=example\r\n' }, ...overrides };
}
test('private invitation round-trips with no server', async () => {
  const data = invitation(); assert.deepEqual(await decodeInvite(await encodeInvite(data)), data);
});
test('expired, distant-future and invalid invitations are rejected', async () => {
  await assert.rejects(decodeInvite(await encodeInvite(invitation({ expires: Date.now() - 1 }))), /expired/);
  await assert.rejects(decodeInvite(await encodeInvite(invitation({ expires: Date.now() + CODE_TTL * 2 }))), /expired/);
  await assert.rejects(decodeInvite(await encodeInvite(invitation({ peer: '../../evil' }))), /not valid/);
  await assert.rejects(decodeInvite(await encodeInvite(invitation({ name: 'a\nspoof' }))), /not valid/);
  await assert.rejects(decodeInvite(await encodeInvite(invitation({ sdp: { type: 'answer', sdp: 'v=0' } }))), /not valid/);
});
test('malformed, oversized and compressed-bomb invitations are rejected', async () => {
  for (const code of ['', 'garbage', 'DRIFT1I.%%%%', 'DRIFT1I.' + 'a'.repeat(120001)]) await assert.rejects(decodeInvite(code));
  await assert.rejects(async () => decodeInvite(await encodeInvite(invitation({ bomb: 'x'.repeat(200000) }))), /too large/);
});
test('only the invitation secret can authenticate the reply', async () => {
  const invite = invitation();
  const reply = { v: 1, session: invite.session, room: invite.room, peer: invite.peer, name: 'Bob', sdp: { type: 'answer', sdp: 'v=0\r\n' } };
  const code = await encodeReply(reply, invite.secret);
  assert.deepEqual(await decodeReply(code, invite.secret, invite.session), reply);
  await assert.rejects(decodeReply(code, randomId(32), invite.session), /authenticated/);
  await assert.rejects(decodeReply(code, invite.secret, randomId()), /different invitation/);
  const parts = code.split('.'); const ciphertext = parts[3]; parts[3] = (ciphertext[0] === 'A' ? 'B' : 'A') + ciphertext.slice(1);
  await assert.rejects(decodeReply(parts.join('.'), invite.secret, invite.session), /authenticated/);
});
test('a reply with invalid SDP or identity is rejected even with the secret', async () => {
  const invite = invitation();
  const code = await encodeReply({ v: 1, session: invite.session, room: invite.room, peer: invite.peer, name: 'Bob', sdp: { type: 'offer', sdp: 'v=0' } }, invite.secret);
  await assert.rejects(decodeReply(code, invite.secret, invite.session), /authenticated/);
});
test('quality presets set bitrate and frame rate without a total upload limit', () => {
  for (const preset of Object.values(PRESETS)) {
    const result = videoParameters({ encodings: [{}] }, preset);
    assert.equal(result.encodings[0].maxBitrate, preset.bitrate);
    assert.equal(result.encodings[0].maxFramerate, preset.fps);
    assert.equal(result.degradationPreference, 'maintain-framerate');
  }
  assert.equal(videoParameters({ encodings: [] }, PRESETS.efficient), null);
});
test('name limits prevent control characters and retain international names', () => {
  assert.ok(validName('علی')); assert.ok(validName('Alice & Bob'));
  for (const name of ['', '  ', 'a'.repeat(33), 'Alice\nBob', 'Alice\x00']) assert.ok(!validName(name));
});

test('deferring candidates preserves media sections and ICE authentication', () => {
  const offer = { type: 'offer', sdp: 'v=0\r\na=ice-ufrag:secret\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:video\r\na=candidate:1 1 UDP 1 192.0.2.1 1234 typ host\r\na=end-of-candidates\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:audio\r\na=candidate:2 1 UDP 1 192.0.2.1 5678 typ host\r\n' };
  const result = deferCandidates(offer);
  assert.ok(!result.description.sdp.includes('a=candidate:'));
  assert.ok(!result.description.sdp.includes('a=end-of-candidates'));
  assert.match(result.description.sdp, /a=ice-ufrag:secret\r\nm=video/);
  assert.match(result.description.sdp, /a=mid:video\r\nm=audio/);
  assert.deepEqual(result.candidates.map(c => [c.sdpMid, c.sdpMLineIndex]), [['video', 0], ['audio', 1]]);
  assert.match(offer.sdp, /a=candidate:/, 'The received invitation must not be mutated');
});

test('compressed replies are smaller and historical replies still authenticate', async () => {
  const invite = invitation();
  const payload = { v: 1, session: invite.session, room: invite.room, peer: invite.peer, name: 'Bob', sdp: { type: 'answer', sdp: 'v=0\r\n' + 'a=example:same-data\r\n'.repeat(100) } };
  const key = await crypto.subtle.importKey('raw', fromBase64(invite.secret), 'AES-GCM', false, ['encrypt']);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const bytes = await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: new TextEncoder().encode('drift-reply-v1:' + invite.session) }, key, new TextEncoder().encode(JSON.stringify(payload)));
  const legacy = `DRIFT1R.${invite.session}.${toBase64(iv)}.${toBase64(new Uint8Array(bytes))}`;
  const code = await encodeReply(payload, invite.secret);
  assert.ok(code.length < legacy.length / 2);
  assert.deepEqual(await decodeReply(code, invite.secret, invite.session), payload);
  assert.deepEqual(await decodeReply(legacy, invite.secret, invite.session), payload);
  const bomb = await encodeReply({ ...payload, bomb: 'x'.repeat(200000) }, invite.secret);
  await assert.rejects(decodeReply(bomb, invite.secret, invite.session), /authenticated/);
});
