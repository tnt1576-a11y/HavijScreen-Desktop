using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HavijScreen.Native;

internal sealed record Quality(string Name, int Width, int Height, int Fps, int Kbps)
{
    internal static readonly Quality[] All = [new("Efficient · 720p / 30 fps",1280,720,30,2500), new("Balanced · 1080p / 30 fps",1920,1080,30,5000), new("Fluid · 1080p / 60 fps",1920,1080,60,8000), new("Crisp · 1440p / 60 fps",2560,1440,60,12000)];
    public override string ToString() => Name;
}
internal sealed record CaptureSource(string Name, IntPtr Handle, uint ProcessId, bool Monitor)
{
    public override string ToString() => Name;
}
internal static class Sources
{
    delegate bool EnumWindow(IntPtr window, IntPtr data);
    delegate bool EnumMonitor(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data);
    [DllImport("user32")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rectangle, EnumMonitor callback, IntPtr data);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32")] static extern long GetWindowLongPtr(IntPtr window, int index);
    internal static IReadOnlyList<CaptureSource> List(bool monitors)
    {
        var result = new List<CaptureSource>();
        if (monitors)
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) => { result.Add(new("Display " + (result.Count + 1), handle, 0, true)); return true; }, IntPtr.Zero);
        }
        else
        {
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero || (GetWindowLongPtr(window,-20) & 0x80) != 0) return true;
                GetWindowThreadProcessId(window, out uint pid);
                if (pid == Environment.ProcessId || pid == 0) return true;
                var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity);
                if (text.Length > 0) result.Add(new(text.ToString(),window,pid,false)); return true;
            }, IntPtr.Zero);
            result.Sort((a,b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name,b.Name));
        }
        return result;
    }
    internal static void Validate(CaptureSource source)
    {
        if (source.Monitor) return;
        if (!IsWindow(source.Handle)) throw new InvalidOperationException("That window closed. Choose another window.");
        GetWindowThreadProcessId(source.Handle,out uint pid);
        if (pid == 0 || pid != source.ProcessId) throw new InvalidOperationException("The selected application changed. Choose the window again.");
    }
}

internal sealed class MediaCapture : IDisposable
{
    Pipeline? video, audio;
    IntPtr videoSink, audioSink;
    bool disposed;
    long frames, bytes, firstFrameTime, lastFrameTime;
    internal long Frames => Interlocked.Read(ref frames);
    internal long Bytes => Interlocked.Read(ref bytes);
    internal double SteadyFps => Frames > 1 && lastFrameTime > firstFrameTime ? (Frames-1)*Stopwatch.Frequency/(double)(lastFrameTime-firstFrameTime) : 0;
    internal string Encoder { get; private set; } = "";
    internal event Action<bool, IntPtr, ulong>? Packet;
    internal event Action<string>? Failed;
    internal static string SelectEncoder() => new[] { "nvd3d11h264enc", "amfh264enc", "qsvh264enc" }.FirstOrDefault(Gst.Has)
        ?? throw new InvalidOperationException("No supported GPU H.264 encoder was found. Update your graphics driver. HavijScreen will not silently switch to CPU encoding.");
    internal static string EncoderSettings(string encoder, Quality quality)
    {
        return encoder switch
        {
            "nvd3d11h264enc" => $"{encoder} name=encoder bitrate={quality.Kbps} rc-mode=cbr preset=p1 tune=ultra-low-latency zerolatency=true gop-size={quality.Fps} bframes=0 rc-lookahead=0 repeat-sequence-header=true",
            "amfh264enc" => $"{encoder} name=encoder bitrate={quality.Kbps} rate-control=cbr usage=ultra-low-latency gop-size={quality.Fps} b-frames=0",
            "qsvh264enc" => $"{encoder} name=encoder bitrate={quality.Kbps} rate-control=cbr gop-size={quality.Fps} b-frames=0",
            _ => throw new InvalidOperationException("Unsupported hardware encoder.")
        };
    }
    internal static string VideoPipeline(CaptureSource source, Quality quality, string encoder, bool synthetic = false)
    {
        string input = synthetic ? "videotestsrc is-live=true pattern=ball ! video/x-raw,format=BGRA,width=1920,height=1080 ! d3d11upload"
            : "d3d11screencapturesrc capture-api=wgc show-cursor=true show-border=true " + (source.Monitor ? "monitor-handle=" : "window-handle=") + unchecked((ulong)source.Handle.ToInt64());
        return $"{input} ! video/x-raw(memory:D3D11Memory),framerate={quality.Fps}/1 ! d3d11convert ! video/x-raw(memory:D3D11Memory),format=NV12,width={quality.Width},height={quality.Height} ! {EncoderSettings(encoder,quality)} ! video/x-h264,profile=constrained-baseline,stream-format=byte-stream,alignment=au ! h264parse config-interval=-1 ! appsink name=encoded-video emit-signals=true sync=false max-buffers=2 drop=true";
    }
    internal static string AudioSource(CaptureSource source, bool systemAudio)
    {
        if (!source.Monitor)
        {
            if (source.ProcessId == 0) throw new InvalidOperationException("The application audio source is unavailable.");
            // The process id is explicit and nonzero. Never fall back to the
            // default endpoint if application audio cannot be activated.
            return $"wasapi2src loopback=true loopback-mode=include-process-tree loopback-target-pid={source.ProcessId} low-latency=true continue-on-error=false";
        }
        if (!systemAudio) throw new InvalidOperationException("System audio was not selected.");
        return "wasapi2src loopback=true low-latency=true continue-on-error=false";
    }
    internal void Start(CaptureSource source, Quality quality, bool shareAudio, bool synthetic = false)
    {
        if (video != null || disposed) throw new InvalidOperationException("The capture session is already in use.");
        if (!synthetic) Sources.Validate(source);
        Encoder = SelectEncoder();
        try
        {
            video = new Pipeline(VideoPipeline(source,quality,Encoder,synthetic));
            video.Failed += message => Failed?.Invoke("Video capture: " + message);
            video.Ended += () => { if (!disposed) Failed?.Invoke("The selected capture source closed."); };
            videoSink = Gst.Named(video.Handle,"encoded-video");
            video.Connect(videoSink,"new-sample",new Gst.NewSample((sink,_) => ReadPacket(sink,false)));
            if (shareAudio)
            {
                string input = synthetic ? "audiotestsrc is-live=true wave=sine freq=440 volume=0.1" : AudioSource(source,source.Monitor);
                audio = new Pipeline(input + " ! audioconvert ! audioresample ! audio/x-raw,format=S16LE,rate=48000,channels=2 ! opusenc bitrate=96000 audio-type=restricted-lowdelay frame-size=10 ! appsink name=encoded-audio emit-signals=true sync=false max-buffers=4 drop=true");
                audio.Failed += message => Failed?.Invoke("Audio capture stopped: " + message);
                audioSink = Gst.Named(audio.Handle,"encoded-audio");
                audio.Connect(audioSink,"new-sample",new Gst.NewSample((sink,_) => ReadPacket(sink,true)));
            }
            video.Play(); audio?.Play();
        }
        catch { Dispose(); throw; }
    }
    int ReadPacket(IntPtr sink, bool isAudio)
    {
        if (disposed) return -2;
        IntPtr sample = Gst.gst_app_sink_try_pull_sample(sink,0);
        if (sample == IntPtr.Zero) return 0;
        try
        {
            IntPtr buffer = Gst.gst_sample_get_buffer(sample);
            if (!isAudio) { long stamp = Stopwatch.GetTimestamp(); Interlocked.CompareExchange(ref firstFrameTime,stamp,0); Interlocked.Exchange(ref lastFrameTime,stamp); Interlocked.Increment(ref frames); }
            Interlocked.Add(ref bytes,(long)Gst.gst_buffer_get_size(buffer).ToUInt64());
            ulong pts = Gst.ReadTime(buffer,Gst.PtsOffset);
            IntPtr segment = Gst.gst_sample_get_segment(sample);
            ulong running = pts != Gst.None && segment != IntPtr.Zero ? Gst.gst_segment_to_running_time(segment,3,pts) : Gst.None;
            // Hardware encoders can shift their output segment (NVENC uses a
            // large positive offset). Normalize through the sample's segment.
            ulong absolute = running == Gst.None ? Gst.gst_clock_get_time(Gst.SharedClock) : Gst.gst_element_get_base_time((isAudio ? audio : video)!.Handle) + running;
            Packet?.Invoke(isAudio,buffer,absolute); return 0;
        }
        catch (Exception error) { ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(error.Message)); return -5; }
        finally { Gst.gst_mini_object_unref(sample); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        audio?.Dispose(); video?.Dispose(); audio = video = null;
        if (audioSink != IntPtr.Zero) Gst.gst_object_unref(audioSink);
        if (videoSink != IntPtr.Zero) Gst.gst_object_unref(videoSink);
        audioSink = videoSink = IntPtr.Zero;
    }
}
