using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace HavijScreen.Native;

internal sealed class ShareOptions
{
    internal string Mode="Performance";
    internal int Height=1080,Fps=60;
    internal bool Cursor=true;
    internal static readonly int[] Resolutions=[0,480,720,1080,1440];
    internal static readonly int[] FrameRates=[15,30,60];
    internal Quality Quality
    {
        get
        {
            if(Mode=="Performance") return Quality.Performance;
            int height=Mode=="High quality"?1440:Mode=="Screenshare"?0:Height, fps=Mode=="High quality"?60:Mode=="Screenshare"?15:Fps;
            int width=height switch { 480=>854,720=>1280,1080=>1920,1440=>2560,_=>0 };
            int kbps=height switch { 480=>fps==60?2500:1500,720=>fps==60?5000:2500,1080=>fps==60?8000:5000,1440=>fps==60?12000:8000,_=>fps==60?10000:6000 };
            if(fps==15) kbps=Math.Max(1500,kbps*3/5);
            return new($"{Mode} · {(height==0?"Source":height+"p")} / {fps} fps",width,height,fps,kbps);
        }
    }
    internal string Summary=>$"{(Quality.Height==0?"Source":Quality.Height+"p")}  ·  {(Quality.PrioritizeGame?"up to ":"")}{Quality.Fps}fps";
}

internal sealed class PickerTab : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool Selected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal Glyph Symbol { get; set; }
    internal PickerTab(string text,Glyph symbol) { Text=text; Symbol=symbol; AccessibleName=text; SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Font=Ui.Font(13); Cursor=Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.Clear(Color.FromArgb(11,16,21)); g.SmoothingMode=SmoothingMode.AntiAlias;
        if(Selected) { using var path=Art.Round(new(3,3,Width-6,Height-6),Ui.Px(this,8)); using var fill=new SolidBrush(SourceTile.PickerBack); g.FillPath(fill,path); }
        int d=Ui.Px(this,16),textWidth=TextRenderer.MeasureText(Text,Font).Width,x=(Width-textWidth-d-Ui.Px(this,10))/2;
        Art.Icon(g,Symbol,new(x,(Height-d)/2,d,d),SourceTile.Muted); TextRenderer.DrawText(g,Text,Font,new Rectangle(x+d+Ui.Px(this,10),0,textWidth,Height),Color.FromArgb(225,230,234),TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(g,new Rectangle(5,5,Width-10,Height-10));
    }
}

internal sealed class SourceDialog : DarkDialog
{
    readonly PickerTab applications=new("Applications",Glyph.Window), monitors=new("Entire Screen",Glyph.Screen);
    readonly SourceGrid grid=new();
    readonly PickerGear gear=new();
    readonly StyledButton refresh=Ui.Button(""),close=Ui.Button("");
    readonly Label mode=Ui.Label("",10.5f,Color.FromArgb(230,235,238)),summary=Ui.Label("",8.5f,SourceTile.Muted),scope=Ui.Label("",7,SourceTile.Muted);
    readonly System.Windows.Forms.Timer thumbnails=new() { Interval=1000 };
    readonly ToolTip hints=new();
    readonly ShareOptions options;
    readonly IReadOnlyList<CaptureSource>? fixtureSources;
    readonly IReadOnlyList<CaptureSource>? fixtureMonitors;
    ToolStripDropDown? settings;
    bool entireScreen, appAudio=true, screenAudio;
    bool shareAudio { get => entireScreen ? screenAudio : appAudio; set { if(entireScreen) screenAudio=value; else appAudio=value; } }
    bool AudioSupported=>entireScreen||OperatingSystem.IsWindowsVersionAtLeast(10,0,20348);
    internal CaptureSource? Selected { get; private set; }
    internal bool Audio=>shareAudio;
    internal Quality SelectedQuality=>options.Quality;
    internal string SelectedMode=>options.Mode;
    internal SourceDialog(Quality? current=null,string currentMode="Custom",bool cursor=true,IReadOnlyList<CaptureSource>? fixtures=null,IReadOnlyList<CaptureSource>? monitorFixtures=null)
    {
        options=new() { Mode=current?.PrioritizeGame==true?"Performance":currentMode=="Gaming"?"High quality":currentMode is "High quality" or "Screenshare"?currentMode:"Custom", Height=current?.Height??1080,Fps=current?.Fps??60,Cursor=cursor };
        fixtureSources=fixtures; fixtureMonitors=monitorFixtures; HideHeader=true; Header.Visible=false; Text="Choose what to share";
        BackColor=Content.BackColor=SourceTile.PickerBack; ClientSize=new Size(960,Math.Min(748,Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea.Height-70)); MinimumSize=new Size(760,560);
        foreach(var button in new[]{refresh,close}) { button.Kind=ButtonKind.Text; button.ForeColor=Color.FromArgb(224,230,235); }
        gear.AccessibleName="Stream settings"; refresh.Symbol=Glyph.Refresh; refresh.AccessibleName="Refresh sources"; close.Symbol=Glyph.Close; close.AccessibleName="Cancel sharing"; close.DialogResult=DialogResult.Cancel; CancelButton=close;
        hints.SetToolTip(gear,"Stream settings"); hints.SetToolTip(refresh,"Refresh sources"); hints.SetToolTip(close,"Cancel sharing");
        mode.Font=Ui.Font(14,FontStyle.Bold); scope.Visible=false;
        Content.Controls.AddRange([applications,monitors,grid,mode,summary,scope,refresh,gear,close]);
        applications.Click+=(_,_)=>Switch(false); monitors.Click+=(_,_)=>Switch(true); refresh.Click+=(_,_)=>Reload(); close.Click+=(_,_)=>Close();
        gear.Click+=(_,_)=>OpenSettings();
        grid.Chosen+=source=>{ try { Sources.Validate(source); Selected=source with { CaptureCursor=options.Cursor }; DialogResult=DialogResult.OK; Close(); } catch(Exception error) { scope.Text=error.Message; scope.Visible=true; Reload(); } };
        Content.Resize+=(_,_)=>Arrange(); LocationChanged+=(_,_)=>grid.UpdateThumbnails();
        Shown+=(_,_)=>{ var work=Screen.FromControl(this).WorkingArea; MinimumSize=new Size(Math.Min(MinimumSize.Width,work.Width-24),Math.Min(MinimumSize.Height,work.Height-24)); Size=new Size(Math.Min(Width,work.Width-24),Math.Min(Height,work.Height-24)); Left=Math.Clamp(Left,work.Left,work.Right-Width); Top=Math.Clamp(Top,work.Top,work.Bottom-Height); Arrange(); Switch(false); thumbnails.Start(); };
        thumbnails.Tick+=(_,_)=>{ if(entireScreen&&Visible&&WindowState!=FormWindowState.Minimized) grid.RefreshMonitors(); };
        FormClosing+=(_,_)=>{ thumbnails.Stop(); grid.DetachThumbnails(); settings?.Close(); };
        FormClosed+=(_,_)=>{ thumbnails.Dispose(); hints.Dispose(); settings?.Dispose(); };
    }
    void Arrange()
    {
        int P(int n)=>Ui.Px(this,n); int w=Content.Width,h=Content.Height;
        int tabWidth=(w-P(96))/2; applications.SetBounds(P(24),P(24),tabWidth,P(42)); monitors.SetBounds(P(24)+tabWidth,P(24),tabWidth,P(42)); close.SetBounds(w-P(58),P(26),P(32),P(38));
        grid.SetBounds(P(24),P(86),w-P(40),Math.Max(P(180),h-P(184)));
        mode.SetBounds(P(24),h-P(68),P(220),P(24)); summary.SetBounds(P(24),h-P(43),P(220),P(22));
        scope.SetBounds(P(250),h-P(65),Math.Max(1,w-P(390)),P(40));
        refresh.SetBounds(w-P(119),h-P(66),P(40),P(40)); gear.SetBounds(w-P(66),h-P(66),P(42),P(40)); grid.Arrange();
    }
    void Switch(bool screen)
    {
        entireScreen=screen; applications.Selected=!screen; monitors.Selected=screen; applications.Invalidate(); monitors.Invalidate();
        if(!AudioSupported) shareAudio=false;
        scope.Visible=false;
        Reload(); UpdateSummary();
    }
    void Reload()
    {
        grid.Populate(fixtureSources==null?Sources.List(entireScreen):entireScreen?fixtureMonitors??fixtureSources:fixtureSources); if(entireScreen) grid.RefreshMonitors();
    }
    void UpdateSummary()
    {
        mode.Text=options.Mode; summary.Text=options.Summary;
    }
    void OpenSettings()
    {
        settings?.Dispose(); var panel=new StreamSettings(options,()=>shareAudio,()=>{ if(AudioSupported) shareAudio=!shareAudio; },UpdateSummary,()=>!AudioSupported?"App audio requires Windows 11":entireScreen?"Audio from all apps on this PC":"Selected app and child processes");
        settings=new ToolStripDropDown { AutoClose=true, Padding=Padding.Empty, Margin=Padding.Empty, BackColor=Color.FromArgb(28,34,42) };
        var host=new ToolStripControlHost(panel) { AutoSize=false, Size=panel.Size, Padding=Padding.Empty, Margin=Padding.Empty }; settings.Items.Add(host);
        panel.Resized+=()=>{ host.Size=panel.Size; settings.Size=panel.Size; settings.Location=PointToScreen(new Point(Width-Ui.Px(this,264),Height-Ui.Px(this,78)-panel.Height)); };
        settings.Show(this,new Point(Width-Ui.Px(this,264),Height-Ui.Px(this,78)-panel.Height));
    }
}

internal sealed class PickerGear : Button
{
    bool hover;
    internal PickerGear()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Cursor=Cursors.Hand;
        MouseEnter+=(_,_)=>{ hover=true; Invalidate(); }; MouseLeave+=(_,_)=>{ hover=false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.Clear(SourceTile.PickerBack); g.SmoothingMode=SmoothingMode.AntiAlias; using var path=Art.Round(new RectangleF(0,0,Width-1,Height-1),Ui.Px(this,8)); using var fill=new SolidBrush(hover?Color.FromArgb(48,56,66):Color.FromArgb(38,45,54)); g.FillPath(fill,path);
        int d=Ui.Px(this,22); Art.Icon(g,Glyph.Gear,new((Width-d)/2f,(Height-d)/2f,d,d),Color.FromArgb(237,240,244)); if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(g,ClientRectangle);
    }
}

internal sealed class StreamSettings : UserControl
{
    readonly ShareOptions options; readonly Func<bool> hasAudio; readonly Func<string> audioScope; readonly Action toggleAudio,changed;
    internal event Action? Resized;
    readonly Color ink=Color.FromArgb(225,230,234);
    internal StreamSettings(ShareOptions options,Func<bool> hasAudio,Action toggleAudio,Action changed,Func<string> audioScope)
    {
        this.options=options; this.hasAudio=hasAudio; this.toggleAudio=toggleAudio; this.changed=changed; this.audioScope=audioScope;
        BackColor=Color.FromArgb(28,34,42); Width=244; ShowSection("main");
    }
    void ShowSection(string section)
    {
        SuspendLayout(); foreach(Control c in Controls.Cast<Control>().ToArray()) c.Dispose(); Controls.Clear(); int P(int n)=>Ui.Px(this,n); int y=P(12); Width=P(244);
        void Header(string text) { var label=Ui.Label(text,9,SourceTile.Muted); label.SetBounds(P(16),y,Width-P(32),P(26)); Controls.Add(label); y+=P(28); }
        void Line() { var panel=new Panel { BackColor=SourceTile.Line }; panel.SetBounds(P(16),y+P(5),Width-P(32),1); Controls.Add(panel); y+=P(12); }
        void Row(string text,Action action,string description="",bool selected=false,bool check=false,bool arrow=false)
        {
            var button=new SettingsRow(text,description,selected,check,arrow); button.SetBounds(P(8),y,Width-P(16),P(description.Length>0?46:36)); y+=button.Height; button.Click+=(_,_)=>action(); Controls.Add(button);
        }
        void Apply() { changed(); ShowSection(section); }
        if(section=="main")
        {
            Header("Stream Mode"); Row("Performance",()=>{ options.Mode="Performance"; Apply(); },"Lower capture load (720p, up to 30fps)",options.Mode=="Performance"); Row("High quality",()=>{ options.Mode="High quality"; Apply(); },"More detail (1440p, 60fps)",options.Mode=="High quality"); Row("Screenshare",()=>{ options.Mode="Screenshare"; Apply(); },"Clearer text (Source, 15fps)",options.Mode=="Screenshare"); Row("Custom",()=>{ options.Mode="Custom"; Apply(); },selected:options.Mode=="Custom");
            Line(); Row("Screen Resolution",()=>ShowSection("resolution"),arrow:true); Row("Frame Rate",()=>ShowSection("fps"),arrow:true); Line(); Row("Mute stream audio",()=>{ toggleAudio(); Apply(); },description:audioScope(),selected:!hasAudio(),check:true); Row("Advanced",()=>ShowSection("advanced"),arrow:true);
        }
        else
        {
            Row("‹  Stream settings",()=>ShowSection("main")); Line();
            if(section=="resolution") { Header("Screen Resolution"); foreach(int value in ShareOptions.Resolutions) Row(value==0?"Source":value+"p",()=>{ int fps=options.Quality.Fps; options.Mode="Custom"; options.Height=value; options.Fps=fps; Apply(); },selected:options.Quality.Height==value); }
            else if(section=="fps") { Header("Frame Rate"); foreach(int value in ShareOptions.FrameRates) Row(value+" fps",()=>{ int height=options.Quality.Height; options.Mode="Custom"; options.Height=height; options.Fps=value; Apply(); },selected:options.Quality.Fps==value); }
            else { Header("Advanced"); Row("Capture cursor",()=>{ options.Cursor=!options.Cursor; Apply(); },selected:options.Cursor,check:true); Line(); Row("Reset to Performance",()=>{ options.Mode="Performance"; options.Cursor=true; Apply(); }); }
        }
        Height=y+P(12); ResumeLayout(); Resized?.Invoke();
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p=new Pen(SourceTile.Line); e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1); }
    sealed class SettingsRow : Button
    {
        readonly string description; readonly bool selected,check,arrow; bool hover;
        internal SettingsRow(string text,string description,bool selected,bool check,bool arrow)
        {
            Text=text; AccessibleName=description.Length>0?text+", "+description:text; this.description=description; this.selected=selected; this.check=check; this.arrow=arrow;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Font=Ui.Font(12); Cursor=Cursors.Hand;
            MouseEnter+=(_,_)=>{ hover=true; Invalidate(); }; MouseLeave+=(_,_)=>{ hover=false; Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics; g.Clear(hover?Color.FromArgb(41,48,58):Color.FromArgb(28,34,42)); g.SmoothingMode=SmoothingMode.AntiAlias; int P(int n)=>Ui.Px(this,n); int y=description.Length>0?P(5):0;
            TextRenderer.DrawText(g,Text,Font,new Rectangle(P(8),y,Width-P(40),description.Length>0?P(22):Height),Color.FromArgb(225,230,234),TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
            if(description.Length>0) { using var f=Ui.Font(10); TextRenderer.DrawText(g,description,f,new Rectangle(P(8),P(25),Width-P(32),P(18)),SourceTile.Muted,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine); }
            var r=new Rectangle(Width-P(25),(Height-P(17))/2,P(17),P(17));
            if(arrow) Art.Icon(g,Glyph.ChevronRight,new(r.X,r.Y,r.Width,r.Height),SourceTile.Muted);
            else if(check||Text is "Performance" or "High quality" or "Screenshare" or "Custom"||Text.EndsWith("p")||Text.EndsWith("fps")||Text=="Source")
            {
                using var pen=new Pen(SourceTile.Muted); if(check) g.DrawRectangle(pen,r); else g.DrawEllipse(pen,r);
                if(selected) { using var fill=new SolidBrush(Color.FromArgb(95,104,236)); if(check) { g.FillRectangle(fill,r); using var white=new Pen(Color.White,1.5f); g.DrawLines(white,[new(r.X+P(3),r.Y+P(8)),new(r.X+P(7),r.Y+P(12)),new(r.X+P(14),r.Y+P(4))]); } else { g.FillEllipse(fill,r); using var white=new SolidBrush(Color.White); g.FillEllipse(white,r.X+P(5),r.Y+P(5),P(7),P(7)); } }
            }
            if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(g,ClientRectangle);
        }
    }
}
