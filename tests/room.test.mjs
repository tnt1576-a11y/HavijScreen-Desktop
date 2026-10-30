import test from 'node:test';
import assert from 'node:assert/strict';
import { Room } from '../app/room.mjs';

class FakePC extends EventTarget {
  connectionState = 'new';
  getTransceivers() { return []; }
  addTransceiver() {}
  createDataChannel() { return { readyState: 'connecting', close() {}, send() {} }; }
  close() { this.connectionState = 'closed'; }
  fail() { this.connectionState = 'failed'; this.onconnectionstatechange?.(); }
  async addIceCandidate() {}
}
globalThis.RTCPeerConnection = FakePC;
globalThis.MediaStream = class { getTracks() { return []; } };

function guestRoom() {
  const room = new Room(); room.hostId = 'remote-host';
  const peer = room.makePeer(room.hostId, 'Host', false);
  const ended = []; room.addEventListener('ended', e => ended.push(e.detail));
  room.bindChannel(peer, { readyState: 'connecting', send() {} });
  return { room, peer, ended };
}

test('a prepared reply remains open while waiting for the host', () => {
  const { room, peer, ended } = guestRoom();
  peer.waitingForHost = true;
  peer.pc.fail(); peer.channel.onclose();
  assert.equal(room.closed, false); assert.equal(room.peers.size, 1); assert.deepEqual(ended, []);
  room.leave();
});

test('a setup failure says connection failed rather than host disconnected', async () => {
  const { room, peer, ended } = guestRoom();
  peer.waitingForHost = true; await room.connectToHost(); peer.pc.fail();
  assert.equal(room.closed, true); assert.equal(room.peers.size, 0);
  assert.equal(ended.length, 1); assert.match(ended[0], /Could not establish a direct connection/);
  assert.doesNotMatch(ended[0], /host disconnected/i);
});

test('a real host departure still closes an established room', () => {
  const { room, peer, ended } = guestRoom();
  peer.channel.readyState = 'open'; peer.channel.onopen(); peer.channel.onclose();
  assert.equal(room.closed, true); assert.deepEqual(ended, ['The host disconnected. The room has closed.']);
});

test('one failed invitation does not close the host room or existing participants', () => {
  const room = new Room(), connected = room.makePeer('connected', 'Friend', true), pending = room.makePeer('pending', 'New friend', true);
  connected.channel.readyState = 'open'; connected.established = true;
  const ended = []; room.addEventListener('ended', e => ended.push(e.detail));
  room.sharing = true; pending.pc.fail();
  assert.equal(room.closed, false); assert.equal(room.sharing, true); assert.equal(room.connected.length, 1); assert.deepEqual(ended, []);
  room.leave();
});

test('a waiting invitation expires with an accurate message and releases resources', async () => {
  const { room, peer, ended } = guestRoom(); peer.waitingForHost = true;
  room.deadline(peer, Date.now() - 1, 'Invitation expired. Ask for a new one.');
  await new Promise(r => setTimeout(r, 10));
  assert.equal(room.closed, true); assert.equal(peer.pc.connectionState, 'closed');
  assert.deepEqual(ended, ['Invitation expired. Ask for a new one.']);
});
