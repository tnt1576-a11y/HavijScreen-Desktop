using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Drift {
  internal static class Program {
    [STAThread]
    static void Main() {
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.Run(new DriftWindow());
    }
  }
  internal sealed class DriftWindow : Form {
    private readonly WebView2 view = new WebView2();
    private const string AppOrigin = "https://drift.internal";
    private const int StopHotkey = 1;
    private bool hotkey;
    private bool sharing;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public DriftWindow() {
      Text = "Drift - Screen sharing, just between you";
      ClientSize = new Size(1380, 870); MinimumSize = new Size(1020, 750);
      StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(17, 19, 19);
      Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
      view.Dock = DockStyle.Fill; view.DefaultBackgroundColor = BackColor; Controls.Add(view);
      Shown += async delegate { await Initialize(); };
      Resize += delegate {
        if (view.CoreWebView2 != null) {
          bool minimized = WindowState == FormWindowState.Minimized;
          view.CoreWebView2.MemoryUsageTargetLevel = minimized ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
          view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"minimized\",\"active\":" + (minimized ? "true" : "false") + "}");
        }
      };
      FormClosed += delegate { if (hotkey) UnregisterHotKey(Handle, StopHotkey); view.Dispose(); };
      FormClosing += delegate(object sender, FormClosingEventArgs e) {
        if (sharing && MessageBox.Show(this, "Closing Drift stops your stream and disconnects this room. Close now?", "Close Drift", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
      };
    }
    private async System.Threading.Tasks.Task Initialize() {
      try {
        string content = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
        if (!File.Exists(Path.Combine(content, "index.html"))) throw new FileNotFoundException("The app folder is missing. Extract the complete Drift zip before running Drift.exe.");
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Drift", "WebView2");
        // One evergreen system WebView2 instance. No bundled browser, elevated
        // privilege, injected module, game hook, external page, or host object.
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions());
        await view.EnsureCoreWebView2Async(environment);
        CoreWebView2 core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("drift.internal", content, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args) { args.Cancel = !LocalUri(args.Uri); };
        core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
        core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs args) { args.Cancel = true; };
        core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs args) {
          // Screen capture uses its own mandatory picker. Never grant microphone,
          // webcam, location, notifications, or persistent capture permission.
          args.State = CoreWebView2PermissionState.Deny;
        };
        core.ScreenCaptureStarting += delegate(object sender, CoreWebView2ScreenCaptureStartingEventArgs args) { if (!LocalUri(core.Source)) args.Cancel = true; };
        core.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args) {
          if (!LocalUri(args.Source) || args.WebMessageAsJson.Length > 200) return;
          try {
            var message = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(args.WebMessageAsJson);
            object type, active;
            if (message != null && message.TryGetValue("type", out type) && type as string == "sharing" && message.TryGetValue("active", out active) && active is bool) {
              sharing = (bool)active; Text = sharing ? "Drift - Sharing your screen" : "Drift - Screen sharing, just between you";
            }
          } catch (ArgumentException) { }
        };
        hotkey = RegisterHotKey(Handle, StopHotkey, 0x0002 | 0x0004 | 0x4000, (uint)Keys.S);
        core.NavigationCompleted += delegate { core.PostWebMessageAsJson("{\"type\":\"native-ready\",\"hotkey\":" + (hotkey ? "true" : "false") + "}"); };
        core.Navigate(AppOrigin + "/index.html");
      } catch (Exception error) {
        MessageBox.Show(this, "Drift could not start.\n\n" + error.Message + "\n\nInstall or update Microsoft Edge WebView2 Runtime, then try again.", "Drift", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
      }
    }
    private static bool LocalUri(string value) {
      Uri uri;
      return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.Host == "drift.internal" && uri.Port == 443;
    }
    protected override void WndProc(ref Message message) {
      if (message.Msg == 0x0312 && message.WParam.ToInt32() == StopHotkey && view.CoreWebView2 != null) view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"stop-sharing\"}");
      base.WndProc(ref message);
    }
  }
}
