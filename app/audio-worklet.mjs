import { AudioRing } from './audio-pcm.mjs';
class WindowsAudioProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const format = options.processorOptions;
    this.ring = new AudioRing(format.sampleRate, sampleRate, format.channels);
    this.port.onmessage = event => { if (event.data instanceof Float32Array) this.ring.push(event.data); };
  }
  process(inputs, outputs) { if (outputs[0]?.length) this.ring.render(outputs[0]); return true; }
}
registerProcessor('drift-windows-audio', WindowsAudioProcessor);
