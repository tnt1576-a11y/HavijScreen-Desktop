using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
    internal static int VideoRecovery(string[] args)
    {
        using var form=new Form { Text="HavijScreen synthetic video recovery test", ClientSize=new Size(960,600) };
        int exit=0;
        form.Shown+=async(_,_)=>
        {
            try
            {
                var policy=new VideoDelivery();
                if(policy.Accept(false,0)||!policy.Accept(true,0)||!policy.Accept(false,2)||policy.Accept(false,3)||policy.Accept(false,0)||policy.Accept(true,3)||!policy.Accept(true,0))
                    throw new InvalidOperationException("Video queue admitted a dependent frame after loss or exceeded its limit.");
                policy.Reset(); if(policy.Accept(false,0)) throw new InvalidOperationException("New viewer did not wait for a keyframe.");
                var integrity=await Task.Run(CheckVideoIntegrity);
                await CheckFullscreen(form);
                WriteReport(args,new { integrity, boundedReferenceSafeQueue=true, fullscreenPreservesPlayback=true, escapeRestoresView=true, stopAndDisposeCloseFullscreen=true, pixelsSaved=false, externalMediaCaptured=false });
            }
            catch(Exception error) { exit=1; WriteReport(args,new { error=error.ToString() }); }
            finally { form.Close(); }
        };
        Application.Run(form); return exit;
    }
    static unsafe string PixelDigest(IntPtr buffer)
    {
        if(Gst.gst_buffer_map(buffer,out var map,1)==0) throw new InvalidOperationException("Could not inspect synthetic decoded frame.");
        try { return Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)map.Data,checked((int)map.Size)))); }
        finally { Gst.gst_buffer_unmap(buffer,ref map); }
    }
    static async Task<object> CheckVideoIntegrity()
    {
        var expected=new ConcurrentDictionary<string,byte>(); var received=new ConcurrentQueue<string>();
        var errors=new ConcurrentQueue<string>();
        using var reference=new Pipeline("appsrc name=input is-live=true format=time block=true max-buffers=4 max-bytes=0 caps=\"video/x-h264,stream-format=byte-stream,alignment=au,profile=constrained-baseline\" ! h264parse ! d3d11h264dec ! d3d11download ! video/x-raw,format=NV12 ! appsink name=output emit-signals=true sync=false max-buffers=2 drop=false");
        reference.Failed+=errors.Enqueue;
        IntPtr input=Gst.Named(reference.Handle,"input"), output=Gst.Named(reference.Handle,"output");
        reference.Connect(output,"new-sample",new Gst.NewSample((sink,_)=>
        {
            IntPtr sample=Gst.gst_app_sink_try_pull_sample(sink,0);
            if(sample!=IntPtr.Zero) try { expected.TryAdd(PixelDigest(Gst.gst_sample_get_buffer(sample)),0); } catch(Exception error) { errors.Enqueue(error.Message); } finally { Gst.gst_mini_object_unref(sample); }
            return 0;
        }));
        using var sender=new NativePeer("integrity-sender","Synthetic",true,false,true);
        using var receiver=new NativePeer("integrity-receiver","Synthetic",false,false,true,buffer=>
        {
            try { received.Enqueue(PixelDigest(buffer)); } catch(Exception error) { errors.Enqueue(error.Message); }
        });
        sender.Failed+=errors.Enqueue; receiver.Failed+=errors.Enqueue;
        var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer); var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
        await Until(()=>sender.ChannelOpen&&receiver.ChannelOpen,15000,"Integrity peers did not connect.");
        // Snow changes every pixel and produces many RTP fragments per frame.
        // A long GOP proves recovery requests work instead of masking loss with periodic IDRs.
        string source=MediaCapture.VideoPipeline(new("Synthetic",IntPtr.Zero,0,false),new("Stress",1280,720,30,8000),MediaCapture.SelectEncoder(),true)
            .Replace("pattern=ball","pattern=snow").Replace("gop-size=30","gop-size=300");
        using var capture=new Pipeline(source); capture.Failed+=errors.Enqueue;
        IntPtr encoded=Gst.Named(capture.Handle,"encoded-video"), encoder=Gst.Named(capture.Handle,"encoder");
        IntPtr encoderPad=Gst.gst_element_get_static_pad(encoder,"src");
        int keyframes=0; long largest=0; bool finished=false; object recoveryGate=new();
        sender.KeyframeRequested+=()=> { lock(recoveryGate) { if(!finished) Gst.gst_pad_send_event(encoderPad,Gst.gst_video_event_new_upstream_force_key_unit(Gst.None,1,0)); } };
        capture.Connect(encoded,"new-sample",new Gst.NewSample((sink,_)=>
        {
            IntPtr sample=Gst.gst_app_sink_try_pull_sample(sink,0); if(sample==IntPtr.Zero) return 0;
            try
            {
                IntPtr buffer=Gst.gst_sample_get_buffer(sample);
                if((Gst.gst_buffer_get_flags(buffer)&(1u<<13))==0) Interlocked.Increment(ref keyframes);
                largest=Math.Max(largest,(long)Gst.gst_buffer_get_size(buffer));
                Gst.gst_app_src_push_buffer(input,Gst.gst_buffer_copy(buffer));
                ulong running=Gst.gst_segment_to_running_time(Gst.gst_sample_get_segment(sample),3,Gst.ReadTime(buffer,Gst.PtsOffset));
                sender.Push(false,buffer,Gst.gst_element_get_base_time(capture.Handle)+running);
            }
            catch(Exception error) { errors.Enqueue(error.ToString()); }
            finally { Gst.gst_mini_object_unref(sample); }
            return 0;
        }));
        try
        {
            reference.Play(); sender.WatchingUs=true; capture.Play();
            await Until(()=>received.Count>=90||!errors.IsEmpty,8000,"Fragmented motion video did not decode.");
            int clean=received.Count;
            if(largest<12000) throw new InvalidOperationException("Motion fixture did not exercise fragmented video.");
            sender.DiagnosticSenderDelay(150000); await Task.Delay(1500); sender.DiagnosticSenderDelay(0);
            int before=received.Count;
            await Until(()=>received.Count>=before+30,5000,"Backlogged sender did not recover.");
            long requests=sender.KeyframeRequests; int priorKeyframes=keyframes;
            sender.DiagnosticPacketLoss(.08); await Task.Delay(1800); sender.DiagnosticPacketLoss(0);
            before=received.Count;
            try { await Until(()=>received.Count>=before+40,5000,"Packet loss did not recover before the next scheduled keyframe."); }
            catch(Exception error) { throw new InvalidOperationException($"{error.Message} received={received.Count}, before={before}, requests={sender.KeyframeRequests}/{requests}, keys={keyframes}/{priorKeyframes}, encoded={expected.Count}, pli={offer.sdp.Contains("nack pli")}, fir={offer.sdp.Contains("ccm fir")}, errors={string.Join(";",errors)}"); }
            if(sender.KeyframeRequests<=requests || keyframes<=priorKeyframes) throw new InvalidOperationException("Loss did not reach the shared encoder as a keyframe request.");
            capture.Dispose(); await Task.Delay(600);
            var digests=received.ToArray(); int corrupt=digests.Count(d=>!expected.ContainsKey(d));
            if(!errors.IsEmpty || corrupt!=0) throw new InvalidOperationException($"Decoded integrity failed: corrupt={corrupt}/{digests.Length}; "+string.Join("; ",errors));
            return new { decodedFrames=digests.Length, cleanMotionFrames=clean, mismatchedFrames=corrupt, largestEncodedFrame=largest, keyframeRequests=sender.KeyframeRequests, keyframes, lossRecovery=true, backlogRecovery=true };
        }
        finally
        {
            lock(recoveryGate) finished=true;
            capture.Dispose(); reference.Dispose();
            foreach(var pointer in new[]{input,output,encoded,encoder,encoderPad}) Gst.gst_object_unref(pointer);
        }
    }
    static async Task CheckFullscreen(Form form)
    {
        using var sender=new NativePeer("fullscreen-sender","Synthetic",true,false,true);
        using var receiver=new NativePeer("fullscreen-receiver","Synthetic stream",false,false);
        var errors=new ConcurrentQueue<string>(); sender.Failed+=errors.Enqueue; receiver.Failed+=errors.Enqueue;
        var offer=await sender.CreateDescription(true); await receiver.ApplyDescription(offer); var answer=await receiver.CreateDescription(false); await sender.ApplyDescription(answer);
        await Until(()=>sender.ChannelOpen&&receiver.ChannelOpen,15000,"Fullscreen peers did not connect.");
        using var card=new VideoCard(receiver) { Dock=DockStyle.Fill,Active=true }; form.Controls.Add(card);
        receiver.SetView(card.VideoHandle,false); sender.WatchingUs=true;
        using var capture=new MediaCapture(); capture.Packet+=sender.Push; capture.Failed+=errors.Enqueue;
        capture.Start(new("Synthetic fullscreen",IntPtr.Zero,0,false),Quality.All[0],true,true);
        await Until(()=>receiver.VideoFrames>=30&&receiver.AudioPackets>=20,6000,"Fullscreen fixture did not decode.");
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var sound=(Button)typeof(VideoCard).GetField("sound",flags)!.GetValue(card)!;
        sound.PerformClick(); receiver.SetVolume(37);
        IntPtr gain=(IntPtr)typeof(NativePeer).GetField("volume",flags)!.GetValue(receiver)!;
        Gst.GetDouble(gain,"volume",out double applied,IntPtr.Zero);
        if(Math.Abs(applied-.37)>.001) throw new InvalidOperationException("Playback volume did not reach the audio pipeline.");
        for(int i=0;i<3;i++)
        {
            card.ToggleFullscreen();
            var window=Application.OpenForms.OfType<FullscreenVideoForm>().Single();
            if(window.Bounds!=Screen.FromControl(form).Bounds || !card.IsFullscreen || !receiver.AudioEnabled) throw new InvalidOperationException("Fullscreen size or audio choice changed.");
            long before=receiver.VideoFrames; await Until(()=>receiver.VideoFrames>before+15,3000,"Fullscreen interrupted decoding.");
            object?[] parameters=[Message.Create(window.Handle,0,IntPtr.Zero,IntPtr.Zero),Keys.Escape];
            typeof(FullscreenVideoForm).GetMethod("ProcessCmdKey",flags)!.Invoke(window,parameters);
            if(card.IsFullscreen||!window.IsDisposed||!receiver.ChannelOpen||!card.Sound) throw new InvalidOperationException("Escape did not restore playback state.");
        }
        card.ToggleFullscreen(); card.Active=false;
        if(card.IsFullscreen) throw new InvalidOperationException("Remote stop left fullscreen open.");
        card.Active=true; card.ToggleFullscreen(); card.Dispose();
        if(Application.OpenForms.OfType<FullscreenVideoForm>().Any()||!errors.IsEmpty) throw new InvalidOperationException("Fullscreen disposal failed: "+string.Join("; ",errors));
        capture.Dispose();
    }
}
