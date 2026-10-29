export function decodePcm(bytes, bits, float) {
  const width = bits / 8;
  if (![1, 2, 3, 4, 8].includes(width) || (float && ![4, 8].includes(width))) throw new Error('Unsupported Windows audio format.');
  const count = Math.floor(bytes.byteLength / width);
  const output = new Float32Array(count);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  for (let i = 0, p = 0; i < count; i++, p += width) {
    let value;
    if (float) value = width === 4 ? view.getFloat32(p, true) : view.getFloat64(p, true);
    else if (width === 1) value = (view.getUint8(p) - 128) / 128;
    else if (width === 2) value = view.getInt16(p, true) / 32768;
    else if (width === 3) value = ((view.getUint8(p) | view.getUint8(p + 1) << 8 | view.getInt8(p + 2) << 16)) / 8388608;
    else if (width === 4) value = view.getInt32(p, true) / 2147483648;
    else throw new Error('Unsupported Windows audio format.');
    output[i] = Number.isFinite(value) ? Math.max(-1, Math.min(1, value)) : 0;
  }
  return output;
}

// Fixed-size ring: no growing chunk queue or allocation inside the audio callback.
export class AudioRing {
  constructor(inputRate, outputRate, channels) {
    this.channels = channels; this.step = inputRate / outputRate;
    this.capacity = Math.ceil(inputRate * .3); this.readyAt = Math.ceil(inputRate * .035);
    this.data = new Float32Array(this.capacity * channels);
    this.read = 0; this.write = 0; this.size = 0; this.phase = 0; this.started = false;
  }
  push(samples) {
    const frames = Math.floor(samples.length / this.channels);
    for (let f = 0; f < frames; f++) {
      if (this.size >= this.capacity) { this.read = (this.read + 1) % this.capacity; this.size--; this.phase = 0; }
      const offset = this.write * this.channels;
      for (let c = 0; c < this.channels; c++) this.data[offset + c] = samples[f * this.channels + c];
      this.write = (this.write + 1) % this.capacity; this.size++;
    }
  }
  sample(index, channel) {
    const offset = index * this.channels;
    let value = this.data[offset + Math.min(channel, this.channels - 1)];
    if (this.channels >= 6) {
      value += .707 * this.data[offset + 2] + .5 * this.data[offset + 4 + channel];
      if (this.channels >= 8) value += .5 * this.data[offset + 6 + channel];
    } else if (this.channels === 4) value += .5 * this.data[offset + 2 + channel];
    return Math.max(-1, Math.min(1, value));
  }
  render(outputs) {
    if (!this.started && this.size >= this.readyAt) this.started = true;
    for (let f = 0; f < outputs[0].length; f++) {
      if (!this.started || this.size < Math.ceil(this.step) + 1) {
        this.started = false;
        for (const output of outputs) output[f] = 0;
        continue;
      }
      const next = (this.read + 1) % this.capacity;
      for (let c = 0; c < outputs.length; c++) outputs[c][f] = this.sample(this.read, c) * (1 - this.phase) + this.sample(next, c) * this.phase;
      this.phase += this.step;
      const consumed = Math.floor(this.phase); this.phase -= consumed;
      this.read = (this.read + consumed) % this.capacity; this.size -= consumed;
    }
  }
}
