using System.Collections.Concurrent;
using System.Reflection;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
    internal static int Watching(string[] args)
    {
        using var form=new Form { Text="HavijScreen viewing checks",ClientSize=new Size(960,600) };
        int exit=0;
        form.Shown+=async(_,_)=>
        {
            try
            {
                var rate=new ResolutionRate(Quality.All[2]);
                for(long now=0;now<=10000;now+=2000) rate.Observe(3000,now);
                if(rate.Height!=480) throw new InvalidOperationException("Resolution did not decrease after sustained limited bandwidth.");
                for(long now=12000;now<40000;now+=2000) rate.Observe(8000,now);
                if(rate.Height!=480) throw new InvalidOperationException("Resolution recovered without enough stable time.");
                for(long now=40000;now<=110000;now+=2000) rate.Observe(8000,now);
                if(rate.Height!=1080) throw new InvalidOperationException("Resolution did not recover to its ceiling.");
                var source=new ResolutionRate(new("Source",0,0,15,3600)); if(source.Observe(300,10000)!=0) throw new InvalidOperationException("Source resolution changed.");
                var recovery=new ConnectionRecovery(); recovery.Enable(1000);
                if(recovery.Tick(7000)||recovery.Reconnecting) throw new InvalidOperationException("Connection recovery started too early.");
                if(recovery.Tick(9000)||!recovery.Reconnecting||recovery.Tick(38000)) throw new InvalidOperationException("Recovery grace period failed.");
                recovery.Contact(38500); if(recovery.Reconnecting||recovery.Tick(39000)) throw new InvalidOperationException("Contact did not restore the connection.");
                recovery.Transport(false,40000); if(!recovery.Tick(70000)) throw new InvalidOperationException("Failed connection never expired.");
                var media=await CheckSynchronizedResolution();
                await CheckReconnectRoom();
                await CheckPlayerControls(form,Program.Option(args,"report"));
                await CheckWatchingLifecycle();
                WriteReport(args,new { media,resolutionHysteresis=true,sourceResolutionPreserved=true,briefInterruptionResumed=true,explicitLeaveImmediate=true,recoveryExpiry=true,volumeAndPopout=true,fullscreenIdleControls=true,stopWhileDisconnected=true,explicitStopDoesNotResume=true,replacementPeerControls=true,minimizeAndDockResume=true,performanceMeasurementAdded=false });
            }
            catch(Exception error) { exit=1; WriteReport(args,new { error=error.ToString() }); }
            finally { form.Close(); }
        };
        Application.Run(form); return exit;
    }
    static async Task<object> CheckSynchronizedResolution()
    {
        using var sender=new NativePeer("sync-sender","Synthetic",true,false,true);
        using var receiver=new NativePeer("sync-viewer","Synthetic",false,false,true);
        var errors=new ConcurrentQueue<string>(); var video=new ConcurrentQueue<double>(); var audio=new ConcurrentQueue<double>();
        sender.Failed+=errors.Enqueue; receiver.Failed+=errors.Enqueue;
        receiver.DiagnosticPresented+=(isAudio,pts,now)=>
        {
            if(pts!=Gst.None) (isAudio?audio:video).Enqueue(((double)now-pts)/Gst.Second*1000);
        };
        var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer); var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
        await Until(()=>sender.ChannelOpen&&receiver.ChannelOpen,15000,"Synchronization peers did not connect.");
        using var capture=new MediaCapture(); capture.Packet+=sender.Push; capture.Failed+=errors.Enqueue;
        sender.WatchingUs=true; capture.Start(new("Synthetic",IntPtr.Zero,0,false),Quality.All[2],true,true);
        await Until(()=>video.Count>=90&&audio.Count>=90,8000,"Synchronized media did not render.");
        foreach(int height in new[]{720,480,1080})
        {
            long before=receiver.VideoFrames, audioBefore=receiver.AudioPackets;
            capture.SetResolution(height);
            await Until(()=>receiver.VideoFrames>=before+60&&receiver.AudioPackets>=audioBefore+40,6000,"Resolution change interrupted video/audio.");
            if(capture.Height!=height) throw new InvalidOperationException("Resolution was not applied.");
        }
        double Median(ConcurrentQueue<double> values)=>values.Order().ElementAt(values.Count/2);
        double videoMs=Median(video),audioMs=Median(audio),skew=Math.Abs(videoMs-audioMs);
        if(skew>50||Math.Abs(videoMs)>250||Math.Abs(audioMs)>250||!errors.IsEmpty) throw new InvalidOperationException($"Playback timing failed: video={videoMs}, audio={audioMs}, errors={string.Join(";",errors)}");
        return new { synchronizedVideoSamples=video.Count,synchronizedAudioSamples=audio.Count,videoSchedulingMs=videoMs,audioSchedulingMs=audioMs,schedulingDifferenceMs=skew,resolutionChanges=3,encoder=capture.Encoder,audioContinued=true };
    }
    static async Task CheckReconnectRoom()
    {
        using var host=new NativeRoom(true) { Stun=false }; using var guest=new NativeRoom(true) { Stun=false };
        var notices=new ConcurrentQueue<string>(); host.Notice+=notices.Enqueue; guest.Notice+=notices.Enqueue; host.Ended+=notices.Enqueue; guest.Ended+=notices.Enqueue;
        try
        {
            guest.ConfigureGuest(guest.Id,host.Id,host.RoomId,false);
            var sender=host.AddPeer(guest.Id,"Guest",true); var receiver=guest.AddPeer(host.Id,"Host",false);
            var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer); var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
            await Until(()=>host.Connected.Count==1&&guest.Connected.Count==1,15000,"Recovery peers did not connect.");
            await host.Share(new("Synthetic recovery",IntPtr.Zero,0,false),Quality.All[0],true,true);
            try { await Until(()=>receiver.Sharing,3000,"Sharing state did not arrive."); } catch { throw new InvalidOperationException($"Sharing missing: host={host.Sharing}, guest={receiver.Sharing}, channels={sender.ChannelOpen}/{receiver.ChannelOpen}, peers={host.Peers.Count}/{guest.Peers.Count}, errors={string.Join(";",notices)}"); } guest.Watch(host.Id,IntPtr.Zero,true);
            await Until(()=>receiver.VideoFrames>=30,6000,"Recovery fixture did not decode.");
            // Delayed pre-share announcements must not overwrite newer state.
            sender.Send(new { type="sharing",active=false,revision=0 }); receiver.Send(new { type="watch",active=false,revision=0 }); await Task.Delay(150);
            if(!receiver.Sharing || host.Viewers!=1) throw new InvalidOperationException("A stale sharing announcement overwrote current state.");
            sender.DiagnosticPauseConnection(true);
            await Until(()=>sender.Reconnecting&&receiver.Reconnecting,14000,"Missing heartbeat did not show Reconnecting.");
            if(host.Peers.Count!=1||guest.Peers.Count!=1||!receiver.Subscribed) throw new InvalidOperationException("Brief interruption discarded membership or viewing state.");
            sender.DiagnosticPauseConnection(false); long before=receiver.VideoFrames;
            await Until(()=>!sender.Reconnecting&&!receiver.Reconnecting&&receiver.VideoFrames>before+30,7000,"Room/stream did not automatically recover.");
            if(host.EncoderStarts!=1) throw new InvalidOperationException("Brief interruption restarted the shared encoder.");
            guest.Dispose(); await Until(()=>host.Peers.Count==0,2000,"Explicit leave incorrectly waited for the recovery timeout.");
        }
        finally { host.Dispose(); guest.Dispose(); await Task.WhenAll(host.Completion,guest.Completion); }
    }
    static async Task CheckPlayerControls(Form form,string? report)
    {
        using var peer=new NativePeer("controls","Synthetic",true,false,true);
        using var card=new VideoCard(peer) { Dock=DockStyle.Fill,Active=true }; form.Controls.Add(card);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var slider=(TrackBar)typeof(VideoCard).GetField("volumeSlider",flags)!.GetValue(card)!;
        slider.Value=37; if(peer.VolumePercent!=37) throw new InvalidOperationException("Volume slider did not set peer gain.");
        if(report!=null) { using var image=new Bitmap(form.Width,form.Height); form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(report+".player.png"); }
        card.TogglePopout(); var window=Application.OpenForms.OfType<FullscreenVideoForm>().Single();
        window.Size=new Size(760,520);
        if(report!=null) { using var image=new Bitmap(window.Width,window.Height); window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(report+".popout.png"); }
        if(card.IsFullscreen||!card.IsDetached) throw new InvalidOperationException("Pop-out state incorrect.");
        card.ToggleFullscreen(); var pointer=window.PointToScreen(new Point(window.Width/2,window.Height/2));
        card.UpdateIdle(Environment.TickCount64,pointer,true); card.UpdateIdle(Environment.TickCount64+4000,pointer,true);
        if(card.ControlsVisible) throw new InvalidOperationException("Idle fullscreen controls did not hide.");
        card.Touch(); if(!card.ControlsVisible) throw new InvalidOperationException("Fullscreen controls did not return.");
        card.ExitFullscreen(); if(card.IsFullscreen||!card.IsDetached||window.Size!=new Size(760,520)) throw new InvalidOperationException("Fullscreen did not restore pop-out bounds.");
        card.ReturnToRoom(); if(card.IsDetached||peer.VolumePercent!=37) throw new InvalidOperationException("Docking lost gain or window state.");
        card.TogglePopout(); card.Active=false;
        if(card.IsDetached||Application.OpenForms.OfType<FullscreenVideoForm>().Any()) throw new InvalidOperationException("Stopping a stream left its pop-out open.");
        await Task.Delay(20);
    }
    static async Task CheckWatchingLifecycle()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,flags)!.GetValue(owner)!;
        void Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,flags)!.Invoke(owner,args);
        void Channel(NativePeer peer,bool open)=>typeof(NativePeer).GetProperty("ChannelOpen",flags)!.SetValue(peer,open);
        using var window=new MainForm(Gst.RuntimePath);
        window.Show();
        var room=Field<NativeRoom>(window,"room");
        try
        {
            await Until(()=>Field<bool>(window,"ready"),3000,"Viewing fixture did not initialize.");
            var peer=room.AddPeer(SetupCrypto.Id(),"Synthetic viewer",true,true);
            // Model an established channel's open/closed states without touching
            // the network or capturing any desktop content.
            Channel(peer,true); peer.Sharing=true;
            Call(window,"Watch",peer);
            var cards=Field<Dictionary<string,VideoCard>>(window,"cards");
            var paused=Field<HashSet<string>>(window,"pausedViews");
            var card=cards[peer.Id];
            peer.SetView(card.VideoHandle,true);
            Channel(peer,false);
            Call(window,"StopWatching",peer.Id);
            if(peer.Subscribed||peer.AudioEnabled||card.Active) throw new InvalidOperationException("Local stop was ignored while the control channel was closed.");
            using(var message=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(peer.WatchMessage())))
                if(message.RootElement.Bool("active")) throw new InvalidOperationException("Recovery would resend a stopped subscription as active.");
            Channel(peer,true); Call(window,"Watch",peer);
            card.TogglePopout(); window.WindowState=FormWindowState.Minimized;
            await Task.Delay(50);
            Field<Button>(card,"stop").PerformClick();
            if(peer.Subscribed||card.Active||card.IsDetached||paused.Contains(peer.Id)) throw new InvalidOperationException("Explicit pop-out stop was queued for resume.");
            window.WindowState=FormWindowState.Normal; await Task.Delay(50);
            if(peer.Subscribed||card.Active) throw new InvalidOperationException("Restoring the room resumed an explicitly stopped stream.");

            // Ordinary minimization and closing a pop-out still pause/resume.
            Call(window,"Watch",peer); window.WindowState=FormWindowState.Minimized;
            if(peer.Subscribed||!paused.Contains(peer.Id)) throw new InvalidOperationException("Minimize did not pause the docked stream.");
            window.WindowState=FormWindowState.Normal;
            if(!peer.Subscribed||!card.Active) throw new InvalidOperationException("Restore did not resume a minimized stream.");
            card.TogglePopout(); window.WindowState=FormWindowState.Minimized; card.ReturnToRoom();
            if(peer.Subscribed||!paused.Contains(peer.Id)) throw new InvalidOperationException("Docking into a minimized room did not pause.");
            window.WindowState=FormWindowState.Normal;
            if(!peer.Subscribed||!card.Active) throw new InvalidOperationException("Restore did not resume a docked stream.");

            room.Drop(peer.Id); await Task.Delay(50);
            var replacement=room.AddPeer(peer.Id,"Synthetic replacement",true,true);
            Channel(replacement,true); replacement.Sharing=true; Call(window,"Watch",replacement);
            var current=cards[peer.Id];
            if(ReferenceEquals(current,card)||!card.IsDisposed||!current.IsBoundTo(replacement)) throw new InvalidOperationException("Viewer retained the old peer connection.");
            Field<TrackBar>(current,"volumeSlider").Value=37;
            Field<Button>(current,"sound").PerformClick();
            current.TogglePopout(); current.ToggleFullscreen(); current.ExitFullscreen(); current.ReturnToRoom();
            if(replacement.VolumePercent!=37||!replacement.AudioEnabled||!replacement.Subscribed||Field<IntPtr>(replacement,"targetWindow")!=current.VideoHandle)
                throw new InvalidOperationException("Replacement peer did not receive volume, sound or player-window updates.");
        }
        finally
        {
            room.Dispose(); await room.Completion;
            window.Close();
            await Until(()=>window.IsDisposed,3000,"Viewing fixture did not close.");
        }
    }
}
