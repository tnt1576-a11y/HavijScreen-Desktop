using System.Runtime.InteropServices;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
    static void CheckQualityMenu(Form owner)
    {
        using var picker=new QualityPicker { Width=270,Height=40 };
        picker.Items.AddRange(Quality.All); picker.SelectedIndex=0; owner.Controls.Add(picker);
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        ContextMenuStrip? previous=null;
        for(int i=0;i<8;i++)
        {
            picker.PerformClick();
            var menu=(ContextMenuStrip)typeof(QualityPicker).GetField("qualityMenu",flags)!.GetValue(picker)!;
            if(previous!=null&&!previous.IsDisposed) throw new InvalidOperationException("The previous quality menu leaked.");
            if(i%2==0)
            {
                int index=(i/2+1)%Quality.All.Length; menu.Items[index].Select();
                typeof(ToolStripDropDown).GetMethod("ProcessDialogKey",flags)!.Invoke(menu,[Keys.Enter]);
                if(picker.SelectedIndex!=index) throw new InvalidOperationException("Quality menu selection failed.");
            }
            else menu.Close(ToolStripDropDownCloseReason.Keyboard);
            if(menu.IsDisposed) throw new InvalidOperationException("Quality menu was disposed inside its closing stack.");
            previous=menu;
        }
        picker.Dispose();
        if(previous is not { IsDisposed:true }) throw new InvalidOperationException("Owner disposal did not release its quality menu.");
    }
    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr window,uint attribute,ref int value,int size);
    static void CheckSourceEligibility()
    {
        const string root=@"C:\Windows";
        foreach(string image in new[]{@"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_abc\TextInputHost.exe",@"C:\Windows\SystemApps\InputApp_abc\InputApp.exe",@"C:\Windows\ImmersiveControlPanel\SystemSettings.exe",@"c:\WINDOWS\ImmersiveControlPanel\SYSTEMSETTINGS.EXE"})
            if(!SourceEligibility.IsExcludedSystemImage(image,root)) throw new InvalidOperationException("A system picker entry was not excluded.");
        foreach(string? image in new[]{@"D:\Games\Settings.exe",@"C:\WindowsElsewhere\TextInputHost.exe",@"C:\Windows\explorer.exe",@"C:\Windows\System32\ApplicationFrameHost.exe",@"D:\Games\SystemSettings.exe",null})
            if(SourceEligibility.IsExcludedSystemImage(image,root)) throw new InvalidOperationException("An unrelated or unidentified application was excluded.");
        using var source=new Form { Text="Settings",ClientSize=new Size(160,100),ShowInTaskbar=false };
        source.Show();
        var images=new Dictionary<uint,string?>();
        if(!SourceEligibility.CanOffer(source.Handle,(uint)Environment.ProcessId,images)) throw new InvalidOperationException("An ordinary window titled Settings was excluded.");
        int cloak=1;
        if(DwmSetWindowAttribute(source.Handle,13,ref cloak,sizeof(int))!=0) throw new InvalidOperationException("Could not cloak the owned test window.");
        try { if(SourceEligibility.CanOffer(source.Handle,(uint)Environment.ProcessId,images)) throw new InvalidOperationException("A cloaked window was offered for sharing."); }
        finally { cloak=0; DwmSetWindowAttribute(source.Handle,13,ref cloak,sizeof(int)); }
        if(!SourceEligibility.CanOffer(source.Handle,(uint)Environment.ProcessId,images)) throw new InvalidOperationException("The restored test window stayed excluded.");
    }
}
