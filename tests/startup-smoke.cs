using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HavijScreen;
internal static class StartupSmoke {
  [STAThread]
  static int Main(string[] args) {
    Application.EnableVisualStyles();
    using (Image carrot=Image.FromFile(args[0]))
    using (Form owner=new Form()) {
      owner.ClientSize=new Size(1380,870);
      using (StartupSurface surface=new StartupSurface(owner,carrot)) {
        owner.Controls.Add(surface); surface.Size=owner.ClientSize;
        using (Bitmap frame=new Bitmap(1380,870)) {
          Stopwatch time=Stopwatch.StartNew(); surface.DrawToBitmap(frame,new Rectangle(0,0,1380,870)); time.Stop();
          int colored=0;
          for(int y=13;y<55;y++) for(int x=25;x<65;x++) { Color c=frame.GetPixel(x,y); if(c.R>140 && c.G>60 && c.B<100) colored++; }
          if(colored<100) throw new Exception("Startup carrot was not painted.");
          if(frame.GetPixel(0,0).ToArgb()!=Color.FromArgb(17,19,19).ToArgb()) throw new Exception("Unexpected startup background.");
          frame.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);
          Console.WriteLine("Native branded startup view painted in "+time.ElapsedMilliseconds+" ms without WebView2.");
        }
      }
    }
    using(Icon icon=Icon.ExtractAssociatedIcon(args[2]))
    using(Bitmap pixels=icon.ToBitmap()) {
      int orange=0;
      for(int y=0;y<pixels.Height;y++) for(int x=0;x<pixels.Width;x++) {Color c=pixels.GetPixel(x,y); if(c.R>140 && c.G>60 && c.B<120) orange++;}
      if(orange<10) throw new Exception("The executable does not have the carrot icon.");
      Console.WriteLine("Packaged executable contains the carrot icon.");
    }
    return 0;
  }
}
