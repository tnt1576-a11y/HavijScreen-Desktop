import test from 'node:test';
import assert from 'node:assert/strict';
import { CODE_TTL, PRESETS, randomId, encodeInvite, decodeInvite, encodeReply, decodeReply, videoParameters, validName } from '../app/protocol.mjs';
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
test('multiple viewers divide the video budget and retain the frame-rate ceiling', () => {
  for (const viewers of [1, 2, 3]) {
    const result = videoParameters({ encodings: [{}] }, PRESETS.crisp, 8, viewers);
    assert.ok(result.encodings[0].maxBitrate * viewers <= 8000000);
    assert.equal(result.encodings[0].maxFramerate, 60);
    assert.equal(result.degradationPreference, 'maintain-framerate');
  }
  assert.equal(videoParameters({ encodings: [{}] }, PRESETS.efficient, 30, 1).encodings[0].maxBitrate, 2500000);
  assert.equal(videoParameters({ encodings: [] }, PRESETS.efficient, 8, 1), null);
});
test('name limits prevent control characters and retain international names', () => {
  assert.ok(validName('علی')); assert.ok(validName('Alice & Bob'));
  for (const name of ['', '  ', 'a'.repeat(33), 'Alice\nBob', 'Alice\x00']) assert.ok(!validName(name));
});
