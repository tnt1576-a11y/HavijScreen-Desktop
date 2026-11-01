using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HavijScreen.Native;

internal sealed record Quality(string Name, int Width, int Height, int Fps, int Kbps)
{
    internal bool PrioritizeGame { get; init; }
    internal static readonly Quality Performance = new("Performance · 720p / auto",1280,720,30,2500) { PrioritizeGame=true };
    internal static readonly Quality[] All = [new("Efficient · 720p / 30 fps",1280,720,30,2500), new("Balanced · 1080p / 30 fps",1920,1080,30,5000), new("Fluid · 1080p / 60 fps",1920,1080,60,8000), new("Crisp · 1440p / 60 fps",2560,1440,60,12000), Performance];
    public override string ToString() => Name;
}
internal sealed record CaptureSource(string Name, IntPtr Handle, uint ProcessId, bool Monitor)
{
    internal Rectangle Bounds { get; init; }
    internal bool CaptureCursor { get; init; } = true;
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
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public int Left,Top,Right,Bottom,WorkLeft,WorkTop,WorkRight,WorkBottom; public uint Flags; }
    [DllImport("user32",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    internal static IReadOnlyList<CaptureSource> List(bool monitors)
    {
        var result = new List<CaptureSource>();
        if (monitors)
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) => { var info=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(handle,ref info); result.Add(new("Screen " + (result.Count + 1), handle, 0, true) { Bounds=Rectangle.FromLTRB(info.Left,info.Top,info.Right,info.Bottom) }); return true; }, IntPtr.Zero);
        }
        else
        {
            var images=new Dictionary<uint,string?>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero || (GetWindowLongPtr(window,-20) & 0x80) != 0) return true;
                GetWindowThreadProcessId(window, out uint pid);
                if (pid == Environment.ProcessId || pid == 0) return true;
                var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity);
                if (text.Length == 0 || !SourceEligibility.CanOffer(window,pid,images)) return true;
                result.Add(new(text.ToString(),window,pid,false)); return true;
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
    Pipeline? video, audio, preview;
    readonly object previewGate=new();
    IntPtr videoSink, audioSink, previewSink, previewSource, rawSink, previewValve, encoderElement, captureRate, encodeSize;
    int maximumHeight;
    internal int Height { get; private set; }
    internal int TargetFps { get; private set; }
    internal int BitrateKbps { get; private set; }
    bool disposed, diagnostic;
    long frames, bytes, firstFrameTime, lastFrameTime;
    internal long Frames => Interlocked.Read(ref frames);
    internal long Bytes => Interlocked.Read(ref bytes);
    internal double SteadyFps => Frames > 1 && lastFrameTime > firstFrameTime ? (Frames-1)*Stopwatch.Frequency/(double)(lastFrameTime-firstFrameTime) : 0;
    internal string Encoder { get; private set; } = "";
    internal event Action<bool, IntPtr, ulong>? Packet;
    internal event Action<string>? Failed;
    internal event Action<string>? PreviewFailed;
    long previewFrames;
    long nextPreviewFrame;
    internal long PreviewFrames => Interlocked.Read(ref previewFrames);
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
    internal static string VideoPipeline(CaptureSource source, Quality quality, string encoder, bool synthetic = false, bool preview=false, bool encode=true)
    {
        // Desktop Duplication avoids WGC's monitor-wide capture indicator and
        // compositor path. Window capture still uses WGC with its OS indicator.
        string input = synthetic ? "videotestsrc is-live=true pattern=ball ! video/x-raw,format=BGRA,width=1920,height=1080 ! d3d11upload"
            : $"d3d11screencapturesrc capture-api={(source.Monitor?"dxgi":"wgc")} show-cursor={source.CaptureCursor.ToString().ToLowerInvariant()} show-border=true " + (source.Monitor ? "monitor-handle=" : "window-handle=") + unchecked((ulong)source.Handle.ToInt64());
        string size=quality.Width>0&&quality.Height>0?$",width={quality.Width},height={quality.Height}":"";
        // Allow in-flight frames using the old cadence while the source handles
        // a reconfigure event; rejecting them immediately can stop WGC.
        string raw=$"{input} ! capsfilter name=capture-rate caps-change-mode=delayed caps=\"video/x-raw(memory:D3D11Memory),framerate={quality.Fps}/1\" ! d3d11convert ! capsfilter name=encode-size caps-change-mode=delayed caps=\"video/x-raw(memory:D3D11Memory),format=NV12{size}\"";
        if(synthetic) raw+=" ! identity name=capture-fixture silent=true";
        string playback="valve name=preview-valve drop=true drop-mode=forward-sticky-events ! appsink name=raw-preview emit-signals=true sync=false async=false max-buffers=1 drop=true wait-on-eos=false enable-last-sample=false";
        if(!encode) return raw+" ! "+playback;
        string encoding=$"{EncoderSettings(encoder,quality)} ! video/x-h264,profile=constrained-baseline,stream-format=byte-stream,alignment=au ! h264parse config-interval=-1 ! appsink name=encoded-video emit-signals=true sync=false max-buffers=2 drop=false";
        return raw+" ! tee name=frames frames. ! queue max-size-buffers=2 max-size-bytes=0 max-size-time=0 leaky=downstream ! "+encoding+" frames. ! "+playback;
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
    internal void Start(CaptureSource source, Quality quality, bool shareAudio, bool synthetic = false, IntPtr previewWindow=default, bool encode=true)
    {
        if (video != null || disposed) throw new InvalidOperationException("The capture session is already in use.");
        if (!synthetic) Sources.Validate(source);
        if(!encode&&previewWindow==IntPtr.Zero) throw new InvalidOperationException("A local preview window is required.");
        // Preview alone never needs the high-resolution/60 fps stream workload.
        if(!encode) quality=new("Local preview",1280,720,Math.Min(30,quality.Fps),quality.Kbps);
        Encoder = encode?SelectEncoder():"";
        diagnostic=synthetic;
        try
        {
            video = new Pipeline(VideoPipeline(source,quality,Encoder,synthetic,previewWindow!=IntPtr.Zero,encode));
            captureRate=Gst.Named(video.Handle,"capture-rate"); encodeSize=Gst.Named(video.Handle,"encode-size"); TargetFps=quality.Fps; maximumHeight=Height=quality.Height;
            video.Failed += message => Failed?.Invoke("Video capture: " + message);
            video.Ended += () => { if (!disposed) Failed?.Invoke("The selected capture source closed."); };
            if(encode) { videoSink = Gst.Named(video.Handle,"encoded-video"); video.Connect(videoSink,"new-sample",new Gst.NewSample((sink,_) => ReadPacket(sink,false))); encoderElement=Gst.Named(video.Handle,"encoder"); BitrateKbps=quality.Kbps; }
            rawSink=Gst.Named(video.Handle,"raw-preview"); previewValve=Gst.Named(video.Handle,"preview-valve");
            video.Connect(rawSink,"new-sample",new Gst.NewSample((sink,_)=>PreviewPacket(sink)));
            if(previewWindow!=IntPtr.Zero) try { SetPreview(previewWindow); } catch(Exception error) { PreviewFailed?.Invoke(error.Message); }
            if (shareAudio&&encode)
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
    internal void DiagnosticDelay(uint microseconds)
    {
        if(!diagnostic||video==null) throw new InvalidOperationException("Capture delay is restricted to synthetic diagnostics.");
        IntPtr fixture=Gst.Named(video.Handle,"capture-fixture");
        try { Gst.gst_util_set_object_arg(fixture,"sleep-time",microseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        finally { Gst.gst_object_unref(fixture); }
    }
    internal void SetFrameRate(int fps)
    {
        if(fps is not (15 or 20 or 30)) throw new ArgumentOutOfRangeException(nameof(fps));
        if(disposed||captureRate==IntPtr.Zero||fps==TargetFps) return;
        // Renegotiate capture cadence in place; leave audio and peer connections
        // running. No additional frames are captured just to discard them.
        Gst.gst_util_set_object_arg(captureRate,"caps",$"video/x-raw(memory:D3D11Memory),framerate={fps}/1");
        TargetFps=fps;
    }
    internal void SetResolution(int height)
    {
        if(disposed || maximumHeight==0 || height==Height) return;
        if(height<480 || height>maximumHeight) throw new ArgumentOutOfRangeException(nameof(height));
        int width=(int)Math.Round(height*16.0/9/2)*2;
        Gst.gst_util_set_object_arg(encodeSize,"caps",$"video/x-raw(memory:D3D11Memory),format=NV12,width={width},height={height}");
        Height=height; RequestKeyframe();
    }
    internal void SetBitrate(int kbps)
    {
        if(disposed||encoderElement==IntPtr.Zero||kbps==BitrateKbps) return;
        Gst.SetInt(encoderElement,"bitrate",kbps,IntPtr.Zero);
        Gst.GetInt(encoderElement,"bitrate",out int applied,IntPtr.Zero);
        if(applied!=kbps) throw new InvalidOperationException("The encoder did not accept the bitrate change.");
        BitrateKbps=applied;
    }
    internal bool RequestKeyframe()
    {
        if(disposed || encoderElement==IntPtr.Zero) return false;
        IntPtr pad=Gst.gst_element_get_static_pad(encoderElement,"src");
        try { return Gst.gst_pad_send_event(pad,Gst.gst_video_event_new_upstream_force_key_unit(Gst.None,1,0))!=0; }
        finally { Gst.gst_object_unref(pad); }
    }
    internal void SetPreview(IntPtr window)
    {
        if(disposed) return;
        Gst.SetInt(previewValve,"drop",1,IntPtr.Zero);
        Pipeline? old;
        lock(previewGate)
        {
            old=preview; preview=null;
            if(previewSource!=IntPtr.Zero) Gst.gst_object_unref(previewSource);
            if(previewSink!=IntPtr.Zero) Gst.gst_object_unref(previewSink);
            previewSource=previewSink=IntPtr.Zero; nextPreviewFrame=0;
        }
        old?.Dispose();
        if(window==IntPtr.Zero) return;
        var next=new Pipeline("appsrc name=preview-input is-live=true format=time block=false max-buffers=1 leaky-type=downstream ! identity name=preview-counter signal-handoffs=true silent=true ! d3d11videosink name=local-preview sync=false async=false enable-last-sample=false");
        try
        {
            next.Failed+=error=> { if(!disposed&&ReferenceEquals(preview,next)) PreviewFailed?.Invoke(error); };
            IntPtr counter=Gst.Named(next.Handle,"preview-counter"); next.Connect(counter,"handoff",new Gst.ObjectSignal((_,_,_)=>Interlocked.Increment(ref previewFrames))); Gst.gst_object_unref(counter);
            lock(previewGate)
            {
                preview=next; previewSource=Gst.Named(next.Handle,"preview-input"); previewSink=Gst.Named(next.Handle,"local-preview");
                Gst.gst_video_overlay_set_window_handle(previewSink,unchecked((UIntPtr)(ulong)window.ToInt64())); next.Play();
            }
            Gst.SetInt(previewValve,"drop",0,IntPtr.Zero);
        }
        catch { SetPreview(IntPtr.Zero); throw; }
    }
    int PreviewPacket(IntPtr sink)
    {
        IntPtr sample=Gst.gst_app_sink_try_pull_sample(sink,0); if(sample==IntPtr.Zero) return 0;
        try
        {
            lock(previewGate)
            {
                if(previewSource==IntPtr.Zero||disposed) return 0;
                long now=Stopwatch.GetTimestamp(), interval=Stopwatch.Frequency/30;
                if(now<nextPreviewFrame) return 0;
                nextPreviewFrame=Math.Max(now,nextPreviewFrame+interval);
                Gst.gst_app_src_set_caps(previewSource,Gst.gst_sample_get_caps(sample));
                // Transfer a reference to the existing GPU memory, without mapping pixels.
                Gst.gst_app_src_push_buffer(previewSource,Gst.gst_mini_object_ref(Gst.gst_sample_get_buffer(sample)));
            }
            return 0;
        }
        catch(Exception error) { ThreadPool.QueueUserWorkItem(_=>PreviewFailed?.Invoke(error.Message)); return 0; }
        finally { Gst.gst_mini_object_unref(sample); }
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
        if (disposed) return;
        if(previewValve!=IntPtr.Zero) SetPreview(IntPtr.Zero);
        disposed = true;
        audio?.Dispose(); video?.Dispose(); audio = video = null;
        if (audioSink != IntPtr.Zero) Gst.gst_object_unref(audioSink);
        if (videoSink != IntPtr.Zero) Gst.gst_object_unref(videoSink);
        foreach(var element in new[]{rawSink,previewValve,encoderElement,captureRate,encodeSize}) if(element!=IntPtr.Zero) Gst.gst_object_unref(element);
        audioSink = videoSink = rawSink = previewValve = encoderElement = captureRate = encodeSize = IntPtr.Zero;
    }
}
