using System.Text.Json;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
    static void CheckPerformanceController()
    {
        static void Require(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
        var rate=new PerformanceRate(); long time=10000, frames=0;
        rate.Reset(time,frames);
        void Tick(int count,int milliseconds=2000) { time+=milliseconds; frames+=count; rate.Observe(time,frames); }
        for(int i=0;i<8;i++) Tick(60);
        Require(rate.Target==30,"Healthy capture was reduced.");
        Tick(20); Tick(60); Tick(20); Tick(60);
        Require(rate.Target==30,"Isolated slow frames reduced capture.");
        Tick(20); Tick(20); Tick(20);
        Require(rate.Target==20,"Sustained overload did not reduce capture.");
        for(int i=0;i<10;i++) Tick(10);
        Require(rate.Target==15,"Performance floor was not reached.");
        for(int i=0;i<40;i++) Tick(0);
        Require(rate.Target==15,"Idle source falsely recovered.");
        Tick(100,20000);
        Require(rate.Target==15,"Suspension falsely recovered.");
        for(int i=0;i<18;i++) Tick(30);
        Require(rate.Target==20,"Healthy capture did not probe a higher rate.");
        for(int i=0;i<7;i++) Tick(10);
        Require(rate.Target==15,"Failed probe did not back off.");
        for(int i=0;i<30;i++) Tick(30);
        Require(rate.Target==15,"Repeated probe ignored backoff.");
        rate.Reset(time,frames);
        Require(rate.Target==30,"A new capture inherited old limits.");
        var legacy=MainForm.RestoreQuality(JsonSerializer.SerializeToElement(new { mode="Custom",quality=4,height=480,fps=15 }));
        Require(!legacy.Quality.PrioritizeGame&&legacy.Quality.Height==480&&legacy.Quality.Fps==15,"Legacy custom quality was replaced by Performance.");
        var oldGaming=MainForm.RestoreQuality(JsonSerializer.SerializeToElement(new { mode="Gaming",quality=3,height=1440,fps=60 }));
        Require(oldGaming.Mode=="High quality"&&oldGaming.Quality.Height==1440&&oldGaming.Quality.Fps==60,"Legacy Gaming choice was lost.");
        var performance=MainForm.RestoreQuality(JsonSerializer.SerializeToElement(new { mode="Performance",quality=4,height=720,fps=30 }));
        Require(performance.Quality.PrioritizeGame,"Performance preference did not restore.");
    }

    static async Task<object> Performance()
    {
        CheckPerformanceController(); Step("performance controller passed");
        using var host=new NativeRoom(true) { Stun=false };
        using var guest=new NativeRoom(true) { Stun=false };
        var errors=new System.Collections.Concurrent.ConcurrentQueue<string>();
        host.Notice+=errors.Enqueue; guest.Notice+=errors.Enqueue;
        try
        {
            await host.Share(new("Synthetic performance fixture",IntPtr.Zero,0,false),Quality.Performance,true,true);
            if(host.EncoderStarts!=0) throw new InvalidOperationException("Performance mode encoded without viewers.");
            guest.ConfigureGuest(guest.Id,host.Id,host.RoomId,false);
            var sender=host.AddPeer(guest.Id,"Viewer",true); var receiver=guest.AddPeer(host.Id,"Host",false);
            var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer);
            var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
            await Until(()=>receiver.Sharing&&sender.ChannelOpen,15000,"Performance peers did not connect.");
            guest.Watch(host.Id,IntPtr.Zero,true);
            await Until(()=>receiver.VideoFrames>=30&&receiver.AudioPackets>=30,8000,"Performance stream did not start.");
            int starts=host.EncoderStarts; long audio=receiver.AudioPackets;
            await host.DiagnosticCaptureDelay(90000); Step("synthetic capture overload applied");
            await Until(()=>host.TargetFps==15||!errors.IsEmpty,40000,"Capture overload did not reduce the live frame rate to 15.");
            if(!errors.IsEmpty) throw new InvalidOperationException(string.Join("; ",errors));
            if(receiver.AudioPackets<audio+200) throw new InvalidOperationException("Audio did not continue during capture overload.");
            await host.DiagnosticCaptureDelay(0); Step("overload removed; waiting for cautious recovery");
            await Until(()=>host.TargetFps>=20||!errors.IsEmpty,100000,"Capture did not recover after overload ended.");
            long before=receiver.VideoFrames; audio=receiver.AudioPackets;
            await Until(()=>receiver.VideoFrames>=before+30&&receiver.AudioPackets>=audio+30,6000,"Media stopped after the frame-rate change.");
            if(host.EncoderStarts!=starts||host.Connected.Count!=1||guest.Connected.Count!=1||!errors.IsEmpty)
                throw new InvalidOperationException("Adaptation restarted capture, disconnected a peer, or failed: "+string.Join("; ",errors));
            Step("synthetic adaptation passed; checking WGC cadence changes");
            await host.StopSharing();
            object wgc=await PerformanceWindow();
            return new { controllerBoundsAndBackoff=true,liveOverloadReducedCapture=true,liveCaptureRecovered=true,audioContinued=true,encoderObjectsPreserved=true,peerConnectionPreserved=true,gameFpsMeasured=false,wgc };
        }
        finally { host.Dispose(); guest.Dispose(); await Task.WhenAll(host.Completion,guest.Completion).WaitAsync(TimeSpan.FromSeconds(8)); }
    }

    static async Task<object> PerformanceWindow()
    {
        string path=Path.Combine(Path.GetTempPath(),"havij-performance-"+Guid.NewGuid().ToString("N")+".json");
        using var fixture=StartHelper("--fixture","--report="+path);
        try
        {
            await Until(()=>File.Exists(path),6000,"Owned WGC fixture did not start.");
            using var json=JsonDocument.Parse(File.ReadAllText(path));
            var source=new CaptureSource("Owned performance fixture",new(json.RootElement.GetProperty("hwnd").GetInt64()),(uint)fixture.Id,false);
            using var capture=new MediaCapture(); string? error=null; capture.Failed+=message=>error=message;
            capture.Start(source,Quality.Performance,false);
            var measured=new Dictionary<int,double>();
            foreach(int fps in new[]{30,20,15,30})
            {
                Step($"WGC requesting {fps} fps (encoded frames: {capture.Frames})");
                capture.SetFrameRate(fps); await Task.Delay(1500);
                long before=capture.Frames; var clock=System.Diagnostics.Stopwatch.StartNew();
                await Task.Delay(3000); double actual=(capture.Frames-before)/clock.Elapsed.TotalSeconds;
                if(error!=null||actual<fps*.75||actual>fps*1.15) throw new InvalidOperationException($"WGC cadence {fps} failed: {actual:F2} fps; {error}");
                measured[fps]=actual;
            }
            return new { ownedWindowOnly=true,measuredFps=measured };
        }
        finally { if(!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(2000); } if(File.Exists(path)) File.Delete(path); }
    }
}
