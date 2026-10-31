using System.Runtime.InteropServices;
using System.Text;

namespace HavijScreen.Native;

internal static class SourceEligibility
{
    delegate bool EnumChild(IntPtr window,IntPtr data);
    [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out uint value,int size);
    [DllImport("user32")] static extern bool EnumChildWindows(IntPtr window,EnumChild callback,IntPtr data);
    [DllImport("kernel32",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder image,ref int size);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    static readonly string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    internal static bool IsExcludedSystemImage(string? image,string windowsDirectory)
    {
        if(!IsWindowsImage(image,windowsDirectory)) return false;
        string name=Path.GetFileName(image!);
        return name.Equals("TextInputHost.exe",StringComparison.OrdinalIgnoreCase)
            || name.Equals("InputApp.exe",StringComparison.OrdinalIgnoreCase)
            || name.Equals("SystemSettings.exe",StringComparison.OrdinalIgnoreCase);
    }
    static bool IsWindowsImage(string? image,string root)=>!string.IsNullOrEmpty(image)&&!string.IsNullOrEmpty(root)
        &&image.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

    internal static bool CanOffer(IntPtr window,uint pid,Dictionary<uint,string?> images)
    {
        // WS_VISIBLE also includes suspended UWP windows and windows on another
        // virtual desktop. DWM cloaking distinguishes those placeholder surfaces.
        if(DwmGetWindowAttribute(window,14,out uint cloaked,sizeof(uint))==0&&cloaked!=0) return false;
        string? image=Image(pid,images);
        if(IsExcludedSystemImage(image,windows)) return false;
        if(IsWindowsImage(image,windows)&&Path.GetFileName(image!).Equals("ApplicationFrameHost.exe",StringComparison.OrdinalIgnoreCase))
        {
            // Older Windows versions host Settings inside ApplicationFrameHost.
            // Inspect child process identities so other hosted apps stay available.
            bool excluded=false;
            EnumChildWindows(window,(child,_)=>
            {
                Sources.GetWindowThreadProcessId(child,out uint childPid);
                if(childPid!=0&&childPid!=pid&&IsExcludedSystemImage(Image(childPid,images),windows)) { excluded=true; return false; }
                return true;
            },IntPtr.Zero);
            if(excluded) return false;
        }
        return true;
    }
    static string? Image(uint pid,Dictionary<uint,string?> images)
    {
        if(images.TryGetValue(pid,out var known)) return known;
        string? image=null;
        // Limited metadata query only: no VM_READ access or game-memory reads.
        IntPtr process=OpenProcess(0x1000,false,pid);
        if(process!=IntPtr.Zero)
        {
            try { var path=new StringBuilder(32768); int size=path.Capacity; if(QueryFullProcessImageName(process,0,path,ref size)) image=path.ToString(); }
            finally { CloseHandle(process); }
        }
        images[pid]=image; return image;
    }
}
