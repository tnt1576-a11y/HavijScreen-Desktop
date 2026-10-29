import { decodePcm } from './audio-pcm.mjs';
export async function startNativeAudio(bridge, onFailure = () => {}, signal) {
  const id = crypto.randomUUID();
  let context, node, destination, track, format, stopped = false, timer, resolveStart, rejectStart;
  const started = new Promise((resolve, reject) => { resolveStart = resolve; rejectStart = reject; });
  const stop = () => {
    if (stopped) return;
    stopped = true; clearTimeout(timer);
    signal?.removeEventListener('abort', abort);
    bridge.removeEventListener('message', receive);
    bridge.postMessage({ type: 'audio-stop', id });
    track?.stop(); node?.disconnect(); context?.close().catch(() => {});
  };
  const abort = () => { rejectStart(new DOMException('Capture stopped.', 'AbortError')); stop(); };
  const receive = async event => {
    const data = event.data;
    if (data?.id !== id || stopped) return;
    if (data.type === 'audio-error') { rejectStart(new Error(data.message || 'Windows audio capture failed.')); stop(); onFailure(data.message); return; }
    if (data.type === 'audio-started') {
      try {
        if (format) return;
        format = data;
        if (data.sampleRate < 8000 || data.sampleRate > 192000 || data.channels < 1 || data.channels > 8) throw new Error('Unsupported Windows audio format.');
        context = new AudioContext({ latencyHint: 'interactive' });
        await context.audioWorklet.addModule(new URL('./audio-worklet.mjs', import.meta.url));
        if (stopped) { await context.close().catch(() => {}); return; }
        node = new AudioWorkletNode(context, 'drift-windows-audio', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2], processorOptions: format });
        destination = context.createMediaStreamDestination(); node.connect(destination);
        await context.resume();
        track = destination.stream.getAudioTracks()[0];
        track.addEventListener('ended', stop, { once: true });
        clearTimeout(timer); resolveStart({ track, stop });
      } catch (error) { rejectStart(error); stop(); }
    } else if (data.type === 'audio-data' && node && format && typeof data.pcm === 'string' && data.pcm.length <= 180000) {
      try {
        const bytes = Uint8Array.from(atob(data.pcm), c => c.charCodeAt(0));
        const samples = decodePcm(bytes, format.bits, format.float);
        node.port.postMessage(samples, [samples.buffer]);
      } catch (error) { stop(); onFailure(error.message); }
    }
  };
  bridge.addEventListener('message', receive);
  signal?.addEventListener('abort', abort, { once: true });
  if (signal?.aborted) { abort(); return started; }
  timer = setTimeout(() => { rejectStart(new Error('Windows audio did not start in time.')); stop(); }, 10000);
  bridge.postMessage({ type: 'audio-start', id });
  return started;
}
