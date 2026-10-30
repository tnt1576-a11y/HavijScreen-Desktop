using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HavijScreen.Native;

internal enum Glyph { None, Screen, Window, Arrow, Link, Plus, Shield, Audio, Stop, Chevron, Close, Minimize, Maximize, Refresh, Bolt }
internal enum ButtonKind { Secondary, Primary, Invite, Text, Title, Close, Danger }

internal static class Art
{
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius*2,Math.Min(r.Width,r.Height)); var path = new GraphicsPath();
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.Left,r.Top,d,d,180,90); path.AddArc(r.Right-d,r.Top,d,d,270,90);
        path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.Left,r.Bottom-d,d,d,90,90); path.CloseFigure(); return path;
    }
    internal static void Icon(Graphics g, Glyph glyph, RectangleF bounds, Color color, float thickness = 1.5f)
    {
        if (glyph == Glyph.None) return;
        var state = g.Save(); g.TranslateTransform(bounds.X,bounds.Y); g.ScaleTransform(bounds.Width/24,bounds.Height/24);
        using var p = new Pen(color,thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        void Lines(params PointF[] points) => g.DrawLines(p,points);
        switch(glyph)
        {
            case Glyph.Screen: using (var r=Round(new(3,4,18,13),1.5f)) g.DrawPath(p,r); Lines(new(8,21),new(16,21)); Lines(new(12,17),new(12,21)); break;
            case Glyph.Window: using (var r=Round(new(3,3,18,18),2)) g.DrawPath(p,r); Lines(new(3,8),new(21,8)); g.DrawEllipse(p,6,5,0.5f,0.5f); g.DrawEllipse(p,10,5,0.5f,0.5f); break;
            case Glyph.Arrow: Lines(new(5,12),new(19,12)); Lines(new(14,7),new(19,12),new(14,17)); break;
            case Glyph.Chevron: Lines(new(7,9),new(12,14),new(17,9)); break;
            case Glyph.Plus: Lines(new(5,12),new(19,12)); Lines(new(12,5),new(12,19)); break;
            case Glyph.Close: Lines(new(6,6),new(18,18)); Lines(new(18,6),new(6,18)); break;
            case Glyph.Minimize: Lines(new(5,12),new(19,12)); break;
            case Glyph.Maximize: g.DrawRectangle(p,5,5,14,14); break;
            case Glyph.Stop: using (var r=Round(new(5,5,14,14),2)) g.DrawPath(p,r); break;
            case Glyph.Shield: Lines(new(12,3),new(20,6),new(20,12),new(18,17),new(12,21),new(6,17),new(4,12),new(4,6),new(12,3)); Lines(new(8,12),new(11,15),new(16,9)); break;
            case Glyph.Audio: Lines(new(11,4),new(5,9),new(2,9),new(2,15),new(5,15),new(11,20),new(11,4)); g.DrawArc(p,10,7,9,10,-65,130); g.DrawArc(p,10,3,14,18,-60,120); break;
            case Glyph.Link: g.DrawArc(p,2,9,12,12,45,270); g.DrawArc(p,10,1,12,12,225,270); Lines(new(8,16),new(16,8)); break;
            case Glyph.Refresh: g.DrawArc(p,4,4,16,16,-65,290); Lines(new(15,3),new(20,5),new(21,0)); break;
            case Glyph.Bolt: Lines(new(13,2),new(4,14),new(11,14),new(10,22),new(20,10),new(13,10),new(13,2)); break;
        }
        g.Restore(state);
    }
    internal static void Tracked(Graphics g,string text,Font font,Color color,Rectangle bounds,float spacing)
    {
        float x=bounds.X; using var brush = new SolidBrush(color); using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags=StringFormatFlags.NoWrap };
        float y=bounds.Y+(bounds.Height-font.GetHeight(g))/2;
        foreach(char c in text) { string item=c.ToString(); g.DrawString(item,font,brush,x,y,format); x+=g.MeasureString(item,font,PointF.Empty,format).Width+spacing; }
    }
}

internal sealed class StyledButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal ButtonKind Kind { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal Glyph Symbol { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool TrailingArrow { get; set; }
    bool hover, down;
    internal StyledButton(string text,ButtonKind kind = ButtonKind.Secondary)
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);
        Text=text; AccessibleName=text; Kind=kind; FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0;
        BackColor=Color.Transparent; ForeColor=Ui.Foreground; Font=Ui.Font(11); Height=42; Margin=Padding.Empty; Cursor=Cursors.Hand;
        UseVisualStyleBackColor=false;
    }
    protected override void OnMouseEnter(EventArgs e) { hover=true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover=down=false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down=true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down=false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { AccessibleName=Text; Invalidate(); base.OnTextChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { using var brush=new SolidBrush(Parent?.BackColor??Ui.Background); e.Graphics.FillRectangle(brush,ClientRectangle); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; Color backdrop=Parent?.BackColor??Ui.Background; if(backdrop.A<255) backdrop=Ui.Surface; using(var clear=new SolidBrush(backdrop)) g.FillRectangle(clear,ClientRectangle); g.SmoothingMode=SmoothingMode.AntiAlias; float s=DeviceDpi/96f;
        bool title=Kind is ButtonKind.Title or ButtonKind.Close, plain=Kind==ButtonKind.Text||title;
        Color fill=Kind switch { ButtonKind.Primary=>hover?Color.FromArgb(246,185,142):Ui.Accent, ButtonKind.Danger=>Color.FromArgb(200,129,102), ButtonKind.Invite=>hover?Color.FromArgb(43,50,38):Color.FromArgb(34,39,31), _=>hover?Color.FromArgb(39,47,35):Ui.Field };
        Color ink=Kind is ButtonKind.Primary or ButtonKind.Danger?Color.FromArgb(40,35,29):Kind==ButtonKind.Text?ForeColor:Kind is ButtonKind.Title or ButtonKind.Close?Ui.Muted:Ui.Foreground;
        if (Kind==ButtonKind.Close&&hover) { fill=Color.FromArgb(174,75,63); ink=Color.White; }
        if (!Enabled) { ink=Ui.Disabled; fill=Color.FromArgb(39,43,35); }
        if (down&&Enabled) fill=ControlPaint.Dark(fill,.07f);
        var r=new RectangleF(.5f*s,.5f*s,Width-s,Height-s);
        using var path=Art.Round(r,title?0:5*s);
        if (!plain||hover&&Enabled) { using var brush=new SolidBrush(fill); g.FillPath(brush,path); }
        if (!plain) { using var pen=new Pen(Kind==ButtonKind.Primary?Color.FromArgb(245,184,139):Ui.Line,s); if(Kind==ButtonKind.Invite) pen.DashPattern=[3,3]; g.DrawPath(pen,path); }
        int inset=(int)(14*s), icon=(int)(16*s), gap=(int)(10*s);
        if (title) Art.Icon(g,Symbol,new((Width-icon)/2f,(Height-icon)/2f,icon,icon),ink,1.2f);
        else
        {
            int x=inset, end=Width-inset;
            if (Symbol!=Glyph.None) { Art.Icon(g,Symbol,new(x,(Height-icon)/2f,icon,icon),ink); x+=icon+gap; }
            if (TrailingArrow) { Art.Icon(g,Glyph.Arrow,new(end-icon,(Height-icon)/2f,icon,icon),ink); end-=icon+gap; }
            var flags=TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine;
            if(Symbol==Glyph.None&&!TrailingArrow) flags|=TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(g,Text,Font,new Rectangle(x,0,Math.Max(0,end-x),Height),ink,flags);
        }
        if(Focused&&ShowFocusCues) { using var p=new Pen(Ui.Accent,1.5f*s); g.DrawPath(p,path); }
    }
}

internal sealed class PersonPanel : Panel
{
    readonly string name,detail; readonly bool self,request;
    internal PersonPanel(string name,string detail,bool self=false,bool request=false)
    {
        this.name=name; this.detail=detail; this.self=self; this.request=request;
        SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint,true);
        BackColor=Ui.Background; Margin=new Padding(0,0,0,8); AccessibleName=name+", "+detail; AccessibleRole=AccessibleRole.Grouping;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; float s=DeviceDpi/96f; int inset=request?Ui.Px(this,10):0;
        if(request) { using var shape=Art.Round(new(.5f,.5f,Width-1,Height-1),5*s); using var brush=new SolidBrush(Color.FromArgb(34,43,30)); using var border=new Pen(Color.FromArgb(75,89,62)); g.FillPath(brush,shape); g.DrawPath(border,shape); }
        var avatar=new RectangleF(inset,7*s,32*s,34*s); using var p=Art.Round(avatar,8*s); using var fill=new SolidBrush(Color.FromArgb(49,55,44)); using var pen=new Pen(Color.FromArgb(65,74,56)); g.FillPath(fill,p); g.DrawPath(pen,p);
        using var initial=Ui.Font(11); TextRenderer.DrawText(g,self?"Y":name[..1].ToUpperInvariant(),initial,Rectangle.Round(avatar),Color.FromArgb(184,197,169),TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPadding);
        using var title=Ui.Font(11,FontStyle.Bold); using var sub=Ui.Font(10); int x=inset+Ui.Px(this,43), available=Width-x-inset-(self?Ui.Px(this,26):0);
        TextRenderer.DrawText(g,name,title,new Rectangle(x,Ui.Px(this,5),Math.Max(1,available),Ui.Px(this,21)),Ui.Foreground,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g,detail,sub,new Rectangle(x,Ui.Px(this,25),Width-x-inset,Ui.Px(this,19)),Ui.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
        if(self) { using var small=Ui.Font(8); Art.Tracked(g,"YOU",small,Ui.Muted,new Rectangle(Width-Ui.Px(this,27),Ui.Px(this,11),Ui.Px(this,27),Ui.Px(this,20)),s); }
    }
}

internal sealed class StatsStrip : Control
{
    readonly string[] headings=["STREAM","UPLOAD","ENCODER","WATCHING YOU"];
    string[] values=["Not sharing","0.00 Mbps","—","0 people"];
    internal StatsStrip() { SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true); BackColor=Ui.Background; }
    internal void UpdateValues(string stream,string upload,string encoder,string viewers)
    {
        string[] next=[stream,upload,encoder,viewers]; if(values.SequenceEqual(next)) return; values=next; AccessibleName=string.Join("; ",headings.Zip(values,(a,b)=>a+": "+b)); Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; using var pen=new Pen(Ui.Divider); g.DrawLine(pen,0,0,Width,0); using var caption=Ui.Font(8); using var value=Ui.Font(11);
        float[] starts=[0,.23f,.43f,.79f],ends=[.23f,.43f,.79f,1];
        for(int i=0;i<4;i++) { int x=(int)(Width*starts[i]),w=(int)(Width*(ends[i]-starts[i]))-Ui.Px(this,8); Art.Tracked(g,headings[i],caption,Ui.Muted,new Rectangle(x,Ui.Px(this,12),w,Ui.Px(this,18)),1.4f*DeviceDpi/96); TextRenderer.DrawText(g,values[i],value,new Rectangle(x,Ui.Px(this,35),w,Ui.Px(this,20)),Color.FromArgb(184,197,172),TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis); }
    }
}

internal sealed class SharingBanner : Panel
{
    readonly Label text=Ui.Label("",8,Color.FromArgb(208,221,194));
    readonly StyledButton stop=Ui.Button("Stop sharing",true);
    internal event Action? Stop;
    internal SharingBanner()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true); BackColor=Ui.Surface;
        stop.Font=Ui.Font(10); stop.Click+=(_,_)=>Stop?.Invoke(); Controls.AddRange([text,stop]);
    }
    internal void SetStatus(string message) { text.Text=message; Invalidate(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); int pad=Ui.Px(this,12); stop.SetBounds(Width-Ui.Px(this,99),Ui.Px(this,9),Ui.Px(this,86),Height-Ui.Px(this,18)); text.SetBounds(Ui.Px(this,28),0,Math.Max(1,Width-Ui.Px(this,134)),Height); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; using var path=Art.Round(new(.5f,.5f,Width-1,Height-1),Ui.Px(this,5)); using var fill=new SolidBrush(Color.FromArgb(37,46,34)); using var border=new Pen(Color.FromArgb(83,99,72)); g.FillPath(fill,path); g.DrawPath(border,path); using var dot=new SolidBrush(Color.FromArgb(168,187,153)); int d=Ui.Px(this,6); g.FillEllipse(dot,Ui.Px(this,13),(Height-d)/2,d,d); }
}

internal sealed class ThemedCheckBox : CheckBox
{
    internal ThemedCheckBox() { SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true); AutoSize=false; Height=32; Font=Ui.Font(12); ForeColor=Ui.Foreground; Cursor=Cursors.Hand; }
    protected override void OnPaintBackground(PaintEventArgs e) { using var brush=new SolidBrush(Parent?.BackColor??Ui.Background); e.Graphics.FillRectangle(brush,ClientRectangle); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; using(var clear=new SolidBrush(Parent?.BackColor??Ui.Background)) g.FillRectangle(clear,ClientRectangle); g.SmoothingMode=SmoothingMode.AntiAlias; int d=Ui.Px(this,17),y=(Height-d)/2; using var path=Art.Round(new(.5f,y+.5f,d,d),Ui.Px(this,4)); using var fill=new SolidBrush(Checked?Ui.Accent:Ui.Field); using var border=new Pen(Checked?Ui.Accent:Ui.Line); g.FillPath(fill,path); g.DrawPath(border,path);
        if(Checked) { using var p=new Pen(Color.FromArgb(40,35,29),Ui.Px(this,2)); g.DrawLines(p,[new(Ui.Px(this,4),y+Ui.Px(this,8)),new(Ui.Px(this,7),y+Ui.Px(this,11)),new(Ui.Px(this,13),y+Ui.Px(this,5))]); }
        TextRenderer.DrawText(g,Text,Font,new Rectangle(Ui.Px(this,28),0,Width-Ui.Px(this,28),Height),Enabled?ForeColor:Ui.Disabled,TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
        if(Focused&&ShowFocusCues) ControlPaint.DrawFocusRectangle(g,ClientRectangle,Ui.Accent,BackColor);
    }
}

internal sealed class CaptionLabel : Label
{
    internal CaptionLabel(string text) { Text=text; Font=Ui.Font(9); ForeColor=Ui.Muted; BackColor=Color.Transparent; AutoSize=false; Height=22; SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true); }
    protected override void OnPaint(PaintEventArgs e) => Art.Tracked(e.Graphics,Text,Font,ForeColor,ClientRectangle,1.6f*DeviceDpi/96);
}

internal sealed class TextField : Panel
{
    readonly TextBox editor = new() { BorderStyle=BorderStyle.None, BackColor=Ui.Field, ForeColor=Ui.Foreground };
    [System.Diagnostics.CodeAnalysis.AllowNull,DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public override string Text { get=>editor?.Text??""; set { if(editor!=null) editor.Text=value??""; } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal int MaxLength { get=>editor.MaxLength; set=>editor.MaxLength=value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal bool ReadOnly { get=>editor.ReadOnly; set=>editor.ReadOnly=value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal string PlaceholderText { get=>editor.PlaceholderText; set=>editor.PlaceholderText=value; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal HorizontalAlignment TextAlign { get=>editor.TextAlign; set=>editor.TextAlign=value; }
    internal TextField(string text="")
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        BackColor=Ui.Background; Font=Ui.Font(12); Height=40; Margin=Padding.Empty; Controls.Add(editor); editor.Font=Font; Text=text;
        editor.TextChanged+=(_,_)=>OnTextChanged(EventArgs.Empty); editor.GotFocus+=(_,_)=>Invalidate(); editor.LostFocus+=(_,_)=>Invalidate();
        Click+=(_,_)=>editor.Focus();
    }
    internal void SelectAll() => editor.SelectAll();
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if(editor!=null) editor.Font=Font; PerformLayout(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if(editor==null) return; int pad=Ui.Px(this,12); editor.SetBounds(pad,Math.Max(0,(Height-editor.PreferredHeight)/2),Math.Max(1,Width-2*pad),editor.PreferredHeight); editor.AccessibleName=AccessibleName; }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); editor.Enabled=Enabled; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias; using var path=Art.Round(new(.5f,.5f,Width-1,Height-1),Ui.Px(this,5)); using var brush=new SolidBrush(Ui.Field); e.Graphics.FillPath(brush,path); using var pen=new Pen(ContainsFocus?Ui.Accent:Ui.Line,1); e.Graphics.DrawPath(pen,path); }
}

internal sealed class QualityPicker : StyledButtonBase
{
    internal readonly List<Quality> Items=[];
    int selected=-1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] internal int SelectedIndex { get=>selected; set { if(selected==value) return; selected=value; Text=SelectedItem?.Name??""; SelectedIndexChanged?.Invoke(this,EventArgs.Empty); Invalidate(); } }
    internal Quality? SelectedItem=>selected>=0&&selected<Items.Count?Items[selected]:null;
    internal event EventHandler? SelectedIndexChanged;
    internal QualityPicker() { AccessibleRole=AccessibleRole.ButtonDropDown; Font=Ui.Font(11); TabStop=true; Cursor=Cursors.Hand; }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e); var menu=new ContextMenuStrip { ShowImageMargin=false, BackColor=Ui.Field, ForeColor=Ui.Foreground, Font=Font, Padding=new Padding(5), Renderer=new MenuRenderer(), AutoSize=false, Width=Width, Height=Items.Count*Ui.Px(this,38)+10 };
        for(int i=0;i<Items.Count;i++) { int index=i; var item=new ToolStripMenuItem(Items[i].Name) { AutoSize=false, Width=Width-12, Height=Ui.Px(this,38), ForeColor=Ui.Foreground, BackColor=Ui.Field }; item.Click+=(_,_)=>SelectedIndex=index; menu.Items.Add(item); }
        menu.Closed+=(_,_)=>menu.Dispose(); menu.Show(this,new Point(0,Height+4));
    }
    protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode is Keys.Down or Keys.Up) { SelectedIndex=Math.Clamp(selected+(e.KeyCode==Keys.Down?1:-1),0,Items.Count-1); e.Handled=true; } else if(e.KeyCode==Keys.Space) { OnClick(EventArgs.Empty); e.Handled=true; } base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; using var path=Art.Round(new(.5f,.5f,Width-1,Height-1),Ui.Px(this,5)); using var fill=new SolidBrush(Ui.Field); g.FillPath(fill,path); using var pen=new Pen(Focused?Ui.Accent:Ui.Line); g.DrawPath(pen,path);
        int pad=Ui.Px(this,12), icon=Ui.Px(this,14); TextRenderer.DrawText(g,Text,Font,new Rectangle(pad,0,Width-3*pad-icon,Height),Enabled?Ui.Foreground:Ui.Disabled,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding|TextFormatFlags.SingleLine); Art.Icon(g,Glyph.Chevron,new(Width-pad-icon,(Height-icon)/2f,icon,icon),Ui.Foreground);
    }
    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { using var b=new SolidBrush(e.Item.Selected?Color.FromArgb(47,57,39):Ui.Field); e.Graphics.FillRectangle(b,new Rectangle(Point.Empty,e.Item.Size)); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using var p=new Pen(Ui.Line); e.Graphics.DrawRectangle(p,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1); }
    }
}
// The native Button base keeps keyboard activation and accessibility semantics.
internal class StyledButtonBase : Button
{
    internal StyledButtonBase() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true); FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; }
}

internal sealed class TheaterPanel : Panel
{
    Bitmap? background;
    internal TheaterPanel() { SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true); BackColor=Ui.Surface; }
    protected override void OnSizeChanged(EventArgs e) { background?.Dispose(); background=null; base.OnSizeChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if(Width<2||Height<2) return;
        if(background==null)
        {
            background=new Bitmap(Width,Height); using var g=Graphics.FromImage(background); g.Clear(Ui.Background); g.SmoothingMode=SmoothingMode.AntiAlias;
            using var rounded=Art.Round(new(.5f,.5f,Width-1,Height-1),Ui.Px(this,8));
            using var glowPath=new GraphicsPath(); glowPath.AddEllipse(-Width,-Height,Width*3,Height*3);
            using var glow=new PathGradientBrush(glowPath) { CenterColor=Color.FromArgb(28,35,25), SurroundColors=[Ui.Surface], CenterPoint=new PointF(Width*.5f,Height*.38f) }; g.FillPath(glow,rounded);
            var save=g.Save(); g.SetClip(rounded); int cell=Ui.Px(this,35); using var grid=new Pen(Color.FromArgb(2,107,128,87));
            for(int x=cell;x<Width;x+=cell) g.DrawLine(grid,x,1,x,Height-1); for(int y=cell;y<Height;y+=cell) g.DrawLine(grid,1,y,Width-1,y); g.Restore(save);
            using var border=new Pen(Ui.Line); g.DrawPath(border,rounded); using var corner=new Pen(Color.FromArgb(82,97,70)); int k=Ui.Px(this,14),l=Ui.Px(this,18); g.DrawLines(corner,[new(k,k+l),new(k,k),new(k+l,k)]); g.DrawLines(corner,[new(Width-k-l,Height-k),new(Width-k,Height-k),new(Width-k,Height-k-l)]);
        }
        e.Graphics.DrawImageUnscaled(background,0,0); base.OnPaint(e);
    }
    protected override void Dispose(bool disposing) { if(disposing) background?.Dispose(); base.Dispose(disposing); }
}

internal sealed class ScreenIllustration : Control
{
    internal ScreenIllustration() { SetStyle(ControlStyles.UserPaint|ControlStyles.SupportsTransparentBackColor|ControlStyles.OptimizedDoubleBuffer,true); BackColor=Color.Transparent; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; float s=Math.Min(Width/180f,Height/160f); g.TranslateTransform(Width/2f-80*s,Height/2f-70*s); g.ScaleTransform(s,s); g.RotateTransform(-5);
        using var back=Art.Round(new(31,16,123,85),7); using var backFill=new SolidBrush(Color.FromArgb(29,37,26)); using var backPen=new Pen(Color.FromArgb(72,86,62)); g.FillPath(backFill,back); g.DrawPath(backPen,back);
        using var front=Art.Round(new(22,24,123,85),7); using var fill=new SolidBrush(Color.FromArgb(44,55,40)); using var pen=new Pen(Color.FromArgb(119,132,105)); g.FillPath(fill,front); g.DrawPath(pen,front); using var bar=new Pen(Color.FromArgb(89,104,78)); g.DrawLine(bar,22,40,145,40);
        using var dots=new SolidBrush(Color.FromArgb(135,151,121)); for(int i=0;i<3;i++) g.FillEllipse(dots,30+i*7,31,3,3);
        var state=g.Save(); g.TranslateTransform(84,72); g.RotateTransform(-35); Art.Icon(g,Glyph.Arrow,new(-16,-16,32,32),Color.FromArgb(216,175,128),1.8f); g.Restore(state);
        g.DrawLines(pen,[new(63,109),new(59,123),new(103,123),new(97,109)]); g.DrawLine(pen,47,124,115,124);
        using var arrow=new Pen(Ui.Accent,2); g.DrawLines(arrow,[new(151,122),new(140,102),new(138,112)]); g.DrawLine(arrow,140,102,149,107);
    }
}
