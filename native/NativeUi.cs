using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace HavijScreen.Native;

internal static class Ui
{
    internal static readonly Color Background=Color.FromArgb(17,19,19), Surface=Color.FromArgb(23,28,23), Field=Color.FromArgb(27,30,27), Line=Color.FromArgb(55,61,54), Divider=Color.FromArgb(42,45,43), Muted=Color.FromArgb(140,151,136), Foreground=Color.FromArgb(232,233,230), Accent=Color.FromArgb(234,167,121), Disabled=Color.FromArgb(105,115,98);
    internal static int Px(Control control,int pixels)=>(int)Math.Round(pixels*control.DeviceDpi/96f);
    internal static Font Font(float pixels,FontStyle style=FontStyle.Regular)=>new("Segoe UI",pixels*.75f,style,GraphicsUnit.Point);
    internal static StyledButton Button(string text,bool primary=false)=>new(text,primary?ButtonKind.Primary:ButtonKind.Secondary);
    internal static Label Label(string text,float size=9,Color? color=null)=>new() { Text=text, AutoSize=false, Height=25, ForeColor=color??Foreground, Font=new Font("Segoe UI",size), BackColor=Color.Transparent, TextAlign=ContentAlignment.MiddleLeft, UseCompatibleTextRendering=false };
    internal static CaptionLabel Caption(string text)=>new(text);
    internal static TextField TextBox(string text="")=>new(text);
    internal static void On(Control control,Action action) { if(control.IsDisposed||!control.IsHandleCreated) return; if(control.InvokeRequired) { try { control.BeginInvoke(action); } catch(InvalidOperationException) {} } else action(); }
    internal static Image Carrot() { using var stream=typeof(Ui).Assembly.GetManifestResourceStream("HavijScreen.Carrot.png")!; using var original=Image.FromStream(stream); return new Bitmap(original); }
}
internal class NativeForm : Form
{
    protected readonly Panel Header=new();
    protected readonly Panel Content=new();
    readonly Image carrot=Ui.Carrot();
    readonly StyledButton minimize=new("",ButtonKind.Title) { Symbol=Glyph.Minimize }, maximize=new("",ButtonKind.Title) { Symbol=Glyph.Maximize }, close=new("",ButtonKind.Close) { Symbol=Glyph.Close };
    readonly bool dialog;
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr handle,int attribute,ref int value,int size);
    [DllImport("user32")] static extern bool ReleaseCapture();
    [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMax { public PointNative Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack; }
    protected override CreateParams CreateParams { get { var p=base.CreateParams; if(!dialog) p.Style|=0x40000|0x20000|0x10000|0x80000; return p; } }
    internal NativeForm(bool dialog=false)
    {
        this.dialog=dialog; AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        Text="HavijScreen"; BackColor=Header.BackColor=Content.BackColor=Ui.Background; ForeColor=Ui.Foreground; Font=Ui.Font(12);
        FormBorderStyle=FormBorderStyle.None; Padding=Padding.Empty; MinimumSize=new Size(1020,700); ClientSize=new Size(1380,870); StartPosition=FormStartPosition.CenterScreen;
        DoubleBuffered=true; Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        minimize.AccessibleName="Minimize"; maximize.AccessibleName="Maximize"; close.AccessibleName=dialog?"Close dialog":"Close HavijScreen";
        Header.Controls.AddRange([minimize,maximize,close]); minimize.Visible=maximize.Visible=!dialog;
        minimize.Click+=(_,_)=>WindowState=FormWindowState.Minimized; maximize.Click+=(_,_)=>ToggleMaximize(); close.Click+=(_,_)=>Close();
        Header.Paint+=(_,e)=>
        {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; float s=DeviceDpi/96f;
            if(dialog) { g.DrawImage(carrot,new Rectangle(Ui.Px(this,22),Ui.Px(this,13),Ui.Px(this,24),Ui.Px(this,29))); using var font=Ui.Font(15,FontStyle.Bold); TextRenderer.DrawText(g,Text,font,new Rectangle(Ui.Px(this,57),0,Header.Width-Ui.Px(this,120),Header.Height),Ui.Foreground,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine); }
            else
            {
                g.DrawImage(carrot,new Rectangle(Ui.Px(this,30),Ui.Px(this,13),Ui.Px(this,38),Ui.Px(this,44)));
                using var font=Ui.Font(26,FontStyle.Bold); TextRenderer.DrawText(g,"HavijScreen",font,new Rectangle(Ui.Px(this,80),0,Ui.Px(this,176),Header.Height),Ui.Foreground,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
                var badge=new RectangleF(265*s,25*s,43*s,23*s); using var path=Art.Round(badge,3*s); using var pen=new Pen(Ui.Line,s); g.DrawPath(pen,path); using var small=Ui.Font(9); TextRenderer.DrawText(g,typeof(NativeForm).Assembly.GetName().Version?.ToString(3)??"",small,Rectangle.Round(badge),Ui.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
            }
            using var line=new Pen(Ui.Divider); g.DrawLine(line,0,Header.Height-1,Header.Width,Header.Height-1);
        };
        Header.MouseDown+=(_,e)=>{ if(e.Button==MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero); } };
        Header.DoubleClick+=(_,_)=>{ if(!dialog) ToggleMaximize(); };
        Controls.Add(Content); Controls.Add(Header);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if(Header==null||Content==null) return;
        int height=Ui.Px(this,dialog?56:72), width=Ui.Px(this,48), edge=!dialog&&WindowState==FormWindowState.Normal?Ui.Px(this,6):0;
        // Keep the frame's hit area clear of child HWNDs so Windows can resize
        // the borderless window using its standard non-client behavior.
        Header.SetBounds(edge,edge,ClientSize.Width-2*edge,height); Content.SetBounds(edge,height+edge,ClientSize.Width-2*edge,Math.Max(0,ClientSize.Height-height-2*edge));
        close.SetBounds(Header.Width-width,0,width,height); maximize.SetBounds(Header.Width-width*2,0,width,height); minimize.SetBounds(Header.Width-width*3,0,width,height);
    }
    void ToggleMaximize()=>WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); int dark=1,rounded=2,none=unchecked((int)0xfffffffe); DwmSetWindowAttribute(Handle,20,ref dark,4); DwmSetWindowAttribute(Handle,33,ref rounded,4); DwmSetWindowAttribute(Handle,34,ref none,4); }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg is 0x83 or 0x85) { message.Result=IntPtr.Zero; return; }
        if(message.Msg==0x86) { message.Result=new IntPtr(1); return; }
        if(message.Msg==0x24&&!dialog)
        {
            base.WndProc(ref message); var screen=Screen.FromHandle(Handle); var bounds=Marshal.PtrToStructure<MinMax>(message.LParam);
            bounds.MaxPosition.X=screen.WorkingArea.Left-screen.Bounds.Left; bounds.MaxPosition.Y=screen.WorkingArea.Top-screen.Bounds.Top;
            bounds.MaxSize.X=screen.WorkingArea.Width; bounds.MaxSize.Y=screen.WorkingArea.Height; Marshal.StructureToPtr(bounds,message.LParam,false); return;
        }
        base.WndProc(ref message);
        if(message.Msg==0x84&&!dialog&&WindowState==FormWindowState.Normal)
        {
            long position=message.LParam.ToInt64(); var p=PointToClient(new Point((short)(position&0xffff),(short)((position>>16)&0xffff))); int edge=Ui.Px(this,6);
            bool left=p.X<edge,right=p.X>=ClientSize.Width-edge,top=p.Y<edge,bottom=p.Y>=ClientSize.Height-edge;
            if(top) message.Result=new IntPtr(left?13:right?14:12); else if(bottom) message.Result=new IntPtr(left?16:right?17:15); else if(left) message.Result=new IntPtr(10); else if(right) message.Result=new IntPtr(11);
        }
    }
    protected override void Dispose(bool disposing) { if(disposing) carrot.Dispose(); base.Dispose(disposing); }
}
internal class DarkDialog : NativeForm
{
    internal DarkDialog():base(true) { MinimumSize=new Size(520,380); StartPosition=FormStartPosition.CenterParent; ShowInTaskbar=false; }
}
internal sealed class SourceDialog : DarkDialog
{
    readonly StyledButton windows=Ui.Button("Application",true), monitors=Ui.Button("Entire screen"), share=Ui.Button("Start sharing",true), cancel=Ui.Button("Cancel"), refresh=Ui.Button("Refresh");
    readonly SourceList list=new();
    readonly ThemedCheckBox sound=new() { Text="Share app audio", Checked=true };
    readonly Label help=Ui.Label("",8,Ui.Muted);
    bool entireScreen;
    internal CaptureSource? Selected { get; private set; }
    internal bool Audio=>sound.Checked;
    internal SourceDialog()
    {
        Text="Choose what to share"; ClientSize=new Size(700,602); MinimumSize=new Size(560,480);
        windows.Symbol=Glyph.Window; monitors.Symbol=Glyph.Screen; share.Symbol=Glyph.Screen; refresh.Symbol=Glyph.Refresh; refresh.Kind=ButtonKind.Text; refresh.ForeColor=Ui.Muted;
        cancel.DialogResult=DialogResult.Cancel; CancelButton=cancel; AcceptButton=share;
        Content.Controls.AddRange([windows,monitors,list,sound,help,refresh,cancel,share]);
        windows.Click+=(_,_)=>{ entireScreen=false; Reload(); }; monitors.Click+=(_,_)=>{ entireScreen=true; Reload(); }; refresh.Click+=(_,_)=>Reload();
        list.SelectedIndexChanged+=(_,_)=>share.Enabled=list.SelectedItem!=null;
        list.DoubleClick+=(_,_)=>share.PerformClick();
        share.Click+=(_,_)=>{ if(list.SelectedItem is CaptureSource source) { Selected=source; DialogResult=DialogResult.OK; Close(); } };
        Content.Resize+=(_,_)=>Arrange(); Shown+=(_,_)=>{ Arrange(); Reload(); };
    }
    void Arrange()
    {
        int P(int n)=>Ui.Px(this,n); int w=Content.ClientSize.Width,h=Content.ClientSize.Height;
        windows.SetBounds(P(24),P(20),P(156),P(38)); monitors.SetBounds(P(188),P(20),P(156),P(38));
        list.SetBounds(P(20),P(78),w-P(40),Math.Max(P(90),h-P(254)));
        sound.SetBounds(P(24),h-P(159),w-P(48),P(30)); help.SetBounds(P(24),h-P(125),w-P(48),P(38));
        refresh.SetBounds(P(18),h-P(68),P(110),P(42)); cancel.SetBounds(w-P(308),h-P(68),P(112),P(42)); share.SetBounds(w-P(184),h-P(68),P(160),P(42));
    }
    void Reload()
    {
        windows.Kind=entireScreen?ButtonKind.Secondary:ButtonKind.Primary; monitors.Kind=entireScreen?ButtonKind.Primary:ButtonKind.Secondary; windows.Invalidate(); monitors.Invalidate();
        list.Items.Clear(); foreach(var source in Sources.List(entireScreen)) list.Items.Add(source);
        sound.Text=entireScreen?"Share all system audio":"Share app audio"; sound.Enabled=entireScreen||OperatingSystem.IsWindowsVersionAtLeast(10,0,20348); sound.Checked=!entireScreen&&sound.Enabled;
        help.Text=entireScreen?"Includes sounds from every app on this PC.":sound.Enabled?"Audio from the selected app and its child processes. Other windows of the same app may share audio.":"App audio requires Windows 11. You can share video without audio.";
        share.Enabled=list.Items.Count>0; if(list.Items.Count>0) list.SelectedIndex=0;
    }
}
internal sealed class SourceList : ListBox
{
    [DllImport("uxtheme",CharSet=CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd,string app,string? id);
    internal SourceList() { DrawMode=DrawMode.OwnerDrawFixed; BorderStyle=BorderStyle.None; BackColor=Ui.Background; ForeColor=Ui.Foreground; Font=Ui.Font(12); IntegralHeight=false; AccessibleName="Capture sources"; }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight=Ui.Px(this,52); SetWindowTheme(Handle,"DarkMode_Explorer",null); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if(e.Index<0||Items[e.Index] is not CaptureSource source) return;
        var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; float s=DeviceDpi/96f; bool selected=(e.State&DrawItemState.Selected)!=0;
        using(var b=new SolidBrush(Ui.Background)) g.FillRectangle(b,e.Bounds);
        var r=new RectangleF(e.Bounds.X+4*s,e.Bounds.Y+3*s,e.Bounds.Width-8*s,e.Bounds.Height-6*s); using var path=Art.Round(r,5*s); using var fill=new SolidBrush(selected?Color.FromArgb(39,49,32):Ui.Field); using var border=new Pen(selected?Color.FromArgb(108,132,82):Ui.Line); g.FillPath(fill,path); g.DrawPath(border,path);
        Art.Icon(g,source.Monitor?Glyph.Screen:Glyph.Window,new(r.X+14*s,r.Y+13*s,20*s,20*s),selected?Ui.Accent:Ui.Muted);
        TextRenderer.DrawText(g,source.Name,Font,new Rectangle((int)(r.X+48*s),(int)r.Y,(int)(r.Width-78*s),(int)r.Height),Ui.Foreground,TextFormatFlags.NoPadding|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
        if(selected) { using var p=new Pen(Ui.Accent,1.6f*s); g.DrawLines(p,new PointF[]{new(r.Right-24*s,r.Y+23*s),new(r.Right-20*s,r.Y+27*s),new(r.Right-13*s,r.Y+18*s)}); }
    }
}
internal sealed class RoomDialog : DarkDialog
{
    readonly NativeLobby lobby;
    readonly bool joining;
    readonly Func<Task> disable;
    readonly Label status=Ui.Label("",8.5f,Ui.Muted), error=Ui.Label("",8,Color.FromArgb(235,164,141));
    readonly CaptionLabel caption=Ui.Caption("ROOM CODE");
    readonly TextField code=Ui.TextBox();
    readonly StyledButton action,copy,cancel;
    readonly ThemedCheckBox stun;
    bool busy;
    internal RoomDialog(NativeLobby lobby,bool joining,string name,bool currentStun,Func<Task> disable)
    {
        this.lobby=lobby; this.joining=joining; this.disable=disable;
        Text=joining?"Join a room":"Invite someone"; ClientSize=new Size(540,394); MinimumSize=new Size(540,394);
        status.Text=joining?"Enter your friend's room code. They approve your request.":"Share a room code with your friend. You choose who joins.";
        code.Font=new Font("Consolas",19); code.TextAlign=HorizontalAlignment.Center; code.MaxLength=24; code.ReadOnly=!joining; code.AccessibleName=joining?"Friend's room code":"Room code"; code.PlaceholderText=joining?"ABCD-EFGH-JKLM-NPQR":"";
        stun=new ThemedCheckBox { Text="Public STUN discovery for internet connections", Checked=currentStun, Visible=!joining&&lobby.Code.Length==0 };
        action=Ui.Button(joining?"Request to join":"Create room code",true); action.Symbol=Glyph.Link; action.TrailingArrow=true;
        copy=Ui.Button("Copy room code"); copy.Visible=false;
        cancel=Ui.Button(joining?"Cancel joining":"Disable room code"); cancel.Kind=ButtonKind.Text; cancel.ForeColor=Ui.Muted; cancel.Visible=false;
        error.Visible=false; Content.Controls.AddRange([status,caption,code,stun,error,copy,action,cancel]); code.Visible=caption.Visible=joining||lobby.Code.Length>0;
        action.Click+=async(_,_)=>
        {
            if(busy) return; busy=true; action.Enabled=false; error.Visible=false;
            try { if(joining) await lobby.Join(code.Text,name); else await lobby.Host(name,stun.Checked); RefreshState(); }
            catch(Exception failure) { if(copy.Visible) status.Text=failure.Message; else { error.Text=failure.Message; error.Visible=true; } }
            finally { busy=false; if(!IsDisposed) action.Enabled=true; }
        };
        copy.Click+=(_,_)=>{ try { Clipboard.SetText(code.Text); copy.Text="Copied"; } catch { code.Focus(); code.SelectAll(); } };
        cancel.Click+=async(_,_)=>{ await disable(); Close(); };
        lobby.Changed+=OnChanged; FormClosed+=(_,_)=>lobby.Changed-=OnChanged;
        Content.Resize+=(_,_)=>Arrange(); Shown+=(_,_)=>{ Arrange(); RefreshState(); };
    }
    void Arrange()
    {
        int P(int n)=>Ui.Px(this,n); int w=Content.ClientSize.Width;
        status.SetBounds(P(26),P(22),w-P(52),P(46)); caption.SetBounds(P(26),P(83),w-P(52),P(22)); code.SetBounds(P(26),P(114),w-P(52),P(48));
        stun.SetBounds(P(26),P(92),w-P(52),P(40)); error.SetBounds(P(26),P(168),w-P(52),P(35)); copy.SetBounds(P(26),P(181),w-P(52),P(42));
        action.SetBounds(P(26),P(213),w-P(52),P(44)); cancel.SetBounds(P(26),P(270),w-P(52),P(36));
    }
    void OnChanged()=>Ui.On(this,RefreshState);
    void RefreshState()
    {
        if(IsDisposed) return;
        if(lobby.State=="connected"||!joining&&lobby.Requests.Any(r=>r.Phase=="pending")) { Close(); return; }
        if(joining&&lobby.Joining) { code.Visible=caption.Visible=action.Visible=false; status.Text=lobby.Status; cancel.Visible=true; error.Visible=lobby.State=="failed"; error.Text=lobby.Status; return; }
        if(!joining&&lobby.Code.Length>0)
        {
            code.Text=lobby.Code; code.Visible=caption.Visible=copy.Visible=cancel.Visible=true; stun.Visible=false;
            action.Visible=lobby.State=="offline"; action.Text="Reconnect room code"; if(action.Visible) { copy.Visible=false; }
            status.Text=lobby.State=="offline"?lobby.Status:"Send this code privately. Join requests appear in your room.";
        }
    }
}
