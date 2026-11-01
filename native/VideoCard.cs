using System.ComponentModel;

namespace HavijScreen.Native;

internal sealed class VideoCard : Panel
{
    readonly NativePeer peer;
    readonly Panel surface = new() { Dock=DockStyle.Fill, BackColor=Color.Black };
    readonly Panel video = new() { BackColor=Color.Black };
    readonly Panel caption = new() { BackColor=Ui.Background };
    readonly Label name, details=Ui.Label("",8,Ui.Muted);
    readonly Button sound=Ui.Button("Enable sound"), fullscreen=Ui.Button("Fullscreen"), popout=Ui.Button("Pop out"), stop=Ui.Button("Stop watching");
    readonly StyledButton more=Ui.Button("");
    readonly ContextMenuStrip options=new() { BackColor=Ui.Field, ForeColor=Ui.Foreground, ShowImageMargin=false };
    readonly TrackBar volumeSlider=new() { Minimum=0, Maximum=100, Value=100, TickStyle=TickStyle.None, BackColor=Ui.Field, AccessibleName="Stream volume" };
    readonly System.Windows.Forms.Timer activity=new() { Interval=250 };
    FullscreenVideoForm? fullscreenWindow;
    bool active, popped, showDetails, controlsShown=true;
    Point lastPointer;
    long lastActivity=Environment.TickCount64, statsTime, lastFrames;
    internal IntPtr VideoHandle=>video.Handle;
    internal bool Sound { get; private set; }
    internal bool IsFullscreen=>fullscreenWindow?.Fullscreen==true;
    internal bool IsDetached=>fullscreenWindow!=null;
    internal bool ControlsVisible=>caption.Visible;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Active { get=>active; set { active=value; activity.Enabled=value; if(!value) ReturnToRoom(); } }
    internal event Action? Stop;
    internal event Action? Docked;
    internal VideoCard(NativePeer peer)
    {
        this.peer=peer; name=Ui.Label(peer.Name,8); name.AutoEllipsis=true;
        BackColor=Color.Black; Margin=new Padding(4); MinimumSize=new Size(200,180);
        stop.Font=sound.Font=fullscreen.Font=popout.Font=Ui.Font(10);
        more.Symbol=Glyph.Gear; more.AccessibleName="Stream options";
        caption.Resize+=(_,_)=>LayoutCaption();
        surface.Resize+=(_,_)=>LayoutSurface();
        DpiChangedAfterParent+=(_,_)=>LayoutSurface();
        caption.Controls.AddRange([name,sound,fullscreen,popout,more,stop]);
        details.AutoEllipsis=true; details.Height=Ui.Px(this,32); details.Padding=new Padding(10,0,10,0); details.Visible=false;
        surface.Controls.AddRange([video,details,caption]); Controls.Add(surface);
        var volumePanel=new Panel { Size=new Size(230,76),BackColor=Ui.Field };
        var volumeLabel=Ui.Label("Volume · 100%",9); volumeLabel.SetBounds(10,3,210,26); volumeSlider.SetBounds(4,31,222,40);
        volumePanel.Controls.AddRange([volumeLabel,volumeSlider]);
        options.Items.Add(new ToolStripControlHost(volumePanel) { Margin=Padding.Empty,Padding=Padding.Empty,AutoSize=false,Size=volumePanel.Size });
        var connection=new ToolStripMenuItem("Connection details") { CheckOnClick=true };
        connection.CheckedChanged+=(_,_)=>{ showDetails=connection.Checked; details.Visible=showDetails && controlsShown; LayoutSurface(); UpdateDetails(); };
        options.Items.Add(connection);
        volumeSlider.ValueChanged+=(_,_)=>{ peer.SetVolume(volumeSlider.Value); volumeLabel.Text=$"Volume · {volumeSlider.Value}%"; Touch(); };
        more.Click+=(_,_)=>{ Touch(); options.Show(more,new Point(0,more.Height)); };
        stop.Click+=(_,_)=>{ ReturnToRoom(); Stop?.Invoke(); };
        sound.Click+=(_,_)=>{ Touch(); Sound=!Sound; sound.Text=Sound?"Mute sound":"Enable sound"; peer.SetView(VideoHandle,Sound); };
        fullscreen.Click+=(_,_)=>ToggleFullscreen(); popout.Click+=(_,_)=>TogglePopout();
        video.HandleCreated+=(_,_)=>{ if(Active) peer.SetView(VideoHandle,Sound); };
        peer.ConnectionChanged+=ConnectionChanged;
        activity.Tick+=(_,_)=>
        {
            UpdateIdle(Environment.TickCount64,Cursor.Position,fullscreenWindow?.ContainsFocus==true);
            if(showDetails && Environment.TickCount64-statsTime>=1000) UpdateDetails();
        };
    }
    internal void UpdateIdle(long now,Point pointer,bool focused)
    {
        if(pointer!=lastPointer) { lastPointer=pointer; if(IsFullscreen && fullscreenWindow!.Bounds.Contains(pointer)) Touch(); }
        bool interacting=options.Visible || (caption.Visible && caption.RectangleToScreen(caption.ClientRectangle).Contains(pointer)) || !focused;
        if(IsFullscreen && !interacting && now-lastActivity>3000) SetControls(false);
    }

    void ConnectionChanged()=>Ui.On(this,()=>{ name.Text=peer.Reconnecting?peer.Name+" · Reconnecting…":peer.Name; Touch(); UpdateDetails(); });
    void LayoutSurface()
    {
        int width=surface.ClientSize.Width,height=surface.ClientSize.Height;
        int bar=controlsShown?Ui.Px(this,width>=Ui.Px(this,640)?46:width<Ui.Px(this,360)?148:108):0;
        int info=controlsShown&&showDetails?Ui.Px(this,32):0;
        caption.SetBounds(0,Math.Max(0,height-bar),width,bar);
        details.SetBounds(0,Math.Max(0,height-bar-info),width,info);
        video.SetBounds(0,0,width,Math.Max(0,height-bar-info));
    }
    void LayoutCaption()
    {
        int P(int n)=>Ui.Px(caption,n); int w=caption.Width;
        bool wide=w>=P(640), small=w<P(360);
        name.SetBounds(P(10),0,Math.Max(1,w-P(wide?482:20)),P(wide?46:27));
        Button[] buttons=[sound,fullscreen,popout,more,stop];
        if(wide)
        {
            int x=w-P(468); int[] widths=[94,108,76,32,106];
            for(int i=0;i<buttons.Length;i++) { buttons[i].SetBounds(x,P(8),P(widths[i]),P(30)); x+=P(widths[i]+8); }
        }
        else
        {
            int columns=small?2:3, width=Math.Max(1,(w-P(20+8*(columns-1)))/columns);
            for(int i=0;i<buttons.Length;i++) buttons[i].SetBounds(P(10)+(i%columns)*(width+P(8)),P(32+(i/columns)*38),width,P(30));
        }
    }
    internal void Touch() { lastActivity=Environment.TickCount64; SetControls(true); }
    void SetControls(bool visible)
    {
        if(controlsShown==visible) return;
        controlsShown=visible; caption.Visible=visible; details.Visible=visible && showDetails; LayoutSurface();
        // Do not retain keyboard focus in a hidden control.
        if(!visible && caption.ContainsFocus) video.Focus();
    }
    void UpdateDetails()
    {
        if(!showDetails) return;
        long now=Environment.TickCount64, count=peer.VideoFrames;
        double fps=statsTime==0?0:(count-lastFrames)*1000.0/Math.Max(1,now-statsTime);
        statsTime=now; lastFrames=count;
        details.Text=$"{(peer.Reconnecting?"Reconnecting":"Encrypted peer-to-peer")}  ·  {fps:F0} decoded fps  ·  Volume {peer.VolumePercent}%";
    }
    void Detach(bool asFullscreen)
    {
        popped=!asFullscreen;
        var window=new FullscreenVideoForm { Text=peer.Name+" — HavijScreen" };
        var area=Screen.FromControl(this).WorkingArea;
        window.Location=new Point(area.Left+Math.Max(0,(area.Width-window.Width)/2),area.Top+Math.Max(0,(area.Height-window.Height)/2));
        fullscreenWindow=window;
        window.SetFullscreen(asFullscreen);
        window.VideoHost.Controls.Add(surface);
        window.ToggleRequested+=ToggleFullscreen;
        window.Activity+=Touch;
        window.FormClosing+=(_,_)=>
        {
            Controls.Add(surface); fullscreenWindow=null; popped=false; fullscreen.Text="Fullscreen"; popout.Text="Pop out"; Touch();
            if(Active && !IsDisposed) { peer.SetView(VideoHandle,Sound); Docked?.Invoke(); }
        };
        window.Show(); peer.SetView(VideoHandle,Sound); UpdateButtons(); Touch();
    }
    void UpdateButtons() { fullscreen.Text=IsFullscreen?"Exit fullscreen":"Fullscreen"; popout.Text=IsDetached?"Return to room":"Pop out"; }
    internal void TogglePopout()
    {
        if(IsDetached) { ReturnToRoom(); return; }
        if(Active && !IsDisposed) Detach(false);
    }
    internal void ToggleFullscreen()
    {
        if(!Active || IsDisposed) return;
        if(fullscreenWindow==null) { Detach(true); return; }
        if(IsFullscreen && !popped) { ReturnToRoom(); return; }
        fullscreenWindow.SetFullscreen(!IsFullscreen); UpdateButtons(); Touch(); peer.SetView(VideoHandle,Sound);
    }
    internal void ExitFullscreen() { if(IsFullscreen) ToggleFullscreen(); }
    internal void ReturnToRoom()=>fullscreenWindow?.Close();
    protected override void Dispose(bool disposing)
    {
        if(disposing) { active=false; peer.ConnectionChanged-=ConnectionChanged; activity.Dispose(); options.Dispose(); ReturnToRoom(); }
        base.Dispose(disposing);
    }
}

internal sealed class FullscreenVideoForm : NativeForm
{
    internal Panel VideoHost=>Content;
    internal bool Fullscreen { get; private set; }
    Rectangle normalBounds;
    internal event Action? ToggleRequested;
    internal event Action? Activity;
    internal FullscreenVideoForm()
    {
        MinimumSize=new Size(520,380); ClientSize=new Size(960,640); StartPosition=FormStartPosition.Manual;
        Bounds=new Rectangle(Screen.PrimaryScreen!.WorkingArea.Location+new Size(70,70),Size);
        normalBounds=Bounds; BackColor=Color.Black; ShowInTaskbar=true;
    }
    internal void SetFullscreen(bool value)
    {
        if(Fullscreen==value) return;
        if(value) { normalBounds=WindowState==FormWindowState.Normal?Bounds:RestoreBounds; WindowState=FormWindowState.Normal; }
        Fullscreen=value; HideHeader=value; Header.Visible=!value;
        Bounds=value?Screen.FromControl(this).Bounds:normalBounds; PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e); if(Fullscreen && Content!=null) Content.Bounds=ClientRectangle;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Activity?.Invoke();
        if(keyData==Keys.F11 || (keyData==Keys.Escape && Fullscreen)) { ToggleRequested?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg,keyData);
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if(Fullscreen && message.Msg==0x84) message.Result=new IntPtr(1);
    }
}
