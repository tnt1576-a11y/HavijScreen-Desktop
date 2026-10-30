using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HavijScreen.Native;

internal sealed class SourceTile : Button
{
    internal static readonly Color PickerBack=Color.FromArgb(23,29,36), Muted=Color.FromArgb(173,184,193), Line=Color.FromArgb(51,61,71);
    internal CaptureSource Source { get; }
    readonly Image? icon;
    readonly List<IntPtr> thumbnails=[];
    Bitmap? monitorImage;
    bool hover, available;
    internal Rectangle PreviewBounds=>new(2,2,Math.Max(1,Width-4),Math.Max(1,Height-Ui.Px(this,32)));
    internal SourceTile(CaptureSource source)
    {
        Source=source; Text=source.Name; AccessibleName="Share "+source.Name; AccessibleRole=AccessibleRole.PushButton;
        SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; BackColor=PickerBack; ForeColor=Color.White; Font=Ui.Font(12,FontStyle.Bold); Cursor=Cursors.Hand; Margin=Padding.Empty;
        if(!source.Monitor) icon=WindowIcon(source.Handle);
    }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; internal Rect(Rectangle r) { Left=r.Left;Top=r.Top;Right=r.Right;Bottom=r.Bottom; } }
    [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int Width,Height; }
    [StructLayout(LayoutKind.Sequential)] struct ThumbnailProperties { public uint Flags; public Rect Destination,Source; public byte Opacity; public int Visible,ClientOnly; }
    [DllImport("dwmapi")] static extern int DwmRegisterThumbnail(IntPtr destination,IntPtr source,out IntPtr thumbnail);
    [DllImport("dwmapi")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail,out NativeSize size);
    [DllImport("dwmapi")] static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail,ref ThumbnailProperties properties);
    [DllImport("dwmapi")] static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("user32")] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32")] static extern IntPtr GetClassLongPtr(IntPtr hwnd,int index);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32")] static extern int SetStretchBltMode(IntPtr dc,int mode);
    [DllImport("gdi32")] static extern bool StretchBlt(IntPtr dst,int x,int y,int width,int height,IntPtr src,int sx,int sy,int sw,int sh,uint operation);
    static Image? WindowIcon(IntPtr hwnd)
    {
        try { SendMessageTimeout(hwnd,0x7f,new IntPtr(2),IntPtr.Zero,2,40,out var handle); if(handle==IntPtr.Zero) handle=GetClassLongPtr(hwnd,-14); return handle!=IntPtr.Zero?Icon.FromHandle(handle).ToBitmap():SystemIcons.Application.ToBitmap(); } catch { return null; }
    }
    Rectangle ShareButton=>new((Width-Ui.Px(this,130))/2,PreviewBounds.Y+(PreviewBounds.Height-Ui.Px(this,44))/2,Ui.Px(this,130),Ui.Px(this,44));
    protected override void OnMouseEnter(EventArgs e) { hover=true; UpdateThumbnail(); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover=false; UpdateThumbnail(); Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { UpdateThumbnail(); Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { UpdateThumbnail(); Invalidate(); base.OnLostFocus(e); }
    internal void DetachThumbnail() { foreach(var thumb in thumbnails) DwmUnregisterThumbnail(thumb); thumbnails.Clear(); }
    internal void UpdateThumbnail()
    {
        DetachThumbnail(); if(Source.Monitor||!IsHandleCreated||FindForm() is not Form owner||!owner.Visible||Parent==null||!Visible) return;
        Rectangle viewport=Parent.RectangleToScreen(Parent.ClientRectangle), tile=RectangleToScreen(PreviewBounds); Rectangle clip=Rectangle.Intersect(viewport,tile);
        if(clip.Width<2||clip.Height<2) return;
        IntPtr first=IntPtr.Zero;
        try
        {
            if(DwmRegisterThumbnail(owner.Handle,Source.Handle,out first)!=0) { available=false; return; }
            if(DwmQueryThumbnailSourceSize(first,out var size)!=0||size.Width<1||size.Height<1) { available=false; return; }
            DwmUnregisterThumbnail(first); first=IntPtr.Zero; available=true;
            double scale=Math.Min(tile.Width/(double)size.Width,tile.Height/(double)size.Height);
            var fit=new Rectangle(tile.X+(tile.Width-(int)(size.Width*scale))/2,tile.Y+(tile.Height-(int)(size.Height*scale))/2,(int)(size.Width*scale),(int)(size.Height*scale));
            Rectangle visible=Rectangle.Intersect(fit,viewport), hole=RectangleToScreen(ShareButton);
            var pieces=new List<Rectangle>();
            if(!(hover||Focused)||!visible.IntersectsWith(hole)) pieces.Add(visible);
            else
            {
                // DWM thumbnails are compositor visuals above the client surface.
                // Leave a hole for the native hover button rather than capturing
                // or copying a second image merely to paint an overlay.
                Rectangle h=Rectangle.Intersect(visible,hole);
                pieces.Add(Rectangle.FromLTRB(visible.Left,visible.Top,visible.Right,h.Top)); pieces.Add(Rectangle.FromLTRB(visible.Left,h.Bottom,visible.Right,visible.Bottom));
                pieces.Add(Rectangle.FromLTRB(visible.Left,h.Top,h.Left,h.Bottom)); pieces.Add(Rectangle.FromLTRB(h.Right,h.Top,visible.Right,h.Bottom));
            }
            foreach(var piece in pieces.Where(r=>r.Width>0&&r.Height>0))
            {
                if(DwmRegisterThumbnail(owner.Handle,Source.Handle,out var thumb)!=0) continue;
                thumbnails.Add(thumb); var destination=owner.RectangleToClient(piece);
                var source=Rectangle.FromLTRB((int)((piece.Left-fit.Left)/scale),(int)((piece.Top-fit.Top)/scale),(int)Math.Ceiling((piece.Right-fit.Left)/scale),(int)Math.Ceiling((piece.Bottom-fit.Top)/scale));
                var properties=new ThumbnailProperties { Flags=31, Destination=new(destination), Source=new(source), Opacity=(byte)(hover||Focused?175:255), Visible=1, ClientOnly=0 };
                DwmUpdateThumbnailProperties(thumb,ref properties);
            }
        }
        finally { if(first!=IntPtr.Zero) DwmUnregisterThumbnail(first); }
    }
    internal void RefreshMonitor()
    {
        if(!Source.Monitor||Source.Bounds.Width<1||Source.Bounds.Height<1||Parent==null||!Bounds.IntersectsWith(Parent.ClientRectangle)) return;
        var next=new Bitmap(480,270,PixelFormat.Format24bppRgb); IntPtr screen=IntPtr.Zero;
        try
        {
            screen=GetDC(IntPtr.Zero); using var g=Graphics.FromImage(next); IntPtr target=g.GetHdc();
            try { SetStretchBltMode(target,4); var r=Source.Bounds; available=StretchBlt(target,0,0,next.Width,next.Height,screen,r.X,r.Y,r.Width,r.Height,0x00cc0020); }
            finally { g.ReleaseHdc(target); }
            var previous=monitorImage; monitorImage=next; next=null!; previous?.Dispose(); Invalidate();
        }
        catch { available=false; }
        finally { next?.Dispose(); if(screen!=IntPtr.Zero) ReleaseDC(IntPtr.Zero,screen); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.Clear(PickerBack); g.SmoothingMode=SmoothingMode.AntiAlias; Rectangle preview=PreviewBounds;
        using var shape=Art.Round(preview,Ui.Px(this,8)); using(var black=new SolidBrush(Color.FromArgb(7,10,13))) g.FillPath(black,shape);
        if(Source.Monitor&&monitorImage!=null)
        {
            var state=g.Save(); g.SetClip(shape); double scale=Math.Min(preview.Width/(double)Source.Bounds.Width,preview.Height/(double)Source.Bounds.Height);
            int w=(int)(Source.Bounds.Width*scale),h=(int)(Source.Bounds.Height*scale); g.DrawImage(monitorImage,new Rectangle(preview.X+(preview.Width-w)/2,preview.Y+(preview.Height-h)/2,w,h));
            if(hover||Focused) { using var shade=new SolidBrush(Color.FromArgb(70,0,0,0)); g.FillRectangle(shade,preview); } g.Restore(state);
        }
        else if(!available)
        {
            Art.Icon(g,Source.Monitor?Glyph.Screen:Glyph.Window,new(preview.X+preview.Width/2-16,preview.Y+preview.Height/2-28,32,32),Muted);
            using var font=Ui.Font(11); TextRenderer.DrawText(g,"Preview unavailable",font,new Rectangle(preview.X,preview.Y+preview.Height/2+12,preview.Width,24),Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
        }
        if(hover||Focused)
        {
            using var outline=new Pen(Color.FromArgb(77,92,108),Ui.Px(this,2)); g.DrawPath(outline,shape);
            using var button=Art.Round(ShareButton,Ui.Px(this,9)); using var white=new SolidBrush(Color.White); g.FillPath(white,button); using var font=Ui.Font(13); TextRenderer.DrawText(g,"Share Screen",font,ShareButton,Color.FromArgb(18,24,30),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        }
        int y=Height-Ui.Px(this,24),d=Ui.Px(this,16); if(icon!=null) g.DrawImage(icon,new Rectangle(Ui.Px(this,4),y,d,d)); else Art.Icon(g,Source.Monitor?Glyph.Screen:Glyph.Window,new(Ui.Px(this,4),y,d,d),Muted);
        TextRenderer.DrawText(g,Text,Font,new Rectangle(Ui.Px(this,28),y-2,Width-Ui.Px(this,30),Ui.Px(this,23)),ForeColor,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
    }
    protected override void Dispose(bool disposing) { if(disposing) { DetachThumbnail(); monitorImage?.Dispose(); icon?.Dispose(); } base.Dispose(disposing); }
}

internal sealed class SourceGrid : Panel
{
    readonly List<SourceTile> tiles=[];
    internal event Action<CaptureSource>? Chosen;
    internal SourceGrid()
    {
        BackColor=SourceTile.PickerBack; AutoScroll=true; Padding=Padding.Empty;
        Scroll+=(_,_)=>UpdateThumbnails(); Resize+=(_,_)=>Arrange();
    }
    [DllImport("uxtheme",CharSet=CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd,string app,string? id);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetWindowTheme(Handle,"DarkMode_Explorer",null); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); UpdateThumbnails(); }
    internal void Populate(IReadOnlyList<CaptureSource> sources)
    {
        SuspendLayout(); foreach(var tile in tiles) tile.Dispose(); tiles.Clear(); Controls.Clear(); AutoScrollPosition=Point.Empty;
        foreach(var source in sources) { var tile=new SourceTile(source); tile.Click+=(_,_)=>Chosen?.Invoke(source); tiles.Add(tile); Controls.Add(tile); }
        ResumeLayout(); Arrange();
    }
    internal void Arrange()
    {
        int P(int n)=>Ui.Px(this,n); int gap=P(16),columns=ClientSize.Width>=P(650)?2:1,w=Math.Max(1,(ClientSize.Width-P(18)-gap*(columns-1))/columns),h=w*9/16+P(32);
        SuspendLayout(); for(int i=0;i<tiles.Count;i++) tiles[i].SetBounds((i%columns)*(w+gap),i/columns*(h+P(20))+AutoScrollPosition.Y,w,h);
        AutoScrollMinSize=new Size(0,(tiles.Count+columns-1)/columns*(h+P(20))); ResumeLayout(); UpdateThumbnails();
    }
    internal void UpdateThumbnails() { foreach(var tile in tiles) tile.UpdateThumbnail(); }
    internal void RefreshMonitors() { foreach(var tile in tiles) tile.RefreshMonitor(); }
    internal void DetachThumbnails() { foreach(var tile in tiles) tile.DetachThumbnail(); }
}
