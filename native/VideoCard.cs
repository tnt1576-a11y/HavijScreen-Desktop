using System.ComponentModel;

namespace HavijScreen.Native;

internal sealed class VideoCard : Panel
{
    readonly NativePeer peer;
    readonly Panel surface = new() { Dock=DockStyle.Fill, BackColor=Color.Black };
    readonly Panel video = new() { Dock=DockStyle.Fill, BackColor=Color.Black };
    readonly Button sound = Ui.Button("Enable sound"), fullscreen = Ui.Button("Fullscreen");
    FullscreenVideoForm? fullscreenWindow;
    bool active;
    internal IntPtr VideoHandle => video.Handle;
    internal bool Sound { get; private set; }
    internal bool IsFullscreen => fullscreenWindow!=null;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Active { get=>active; set { active=value; if(!value) ExitFullscreen(); } }
    internal event Action? Stop;
    internal VideoCard(NativePeer peer)
    {
        this.peer=peer; BackColor=Color.Black; Margin=new Padding(4); MinimumSize=new Size(200,180);
        var caption=new Panel { Dock=DockStyle.Bottom, Height=46, BackColor=Ui.Background };
        var name=Ui.Label(peer.Name,8); name.AutoEllipsis=true;
        var stop=Ui.Button("Stop watching"); stop.Font=sound.Font=fullscreen.Font=Ui.Font(10);
        caption.Resize+=(_,_)=>
        {
            int P(int n)=>Ui.Px(caption,n);
            bool narrow=caption.Width<P(500), stacked=caption.Width<P(340);
            caption.Height=P(stacked?114:narrow?76:46);
            if(stacked)
            {
                name.SetBounds(P(10),0,Math.Max(1,caption.Width-P(20)),P(27));
                int w=Math.Max(1,(caption.Width-P(28))/2);
                sound.SetBounds(P(10),P(32),w,P(30)); fullscreen.SetBounds(P(18)+w,P(32),w,P(30));
                stop.SetBounds(P(10),P(70),Math.Max(1,caption.Width-P(20)),P(30));
            }
            else if(narrow)
            {
                name.SetBounds(P(10),0,caption.Width-P(20),P(27)); int w=(caption.Width-P(36))/3;
                sound.SetBounds(P(10),P(32),w,P(30)); fullscreen.SetBounds(P(18)+w,P(32),w,P(30)); stop.SetBounds(P(26)+2*w,P(32),w,P(30));
            }
            else
            {
                name.SetBounds(P(10),0,Math.Max(1,caption.Width-P(358)),caption.Height);
                sound.SetBounds(caption.Width-P(344),P(8),P(102),P(30));
                fullscreen.SetBounds(caption.Width-P(234),P(8),P(114),P(30));
                stop.SetBounds(caption.Width-P(112),P(8),P(102),P(30));
            }
        };
        caption.Controls.AddRange([name,sound,fullscreen,stop]); surface.Controls.AddRange([video,caption]); Controls.Add(surface);
        stop.Click+=(_,_)=>{ ExitFullscreen(); Stop?.Invoke(); };
        sound.Click+=(_,_)=>{ Sound=!Sound; sound.Text=Sound?"Mute sound":"Enable sound"; peer.SetView(VideoHandle,Sound); };
        fullscreen.Click+=(_,_)=>ToggleFullscreen();
        video.HandleCreated+=(_,_)=>{ if(Active) peer.SetView(VideoHandle,Sound); };
    }
    internal void ToggleFullscreen()
    {
        if(IsFullscreen) { ExitFullscreen(); return; }
        if(!Active || IsDisposed) return;
        var owner=FindForm();
        var window=new FullscreenVideoForm { Text=peer.Name+" — HavijScreen", Bounds=Screen.FromControl(this).Bounds };
        fullscreenWindow=window; fullscreen.Text="Exit fullscreen";
        // Reparent the existing surface, preserving the decoder, sound choice,
        // and room subscription. The card remains in the room's layout grid.
        window.Controls.Add(surface);
        window.FormClosing+=(_,_)=>
        {
            Controls.Add(surface); fullscreenWindow=null; fullscreen.Text="Fullscreen";
            if(Active && !IsDisposed) peer.SetView(VideoHandle,Sound);
        };
        window.Show(owner); peer.SetView(VideoHandle,Sound);
    }
    internal void ExitFullscreen() => fullscreenWindow?.Close();
    protected override void Dispose(bool disposing)
    {
        if(disposing) { active=false; ExitFullscreen(); }
        base.Dispose(disposing);
    }
}

internal sealed class FullscreenVideoForm : Form
{
    internal FullscreenVideoForm()
    {
        FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
        BackColor=Color.Black; ShowInTaskbar=false; AutoScaleMode=AutoScaleMode.Dpi;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if(keyData is Keys.Escape or Keys.F11) { Close(); return true; }
        return base.ProcessCmdKey(ref msg,keyData);
    }
}
