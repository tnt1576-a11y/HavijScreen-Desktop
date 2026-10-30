using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Threading;
using NAudio.Wave;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HavijScreen {
  internal static class Program {
    [STAThread]
    static void Main() {
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.Run(new HavijScreenWindow());
    }
  }
  internal sealed class HavijScreenWindow : Form {
    private readonly WebView2 view = new WebView2();
    private const string AppOrigin = "https://havijscreen.internal";
    private StartupSurface startup;
    private Image carrot;
    private bool interfaceReady;
    private const int StopHotkey = 1;
    private bool hotkey;
    private bool sharing;
    private LoopbackAudio loopback;
    private string audioId;
    private int pendingAudioMessages;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    protected override CreateParams CreateParams {
      get {
        CreateParams parameters = base.CreateParams;
        parameters.Style |= 0x00040000 | 0x00020000 | 0x00010000 | 0x00080000;
        return parameters;
      }
    }
    public HavijScreenWindow() {
      Text = "HavijScreen";
      FormBorderStyle = FormBorderStyle.None;
      Padding = new Padding(6);
      ClientSize = new Size(1380, 870); MinimumSize = new Size(1020, 750);
      StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(17, 19, 19);
      Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
      using (Stream asset = typeof(Program).Assembly.GetManifestResourceStream("HavijScreen.Carrot.png")) { if (asset != null) using (Image source = Image.FromStream(asset)) carrot = new Bitmap(source); }
      view.Dock = DockStyle.Fill; view.DefaultBackgroundColor = BackColor; Controls.Add(view);
      startup = new StartupSurface(this,carrot); Controls.Add(startup); startup.BringToFront();
      Shown += async delegate { startup.Refresh(); await Initialize(); };
      Resize += delegate {
        if (view.CoreWebView2 != null) {
          bool minimized = WindowState == FormWindowState.Minimized;
          view.CoreWebView2.MemoryUsageTargetLevel = minimized ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
          view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"minimized\",\"active\":" + (minimized ? "true" : "false") + "}");
          PostWindowState();
        }
      };
      FormClosed += delegate { StopNativeAudio(); if (hotkey) UnregisterHotKey(Handle, StopHotkey); view.Dispose(); if (carrot != null) carrot.Dispose(); };
      FormClosing += delegate(object sender, FormClosingEventArgs e) {
        if (sharing && MessageBox.Show(this, "Closing HavijScreen stops your stream and disconnects this room. Close now?", "Close HavijScreen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
      };
    }
    private async System.Threading.Tasks.Task Initialize() {
      try {
        string content = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
        if (!File.Exists(Path.Combine(content, "index.html"))) throw new FileNotFoundException("The app folder is missing. Extract the complete HavijScreen zip before running HavijScreen.exe.");
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HavijScreen", "WebView2");
        // One evergreen system WebView2 instance. No bundled browser, elevated
        // privilege, injected module, game hook, external page, or host object.
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions());
        if (IsDisposed) return;
        await view.EnsureCoreWebView2Async(environment);
        if (IsDisposed) return;
        CoreWebView2 core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("havijscreen.internal", content, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.IsNonClientRegionSupportEnabled = true;
        core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args) { args.Cancel = !LocalUri(args.Uri); };
        core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
        core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs args) { args.Cancel = true; };
        core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs args) {
          // Clipboard reads require the user's Paste button gesture on our
          // local UI. Screen capture keeps its own mandatory picker.
          args.State = args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead && args.IsUserInitiated && LocalUri(args.Uri)
            ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
          args.SavesInProfile = false;
        };
        core.ScreenCaptureStarting += delegate(object sender, CoreWebView2ScreenCaptureStartingEventArgs args) { if (!LocalUri(core.Source)) args.Cancel = true; };
        core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args) {
          if (!LocalUri(args.Source) || args.WebMessageAsJson.Length > 200) return;
          try {
            var message = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(args.WebMessageAsJson);
            object type, active;
            if (message != null && message.TryGetValue("type", out type)) {
              switch (type as string) {
                case "sharing":
                  if (message.TryGetValue("active", out active) && active is bool) {
                    sharing = (bool)active; Text = sharing ? "HavijScreen - Sharing" : "HavijScreen";
                  }
                  break;
                case "window-minimize": WindowState = FormWindowState.Minimized; break;
                case "window-maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                case "window-close": Close(); break;
                case "ui-ready":
                  if (!interfaceReady) { interfaceReady = true; Controls.Remove(startup); startup.Dispose(); startup = null; view.BringToFront(); }
                  break;
                case "audio-start":
                  object startId;
                  if (message.TryGetValue("id", out startId)) StartNativeAudio(startId as string);
                  break;
                case "audio-stop":
                  object stopId;
                  if (message.TryGetValue("id", out stopId) && stopId as string == audioId) StopNativeAudio();
                  break;
              }
            }
          } catch (ArgumentException) { }
        };
        hotkey = RegisterHotKey(Handle, StopHotkey, 0x0002 | 0x0004 | 0x4000, (uint)Keys.S);
        core.NavigationCompleted += delegate { core.PostWebMessageAsJson("{\"type\":\"native-ready\",\"audioLoopback\":true,\"hotkey\":" + (hotkey ? "true" : "false") + "}"); PostWindowState(); };
        core.Navigate(AppOrigin + "/index.html");
      } catch (Exception error) {
        if (!IsDisposed) { MessageBox.Show(this, "HavijScreen could not start.\n\n" + error.Message + "\n\nInstall or update Microsoft Edge WebView2 Runtime, then try again.", "HavijScreen", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
      }
    }
    private static bool LocalUri(string value) {
      Uri uri;
      return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == "havijscreen.internal" && uri.Port == 443;
    }
    private void PostWindowState() {
      if (view.CoreWebView2 != null) view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"window-state\",\"maximized\":" + (WindowState == FormWindowState.Maximized ? "true" : "false") + "}");
    }
    private void PostAudio(object message) {
      if (!IsDisposed && view.CoreWebView2 != null && LocalUri(view.CoreWebView2.Source)) view.CoreWebView2.PostWebMessageAsJson(new JavaScriptSerializer().Serialize(message));
    }
    private void StartNativeAudio(string id) {
      Guid parsed;
      if (id == null || !Guid.TryParse(id, out parsed)) return;
      StopNativeAudio();
      LoopbackAudio engine = new LoopbackAudio();
      loopback = engine; audioId = id;
      engine.Data += delegate(byte[] bytes, int count, int alignment) {
        if (loopback != engine || IsDisposed) return;
        if (Interlocked.Increment(ref pendingAudioMessages) > 3) { Interlocked.Decrement(ref pendingAudioMessages); return; }
        // Capture buffers are reused by WASAPI. Encode before returning from the
        // callback, and keep the UI-message queue bounded when the app is busy.
        System.Collections.Generic.List<string> packets = new System.Collections.Generic.List<string>();
        int packetSize = 24000 - 24000 % alignment;
        for (int offset = 0; offset < count; offset += packetSize) packets.Add(Convert.ToBase64String(bytes, offset, Math.Min(packetSize, count - offset)));
        try {
          BeginInvoke((Action)delegate {
            try { if (loopback == engine && audioId == id) foreach (string pcm in packets) PostAudio(new { type = "audio-data", id = id, pcm = pcm }); }
            finally { Interlocked.Decrement(ref pendingAudioMessages); }
          });
        } catch (InvalidOperationException) { Interlocked.Decrement(ref pendingAudioMessages); }
      };
      engine.Failed += delegate(Exception error) {
        if (IsDisposed || loopback != engine) return;
        PostAudio(new { type = "audio-error", id = id, message = "Windows audio capture stopped: " + error.Message });
        StopNativeAudio();
      };
      try {
        engine.Start();
        WaveFormat format = engine.Format;
        PostAudio(new { type = "audio-started", id = id, sampleRate = format.SampleRate, channels = format.Channels, bits = format.BitsPerSample, @float = format.Encoding == WaveFormatEncoding.IeeeFloat });
      } catch (Exception error) {
        StopNativeAudio();
        PostAudio(new { type = "audio-error", id = id, message = "Windows audio could not start: " + error.Message });
      }
    }
    private void StopNativeAudio() {
      LoopbackAudio previous = loopback; loopback = null; audioId = null;
      if (previous != null) previous.Dispose();
    }
    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      int dark = 1, rounded = 2, noBorder = unchecked((int)0xfffffffe);
      DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
      DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
      DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
    }
    protected override void WndProc(ref Message message) {
      if (message.Msg == 0x0083 || message.Msg == 0x0085) { message.Result = IntPtr.Zero; return; }
      if (message.Msg == 0x0086) { message.Result = new IntPtr(1); return; }
      if (message.Msg == 0x0024) {
        base.WndProc(ref message);
        Screen screen = Screen.FromHandle(Handle);
        MinMaxInfo bounds = (MinMaxInfo)Marshal.PtrToStructure(message.LParam, typeof(MinMaxInfo));
        bounds.MaxPosition.X = screen.WorkingArea.Left - screen.Bounds.Left;
        bounds.MaxPosition.Y = screen.WorkingArea.Top - screen.Bounds.Top;
        bounds.MaxSize.X = screen.WorkingArea.Width; bounds.MaxSize.Y = screen.WorkingArea.Height;
        Marshal.StructureToPtr(bounds, message.LParam, false); return;
      }
      if (message.Msg == 0x0312 && message.WParam.ToInt32() == StopHotkey && view.CoreWebView2 != null) view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"stop-sharing\"}");
      base.WndProc(ref message);
      if (message.Msg == 0x0084 && WindowState == FormWindowState.Normal) {
        long position = message.LParam.ToInt64();
        Point point = PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
        int grip = Padding.Left;
        bool left = point.X < grip, right = point.X >= ClientSize.Width - grip;
        bool top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
        if (top) message.Result = new IntPtr(left ? 13 : right ? 14 : 12);
        else if (bottom) message.Result = new IntPtr(left ? 16 : right ? 17 : 15);
        else if (left) message.Result = new IntPtr(10);
        else if (right) message.Result = new IntPtr(11);
      }
    }
  }
}
