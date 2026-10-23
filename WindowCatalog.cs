using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PhoneRemote;

internal sealed class WindowCatalog
{
    private record Entry(nint Handle,uint Pid,long Started,string Class,string App,string Title);
    private readonly object sync=new();
    private Dictionary<string,Entry> published=new();
    private DateTimeOffset refreshed;
    internal PcWindow[] Read()
    {
        lock(sync)
        {
            if(DateTimeOffset.UtcNow-refreshed<TimeSpan.FromSeconds(2))return Snapshot();
            var next=new Dictionary<string,Entry>();
            EnumWindows((handle,_)=>
            {
                if(next.Count>=100)return false;
                var entry=Inspect(handle);if(entry is null)return true;
                var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{handle}:{entry.Pid}:{entry.Started}:{entry.Class}")))[..24];
                next[id]=entry;return true;
            },0);
            published=next;refreshed=DateTimeOffset.UtcNow;return Snapshot();
        }
    }
    private PcWindow[] Snapshot()=>published.Select(pair=>new PcWindow(pair.Key,pair.Value.App,pair.Value.Title)).ToArray();
    internal bool Focus(string id)
    {
        lock(sync)
        {
            if(!published.TryGetValue(id,out var original))return false;
            var current=Inspect(original.Handle);
            if(current is null||current.Pid!=original.Pid||current.Started!=original.Started||current.Class!=original.Class)return false;
            if(IsIconic(original.Handle))ShowWindowAsync(original.Handle,9);
            return SetForegroundWindow(original.Handle);
        }
    }
    private static Entry? Inspect(nint handle)
    {
        if(!IsWindowVisible(handle)||GetWindowTextLength(handle)==0)return null;
        GetWindowThreadProcessId(handle,out var pid);if(pid==Environment.ProcessId||pid==0)return null;
        if(DwmGetWindowAttribute(handle,14,out var cloaked,sizeof(int))>=0&&cloaked!=0)return null;
        var title=new StringBuilder(257);GetWindowText(handle,title,title.Capacity);
        var windowClass=new StringBuilder(128);GetClassName(handle,windowClass,windowClass.Capacity);
        if(title.Length==0||windowClass.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")return null;
        try
        {
            using var process=Process.GetProcessById((int)pid);
            var name=process.ProcessName;
            var app=name.ToLowerInvariant() switch{"chrome"=>"Chrome","msedge"=>"Edge","discord"=>"Discord","explorer"=>"Проводник","vlc"=>"VLC","spotify"=>"Spotify",_=>name};
            var clean=new string(title.ToString().Where(c=>!char.IsControl(c)).ToArray());
            return new(handle,pid,process.StartTime.ToUniversalTime().Ticks,windowClass.ToString(),app,clean);
        }
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return null;}
    }
    private delegate bool WindowCallback(nint window,nint parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(WindowCallback callback,nint parameter);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]private static extern bool ShowWindowAsync(nint window,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint window,StringBuilder text,int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint window,StringBuilder text,int length);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int length);
}
