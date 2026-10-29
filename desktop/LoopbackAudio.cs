using System;
using NAudio.Wave;

namespace HavijScreen {
  // Shared-mode capture of the default playback endpoint. It uses that endpoint's
  // mix format, avoiding Chromium's failed audio-source initialization path.
  internal sealed class LoopbackAudio : IDisposable {
    private WasapiLoopbackCapture capture;
    public event Action<byte[], int, int> Data;
    public event Action<Exception> Failed;
    public WaveFormat Format { get { return capture.WaveFormat; } }
    public void Start() {
      WasapiLoopbackCapture active = new WasapiLoopbackCapture(); capture = active;
      capture.DataAvailable += delegate(object sender, WaveInEventArgs args) {
        if (args.BytesRecorded > 0 && Data != null) Data(args.Buffer, args.BytesRecorded, active.WaveFormat.BlockAlign);
      };
      capture.RecordingStopped += delegate(object sender, StoppedEventArgs args) {
        if (args.Exception != null && Failed != null) Failed(args.Exception);
      };
      capture.StartRecording();
    }
    public void Dispose() {
      WasapiLoopbackCapture previous = capture; capture = null;
      if (previous != null) previous.Dispose();
    }
  }
}
