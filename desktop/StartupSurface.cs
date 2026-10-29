using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HavijScreen {
  // This native surface paints before WebView2 starts, so a cold runtime never
  // exposes an empty browser window. It stays usable until the web UI has painted.
  internal sealed class StartupSurface : Control {
    private readonly Form owner;
    private readonly Image carrot;
    private int hover = -1;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    public StartupSurface(Form owner, Image carrot) {
      this.owner = owner; this.carrot = carrot; Dock = DockStyle.Fill;
      BackColor = Color.FromArgb(17,19,19);
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    private int ButtonAt(int x, int y) { return y < 68 && x >= Width - 144 ? Math.Min(2, (x - (Width - 144)) / 48) : -1; }
    protected override void OnMouseMove(MouseEventArgs e) { int next = ButtonAt(e.X,e.Y); if (next != hover) { hover = next; Invalidate(new Rectangle(Width-144,0,144,68)); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(new Rectangle(Width-144,0,144,68)); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button != MouseButtons.Left || e.Y >= 68) return;
      int button = ButtonAt(e.X,e.Y);
      if (button == 0) owner.WindowState = FormWindowState.Minimized;
      else if (button == 1) owner.WindowState = owner.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
      else if (button == 2) owner.Close();
      else { ReleaseCapture(); SendMessage(owner.Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); }
    }
    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.Clear(BackColor);
      g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
      using (Pen border = new Pen(Color.FromArgb(42,45,43))) g.DrawLine(border,0,67,Width,67);
      if (carrot != null) { g.DrawImage(carrot,new Rectangle(25,13,40,42)); g.DrawImage(carrot,new Rectangle((Width-88)/2,(Height-88)/2-24,88,88)); }
      using (Font brand = new Font("Segoe UI",22,FontStyle.Bold,GraphicsUnit.Pixel))
      using (SolidBrush text = new SolidBrush(Color.FromArgb(232,233,230))) g.DrawString("HavijScreen",brand,text,74,19);
      using (Font font = new Font("Segoe UI",12,FontStyle.Regular,GraphicsUnit.Pixel))
      using (SolidBrush muted = new SolidBrush(Color.FromArgb(142,153,132))) {
        string label = "Starting..."; SizeF size = g.MeasureString(label,font);
        g.DrawString(label,font,muted,(Width-size.Width)/2,(Height+88)/2-6);
      }
      for (int i=0;i<3;i++) {
        int left = Width-144+i*48;
        if (hover == i) using (SolidBrush fill = new SolidBrush(i==2 ? Color.FromArgb(174,75,63) : Color.FromArgb(37,44,34))) g.FillRectangle(fill,left,0,48,68);
        using (Pen line = new Pen(Color.FromArgb(161,171,154),1)) {
          int x=left+24,y=34;
          if (i==0) g.DrawLine(line,x-5,y,x+5,y);
          else if (i==1) g.DrawRectangle(line,x-4,y-4,8,8);
          else { g.DrawLine(line,x-4,y-4,x+4,y+4); g.DrawLine(line,x-4,y+4,x+4,y-4); }
        }
      }
    }
  }
}
