using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
    internal static string? ProgressPath;
    static void Step(string text) { if (ProgressPath != null) File.AppendAllText(ProgressPath,DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine); }
    internal static object Run(string[] args)
    {
        if (args.Contains("--tone"))
        {
            int hz = int.TryParse(Program.Option(args,"frequency"),out int value) ? value : 440;
            using var tone = new Pipeline($"audiotestsrc is-live=true wave=sine freq={hz} volume=0.01 ! audioconvert ! audioresample ! wasapi2sink low-latency=true");
            tone.Play(); Thread.Sleep(14000); return new { tone = true };
        }
        if (args.Contains("--probe")) return new
        {
            native = true, browserRuntime = false,
            encoders = new[] { "nvd3d11h264enc","amfh264enc","qsvh264enc" }.Where(Gst.Has).ToArray(),
            wgc = Gst.Has("d3d11screencapturesrc"), processAudio = Gst.Has("wasapi2src"), webRtc = Gst.Has("webrtcbin"),
            hardwareDecoder = Gst.Has("d3d11h264dec"), opus = Gst.Has("opusenc") && Gst.Has("opusdec"),
            bufferTimestampOffset = Gst.PtsOffset
        };
        if (args.Contains("--benchmark")) return Benchmark();
        if (args.Contains("--performance-test")) return Performance().GetAwaiter().GetResult();
        if (args.Contains("--performance-window-test")) return PerformanceWindow().GetAwaiter().GetResult();
        if (args.Contains("--rtc-test")) return DirectMedia().GetAwaiter().GetResult();
        if (args.Contains("--room-test")) return RoomRoundTrip().GetAwaiter().GetResult();
        if (args.Contains("--capture-test")) return WindowCapture().GetAwaiter().GetResult();
        return new { setupSecurity = SetupSecurity(), benchmark = Benchmark(), applicationAudio = AudioIsolation(), directMedia = DirectMedia().GetAwaiter().GetResult() };
    }
    static object SetupSecurity()
    {
        var keys = SetupCrypto.Keys(SetupCrypto.NewCode());
        string src = SetupCrypto.GuestRoute(), dst = keys.Route, id = SetupCrypto.Id();
        var envelope = JsonSerializer.SerializeToElement(SetupCrypto.Seal(keys.Key,src,dst,new { type = "request", request = id }));
        if (SetupCrypto.Open(keys.Key,src,dst,envelope).Text("request") != id) throw new InvalidOperationException("Setup encryption round trip failed.");
        int rejected = 0;
        foreach (var attempt in new Action[] {
            () => SetupCrypto.Open(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),src,dst,envelope),
            () => SetupCrypto.Open(keys.Key,SetupCrypto.GuestRoute(),dst,envelope),
            () => SetupCrypto.Open(keys.Key,src,dst,JsonSerializer.SerializeToElement(new { hs=1, iv=envelope.Text("iv"), data="invalid" })),
            () => SetupCrypto.Keys("bad code") })
        { try { attempt(); } catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or InvalidOperationException or FormatException or ArgumentException) { rejected++; } }
        using var a = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var b = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        byte[] first = SetupCrypto.PairKey(a,SetupCrypto.PublicKey(b),id), second = SetupCrypto.PairKey(b,SetupCrypto.PublicKey(a),id);
        if (rejected != 4 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(first,second)) throw new InvalidOperationException("Setup authentication checks failed.");
        string audio = MediaCapture.AudioSource(new("Fixture",new IntPtr(1),1234,false),false);
        if (!audio.Contains("include-process-tree") || !audio.Contains("loopback-target-pid=1234")) throw new InvalidOperationException("Application audio scope was lost.");
        return new { authenticatedEncryption = true, invalidInputsRejected = rejected, ecdhAgreement = true, explicitProcessAudioScope = true };
    }
    static async Task<object> RoomRoundTrip()
    {
        using var hostRoom = new NativeRoom(true) { Stun = false };
        using var guestRoom = new NativeRoom(true) { Stun = false };
        using var host = new NativeLobby(hostRoom); using var guest = new NativeLobby(guestRoom);
        using var thirdRoom = new NativeRoom(true) { Stun = false }; using var third = new NativeLobby(thirdRoom);
        using var fourthRoom = new NativeRoom(true) { Stun = false }; using var fourth = new NativeLobby(fourthRoom);
        using var extraRoom = new NativeRoom(true); using var extra = new NativeLobby(extraRoom);
        var failures = new List<string>(); host.Failure += message => failures.Add(message); guest.Failure += message => failures.Add(message);
        hostRoom.Notice += message => failures.Add(message); guestRoom.Notice += message => failures.Add(message);
        try
        {
            Step("host opening"); string code = await host.Host("Synthetic host",false);
            using (var declinedRoom = new NativeRoom(true))
            using (var declined = new NativeLobby(declinedRoom))
            {
                await declined.Join(code,"Synthetic declined"); await Until(() => host.Requests.Count == 1,15000,"Decline test request did not arrive.");
                await host.Reject(host.Requests[0].Route); await Until(() => declined.State == "failed",5000,"Decline did not reach the guest.");
                if (declinedRoom.Peers.Count != 0) throw new InvalidOperationException("Declined guest opened a peer connection.");
            }
            using (var canceledRoom = new NativeRoom(true))
            using (var canceled = new NativeLobby(canceledRoom))
            {
                await canceled.Join(code,"Synthetic canceled"); await Until(() => host.Requests.Count == 1,15000,"Cancel test request did not arrive.");
                await canceled.Stop(); await Until(() => host.Requests.Count == 0,5000,"Cancel did not clear the request.");
            }
            Step("decline and cancel passed"); Step("guest requesting"); await guest.Join(code,"Synthetic guest");
            await Until(() => host.Requests.Count == 1 || failures.Count > 0,20000,"The native public-service join request did not arrive.");
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ",failures));
            Step("request pending"); await Task.Delay(6500);
            bool noPeersBeforeApproval = hostRoom.Peers.Count == 0 && guestRoom.Peers.Count == 0;
            Step("approving"); await host.Approve(host.Requests[0].Route); Step("approved");
            await Until(() => hostRoom.Connected.Count == 1 && guestRoom.Connected.Count == 1 || failures.Count > 0,25000,"The approved native room did not connect.");
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ",failures));
            Step("connected"); await hostRoom.Share(new("Synthetic frames",IntPtr.Zero,0,false),Quality.All[2],true,true);
            await Until(() => guestRoom.Connected[0].Sharing,3000,"The screen announcement did not arrive.");
            Step("watching"); guestRoom.Watch(hostRoom.Id,IntPtr.Zero,true);
            await Until(() => guestRoom.Connected[0].VideoFrames >= 60 && guestRoom.Connected[0].AudioPackets >= 30,12000,"The native room's shared media did not decode.");
            Step("media received"); long frames = guestRoom.Connected[0].VideoFrames;
            foreach (var item in new[] { (third,thirdRoom,"Synthetic third"),(fourth,fourthRoom,"Synthetic fourth") })
            {
                Step("additional guest requesting"); await item.Item1.Join(code,item.Item3);
                await Until(() => host.Requests.Count == 1,15000,"Additional join request did not arrive.");
                await host.Approve(host.Requests[0].Route);
                await Until(() => item.Item2.Connected.Any(p => p.Id == hostRoom.Id),20000,"Additional guest did not connect.");
                await Until(() => item.Item2.Connected.Single(p => p.Id == hostRoom.Id).Sharing,3000,"The late viewer did not receive the share state.");
                item.Item2.Watch(hostRoom.Id,IntPtr.Zero,true);
            }
            await Until(() => new[] {hostRoom,guestRoom,thirdRoom,fourthRoom}.All(r => r.Connected.Count == 3),35000,"Four-person native mesh did not connect.");
            await Until(() => new[] {guestRoom,thirdRoom,fourthRoom}.All(r => r.Connected.Single(p => p.Id == hostRoom.Id).VideoFrames >= 60),12000,"Multiple viewers did not decode the shared encode.");
            if (hostRoom.EncoderStarts != 1 || hostRoom.Viewers != 3) throw new InvalidOperationException("Multiple viewers started duplicate encoders.");
            Step("three viewers using one encode");
            await extra.Join(code,"Synthetic extra"); await Until(() => extra.State == "failed",10000,"The full room did not decline another participant.");
            await guestRoom.Share(new("Synthetic return",IntPtr.Zero,0,false),Quality.All[0],false,true);
            await Until(() => hostRoom.Connected.Single(p => p.Id == guestRoom.Id).Sharing,3000,"Return sharing did not arrive.");
            hostRoom.Watch(guestRoom.Id,IntPtr.Zero,true);
            await Until(() => hostRoom.Connected.Single(p => p.Id == guestRoom.Id).VideoFrames >= 30,8000,"Return video did not decode.");
            Step("two-way native sharing passed"); await guestRoom.StopSharing();
            await host.Stop();
            await Task.Delay(500);
            bool mediaIndependent = hostRoom.Connected.Count == 3 && guestRoom.Connected.Count == 3 && guestRoom.Connected.Single(p => p.Id == hostRoom.Id).VideoFrames > frames;
            if (!noPeersBeforeApproval || !mediaIndependent) throw new InvalidOperationException("Native room admission or service-independent streaming did not pass.");
            Step("service stopped"); await hostRoom.StopSharing(); Step("sharing stopped");
            await Until(() => new[] {guestRoom,thirdRoom,fourthRoom}.All(r => !r.Connected.Single(p => p.Id == hostRoom.Id).Sharing),3000,"Stop sharing was not delivered.");
            return new { publicSetup = true, noPeersBeforeApproval, encryptedNativeRoom = true, decodedVideo = frames, mediaIndependentOfSetupService = mediaIndependent, encodersForThreeViewers = hostRoom.EncoderStarts, fourPersonMesh = true, fullRoomDeclined = true, twoWaySharing = true, failures };
        }
        finally { Step("disposing"); var rooms = new[] {hostRoom,guestRoom,thirdRoom,fourthRoom,extraRoom}; foreach (var room in rooms) room.Dispose(); await Task.WhenAll(rooms.Select(r => r.Completion)).WaitAsync(TimeSpan.FromSeconds(8)); Step("disposed"); }
    }
    static async Task<object> WindowCapture()
    {
        string fixtureReport = Path.Combine(Path.GetTempPath(),"havijscreen-fixture-"+Guid.NewGuid().ToString("N")+".json");
        using var fixture = StartHelper("--fixture","--report="+fixtureReport);
        try
        {
            await Until(() => File.Exists(fixtureReport),5000,"The owned capture fixture did not open.");
            using var json = JsonDocument.Parse(File.ReadAllText(fixtureReport));
            var source = new CaptureSource("Owned capture fixture",new IntPtr(json.RootElement.GetProperty("hwnd").GetInt64()),(uint)fixture.Id,false);
            Sources.Validate(source);
            using var capture = new MediaCapture(); string? failure = null;
            capture.Failed += message => failure = message;
            capture.Start(source,Quality.All[2],false);
            await Until(() => capture.Frames >= 180 || failure != null,8000,"Windows Graphics Capture did not produce frames.");
            capture.Dispose(); if (failure != null) throw new InvalidOperationException(failure);
            return new { windowsGraphicsCapture = true, source = "owned synthetic window only", frames = capture.Frames, steadyEncodedFps = capture.SteadyFps, hardwareEncoder = capture.Encoder, rawFrameCpuCopy = false };
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(2000); } if (File.Exists(fixtureReport)) File.Delete(fixtureReport); }
    }
    static async Task<object> DirectMedia()
    {
        using var sender = new NativePeer("sender","Sender",true,false,true);
        using var receiver = new NativePeer("receiver","Receiver",false,false,true);
        var errors = new List<string>(); sender.Failed += message => { lock(errors) errors.Add(message); }; receiver.Failed += message => { lock(errors) errors.Add(message); };
        var offer = await sender.CreateDescription(true); await receiver.ApplyDescription(offer);
        var answer = await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
        string SdpSummary(SessionDescription sdp) => string.Join("; ",sdp.sdp.Split('\n').Where(line => line.StartsWith("m=") || line.StartsWith("a=send") || line.StartsWith("a=recv") || line.StartsWith("a=inactive") || line.StartsWith("a=fmtp")));
        await Until(() => sender.ChannelOpen && receiver.ChannelOpen,15000,"The native peer connection did not open.");
        bool received = false;
        receiver.Message += text => { received = text.Contains("native-test"); return Task.CompletedTask; };
        sender.Send(new { type = "native-test" }); await Until(() => received,3000,"The encrypted control message did not arrive.");
        sender.WatchingUs = true; receiver.SetView(IntPtr.Zero,false);
        using var capture = new MediaCapture();
        capture.Packet += sender.Push; capture.Failed += message => { lock(errors) errors.Add(message); };
        capture.Start(new("Synthetic media",IntPtr.Zero,0,false),Quality.All[2],true,true);
        try { await Until(() => (receiver.VideoFrames >= 90 && receiver.AudioPackets >= 50) || errors.Count > 0,12000,"Native video/audio did not decode at the receiver."); }
        catch (TimeoutException) { throw new InvalidOperationException($"Native media timed out: video={receiver.VideoFrames}, audio={receiver.AudioPackets}, captured={capture.Frames}, sentBytes={sender.SentBytes}, errors={string.Join("; ",errors)}, offer={SdpSummary(offer)}, answer={SdpSummary(answer)}"); }
        capture.Dispose();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ",errors));
        return new { encryptedControl = received, decodedVideoFrames = receiver.VideoFrames, decodedAudioPackets = receiver.AudioPackets, hardwareEncoder = capture.Encoder, nativeWebRtc = true, rawDesktopCaptured = false };
    }
    internal static async Task Until(Func<bool> condition, int milliseconds, string message)
    {
        var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException(message); await Task.Delay(25); }
    }
    static object Benchmark()
    {
        using var capture = new MediaCapture();
        string? failure = null; capture.Failed += message => failure = message;
        var source = new CaptureSource("Synthetic frames",IntPtr.Zero,0,false);
        using var process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime; var time = Stopwatch.StartNew();
        capture.Start(source,Quality.All[2],false,true);
        Thread.Sleep(8000); time.Stop();
        capture.Dispose();
        if (failure != null) throw new InvalidOperationException(failure);
        if (capture.Frames < 350) throw new InvalidOperationException("The GPU pipeline did not sustain the synthetic 1080p60 test: " + capture.Frames + " frames.");
        process.Refresh();
        double cores = (process.TotalProcessorTime-cpu).TotalSeconds/time.Elapsed.TotalSeconds;
        return new { scenario = "Synthetic 1080p60 encode including CPU-generated fixture; not a gameplay FPS measurement", encoder = capture.Encoder, frames = capture.Frames, secondsIncludingStartup = time.Elapsed.TotalSeconds, steadyEncodedFps = capture.SteadyFps, cpuCoresIncludingFixture = cores, cpuPercentMachine = 100*cores/Environment.ProcessorCount, workingSetMB = process.WorkingSet64/1048576.0, encodedMbps = capture.Bytes*8/time.Elapsed.TotalSeconds/1e6, encoderInputInGpuMemory = true };
    }
    static Process StartHelper(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--runtime="+Gst.RuntimePath);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start synthetic audio fixture.");
    }
    static object AudioIsolation()
    {
        using var included = StartHelper("--tone","--frequency=440");
        using var excluded = StartHelper("--tone","--frequency=1000");
        try
        {
            var target = new CaptureSource("Synthetic tone",IntPtr.Zero,(uint)included.Id,false);
            using var capture = new Pipeline(MediaCapture.AudioSource(target,false) + " ! audioconvert ! audioresample ! audio/x-raw,format=F32LE,rate=48000,channels=2 ! appsink name=pcm sync=false max-buffers=32 drop=true");
            string? failure = null; capture.Failed += message => failure = message;
            IntPtr sink = Gst.Named(capture.Handle,"pcm");
            var samples = new List<float>(240000);
            try
            {
                Thread.Sleep(1500); capture.Play();
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 5000)
                {
                    IntPtr sample = Gst.gst_app_sink_try_pull_sample(sink,100_000_000);
                    if (sample == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr buffer = Gst.gst_sample_get_buffer(sample);
                        if (!Gst.gst_buffer_map(buffer,out var map,1).Equals(0))
                        {
                            try
                            {
                                int count = checked((int)map.Size.ToUInt64()/4); var values = new float[count]; Marshal.Copy(map.Data,values,0,count);
                                for (int i = 0; i+1 < count; i += 2) samples.Add((values[i]+values[i+1])*.5f);
                            }
                            finally { Gst.gst_buffer_unmap(buffer,ref map); }
                        }
                    }
                    finally { Gst.gst_mini_object_unref(sample); }
                }
            }
            finally { Gst.gst_object_unref(sink); }
            if (failure != null) throw new InvalidOperationException(failure);
            double wanted = Magnitude(samples,440), unwanted = Magnitude(samples,1000);
            if (samples.Count < 48000 || wanted < .0001 || unwanted > wanted*.03) throw new InvalidOperationException($"Application audio isolation failed: samples={samples.Count}, target={wanted}, unrelated={unwanted}.");
            return new { capturedProcess = "synthetic 440 Hz", excludedProcess = "synthetic 1000 Hz", samples = samples.Count, targetMagnitude = wanted, unrelatedMagnitude = unwanted, unrelatedToTarget = unwanted/wanted, passed = true, audioSaved = false };
        }
        finally
        {
            foreach (Process process in new[] { included,excluded }) { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
        }
    }
    internal static double Magnitude(IReadOnlyList<float> samples, double frequency)
    {
        double omega = 2*Math.PI*frequency/48000, cosine = Math.Cos(omega), coefficient = 2*cosine, a = 0, b = 0;
        foreach (float sample in samples) { double next = sample+coefficient*a-b; b = a; a = next; }
        return 2*Math.Sqrt(Math.Max(0,a*a+b*b-coefficient*a*b))/Math.Max(1,samples.Count);
    }
    internal static int Fixture(string[] args)
    {
        using var form = new Form { Text = "HavijScreen synthetic capture source", ClientSize = new Size(960,540), BackColor = Color.FromArgb(28,70,46), StartPosition = FormStartPosition.CenterScreen };
        int frame = 0; using var timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += (_,_) => { frame++; form.Invalidate(); };
        form.Paint += (_,e) => { using var brush = new SolidBrush(Color.FromArgb(230,166,110)); e.Graphics.FillEllipse(brush,30+(frame*7)%800,220,90,90); using var font = new Font("Segoe UI",26); e.Graphics.DrawString("HavijScreen synthetic source",font,Brushes.White,30,40); };
        form.Shown += (_,_) =>
        {
            string? report = Program.Option(args,"report");
            if (report != null) File.WriteAllText(report,JsonSerializer.Serialize(new { pid = Environment.ProcessId, hwnd = form.Handle.ToInt64() }));
            timer.Start();
        };
        Application.Run(form); return 0;
    }
    internal static int Playback(string[] args)
    {
        using var form = new Form { Text = "HavijScreen native playback test", ClientSize = new Size(960,540), BackColor = Color.Black, StartPosition = FormStartPosition.CenterScreen };
        int exit = 0;
        form.Shown += async (_,_) =>
        {
            IntPtr window = form.Handle;
            try
            {
                await Task.Run(async () =>
                {
                    using var sender = new NativePeer("render-sender","Synthetic sender",true,false,true);
                    using var viewer = new NativePeer("render-viewer","Native viewer",false,false);
                    var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    sender.Failed += errors.Enqueue; viewer.Failed += errors.Enqueue;
                    var offer = await sender.CreateDescription(true); await viewer.ApplyDescription(offer);
                    var answer = await viewer.CreateDescription(false); await sender.ApplyDescription(answer);
                    await Until(() => sender.ChannelOpen && viewer.ChannelOpen,15000,"Native playback peers did not connect.");
                    viewer.SetView(window,false); sender.WatchingUs = true;
                    using var capture = new MediaCapture(); capture.Packet += sender.Push; capture.Failed += errors.Enqueue;
                    capture.Start(new("Synthetic playback",IntPtr.Zero,0,false),Quality.All[2],true,true);
                    await Until(() => viewer.VideoFrames >= 180 || !errors.IsEmpty,10000,"Native embedded video did not decode.");
                    if (!errors.IsEmpty) throw new InvalidOperationException(string.Join("; ",errors));
                    string? report = Program.Option(args,"report");
                    if (report != null) File.WriteAllText(report,JsonSerializer.Serialize(new { nativeEmbeddedPlayback = true, videoFrames = viewer.VideoFrames, audioPackets = viewer.AudioPackets, soundMuted = true }));
                    await Task.Delay(15000); capture.Dispose();
                });
            }
            catch (Exception error) { exit = 1; string? report = Program.Option(args,"report"); if (report != null) File.WriteAllText(report,JsonSerializer.Serialize(new { error = error.ToString() })); }
            form.Close();
        };
        Application.Run(form); return exit;
    }
    internal static int Preview(string[] args)
    {
        using var form=new Form { Text="HavijScreen GPU preview test",ClientSize=new Size(960,540),BackColor=Color.Black,StartPosition=FormStartPosition.CenterScreen };
        int exit=0;
        form.Shown+=async(_,_)=>
        {
            IntPtr window=form.Handle; using var host=new NativeRoom(true) { Stun=false,Name="Synthetic preview host" }; using var guest=new NativeRoom(true) { Stun=false,Name="Synthetic viewer" };
            var errors=new System.Collections.Concurrent.ConcurrentQueue<string>(); host.Notice+=errors.Enqueue; guest.Notice+=errors.Enqueue;
            try
            {
                await host.Share(new("Synthetic GPU frames",IntPtr.Zero,0,false),Quality.All[2],true,true);
                if(host.EncoderStarts!=0||host.PreviewFrames!=0) throw new InvalidOperationException("Capture started while preview was off and nobody watched.");
                await host.Preview(window); await Until(()=>host.PreviewFrames>=30,6000,"Preview-only video did not render.");
                if(host.EncoderStarts!=0||host.Encoder!="") throw new InvalidOperationException("Preview-only mode started an encoder.");
                guest.ConfigureGuest(guest.Id,host.Id,host.RoomId,false);
                var sender=host.AddPeer(guest.Id,guest.Name,true); var receiver=guest.AddPeer(host.Id,host.Name,false);
                var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer); var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
                await Until(()=>host.Connected.Count==1&&guest.Connected.Count==1,15000,"Preview test peers did not connect.");
                await Until(()=>receiver.Sharing,3000,"Share state was not delivered."); guest.Watch(host.Id,IntPtr.Zero,true);
                await Until(()=>receiver.VideoFrames>=60&&receiver.AudioPackets>=30&&host.PreviewFrames>=30,10000,"Shared GPU preview and native media did not run together.");
                if(host.EncoderStarts!=1) throw new InvalidOperationException("Preview plus sharing used more than one encoder.");
                long sourceStart=receiver.VideoFrames;
                await host.Share(new("Synthetic source-resolution frames",IntPtr.Zero,0,false),new ShareOptions { Mode="Screenshare" }.Quality,true,true);
                await Until(()=>receiver.VideoFrames>=sourceStart+30&&host.PreviewFrames>=15,8000,"Source-resolution 15 fps mode did not render and stream.");
                int starts=host.EncoderStarts;
                long before=receiver.VideoFrames; await host.Preview(IntPtr.Zero); await Until(()=>receiver.VideoFrames>=before+30,6000,"Stopping local preview interrupted outgoing media.");
                await host.Preview(window); before=receiver.VideoFrames; await Until(()=>receiver.VideoFrames>=before+30,6000,"Restoring preview interrupted outgoing media.");
                if(host.EncoderStarts!=starts) throw new InvalidOperationException("Preview toggling restarted the encoder.");
                await Until(()=>host.FeedbackSamples>0,15000,"No native RTCP feedback was read.");
                sender.DiagnosticPacketLoss(.15);
                await Until(()=>host.BitrateKbps<3600,25000,"Packet loss did not reduce the live encoder bitrate.");
                sender.DiagnosticPacketLoss(0);
                await Task.Delay(6000); int reduced=host.BitrateKbps;
                await Until(()=>host.BitrateKbps>reduced,45000,"Bitrate did not recover after packet loss ended.");
                if(host.EncoderStarts!=starts) throw new InvalidOperationException("Adaptive bitrate restarted the encoder.");
                guest.Watch(host.Id,IntPtr.Zero,false); await host.Preview(window); await Until(()=>host.PreviewFrames>=30&&host.Encoder=="",6000,"Preview-only capture was not restored after the last viewer left.");
                await host.Preview(IntPtr.Zero); if(host.PreviewFrames!=0||host.Encoder!=""||!errors.IsEmpty) throw new InvalidOperationException("Preview did not release its capture resources: "+string.Join("; ",errors));
                string? report=Program.Option(args,"report"); if(report!=null) File.WriteAllText(report,JsonSerializer.Serialize(new { gpuPreview=true,previewOffWithoutViewersIdle=true,previewOnlyWithoutEncoding=true,oneEncoderWithPreviewAndViewer=true,sourceResolution15Fps=true,audioPreviewMuted=true,streamSurvivedPreviewOff=true,previewToggleWithoutEncoderRestart=true,rtcpPacketLossReducedBitrate=true,bitrateRecoveredWithoutRestart=true,captureReleasedAfterPreviewOff=true }));
            }
            catch(Exception error) { exit=1; string? report=Program.Option(args,"report"); if(report!=null) File.WriteAllText(report,JsonSerializer.Serialize(new { error=error.ToString(),errors })); }
            finally { host.Dispose(); guest.Dispose(); try { await Task.WhenAll(host.Completion,guest.Completion).WaitAsync(TimeSpan.FromSeconds(6)); } catch {} form.Close(); }
        };
        Application.Run(form); return exit;
    }
    internal static int Viewer(string[] args,string? runtime)
    {
        using var form=new MainForm(runtime); int exit=0;
        form.Shown+=async(_,_)=>
        {
            try
            {
                const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                await Until(()=>(bool)typeof(MainForm).GetField("ready",flags)!.GetValue(form)!,10000,"Viewer startup did not become ready.");
                if(new[]{"nvd3d11h264enc","amfh264enc","qsvh264enc"}.Any(Gst.Has)) throw new InvalidOperationException("Viewer test requires a runtime without encoder plugins.");
                foreach(string field in new[]{"join","invite"}) if(!((Control)typeof(MainForm).GetField(field,flags)!.GetValue(form)!).Enabled) throw new InvalidOperationException("Room controls disabled without an encoder.");
                WriteReport(args,new { viewerReadyWithoutEncoder=true,roomControlsEnabled=true });
            }
            catch(Exception error) { exit=1; WriteReport(args,new { error=error.ToString() }); }
            finally { form.Close(); }
        };
        Application.Run(form); return exit;
    }
    static void WriteReport(string[] args,object result) { string? path=Program.Option(args,"report"); if(path!=null) File.WriteAllText(path,JsonSerializer.Serialize(result)); }
    internal static int Regressions(string[] args)
    {
        using var form=new Form { Text="HavijScreen regression checks",ClientSize=new Size(320,160) }; int exit=0;
        form.Shown+=async(_,_)=>
        {
            using var host=new NativeRoom(true) { Stun=false }; using var guest=new NativeRoom(true) { Stun=false };
            try
            {
                CheckSourceEligibility();
                CheckQualityMenu(form);
                using(var picker=new SourceDialog(fixtures:Array.Empty<CaptureSource>()))
                {
                    const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                    var switchTab=typeof(SourceDialog).GetMethod("Switch",flags)!; var audio=typeof(SourceDialog).GetProperty("shareAudio",flags)!;
                    switchTab.Invoke(picker,[false]); audio.SetValue(picker,false); switchTab.Invoke(picker,[false]);
                    if(picker.Audio) throw new InvalidOperationException("Reselecting Applications lost the mute choice.");
                    switchTab.Invoke(picker,[true]); if(picker.Audio) throw new InvalidOperationException("System audio was not muted by default.");
                    audio.SetValue(picker,true); switchTab.Invoke(picker,[false]); if(picker.Audio) throw new InvalidOperationException("Switching tabs lost application mute.");
                    switchTab.Invoke(picker,[true]); if(!picker.Audio) throw new InvalidOperationException("Switching tabs lost explicit monitor audio selection.");
                }
                var rate=new NetworkRate(8000); rate.Observe("slow",new(1,.2,.05),10000); int reduced=rate.Target;
                rate.Observe("slow",new(1,0,.05),20000); if(rate.Target!=reduced) throw new InvalidOperationException("Repeated feedback triggered recovery.");
                rate.Observe("fast",new(1,0,.01),21000); if(rate.Target!=reduced) throw new InvalidOperationException("Fast viewer overrode slow viewer.");
                for(uint i=2;i<60;i++) rate.Observe("slow",new(i,.5,.05),20000+i*5000);
                if(rate.Target!=1000) throw new InvalidOperationException("Bitrate floor failed.");
                rate.Retain(["fast"]); if(rate.Target!=8000) throw new InvalidOperationException("Departed viewer still limited bitrate.");
                using(var finite=new Pipeline(MediaCapture.VideoPipeline(new("Finite fixture",IntPtr.Zero,0,false),Quality.All[0],"",true,false,false).Replace("pattern=ball","num-buffers=15 pattern=ball")))
                {
                    bool ended=false; finite.Ended+=()=>ended=true; finite.Play(); await Until(()=>ended,5000,"Disabled preview swallowed end-of-stream.");
                }
                using(var source=new Form()) { await host.Share(new("Owned window",source.Handle,(uint)Environment.ProcessId,false),Quality.All[0],false); }
                guest.ConfigureGuest(guest.Id,host.Id,host.RoomId,false);
                var sender=host.AddPeer(guest.Id,"Viewer",true); var receiver=guest.AddPeer(host.Id,"Host",false);
                var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer); var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
                await Until(()=>host.Connected.Count==1&&guest.Connected.Count==1&&receiver.Sharing,15000,"Regression peers did not connect.");
                guest.Watch(host.Id,IntPtr.Zero,true); await Until(()=>host.CaptureError!=null&&!receiver.Sharing,5000,"Closed source did not stop sharing.");
                if(host.Connected.Count!=1||guest.Connected.Count!=1) throw new InvalidOperationException("Capture failure disconnected a viewer.");
                await host.Share(new("Synthetic retry",IntPtr.Zero,0,false),Quality.All[0],false,true);
                await Until(()=>receiver.Sharing,3000,"Retry share did not arrive."); guest.Watch(host.Id,IntPtr.Zero,true);
                await Until(()=>receiver.VideoFrames>=30,6000,"Could not share again in the same room.");
                WriteReport(args,new { qualityMenuLifetime=true,systemPickerEntriesExcluded=true,cloakedPickerWindowsExcluded=true,audioChoicesPreserved=true,systemAudioDefaultMuted=true,closedSourceKeepsRoom=true,retryWithoutRejoining=true,rateBoundsAndMultipleViewers=true,staleFeedbackIgnored=true,disabledPreviewForwardsEndOfStream=true });
            }
            catch(Exception error) { exit=1; WriteReport(args,new { error=error.ToString() }); }
            finally { host.Dispose(); guest.Dispose(); await Task.WhenAll(host.Completion,guest.Completion); form.Close(); }
        };
        Application.Run(form); return exit;
    }
    internal static int PickerPreview(string[] args)
    {
        using var first=new Form { Text="Crystal Caverns · synthetic fixture",ClientSize=new Size(960,540),BackColor=Color.FromArgb(25,47,69),ShowInTaskbar=true,StartPosition=FormStartPosition.Manual,Location=new Point(60,80) };
        using var second=new Form { Text="Weekend playlist · synthetic fixture",ClientSize=new Size(900,560),BackColor=Color.FromArgb(53,32,65),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(420,130) };
        first.Paint+=(_,e)=>{ var g=e.Graphics; using var moon=new SolidBrush(Color.FromArgb(239,186,125)); g.FillEllipse(moon,710,70,100,100); using var hills=new SolidBrush(Color.FromArgb(37,91,107)); g.FillPolygon(hills,[new(0,540),new(0,350),new(220,160),new(500,400),new(700,260),new(960,480),new(960,540)]); using var f=Ui.Font(30,FontStyle.Bold); g.DrawString("CRYSTAL CAVERNS",f,Brushes.White,35,35); using var small=Ui.Font(14); g.DrawString("Owned synthetic window · no game is running",small,Brushes.LightGray,38,85); };
        second.Paint+=(_,e)=>{ var g=e.Graphics; using var f=Ui.Font(26,FontStyle.Bold); g.DrawString("Weekend playlist",f,Brushes.White,35,30); for(int i=0;i<5;i++) { using var fill=new SolidBrush(Color.FromArgb(65+i*8,46+i*5,84+i*5)); g.FillRectangle(fill,35,110+i*78,820,60); using var small=Ui.Font(16); g.DrawString("Synthetic track "+(i+1),small,Brushes.LightGray,60,126+i*78); } };
        first.Shown+=(_,_)=>
        {
            second.Show(); var fixtures=new[] { new CaptureSource(first.Text,first.Handle,(uint)Environment.ProcessId,false),new CaptureSource(second.Text,second.Handle,(uint)Environment.ProcessId,false) };
            var monitors=new[] { new CaptureSource("Screen 1 · synthetic fixture",IntPtr.Zero,0,true) { Bounds=first.RectangleToScreen(first.ClientRectangle) },new CaptureSource("Screen 2 · synthetic fixture",IntPtr.Zero,0,true) { Bounds=second.RectangleToScreen(second.ClientRectangle) } };
            using var picker=new SourceDialog(Quality.All[2],fixtures:fixtures,monitorFixtures:monitors) { StartPosition=FormStartPosition.CenterScreen,ShowInTaskbar=true };
            picker.ShowDialog(first); string? report=Program.Option(args,"report"); if(report!=null) File.WriteAllText(report,JsonSerializer.Serialize(new { selected=picker.Selected?.Name,quality=picker.SelectedQuality,audio=picker.Audio })); first.Close();
        };
        Application.Run(first); return 0;
    }
}
