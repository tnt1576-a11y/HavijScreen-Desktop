using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Security.Cryptography;

namespace HavijScreen.Native;

internal sealed record SessionDescription(string type, string sdp);
internal sealed class NativePeer : IDisposable
{
    readonly object gate = new();
    readonly Pipeline pipeline;
    readonly bool headless;
    readonly List<IntPtr> receiveBins = [];
    readonly List<Delegate> promises = [];
    readonly Channel<string> controls = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly CancellationTokenSource lifetime = new();
    IntPtr rtc, videoSource, audioSource, channel, videoSink, volume;
    bool disposed;
    IntPtr targetWindow;
    long frames, audioPackets, sentBytes;
    TaskCompletionSource<bool> gathering = NewCompletion<bool>();
    internal string Id { get; }
    internal string Name { get; set; }
    internal bool ChannelOpen { get; private set; }
    internal bool Established { get; private set; }
    internal bool WatchingUs { get; set; }
    internal bool Subscribed { get; set; }
    internal bool Sharing { get; set; }
    internal bool AudioEnabled { get; private set; }
    internal long VideoFrames => Interlocked.Read(ref frames);
    internal long AudioPackets => Interlocked.Read(ref audioPackets);
    internal long SentBytes => Interlocked.Read(ref sentBytes);
    internal event Action? Opened;
    internal event Action? Closed;
    internal event Func<string,Task>? Message;
    internal event Action<string>? Failed;
    internal event Action? VideoArrived;
    internal event Action<uint,string>? IceCandidate;
    static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Handoff(IntPtr sender, IntPtr buffer, IntPtr pad, IntPtr data);

    internal NativePeer(string id, string name, bool initiator, bool stun, bool diagnostic = false)
    {
        Id = id; Name = name; headless = diagnostic;
        _ = Task.Run(async () =>
        {
            try { await foreach (string text in controls.Reader.ReadAllAsync(lifetime.Token)) { if (Message != null) await Message(text); } }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed) Failed?.Invoke(error.Message); }
        });
        int videoSsrc = RandomNumberGenerator.GetInt32(1,int.MaxValue), audioSsrc = RandomNumberGenerator.GetInt32(1,int.MaxValue);
        string description = "webrtcbin name=rtc bundle-policy=max-bundle latency=80 "
            + $"appsrc name=sendvideo is-live=true format=time block=false min-latency=0 max-latency=0 max-buffers=3 leaky-type=downstream caps=\"video/x-h264,stream-format=byte-stream,alignment=au,profile=constrained-baseline\" ! queue max-size-buffers=3 max-size-bytes=0 max-size-time=0 leaky=downstream ! rtph264pay pt=96 ssrc={videoSsrc} config-interval=-1 aggregate-mode=zero-latency ! application/x-rtp,media=video,encoding-name=H264,clock-rate=90000,payload=96,packetization-mode=(string)1,ssrc=(uint){videoSsrc} ! rtc. "
            + $"appsrc name=sendaudio is-live=true format=time block=false min-latency=0 max-latency=0 max-buffers=8 leaky-type=downstream caps=\"audio/x-opus,rate=48000,channels=2,channel-mapping-family=0\" ! queue max-size-buffers=8 max-size-bytes=0 max-size-time=0 leaky=downstream ! rtpopuspay pt=111 ssrc={audioSsrc} ! application/x-rtp,media=audio,encoding-name=OPUS,clock-rate=48000,payload=111,encoding-params=(string)2,ssrc=(uint){audioSsrc} ! rtc.";
        pipeline = new Pipeline(description);
        pipeline.Failed += message => Failed?.Invoke(message);
        rtc = Gst.Named(pipeline.Handle,"rtc"); videoSource = Gst.Named(pipeline.Handle,"sendvideo"); audioSource = Gst.Named(pipeline.Handle,"sendaudio");
        if (stun) Gst.SetString(rtc,"stun-server","stun://stun.cloudflare.com:3478",IntPtr.Zero);
        pipeline.Connect(rtc,"notify::ice-gathering-state",new Gst.Notify((_,_,_) =>
        {
            lock(gate) { if (disposed) return; Gst.GetInt(rtc,"ice-gathering-state",out int state,IntPtr.Zero); if (state == 2) gathering.TrySetResult(true); }
        }));
        pipeline.Connect(rtc,"notify::connection-state",new Gst.Notify((_,_,_) =>
        {
            lock(gate)
            {
                if (disposed) return;
                Gst.GetInt(rtc,"connection-state",out int state,IntPtr.Zero);
                if (state == 4) ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke("Could not establish a direct connection. These networks may need a trusted LAN/VPN or a TURN relay."));
            }
        }));
        pipeline.Connect(rtc,"on-ice-candidate",new Gst.IceSignal((_,line,text,_) => IceCandidate?.Invoke(line,Gst.Text(text))));
        pipeline.Connect(rtc,"on-data-channel",new Gst.ObjectSignal((_,item,_) => BindChannel(item,true)));
        pipeline.Connect(rtc,"pad-added",new Gst.ObjectSignal((_,pad,_) =>
        {
            try { AttachReceiver(pad); } catch (Exception error) { ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(error.Message)); }
        }));
        pipeline.Play();
        if (initiator)
        {
            Gst.CreateChannel(rtc,"create-data-channel","havijscreen-control",IntPtr.Zero,out var created);
            if (created == IntPtr.Zero) throw new InvalidOperationException("Could not prepare the encrypted control channel.");
            BindChannel(created,false);
        }
    }
    void BindChannel(IntPtr value, bool borrowed)
    {
        lock (gate)
        {
            if (disposed || channel != IntPtr.Zero) return;
            channel = borrowed ? Gst.g_object_ref(value) : value;
            pipeline.Connect(channel,"on-open",new Gst.VoidSignal((_,_) =>
            {
                if (disposed) return; ChannelOpen = Established = true; ThreadPool.QueueUserWorkItem(_ => Opened?.Invoke());
            }));
            pipeline.Connect(channel,"on-close",new Gst.VoidSignal((_,_) => { ChannelOpen = false; if (!disposed) ThreadPool.QueueUserWorkItem(_ => Closed?.Invoke()); }));
            pipeline.Connect(channel,"on-error",new Gst.ObjectSignal((_,_,_) => { if (!disposed) ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke("The encrypted control channel failed.")); }));
            pipeline.Connect(channel,"on-message-string",new Gst.StringSignal((_,text,_) =>
            {
                if (disposed) return;
                string? message = LimitedText(text,160000);
                if (message != null && !controls.Writer.TryWrite(message)) ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke("A peer sent too many control messages."));
            }));
        }
    }
    static unsafe string? LimitedText(IntPtr value, int limit)
    {
        if (value == IntPtr.Zero) return null;
        byte* p = (byte*)value; int size = 0; while (size <= limit && p[size] != 0) size++;
        return size > limit ? null : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p,size));
    }
    internal async Task<SessionDescription> CreateDescription(bool offer, CancellationToken cancellation = default, bool gather = true)
    {
        var result = NewCompletion<SessionDescription>();
        Gst.PromiseChanged? callback = null;
        callback = (promise,_) =>
        {
            try
            {
                IntPtr reply = Gst.gst_promise_get_reply(promise);
                IntPtr value = Gst.gst_structure_get_value(reply,offer ? "offer" : "answer");
                if (value == IntPtr.Zero) throw new InvalidOperationException("The media engine could not negotiate this connection.");
                IntPtr native = Gst.g_value_get_boxed(value);
                var description = Marshal.PtrToStructure<Gst.Description>(native);
                IntPtr text = Gst.gst_sdp_message_as_text(description.Sdp);
                try { result.TrySetResult(new(offer ? "offer" : "answer",Gst.Text(text))); } finally { Gst.g_free(text); }
            }
            catch (Exception error) { result.TrySetException(error); }
            finally { Gst.gst_promise_unref(promise); }
        };
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this); promises.Add(callback);
            IntPtr promise = Gst.Promise(callback);
            Gst.CreateDescription(rtc,offer ? "create-offer" : "create-answer",IntPtr.Zero,promise);
        }
        var local = await result.Task.WaitAsync(TimeSpan.FromSeconds(12),cancellation);
        await ApplyDescription(local,true,cancellation);
        int state;
        lock(gate) { ObjectDisposedException.ThrowIf(disposed,this); Gst.GetInt(rtc,"ice-gathering-state",out state,IntPtr.Zero); }
        if (gather && state != 2) await gathering.Task.WaitAsync(TimeSpan.FromSeconds(15),cancellation);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            Gst.GetPointer(rtc,"local-description",out var native,IntPtr.Zero);
            if (native == IntPtr.Zero) throw new InvalidOperationException("No local connection description was available.");
            try
            {
                IntPtr text = Gst.gst_sdp_message_as_text(Marshal.PtrToStructure<Gst.Description>(native).Sdp);
                try { return new(local.type,Gst.Text(text)); } finally { Gst.g_free(text); }
            }
            finally { Gst.gst_webrtc_session_description_free(native); }
        }
    }
    internal void AddCandidate(uint line, string candidate)
    {
        if (line >= 8 || candidate.Length > 4096 || !candidate.StartsWith("candidate:")) return;
        lock (gate) { if (!disposed) Gst.AddIce(rtc,"add-ice-candidate",line,candidate); }
    }
    internal Task ApplyDescription(SessionDescription description, bool local = false, CancellationToken cancellation = default)
    {
        if (description.type is not ("offer" or "answer") || description.sdp.Length > 100000 || !description.sdp.StartsWith("v=0")) throw new InvalidOperationException("Invalid connection description.");
        byte[] bytes = Encoding.UTF8.GetBytes(description.sdp);
        var result = NewCompletion<bool>();
        Gst.PromiseChanged callback = (promise,_) =>
        {
            try
            {
                IntPtr reply = Gst.gst_promise_get_reply(promise);
                if (reply != IntPtr.Zero && Gst.gst_structure_get_value(reply,"error") != IntPtr.Zero) result.TrySetException(new InvalidOperationException("The media engine rejected the connection description."));
                else result.TrySetResult(true);
            }
            finally { Gst.gst_promise_unref(promise); }
        };
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this); promises.Add(callback);
            Gst.gst_sdp_message_new(out var sdp);
            if (Gst.gst_sdp_message_parse_buffer(bytes,(uint)bytes.Length,sdp) != 0) { Gst.gst_sdp_message_free(sdp); throw new InvalidOperationException("Could not parse connection details."); }
            IntPtr native = Gst.gst_webrtc_session_description_new(description.type == "offer" ? 1 : 3,sdp);
            try
            {
                IntPtr promise = Gst.Promise(callback);
                Gst.SetDescription(rtc,local ? "set-local-description" : "set-remote-description",native,promise);
            }
            finally { Gst.gst_webrtc_session_description_free(native); }
        }
        return result.Task.WaitAsync(TimeSpan.FromSeconds(12),cancellation);
    }
    internal bool Send(object message)
    {
        string text = JsonSerializer.Serialize(message);
        if (text.Length > 150000) return false;
        lock (gate)
        {
            if (disposed || !ChannelOpen || channel == IntPtr.Zero) return false;
            int success = Gst.gst_webrtc_data_channel_send_string_full(channel,text,out var error);
            if (error != IntPtr.Zero) Gst.g_error_free(error);
            return success != 0;
        }
    }
    internal void Push(bool isAudio, IntPtr encoded, ulong absoluteTime)
    {
        lock (gate)
        {
            if (disposed || !ChannelOpen || !WatchingUs) return;
            IntPtr copy = Gst.gst_buffer_copy(encoded); // Metadata only; encoded payload memory is shared.
            ulong baseTime = Gst.gst_element_get_base_time(pipeline.Handle);
            ulong pts = absoluteTime > baseTime ? absoluteTime-baseTime : 0;
            Gst.SetTime(copy,Gst.PtsOffset,pts); Gst.SetTime(copy,Gst.DtsOffset,Gst.None);
            Interlocked.Add(ref sentBytes,(long)Gst.gst_buffer_get_size(copy).ToUInt64());
            Gst.gst_app_src_push_buffer(isAudio ? audioSource : videoSource,copy);
        }
    }
    internal void SetView(IntPtr window, bool audioEnabled)
    {
        lock (gate)
        {
            if (disposed) return;
            targetWindow = window; Subscribed = window != IntPtr.Zero || headless; AudioEnabled = audioEnabled;
            if (videoSink != IntPtr.Zero && window != IntPtr.Zero) Gst.gst_video_overlay_set_window_handle(videoSink,unchecked((UIntPtr)(ulong)window.ToInt64()));
            if (volume != IntPtr.Zero) Gst.SetInt(volume,"mute",audioEnabled && Subscribed ? 0 : 1,IntPtr.Zero);
        }
    }
    void AttachReceiver(IntPtr pad)
    {
        lock (gate)
        {
            if (disposed) return;
            IntPtr caps = Gst.gst_pad_get_current_caps(pad); if (caps == IntPtr.Zero) caps = Gst.gst_pad_query_caps(pad,IntPtr.Zero);
            IntPtr nativeText = Gst.gst_caps_to_string(caps); string text = Gst.Text(nativeText); Gst.g_free(nativeText); Gst.gst_mini_object_unref(caps);
            bool isAudio = text.Contains("media=(string)audio") || text.Contains("media=audio");
            bool isVideo = text.Contains("media=(string)video") || text.Contains("media=video");
            if (!isAudio && !isVideo) return;
            string chain = isVideo
                ? "queue max-size-buffers=3 max-size-bytes=0 max-size-time=0 leaky=downstream ! rtph264depay ! h264parse ! d3d11h264dec ! identity name=frame-counter signal-handoffs=true silent=true ! " + (headless ? "fakesink name=receiver sync=false" : "d3d11videosink name=receiver sync=false async=false enable-last-sample=false")
                : "queue max-size-time=200000000 max-size-buffers=0 max-size-bytes=0 ! rtpopusdepay ! opusdec ! audioconvert ! audioresample ! identity name=frame-counter signal-handoffs=true silent=true ! " + (headless ? "fakesink name=receiver sync=false" : "volume name=audio-volume mute=true ! wasapi2sink low-latency=true sync=true");
            IntPtr bin = Gst.gst_parse_bin_from_description(chain,1,out var error);
            if (error != IntPtr.Zero || bin == IntPtr.Zero) throw new InvalidOperationException(Gst.Error(error));
            Gst.gst_bin_add(pipeline.Handle,bin); receiveBins.Add(bin);
            IntPtr counter = Gst.Named(bin,"frame-counter");
            pipeline.Connect(counter,"handoff",new Gst.ObjectSignal((_,_,_) => { if (isVideo) Interlocked.Increment(ref frames); else Interlocked.Increment(ref audioPackets); }));
            Gst.gst_object_unref(counter);
            if (!headless && isVideo)
            {
                videoSink = Gst.Named(bin,"receiver");
                if (targetWindow != IntPtr.Zero) Gst.gst_video_overlay_set_window_handle(videoSink,unchecked((UIntPtr)(ulong)targetWindow.ToInt64()));
                ThreadPool.QueueUserWorkItem(_ => VideoArrived?.Invoke());
            }
            else if (!headless) { volume = Gst.Named(bin,"audio-volume"); Gst.SetInt(volume,"mute",AudioEnabled && Subscribed ? 0 : 1,IntPtr.Zero); }
            IntPtr sinkPad = Gst.gst_element_get_static_pad(bin,"sink");
            try { if (Gst.gst_pad_link(pad,sinkPad) != 0) throw new InvalidOperationException("Could not attach the received stream."); }
            finally { Gst.gst_object_unref(sinkPad); }
            Gst.gst_element_sync_state_with_parent(bin);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; ChannelOpen = false;
            lifetime.Cancel(); controls.Writer.TryComplete();
        }
        // Native state changes can wait for streaming callbacks. Those callbacks
        // must be able to acquire gate, observe disposed, and return.
        pipeline.Dispose();
        foreach (IntPtr pointer in new[] { channel,videoSink,volume,videoSource,audioSource,rtc }) if (pointer != IntPtr.Zero) Gst.g_object_unref(pointer);
        channel = videoSink = volume = videoSource = audioSource = rtc = IntPtr.Zero;
        GC.KeepAlive(promises);
    }
}
