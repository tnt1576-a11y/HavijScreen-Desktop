using System.Runtime.InteropServices;

namespace HavijScreen.Native;

internal static partial class Diagnostics
{
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
