using System.Runtime.InteropServices;
using System.Text.Json;

namespace HavijScreen.Native;

internal static class Ui
{
    internal static readonly Color Background = Color.FromArgb(17,19,19), Surface = Color.FromArgb(24,30,23), Line = Color.FromArgb(50,60,44), Muted = Color.FromArgb(145,160,132), Foreground = Color.FromArgb(229,235,223), Accent = Color.FromArgb(235,168,120);
    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = false, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.FromArgb(34,37,28) : Foreground, Font = new Font("Segoe UI",9), Cursor = Cursors.Hand, Margin = new Padding(0,8,0,0), AccessibleName = text };
        button.FlatAppearance.BorderColor = primary ? Accent : Line; return button;
    }
    internal static Label Label(string text, float size = 9, Color? color = null) => new() { Text = text, AutoSize = false, Height = 25, ForeColor = color ?? Foreground, Font = new Font("Segoe UI",size), BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft };
    internal static TextBox TextBox(string text = "") => new() { Text = text, BackColor = Surface, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI",11), Height = 34, Margin = new Padding(0,4,0,14) };
    internal static void On(Control control, Action action) { if (control.IsDisposed || !control.IsHandleCreated) return; if (control.InvokeRequired) { try { control.BeginInvoke(action); } catch(InvalidOperationException) { } } else action(); }
    internal static Image Carrot() { using var stream = typeof(Ui).Assembly.GetManifestResourceStream("HavijScreen.Carrot.png")!; using var original = Image.FromStream(stream); return new Bitmap(original); }
}
internal class NativeForm : Form
{
    protected readonly Panel Header = new() { Dock = DockStyle.Top, Height = 68 };
    protected readonly Panel Content = new() { Dock = DockStyle.Fill };
    readonly Image carrot = Ui.Carrot();
    readonly Button minimize, maximize, close;
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    [DllImport("user32")] static extern bool ReleaseCapture();
    [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMax { public PointNative Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.Style |= 0x40000|0x20000|0x10000|0x80000; return p; } }
    internal NativeForm()
    {
        Text = "HavijScreen"; BackColor = Ui.Background; ForeColor = Ui.Foreground; Font = new Font("Segoe UI",10);
        FormBorderStyle = FormBorderStyle.None; Padding = new Padding(6); MinimumSize = new Size(1020,720); ClientSize = new Size(1380,870); StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        minimize = Ui.Button("−"); maximize = Ui.Button("□"); close = Ui.Button("×");
        minimize.AccessibleName = "Minimize"; maximize.AccessibleName = "Maximize"; close.AccessibleName = "Close HavijScreen";
        foreach (var button in new[] { minimize,maximize,close }) { button.Size = new Size(46,56); button.Margin = Padding.Empty; button.FlatAppearance.BorderSize = 0; button.BackColor = Ui.Background; Header.Controls.Add(button); }
        minimize.Click += (_,_) => WindowState = FormWindowState.Minimized; maximize.Click += (_,_) => ToggleMaximize(); close.Click += (_,_) => Close();
        Header.Resize += (_,_) => { minimize.Location = new Point(Header.Width-144,0); maximize.Location = new Point(Header.Width-96,0); close.Location = new Point(Header.Width-48,0); };
        Header.Paint += (_,e) => { e.Graphics.DrawImage(carrot,new Rectangle(23,12,38,43)); using var font = new Font("Segoe UI",20,FontStyle.Bold); using var brush = new SolidBrush(Ui.Foreground); e.Graphics.DrawString("HavijScreen",font,brush,72,16); using var small = new Font("Segoe UI",8); using var muted = new SolidBrush(Ui.Muted); e.Graphics.DrawString("0.3.0",small,muted,255,28); using var line = new Pen(Ui.Line); e.Graphics.DrawLine(line,0,Header.Height-1,Header.Width,Header.Height-1); };
        Header.MouseDown += (_,e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero); } };
        Header.DoubleClick += (_,_) => ToggleMaximize();
        Controls.Add(Content); Controls.Add(Header);
    }
    void ToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e); int dark = 1, rounded = 2, none = unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(Handle,20,ref dark,4); DwmSetWindowAttribute(Handle,33,ref rounded,4); DwmSetWindowAttribute(Handle,34,ref none,4);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg is 0x83 or 0x85) { message.Result = IntPtr.Zero; return; }
        if (message.Msg == 0x86) { message.Result = new IntPtr(1); return; }
        if (message.Msg == 0x24)
        {
            base.WndProc(ref message); var screen = Screen.FromHandle(Handle); var bounds = Marshal.PtrToStructure<MinMax>(message.LParam);
            bounds.MaxPosition.X = screen.WorkingArea.Left-screen.Bounds.Left; bounds.MaxPosition.Y = screen.WorkingArea.Top-screen.Bounds.Top;
            bounds.MaxSize.X = screen.WorkingArea.Width; bounds.MaxSize.Y = screen.WorkingArea.Height; Marshal.StructureToPtr(bounds,message.LParam,false); return;
        }
        base.WndProc(ref message);
        if (message.Msg == 0x84 && WindowState == FormWindowState.Normal)
        {
            long position = message.LParam.ToInt64(); var point = PointToClient(new Point((short)(position&0xffff),(short)((position>>16)&0xffff)));
            bool left = point.X < 6, right = point.X >= ClientSize.Width-6, top = point.Y < 6, bottom = point.Y >= ClientSize.Height-6;
            if (top) message.Result = new IntPtr(left ? 13 : right ? 14 : 12); else if (bottom) message.Result = new IntPtr(left ? 16 : right ? 17 : 15); else if (left) message.Result = new IntPtr(10); else if (right) message.Result = new IntPtr(11);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) carrot.Dispose(); base.Dispose(disposing); }
}
internal class DarkDialog : Form
{
    internal DarkDialog() { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); }
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); int enabled = 1; DwmSetWindowAttribute(Handle,20,ref enabled,4); }
}
internal sealed class SourceDialog : DarkDialog
{
    readonly RadioButton windows = new() { Text = "Application", Checked = true, AutoSize = true }, monitors = new() { Text = "Entire screen", AutoSize = true };
    readonly ListBox list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, BackColor = Ui.Surface, ForeColor = Ui.Foreground, Font = new Font("Segoe UI",11) };
    readonly CheckBox sound = new() { Text = "Share app audio", Checked = true, AutoSize = true };
    readonly Label help = Ui.Label("Audio from the selected application and its child processes.",9,Ui.Muted);
    internal CaptureSource? Selected { get; private set; }
    internal bool Audio => sound.Checked;
    internal SourceDialog()
    {
        Text = "Choose what to share"; BackColor = Ui.Background; ForeColor = Ui.Foreground; ClientSize = new Size(650,500); MinimumSize = new Size(560,400); StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false; Font = new Font("Segoe UI",10);
        var modes = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, Padding = new Padding(16), BackColor = Ui.Surface }; modes.Controls.AddRange([windows,monitors]);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 142, Padding = new Padding(16) };
        sound.Location = new Point(16,13); help.Location = new Point(16,38); help.Height = 44;
        var share = Ui.Button("Start sharing",true); share.Size = new Size(160,40);
        var cancel = Ui.Button("Cancel"); cancel.Size = new Size(110,40); cancel.DialogResult = DialogResult.Cancel;
        var refresh = Ui.Button("Refresh"); refresh.Location = new Point(16,90); refresh.Width = 110;
        footer.Resize += (_,_) => { help.Width = footer.ClientSize.Width-32; share.Location = new Point(footer.ClientSize.Width-176,90); cancel.Location = new Point(footer.ClientSize.Width-294,90); };
        footer.Controls.AddRange([sound,help,share,cancel,refresh]); var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) }; middle.Controls.Add(list);
        Controls.Add(middle); Controls.Add(footer); Controls.Add(modes);
        windows.CheckedChanged += (_,_) => { if (windows.Checked) Reload(); }; monitors.CheckedChanged += (_,_) => { if (monitors.Checked) Reload(); };
        refresh.Click += (_,_) => Reload(); list.DoubleClick += (_,_) => share.PerformClick();
        share.Click += (_,_) => { if (list.SelectedItem is CaptureSource source) { Selected = source; DialogResult = DialogResult.OK; Close(); } };
        AcceptButton = share; CancelButton = cancel; Shown += (_,_) => Reload();
    }
    void Reload()
    {
        list.Items.Clear(); foreach (var source in Sources.List(monitors.Checked)) list.Items.Add(source);
        sound.Text = monitors.Checked ? "Share all system audio" : "Share app audio";
        sound.Enabled = monitors.Checked || OperatingSystem.IsWindowsVersionAtLeast(10,0,20348);
        sound.Checked = !monitors.Checked && sound.Enabled;
        help.Text = monitors.Checked ? "Includes sounds from every app on this PC." : (sound.Enabled ? "Audio from this application and its child processes; other windows of the same app may share audio." : "Application audio requires Windows 11. Video-only sharing is available.");
        if (list.Items.Count > 0) list.SelectedIndex = 0;
    }
}
internal sealed class RoomDialog : DarkDialog
{
    readonly NativeLobby lobby;
    readonly bool joining;
    readonly Func<Task> disable;
    readonly Label status = Ui.Label("",10,Ui.Muted), error = Ui.Label("",9,Color.FromArgb(235,164,141));
    readonly TextBox code = Ui.TextBox();
    readonly Button action, copy, cancel;
    readonly CheckBox stun;
    bool busy;
    internal RoomDialog(NativeLobby lobby, bool joining, string name, bool currentStun, Func<Task> disable)
    {
        this.lobby = lobby; this.joining = joining; this.disable = disable;
        Text = joining ? "Join a room" : "Invite someone"; BackColor = Ui.Background; ForeColor = Ui.Foreground; ClientSize = new Size(520,340); FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var title = Ui.Label(Text,20); title.SetBounds(24,18,470,45);
        status.SetBounds(24,72,470,48); status.Text = joining ? "Enter your friend’s room code. They approve your request." : "PeerJS Cloud delivers encrypted setup. Screen and audio stay peer-to-peer.";
        code.SetBounds(24,128,470,40); code.Font = new Font("Consolas",19); code.TextAlign = HorizontalAlignment.Center; code.MaxLength = 24; code.ReadOnly = !joining;
        code.PlaceholderText = joining ? "ABCD-EFGH-JKLM-NPQR" : "";
        stun = new CheckBox { Text = "Public STUN discovery for internet connections", ForeColor = Ui.Muted, AutoSize = true, Checked = currentStun, Location = new Point(24,128), Visible = !joining && lobby.Code.Length == 0 };
        action = Ui.Button(joining ? "Request to join" : "Create room code",true); action.SetBounds(24,214,470,42);
        copy = Ui.Button("Copy room code"); copy.SetBounds(24,174,470,34); copy.Visible = false;
        cancel = Ui.Button(joining ? "Cancel joining" : "Disable room code"); cancel.SetBounds(24,272,470,34); cancel.Visible = false;
        error.SetBounds(24,174,470,36); error.Visible = false;
        Controls.AddRange([title,status,code,stun,error,copy,action,cancel]);
        code.Visible = joining || lobby.Code.Length > 0;
        action.Click += async (_,_) =>
        {
            if (busy) return; busy = true; action.Enabled = false; error.Visible = false;
            try { if (joining) await lobby.Join(code.Text,name); else await lobby.Host(name,stun.Checked); RefreshState(); }
            catch (Exception failure) { if (copy.Visible) status.Text = failure.Message; else { error.Text = failure.Message; error.Visible = true; } }
            finally { busy = false; action.Enabled = true; }
        };
        copy.Click += (_,_) => { try { Clipboard.SetText(code.Text); copy.Text = "Copied"; } catch { code.Focus(); code.SelectAll(); } };
        cancel.Click += async (_,_) => { await disable(); Close(); };
        lobby.Changed += OnChanged; FormClosed += (_,_) => lobby.Changed -= OnChanged;
        Shown += (_,_) => RefreshState();
    }
    void OnChanged() => Ui.On(this,RefreshState);
    void RefreshState()
    {
        if (IsDisposed) return;
        if (lobby.State == "connected" || (!joining && lobby.Requests.Any(r => r.Phase == "pending"))) { Close(); return; }
        if (joining && lobby.Joining)
        {
            code.Visible = action.Visible = false; status.Text = lobby.Status; cancel.Visible = true;
            error.Visible = lobby.State == "failed"; error.Text = lobby.Status; return;
        }
        if (!joining && lobby.Code.Length > 0)
        {
            code.Text = lobby.Code; code.Visible = copy.Visible = cancel.Visible = true; stun.Visible = false;
            action.Visible = lobby.State == "offline"; action.Text = "Reconnect room code";
            status.Text = lobby.State == "offline" ? lobby.Status : "Send this code privately. Join requests appear in the room.";
        }
    }
}
