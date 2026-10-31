using System.Diagnostics;
using System.Text.Json;

namespace HavijScreen.Native;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? runtime = Option(args,"runtime");
        string? report = Option(args,"report");
        try
        {
            if (args.Contains("--video-recovery-test")) { Gst.Initialize(runtime); return Diagnostics.VideoRecovery(args); }
            if (args.Contains("--fixture")) return Diagnostics.Fixture(args);
            if (args.Contains("--picker-preview")) return Diagnostics.PickerPreview(args);
            if (args.Contains("--regression-test")) { Gst.Initialize(runtime); return Diagnostics.Regressions(args); }
            if (args.Contains("--viewer-test")) return Diagnostics.Viewer(args,runtime);
            if (args.Contains("--preview-test")) { Gst.Initialize(runtime); return Diagnostics.Preview(args); }
            if (args.Contains("--monitor-preview-test")) { Gst.Initialize(runtime); return Diagnostics.MonitorPreview(args); }
            if (args.Contains("--playback-test")) { Gst.Initialize(runtime); return Diagnostics.Playback(args); }
            if (args.Contains("--probe") || args.Contains("--benchmark") || args.Contains("--performance-test") || args.Contains("--performance-window-test") || args.Contains("--self-test") || args.Contains("--tone") || args.Contains("--rtc-test") || args.Contains("--room-test") || args.Contains("--capture-test"))
            {
                Diagnostics.ProgressPath = report == null ? null : report+".steps.txt";
                Gst.Initialize(runtime);
                object result = Diagnostics.Run(args);
                string json = JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented = true });
                if (report != null) File.WriteAllText(report,json); Console.WriteLine(json); return 0;
            }
            using var window = new MainForm(runtime);
            string[]? size = Option(args,"window-size")?.Split('x');
            if(size?.Length==2&&int.TryParse(size[0],out int width)&&int.TryParse(size[1],out int height)) window.ClientSize=new Size(Math.Clamp(width,1020,3840),Math.Clamp(height,700,2160));
            Application.Run(window);
            return 0;
        }
        catch (Exception error)
        {
            string json = JsonSerializer.Serialize(new { error = error.Message, details = error.ToString() });
            if (report != null) File.WriteAllText(report,json);
            if (!args.Any(a => a.StartsWith("--"))) MessageBox.Show(error.Message,"HavijScreen",MessageBoxButtons.OK,MessageBoxIcon.Error);
            Console.Error.WriteLine(json); return 1;
        }
    }
    internal static string? Option(string[] args, string name) => args.FirstOrDefault(a => a.StartsWith("--"+name+"=",StringComparison.Ordinal))?.Split('=',2)[1];
}
