using System;
using System.Threading;
using HavijScreen;
internal static class AudioProbe {
  static int Main() {
    long received = 0; Exception failure = null;
    using (LoopbackAudio audio = new LoopbackAudio()) {
      audio.Data += delegate(byte[] bytes, int count, int alignment) { Interlocked.Add(ref received, count); };
      audio.Failed += delegate(Exception error) { failure = error; };
      try {
        audio.Start();
        Console.WriteLine("Started Windows loopback: " + audio.Format.SampleRate + " Hz, " + audio.Format.Channels + " channels, " + audio.Format.Encoding);
        Thread.Sleep(1800);
        if (failure != null) throw failure;
        Console.WriteLine("Captured PCM bytes in memory: " + Interlocked.Read(ref received));
        Console.WriteLine("No audio was saved or transmitted.");
        return 0;
      } catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
  }
}
