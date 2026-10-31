using System.Collections.Concurrent;
using System.Text.Json;

namespace HavijScreen.Native;

internal sealed class NativeRoom : IDisposable
{
    readonly object membership = new();
    readonly SemaphoreSlim mediaGate = new(1,1);
    readonly ConcurrentDictionary<string,NativePeer> peers = new();
    NativePeer[] outgoing = [];
    readonly CancellationTokenSource lifetime = new();
    MediaCapture? capture;
    CaptureSource? source;
    Quality quality = Quality.All[0];
    bool audio, synthetic, disposed;
    readonly bool diagnostic;
    readonly ConcurrentBag<Task> shutdown = [];
    internal Task Completion { get; private set; } = Task.CompletedTask;
    int captureGeneration;
    NetworkRate rates=new(Quality.All[0].Kbps);
    readonly PerformanceRate performance=new();
    internal int TargetFps=>runningGeneration==captureGeneration?capture?.TargetFps??quality.Fps:quality.Fps;
    internal int BitrateKbps=>capture?.BitrateKbps??0;
    internal int FeedbackSamples { get; private set; }
    string name = "Player";
    internal string Id { get; private set; } = SetupCrypto.Id();
    internal string RoomId { get; private set; } = SetupCrypto.Id();
    internal string HostId { get; private set; }
    internal bool IsHost => Id == HostId;
    internal bool Stun { get; set; } = true;
    internal bool Sharing { get; private set; }
    internal string Name { get => name; set { if (!SetupCrypto.ValidName(value)) throw new InvalidOperationException("Choose a name with 1–32 characters."); name = value.Trim(); } }
    internal IReadOnlyList<NativePeer> Peers => peers.Values.ToArray();
    internal IReadOnlyList<NativePeer> Connected => peers.Values.Where(p => p.ChannelOpen).ToArray();
    internal int Viewers => peers.Values.Count(p => p.ChannelOpen && p.WatchingUs);
    internal string Encoder => capture?.Encoder ?? "";
    internal string? CaptureError { get; private set; }
    internal long EncodedFrames => capture?.Frames ?? 0;
    internal int EncoderStarts { get; private set; }
    internal event Action? Changed;
    internal event Action<string>? Notice;
    internal event Action<string>? Ended;
    internal event Action<string>? Unwatch;
    internal NativeRoom(bool diagnostic = false) { this.diagnostic = diagnostic; HostId = Id; _=Task.Run(RateLoop); }
    internal void ConfigureGuest(string id, string host, string room, bool stun)
    {
        if (peers.Count != 0 || !SetupCrypto.ValidId(id) || !SetupCrypto.ValidId(host) || !SetupCrypto.ValidId(room) || id == host) throw new InvalidOperationException("Invalid room connection.");
        Id = id; HostId = host; RoomId = room; Stun = stun;
    }
    internal NativePeer AddPeer(string id, string name, bool initiator, bool diagnostic = false)
    {
        NativePeer peer;
        lock (membership)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (peers.Count >= 3 || peers.ContainsKey(id) || !SetupCrypto.ValidId(id) || !SetupCrypto.ValidName(name)) throw new InvalidOperationException("The room is full or this person is already joining.");
            peer = new NativePeer(id,name,initiator,Stun,diagnostic || this.diagnostic);
            peers[id] = peer;
        }
        peer.Opened += () =>
        {
            if (disposed || !peers.ContainsKey(peer.Id)) return;
            peer.Send(new { type = "hello", name = Name, sharing = Sharing, info = Info() });
            Changed?.Invoke(); if (IsHost) UpdateMesh();
        };
        peer.Closed += () => Drop(peer.Id,true);
        peer.Failed += error =>
        {
            if (disposed || !peers.ContainsKey(peer.Id)) return;
            bool hostPending = peer.Id == HostId && !peer.Established;
            Drop(peer.Id,peer.Established);
            if (hostPending) End(error); else Notice?.Invoke(error);
        };
        peer.Message += text => Handle(peer,text);
        peer.KeyframeRequested += () => { _=RequestKeyframe(peer); };
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60000,lifetime.Token);
                if (!disposed && peers.TryGetValue(peer.Id,out var pending) && !pending.Established)
                {
                    Drop(peer.Id);
                    string error = "Could not establish a direct connection. These networks may need a trusted LAN/VPN or a TURN relay.";
                    if (peer.Id == HostId) End(error); else Notice?.Invoke(error);
                }
            }
            catch (OperationCanceledException) { }
        });
        Changed?.Invoke(); return peer;
    }
    int keyframePending;
    long lastKeyframeRequest;
    async Task RequestKeyframe(NativePeer peer)
    {
        if(disposed || !peer.WatchingUs || !peers.TryGetValue(peer.Id,out var current) || !ReferenceEquals(current,peer)) return;
        if(Interlocked.Exchange(ref keyframePending,1)!=0) return;
        try
        {
            // All viewers share one encoder. Coalesce their requests, including
            // a trailing request during the cooldown, rather than discarding it.
            int delay=(int)Math.Max(0,500-(Environment.TickCount64-Interlocked.Read(ref lastKeyframeRequest)));
            if(delay>0) await Task.Delay(delay,lifetime.Token);
            await mediaGate.WaitAsync(lifetime.Token);
            try
            {
                if(!disposed && Sharing && Viewers>0 && capture?.RequestKeyframe()==true)
                    Interlocked.Exchange(ref lastKeyframeRequest,Environment.TickCount64);
            }
            finally { mediaGate.Release(); }
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { if(!disposed) Notice?.Invoke("Video recovery: "+error.Message); }
        finally { Interlocked.Exchange(ref keyframePending,0); }
    }
    object Info() => new { width = quality.Width, height = quality.Height, fps = TargetFps, audio, appAudio = source?.Monitor == false && audio };
    async Task Handle(NativePeer peer, string text)
    {
        if (disposed || !peers.ContainsKey(peer.Id)) return;
        using var document = JsonDocument.Parse(text,new JsonDocumentOptions { MaxDepth = 24 }); var message = document.RootElement;
        switch (message.Text("type"))
        {
            case "hello":
                string incomingName = message.Text("name",32); if (!SetupCrypto.ValidName(incomingName)) return;
                peer.Name = incomingName; peer.Sharing = message.Bool("sharing"); Changed?.Invoke(); break;
            case "sharing":
                peer.Sharing = message.Bool("active"); if (!peer.Sharing) { peer.SetView(IntPtr.Zero,false); peer.Subscribed = false; Unwatch?.Invoke(peer.Id); } Changed?.Invoke(); break;
            case "watch":
                peer.WatchingUs = message.Bool("active") && Sharing; RefreshOutgoing(); await ReconcileMedia(); Changed?.Invoke(); break;
            case "mesh-plan" when !IsHost && peer.Id == HostId:
                var people = message.Item("people"); if (people.ValueKind != JsonValueKind.Array || people.GetArrayLength() > 3) return;
                foreach (var person in people.EnumerateArray())
                {
                    string id = person.Text("id",22), personName = person.Text("name",32);
                    if (!SetupCrypto.ValidId(id) || !SetupCrypto.ValidName(personName) || id == Id || peers.ContainsKey(id) || peers.Count >= 3) continue;
                    if (StringComparer.Ordinal.Compare(Id,id) < 0) await StartMesh(id,personName);
                }
                break;
            case "relay" when IsHost:
                string target = message.Text("to",22); var body = message.Item("body");
                if (peers.TryGetValue(target,out var destination) && destination.ChannelOpen && target != peer.Id && body.ValueKind == JsonValueKind.Object)
                    destination.Send(new { type = "signal", from = peer.Id, name = peer.Name, body });
                break;
            case "signal" when !IsHost && peer.Id == HostId:
                string from = message.Text("from",22), fromName = message.Text("name",32);
                if (SetupCrypto.ValidId(from) && SetupCrypto.ValidName(fromName) && from != Id && from != HostId) await ReceiveMesh(from,fromName,message.Item("body"));
                break;
            case "removed" when !IsHost && peer.Id == HostId:
                string removed = message.Text("id",22);
                if (removed == Id) End("The host removed you from the room."); else Drop(removed);
                break;
            case "room-ended" when !IsHost && peer.Id == HostId: End("The host ended the room."); break;
        }
    }
    void UpdateMesh()
    {
        if (!IsHost || disposed) return;
        var people = Connected.Select(p => new { id = p.Id, name = p.Name }).ToArray(); Broadcast(new { type = "mesh-plan", people });
    }
    void Relay(string id, object body) { if (peers.TryGetValue(HostId,out var host)) host.Send(new { type = "relay", to = id, body }); }
    async Task StartMesh(string id, string name)
    {
        var peer = AddPeer(id,name,true);
        try { var sdp = await peer.CreateDescription(true,lifetime.Token); Relay(id,new { type = "offer", sdp }); }
        catch { Drop(id); throw; }
    }
    async Task ReceiveMesh(string id, string name, JsonElement body)
    {
        SessionDescription description = ParseDescription(body.Item("sdp"));
        if (body.Text("type") == "offer")
        {
            if (peers.ContainsKey(id) || peers.Count >= 3 || StringComparer.Ordinal.Compare(id,Id) > 0) return;
            var peer = AddPeer(id,name,false);
            try { await peer.ApplyDescription(description,false,lifetime.Token); var answer = await peer.CreateDescription(false,lifetime.Token); Relay(id,new { type = "answer", sdp = answer }); }
            catch { Drop(id); throw; }
        }
        else if (body.Text("type") == "answer" && peers.TryGetValue(id,out var pending)) await pending.ApplyDescription(description,false,lifetime.Token);
    }
    internal static SessionDescription ParseDescription(JsonElement value)
    {
        string type = value.Text("type"), sdp = value.Text("sdp",100000);
        if (type is not ("offer" or "answer") || !sdp.StartsWith("v=0")) throw new InvalidOperationException("Invalid peer connection description.");
        return new(type,sdp);
    }
    internal void Broadcast(object message) { foreach (var peer in Connected) peer.Send(message); }
    internal async Task Share(CaptureSource captureSource, Quality captureQuality, bool shareAudio, bool diagnostic = false)
    {
        if (!diagnostic) Sources.Validate(captureSource);
        CaptureError=null; source = captureSource; quality = captureQuality; audio = shareAudio; synthetic = diagnostic; Sharing = true; Interlocked.Increment(ref captureGeneration);
        Broadcast(new { type = "sharing", active = true, info = Info() }); await ReconcileMedia(); Changed?.Invoke();
    }
    internal async Task StopSharing()
    {
        Sharing = false; foreach (var peer in peers.Values) peer.WatchingUs = false; RefreshOutgoing(); Interlocked.Increment(ref captureGeneration);
        Broadcast(new { type = "sharing", active = false, info = (object?)null }); await ReconcileMedia(); Changed?.Invoke();
    }
    int runningGeneration = -1;
    IntPtr previewWindow, runningPreview;
    bool runningEncode;
    internal long PreviewFrames => capture?.PreviewFrames??0;
    internal async Task DiagnosticCaptureDelay(uint microseconds)
    {
        await mediaGate.WaitAsync();
        try { if(!diagnostic||!synthetic||capture==null) throw new InvalidOperationException("Synthetic diagnostic capture required."); capture.DiagnosticDelay(microseconds); }
        finally { mediaGate.Release(); }
    }
    internal async Task Preview(IntPtr window) { if(previewWindow==window) return; previewWindow=window; await ReconcileMedia(); Changed?.Invoke(); }
    async Task ReconcileMedia()
    {
        await mediaGate.WaitAsync();
        try
        {
            bool encode=Viewers>0;
            IntPtr preview=previewWindow;
            bool needed = !disposed && Sharing && source != null && (encode||preview!=IntPtr.Zero);
            int generation = captureGeneration;
            if (capture != null && (!needed || generation != runningGeneration || runningEncode!=encode))
            {
                var previous = capture; capture = null; await Task.Run(previous.Dispose);
            }
            if (needed && capture == null)
            {
                var next = new MediaCapture();
                next.Packet += (isAudio,buffer,time) => { foreach (var peer in Volatile.Read(ref outgoing)) peer.Push(isAudio,buffer,time); };
                next.Failed += error => { _ = CaptureFailed(next,error); };
                next.PreviewFailed += error => { _ = PreviewFailed(next,error); };
                try { await Task.Run(() => next.Start(source!,quality,audio,synthetic,preview,encode)); capture = next; runningGeneration = generation; runningPreview=preview; runningEncode=encode; rates=new(quality.Kbps); performance.Reset(Environment.TickCount64,next.Frames); if(encode) EncoderStarts++; }
                catch(Exception error) { next.Dispose(); ClearFailedShare(error.Message); }
            }
            else if(capture!=null&&runningPreview!=preview)
            {
                try { await Task.Run(()=>capture.SetPreview(preview)); runningPreview=preview; }
                catch(Exception error) { previewWindow=runningPreview=IntPtr.Zero; Notice?.Invoke("Local preview: "+error.Message); }
            }
        }
        finally { mediaGate.Release(); }
    }
    async Task RateLoop()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while(await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var active=Volatile.Read(ref outgoing); if(active.Length==0||!Sharing) continue;
                await mediaGate.WaitAsync(lifetime.Token);
                try
                {
                    if(!disposed&&Sharing&&capture!=null&&runningEncode&&runningGeneration==captureGeneration&&quality.PrioritizeGame)
                        capture.SetFrameRate(performance.Observe(Environment.TickCount64,capture.Frames));
                }
                finally { mediaGate.Release(); }
                int generation=captureGeneration;
                var feedback=await Task.WhenAll(active.Select(async p=>(Peer:p,Value:await p.Feedback(lifetime.Token))));
                await mediaGate.WaitAsync(lifetime.Token);
                try
                {
                    if(disposed||!Sharing||capture==null||!runningEncode||generation!=captureGeneration) continue;
                    var current=Volatile.Read(ref outgoing); rates.Retain(current.Select(p=>p.Id));
                    foreach(var item in feedback) if(item.Value!=null&&current.Contains(item.Peer)) { rates.Observe(item.Peer.Id,item.Value,Environment.TickCount64); FeedbackSamples++; }
                    capture.SetBitrate(rates.Target);
                }
                finally { mediaGate.Release(); }
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { if(!disposed) Notice?.Invoke("Automatic bitrate adjustment stopped: "+error.Message); }
    }
    void ClearFailedShare(string error)
    {
        Sharing=false; CaptureError=error+" Choose the source again, or share video without audio.";
        foreach(var peer in peers.Values) peer.WatchingUs=false;
        RefreshOutgoing(); Broadcast(new { type="sharing",active=false }); Changed?.Invoke(); Notice?.Invoke(CaptureError);
    }
    async Task CaptureFailed(MediaCapture expected,string error)
    {
        await mediaGate.WaitAsync();
        try { if(disposed||capture!=expected) return; capture=null; await Task.Run(expected.Dispose); ClearFailedShare(error); }
        finally { mediaGate.Release(); }
    }
    async Task PreviewFailed(MediaCapture expected,string error)
    {
        await mediaGate.WaitAsync();
        try { if(disposed||capture!=expected) return; await Task.Run(()=>expected.SetPreview(IntPtr.Zero)); previewWindow=runningPreview=IntPtr.Zero; Notice?.Invoke("Local preview: "+error); }
        finally { mediaGate.Release(); }
    }
    internal void Watch(string id, IntPtr window, bool enabled, bool sound = false)
    {
        if (!peers.TryGetValue(id,out var peer) || !peer.ChannelOpen || (enabled && !peer.Sharing)) return;
        peer.SetView(enabled ? window : IntPtr.Zero,sound); peer.Subscribed = enabled;
        peer.Send(new { type = "watch", active = enabled }); if (!enabled) Unwatch?.Invoke(id); Changed?.Invoke();
    }
    internal void Remove(string id) { if (!IsHost) return; Broadcast(new { type = "removed", id }); Drop(id); }
    void RefreshOutgoing() => Volatile.Write(ref outgoing,peers.Values.Where(p => p.WatchingUs && p.ChannelOpen).ToArray());
    internal void Drop(string id, bool notify = false)
    {
        if (!peers.TryRemove(id,out var peer)) return;
        RefreshOutgoing();
        bool established = peer.Established;
        shutdown.Add(Task.Run(peer.Dispose)); Unwatch?.Invoke(id); if (!disposed) _ = ReconcileMedia(); Changed?.Invoke();
        if (notify && IsHost && established) Broadcast(new { type = "removed", id });
        if (notify && id == HostId && !disposed) End(established ? "The host disconnected. The room has closed." : "Could not finish joining the host.");
    }
    void End(string message) { if (disposed) return; Dispose(); Ended?.Invoke(message); }
    public void Dispose()
    {
        if (disposed) return;
        if (IsHost) Broadcast(new { type = "room-ended" });
        disposed = true; Sharing = false; lifetime.Cancel();
        foreach (var id in peers.Keys) Drop(id);
        Completion = Task.WhenAll(shutdown.Append(ReconcileMedia()));
    }
}
