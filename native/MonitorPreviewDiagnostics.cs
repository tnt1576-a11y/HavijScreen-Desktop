using System.Diagnostics;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
    internal static int MonitorPreview(string[] args)
    {
        using var form=new Form { Text="HavijScreen local monitor preview check",ClientSize=new Size(640,360),BackColor=Color.Black };
        int exit=0;
        form.Shown+=async(_,_)=>
        {
            var results=new List<object>();
            try
            {
                foreach(var source in Sources.List(true))
                {
                    string pipeline=MediaCapture.VideoPipeline(source,Quality.All[2],MediaCapture.SelectEncoder());
                    if(!pipeline.Contains("capture-api=dxgi")) throw new InvalidOperationException("Monitor capture did not use Desktop Duplication.");
                    string? error=null;
                    using(var preview=new MediaCapture())
                    {
                        preview.Failed+=message=>error=message; preview.PreviewFailed+=message=>error=message;
                        preview.Start(source,Quality.All[3],false,previewWindow:form.Handle,encode:false);
                        await Until(()=>preview.PreviewFrames>=30||error!=null,8000,"Monitor preview did not render.");
                        if(error!=null) throw new InvalidOperationException(error);
                        if(preview.TargetFps!=30||preview.Encoder!=""||preview.Frames!=0) throw new InvalidOperationException("Preview-only capture was not bounded or started an encoder.");
                        preview.SetPreview(IntPtr.Zero); long stopped=preview.PreviewFrames; await Task.Delay(300);
                        if(preview.PreviewFrames>stopped+1) throw new InvalidOperationException("Disabled preview continued rendering.");
                    }
                    using(var capture=new MediaCapture())
                    {
                        capture.Failed+=message=>error=message; capture.PreviewFailed+=message=>error=message;
                        capture.Start(source,Quality.All[2],false,previewWindow:form.Handle);
                        await Until(()=>capture.Frames>=60||error!=null,8000,"Monitor encode did not start.");
                        long before=capture.Frames, shown=capture.PreviewFrames; var watch=Stopwatch.StartNew();
                        await Task.Delay(3000); double seconds=watch.Elapsed.TotalSeconds;
                        double encoded=(capture.Frames-before)/seconds, rendered=(capture.PreviewFrames-shown)/seconds;
                        if(error!=null||encoded<45||rendered<10||rendered>32) throw new InvalidOperationException($"Monitor cadence failed: encode={encoded:F1}, preview={rendered:F1}, {error}");
                        var rates=new Dictionary<int,double>();
                        foreach(int fps in new[]{30,20,15,30})
                        {
                            capture.SetFrameRate(fps); await Task.Delay(1000);
                            before=capture.Frames; watch.Restart(); await Task.Delay(2000);
                            double actual=(capture.Frames-before)/watch.Elapsed.TotalSeconds;
                            if(error!=null||actual<fps*.75||actual>fps*1.2) throw new InvalidOperationException($"Desktop Duplication rate change to {fps} failed: {actual:F1}; {error}");
                            rates[fps]=actual;
                        }
                        results.Add(new { monitor=source.Name,encodedFps=encoded,previewFps=rendered,adaptiveRates=rates });
                    }
                }
                if(results.Count==0) throw new InvalidOperationException("No monitors available for local capture check.");
                WriteReport(args,new { desktopDuplication=true,previewOnlyBounded=true,previewRenderingCapped=true,streamRatePreserved=true,screenPixelsSaved=false,screenPixelsSent=false,mouseJitterRequiresUserCheck=true,results });
            }
            catch(Exception error) { exit=1; WriteReport(args,new { error=error.ToString(),results }); }
            finally { form.Close(); }
        };
        Application.Run(form); return exit;
    }
}
