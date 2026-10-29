import test from 'node:test';
import assert from 'node:assert/strict';
import { requestCapture } from '../app/capture.mjs';

test('capture starts with plain video and audio, without driver-sensitive constraints', async () => {
  const stream = {}; const calls = [];
  const result = await requestCapture({ getDisplayMedia: async options => { calls.push(options); return stream; } });
  assert.deepEqual(calls, [{ video: true, audio: true }]);
  assert.equal(result.stream, stream); assert.equal(result.audioFallback, false);
});
test('an audio-source startup failure retries video only and reports the fallback', async () => {
  const stream = {}; const calls = []; let notified = 0;
  const result = await requestCapture({ getDisplayMedia: async options => {
    calls.push(options);
    if (options.audio) throw new DOMException('Could not start audio source', 'NotReadableError');
    return stream;
  } }, false, () => notified++);
  assert.deepEqual(calls, [{ video: true, audio: true }, { video: true, audio: false }]);
  assert.equal(result.stream, stream); assert.equal(result.audioFallback, true); assert.equal(notified, 1);
});
test('cancelling or denying capture never reopens the picker', async () => {
  let calls = 0;
  await assert.rejects(requestCapture({ getDisplayMedia: async () => { calls++; throw new DOMException('Permission denied', 'NotAllowedError'); } }), { name: 'NotAllowedError' });
  assert.equal(calls, 1);
});
test('video and unrelated errors are not hidden by an audio fallback', async () => {
  let calls = 0;
  await assert.rejects(requestCapture({ getDisplayMedia: async () => { calls++; throw new DOMException('Could not start video source', 'NotReadableError'); } }), /video source/);
  assert.equal(calls, 1);
});
test('retry that requires a fresh user gesture exposes an explicit video-only action', async () => {
  await assert.rejects(requestCapture({ getDisplayMedia: async options => {
    if (options.audio) throw new DOMException('Could not start audio source', 'NotReadableError');
    throw new DOMException('A user gesture is required', 'InvalidStateError');
  } }), error => error.name === 'InvalidStateError' && error.retryWithoutAudio === true);
});
test('explicit video-only retry does not attempt audio again', async () => {
  const calls = [];
  await requestCapture({ getDisplayMedia: async options => { calls.push(options); return {}; } }, true);
  assert.deepEqual(calls, [{ video: true, audio: false }]);
});
test('native builds offer Windows audio recovery before asking to select a source again', async () => {
  let calls = 0;
  await assert.rejects(requestCapture({ getDisplayMedia: async () => { calls++; throw new DOMException('Could not start audio source', 'NotReadableError'); } }, false, () => {}, true), error => error.retryWithNativeAudio === true);
  assert.equal(calls, 1);
});
