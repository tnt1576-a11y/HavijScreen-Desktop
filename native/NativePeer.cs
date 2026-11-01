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
    readonly ConnectionRecovery recovery = new();
    bool heartbeat, recoveryReported, diagnosticPaused;
    internal bool Reconnecting { get { lock(gate) return recovery.Reconnecting; } }
    internal event Action? ConnectionChanged;
    internal event Action? Recovered;
    readonly Pipeline pipeline;
    readonly bool headless;
    readonly Action<IntPtr>? diagnosticFrame;
    readonly List<IntPtr> receiveBins = [];
    readonly List<Delegate> promises = [];
    readonly Channel<string> controls = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly CancellationTokenSource lifetime = new();
    IntPtr rtc, videoSource, audioSource, channel, videoSink, volume;
    bool disposed;
    readonly VideoDelivery delivery = new();
    bool watchingUs;
    long lastKeyframeRequest;
    bool keyframePending;
    IntPtr videoPad;
    uint videoProbeId;
    readonly Gst.PadProbe keyframeProbe;
    internal long KeyframeRequests { get; private set; }
    internal event Action? KeyframeRequested;
    readonly uint videoSsrc;
    int statsPending;
    IntPtr targetWindow;
    long frames, audioPackets, sentBytes;
    TaskCompletionSource<bool> gathering = NewCompletion<bool>();
    internal string Id { get; }
    internal string Name { get; set; }
    internal bool ChannelOpen { get; private set; }
    internal bool Established { get; private set; }
    internal bool WatchingUs
    {
        get { lock(gate) return watchingUs; }
        set { lock(gate) { if(value && !watchingUs) { delivery.Reset(); QueueKeyframeRequest(); } watchingUs=value; } }
    }
    sealed record Subscription(long Revision,bool Active);
    Subscription subscription=new(0,false);
    internal bool Subscribed
    {
        get=>Volatile.Read(ref subscription).Active;
        set
        {
            Subscription old;
            do { old=Volatile.Read(ref subscription); if(old.Active==value) return; }
            while(!ReferenceEquals(Interlocked.CompareExchange(ref subscription,new(old.Revision+1,value),old),old));
        }
    }
    internal object WatchMessage() { var value=Volatile.Read(ref subscription); return new { type="watch",active=value.Active,revision=value.Revision }; }
    internal long WatchRevision { get; set; } = -1;
    internal bool Sharing { get; set; }
    internal long SharingRevision { get; set; } = -1;
    internal bool AudioEnabled { get; private set; }
    internal int VolumePercent { get; private set; } = 100;
    internal event Action<bool,ulong,ulong>? DiagnosticPresented;
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

    internal NativePeer(string id, string name, bool initiator, bool stun, bool diagnostic = false, Action<IntPtr>? diagnosticFrame = null)
    {
        if(diagnosticFrame!=null && !diagnostic) throw new ArgumentException("Frame inspection is restricted to synthetic diagnostics.");
        this.diagnosticFrame=diagnosticFrame;
        Id = id; Name = name; headless = diagnostic;
        _ = Task.Run(async () =>
        {
            try { await foreach (string text in controls.Reader.ReadAllAsync(lifetime.Token))
            {
                lock(gate) { if(disposed || diagnosticPaused) continue; recovery.Contact(Environment.TickCount64); ReportRecovery(); }
                if (Message != null) await Message(text);
            } }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed) Failed?.Invoke(error.Message); }
        });
        videoSsrc = (uint)RandomNumberGenerator.GetInt32(1,int.MaxValue); int audioSsrc = RandomNumberGenerator.GetInt32(1,int.MaxValue);
        string sendFixture=headless?"identity name=send-fixture ! ":"";
        string impairment=headless?"identity name=network-fixture ! ":"";
        string description = "webrtcbin name=rtc bundle-policy=max-bundle latency=80 "
            + $"appsrc name=sendvideo is-live=true format=time block=false min-latency=0 max-latency=0 max-buffers=3 max-bytes=0 max-time=0 leaky-type=none caps=\"video/x-h264,stream-format=byte-stream,alignment=au,profile=constrained-baseline\" ! {sendFixture}rtph264pay pt=96 ssrc={videoSsrc} config-interval=-1 aggregate-mode=zero-latency ! {impairment}application/x-rtp,media=video,encoding-name=H264,clock-rate=90000,payload=96,packetization-mode=(string)1,ssrc=(uint){videoSsrc} ! rtc. "
            + $"appsrc name=sendaudio is-live=true format=time block=false min-latency=0 max-latency=0 max-buffers=8 leaky-type=downstream caps=\"audio/x-opus,rate=48000,channels=2,channel-mapping-family=0\" ! queue max-size-buffers=8 max-size-bytes=0 max-size-time=0 leaky=downstream ! rtpopuspay pt=111 ssrc={audioSsrc} ! application/x-rtp,media=audio,encoding-name=OPUS,clock-rate=48000,payload=111,encoding-params=(string)2,ssrc=(uint){audioSsrc} ! rtc.";
        pipeline = new Pipeline(description);
        pipeline.Failed += message => Failed?.Invoke(message);
        rtc = Gst.Named(pipeline.Handle,"rtc"); videoSource = Gst.Named(pipeline.Handle,"sendvideo"); audioSource = Gst.Named(pipeline.Handle,"sendaudio");
        // RTCP PLI/FIR reaches this appsrc, but the encoder is in a separate
        // shared capture pipeline. Forward requests without blocking native threads.
        keyframeProbe = (_,info,_) =>
        {
            IntPtr evt=Gst.gst_pad_probe_info_get_event(info);
            if(evt!=IntPtr.Zero && Gst.gst_video_event_is_force_key_unit(evt)!=0)
                lock(gate) { if(!disposed) QueueKeyframeRequest(); }
            return 1; // GST_PAD_PROBE_OK
        };
        videoPad=Gst.gst_element_get_static_pad(videoSource,"src");
        videoProbeId=Gst.gst_pad_add_probe(videoPad,1<<7,keyframeProbe,IntPtr.Zero,IntPtr.Zero);
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
                if(Established && state is 2 or 3 or 4 or 5)
                {
                    recovery.Transport(state==2 && ChannelOpen,Environment.TickCount64); ReportRecovery();
                }
                else if (state == 4) ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke("Could not establish a direct connection. These networks may need a trusted LAN/VPN or a TURN relay."));
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
        _=Task.Run(RecoveryLoop);
    }
    void BindChannel(IntPtr value, bool borrowed)
    {
        lock (gate)
        {
            if (disposed || channel != IntPtr.Zero) return;
            channel = borrowed ? Gst.g_object_ref(value) : value;
            pipeline.Connect(channel,"on-open",new Gst.VoidSignal((_,_) =>
            {
                if(disposed) return; ChannelOpen=Established=true;
                ThreadPool.QueueUserWorkItem(_=>
                {
                    lock(gate) { if(disposed) return; recovery.Transport(true,Environment.TickCount64); recovery.Contact(Environment.TickCount64); ReportRecovery(); }
                    Opened?.Invoke();
                });
            }));
            pipeline.Connect(channel,"on-close",new Gst.VoidSignal((_,_) =>
            {
                ChannelOpen=false;
                ThreadPool.QueueUserWorkItem(_=>ChannelInterrupted(false));
            }));
            pipeline.Connect(channel,"on-error",new Gst.ObjectSignal((_,_,_) =>
            {
                ThreadPool.QueueUserWorkItem(_=>ChannelInterrupted(true));
            }));
            pipeline.Connect(channel,"on-message-string",new Gst.StringSignal((_,text,_) =>
            {
                if(disposed || diagnosticPaused) return;
                string? message = LimitedText(text,160000);
                if (message != null && !controls.Writer.TryWrite(message)) ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke("A peer sent too many control messages."));
            }));
        }
    }
    void ChannelInterrupted(bool error)
    {
        lock(gate)
        {
            if(disposed) return;
            if(Established) { recovery.Transport(false,Environment.TickCount64); ReportRecovery(); return; }
        }
        if(error) Failed?.Invoke("The encrypted control channel failed."); else Closed?.Invoke();
    }
    void ReportRecovery()
    {
        bool next=recovery.Reconnecting;
        if(next==recoveryReported) return;
        recoveryReported=next;
        ThreadPool.QueueUserWorkItem(_=> { if(disposed) return; ConnectionChanged?.Invoke(); if(!next) Recovered?.Invoke(); });
    }
    internal void EnableRecovery()
    {
        lock(gate) { if(heartbeat) return; heartbeat=true; recovery.Enable(Environment.TickCount64); }
    }
    async Task RecoveryLoop()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                bool ping, expired;
                lock(gate)
                {
                    if(disposed) return;
                    if(!Established) continue;
                    expired=recovery.Tick(Environment.TickCount64); ReportRecovery(); ping=heartbeat && ChannelOpen;
                }
                if(expired) { Failed?.Invoke("The connection did not return within 30 seconds. Please rejoin the room."); return; }
                if(ping) Send(new { type="ping" });
            }
        }
        catch(OperationCanceledException) { }
    }
    internal void DiagnosticPauseConnection(bool pause)
    {
        if(!headless) throw new InvalidOperationException("Connection interruption is restricted to diagnostic peers.");
        lock(gate) diagnosticPaused=pause;
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
            if (disposed || diagnosticPaused || !ChannelOpen || channel == IntPtr.Zero) return false;
            int success = Gst.gst_webrtc_data_channel_send_string_full(channel,text,out var error);
            if (error != IntPtr.Zero) Gst.g_error_free(error);
            return success != 0;
        }
    }
    void QueueKeyframeRequest()
    {
        if(keyframePending || disposed) return;
        keyframePending=true;
        int delay=(int)Math.Max(0,500-(Environment.TickCount64-lastKeyframeRequest));
        _=Task.Run(async()=>
        {
            try
            {
                if(delay>0) await Task.Delay(delay,lifetime.Token);
                lock(gate)
                {
                    keyframePending=false;
                    if(disposed || !watchingUs) return;
                    lastKeyframeRequest=Environment.TickCount64; KeyframeRequests++;
                }
                KeyframeRequested?.Invoke();
            }
            catch(OperationCanceledException) { }
        });
    }
    internal void Push(bool isAudio, IntPtr encoded, ulong absoluteTime)
    {
        lock (gate)
        {
            if (disposed || diagnosticPaused || !ChannelOpen || !WatchingUs) return;
            if(!isAudio && !delivery.Accept((Gst.gst_buffer_get_flags(encoded)&(1u<<13))==0,Gst.gst_app_src_get_current_level_buffers(videoSource)))
            {
                QueueKeyframeRequest(); return;
            }
            IntPtr copy = Gst.gst_buffer_copy(encoded); // Metadata only; encoded payload memory is shared.
            ulong baseTime = Gst.gst_element_get_base_time(pipeline.Handle);
            ulong pts = absoluteTime > baseTime ? absoluteTime-baseTime : 0;
            Gst.SetTime(copy,Gst.PtsOffset,pts); Gst.SetTime(copy,Gst.DtsOffset,Gst.None);
            Interlocked.Add(ref sentBytes,(long)Gst.gst_buffer_get_size(copy).ToUInt64());
            Gst.gst_app_src_push_buffer(isAudio ? audioSource : videoSource,copy);
        }
    }
    internal void DiagnosticPacketLoss(double fraction)
    {
        if(!headless||fraction<0||fraction>.5) throw new InvalidOperationException("Packet loss injection is restricted to diagnostic peers.");
        lock(gate) { ObjectDisposedException.ThrowIf(disposed,this); IntPtr fixture=Gst.Named(pipeline.Handle,"network-fixture"); try { Gst.gst_util_set_object_arg(fixture,"drop-probability",fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)); } finally { Gst.gst_object_unref(fixture); } }
    }
    internal void DiagnosticSenderDelay(uint microseconds)
    {
        if(!headless) throw new InvalidOperationException("Delay is restricted to diagnostic peers.");
        lock(gate) { ObjectDisposedException.ThrowIf(disposed,this); IntPtr fixture=Gst.Named(pipeline.Handle,"send-fixture"); try { Gst.gst_util_set_object_arg(fixture,"sleep-time",microseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); } finally { Gst.gst_object_unref(fixture); } }
    }
    internal async Task<NetworkFeedback?> Feedback(CancellationToken cancellation)
    {
        if(Interlocked.Exchange(ref statsPending,1)!=0) return null;
        var result=NewCompletion<NetworkFeedback?>();
        try
        {
            Gst.PromiseChanged callback=(promise,_)=>
            {
                try { result.TrySetResult(ReadFeedback(Gst.gst_promise_get_reply(promise),videoSsrc)); }
                catch { result.TrySetResult(null); }
                finally { Gst.gst_promise_unref(promise); Interlocked.Exchange(ref statsPending,0); }
            };
            lock(gate)
            {
                if(disposed||!ChannelOpen||!WatchingUs) { Interlocked.Exchange(ref statsPending,0); return null; }
                Gst.CreateDescription(rtc,"get-stats",IntPtr.Zero,Gst.Promise(callback));
            }
            return await result.Task.WaitAsync(TimeSpan.FromSeconds(2),cancellation);
        }
        catch(OperationCanceledException) { return null; }
        catch(TimeoutException) { return null; } // Keep one native request in flight until its callback completes.
    }
    static NetworkFeedback? ReadFeedback(IntPtr reply,uint videoSsrc)
    {
        if(reply==IntPtr.Zero) return null;
        for(int i=0;i<Gst.gst_structure_n_fields(reply);i++)
        {
            string name=Gst.Text(Gst.gst_structure_nth_field_name(reply,i));
            if(!name.StartsWith("rtp-remote-inbound-stream-stats_",StringComparison.Ordinal)) continue;
            IntPtr entry=Gst.gst_value_get_structure(Gst.gst_structure_get_value(reply,name));
            if(entry==IntPtr.Zero||Gst.gst_structure_get_uint(entry,"ssrc",out uint ssrc)==0||ssrc!=videoSsrc) continue;
            if(Gst.gst_structure_get_double(entry,"fraction-lost",out double loss)==0) continue;
            Gst.gst_structure_get_double(entry,"round-trip-time",out double rtt);
            IntPtr rawValue=Gst.gst_structure_get_value(entry,"gst-rtpsource-stats"); if(rawValue==IntPtr.Zero) continue;
            IntPtr raw=Gst.gst_value_get_structure(rawValue);
            if(raw!=IntPtr.Zero&&Gst.gst_structure_get_uint(raw,"rb-exthighestseq",out uint sequence)!=0) return new(sequence,loss,rtt);
        }
        return null;
    }
    internal void SetVolume(int percent)
    {
        lock(gate)
        {
            VolumePercent=Math.Clamp(percent,0,100);
            if(!disposed && volume!=IntPtr.Zero) Gst.gst_util_set_object_arg(volume,"volume",(VolumePercent/100.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                ? "queue max-size-buffers=0 max-size-bytes=2097152 max-size-time=200000000 ! rtph264depay request-keyframe=true wait-for-keyframe=true ! video/x-h264,alignment=au ! h264parse ! d3d11h264dec ! queue max-size-buffers=2 max-size-bytes=0 max-size-time=0 leaky=downstream ! identity name=frame-counter signal-handoffs=true silent=true ! " + (diagnosticFrame!=null ? "d3d11download ! video/x-raw,format=NV12 ! appsink name=receiver emit-signals=true sync=false max-buffers=2 drop=false" : headless ? "fakesink name=receiver sync=true async=false signal-handoffs=true" : "d3d11videosink name=receiver sync=true max-lateness=50000000 qos=true async=false enable-last-sample=false")
                : "queue max-size-time=200000000 max-size-buffers=0 max-size-bytes=0 ! rtpopusdepay ! opusdec ! audioconvert ! audioresample ! identity name=frame-counter signal-handoffs=true silent=true ! " + (headless ? "fakesink name=receiver sync=true async=false signal-handoffs=true" : "volume name=audio-volume mute=true ! wasapi2sink low-latency=true sync=true");
            IntPtr bin = Gst.gst_parse_bin_from_description(chain,1,out var error);
            if (error != IntPtr.Zero || bin == IntPtr.Zero) throw new InvalidOperationException(Gst.Error(error));
            Gst.gst_bin_add(pipeline.Handle,bin); receiveBins.Add(bin);
            IntPtr counter = Gst.Named(bin,"frame-counter");
            pipeline.Connect(counter,"handoff",new Gst.ObjectSignal((_,_,_) => { if (isVideo) Interlocked.Increment(ref frames); else Interlocked.Increment(ref audioPackets); }));
            Gst.gst_object_unref(counter);
            if(isVideo && diagnosticFrame!=null)
            {
                IntPtr sink=Gst.Named(bin,"receiver");
                pipeline.Connect(sink,"new-sample",new Gst.NewSample((item,_)=>
                {
                    IntPtr sample=Gst.gst_app_sink_try_pull_sample(item,0);
                    if(sample!=IntPtr.Zero) try { diagnosticFrame(Gst.gst_sample_get_buffer(sample)); } finally { Gst.gst_mini_object_unref(sample); }
                    return 0;
                }));
                Gst.gst_object_unref(sink);
            }
            if(headless && diagnosticFrame==null)
            {
                IntPtr sink=Gst.Named(bin,"receiver");
                pipeline.Connect(sink,"handoff",new Handoff((_,buffer,_,_)=>DiagnosticPresented?.Invoke(!isVideo,Gst.ReadTime(buffer,Gst.PtsOffset),Gst.gst_clock_get_time(Gst.SharedClock)-Gst.gst_element_get_base_time(pipeline.Handle))));
                Gst.gst_object_unref(sink);
            }
            if (!headless && isVideo)
            {
                videoSink = Gst.Named(bin,"receiver");
                if (targetWindow != IntPtr.Zero) Gst.gst_video_overlay_set_window_handle(videoSink,unchecked((UIntPtr)(ulong)targetWindow.ToInt64()));
                ThreadPool.QueueUserWorkItem(_ => VideoArrived?.Invoke());
            }
            else if (!headless) { volume = Gst.Named(bin,"audio-volume"); Gst.SetInt(volume,"mute",AudioEnabled && Subscribed ? 0 : 1,IntPtr.Zero); SetVolume(VolumePercent); }
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
        if(videoPad!=IntPtr.Zero) Gst.gst_pad_remove_probe(videoPad,videoProbeId);
        pipeline.Dispose();
        if(videoPad!=IntPtr.Zero) { Gst.gst_object_unref(videoPad); videoPad=IntPtr.Zero; }
        GC.KeepAlive(keyframeProbe);
        foreach (IntPtr pointer in new[] { channel,videoSink,volume,videoSource,audioSource,rtc }) if (pointer != IntPtr.Zero) Gst.g_object_unref(pointer);
        channel = videoSink = volume = videoSource = audioSource = rtc = IntPtr.Zero;
        GC.KeepAlive(promises);
    }
}
