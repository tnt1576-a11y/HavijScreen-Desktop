import test from 'node:test';
import assert from 'node:assert/strict';
import { decodePcm, AudioRing } from '../app/audio-pcm.mjs';
test('Windows float32 and PCM formats convert correctly without non-finite samples', () => {
  const floats = new Float32Array([.25, -.5, NaN, Infinity, 2]);
  assert.deepEqual([...decodePcm(new Uint8Array(floats.buffer), 32, true)], [.25, -.5, 0, 0, 1]);
  const integers = new Int16Array([0, 16384, -32768]);
  assert.deepEqual([...decodePcm(new Uint8Array(integers.buffer), 16, false)], [0, .5, -1]);
  assert.deepEqual([...decodePcm(new Uint8Array([0, 0, 64, 0, 0, 128]), 24, false)], [.5, -1]);
});
test('eight-channel 44.1 kHz Windows mix is downmixed and resampled to stereo', () => {
  const ring = new AudioRing(44100, 48000, 8);
  const input = new Float32Array(4410 * 8);
  for (let i = 0; i < 4410; i++) { input[i * 8] = .2; input[i * 8 + 1] = -.2; }
  ring.push(input);
  const outputs = [new Float32Array(128), new Float32Array(128)]; ring.render(outputs);
  assert.ok(outputs[0].every(x => Math.abs(x - .2) < .00001));
  assert.ok(outputs[1].every(x => Math.abs(x + .2) < .00001));
  assert.ok(ring.size < 4410 && ring.size > 4200);
});
test('audio queue stays bounded under backpressure and produces silence on underrun', () => {
  const ring = new AudioRing(48000, 48000, 2);
  ring.push(new Float32Array(48000 * 2)); assert.equal(ring.size, ring.capacity);
  const outputs = [new Float32Array(128), new Float32Array(128)];
  for (let i = 0; i < 200; i++) ring.render(outputs);
  assert.ok(outputs.every(channel => channel.every(x => x === 0)));
  assert.ok(ring.size >= 0 && ring.size <= ring.capacity);
});
