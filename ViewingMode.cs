using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhoneRemote;

public record ViewingModeRequest(bool? Enabled);
public sealed class ViewingMode : IDisposable
{
    private readonly object sync = new();
    private readonly Func<bool,bool>? apply;
    private SafeFileHandle? request;
    private string? owner;
    private bool enabled, disposed;
    public ViewingMode(Func<bool,bool>? apply = null) => this.apply = apply;
    public bool Enabled { get { lock(sync) return enabled; } }
    public bool Set(bool value, string? device = null)
    {
        lock(sync)
        {
            if(disposed) return false;
            if(enabled == value) { if(value) owner=device; return true; }
            if(apply is not null) { if(!apply(value)) return false; }
            else if(value)
            {
                var reason=Marshal.StringToHGlobalUni("parallexity: режим просмотра");
                try
                {
                    var context=new ReasonContext { Flags=1, SimpleReason=reason };
                    var handle=PowerCreateRequest(ref context);
                    if(handle.IsInvalid) { handle.Dispose(); return false; }
                    // A handle owns the requests; closing it releases both, even after a crash.
                    if(!PowerSetRequest(handle,0)||!PowerSetRequest(handle,1)) { handle.Dispose(); return false; }
                    request=handle;
                }
                finally { Marshal.FreeHGlobal(reason); }
            }
            else { request?.Dispose(); request=null; }
            enabled=value;owner=value?device:null;return true;
        }
    }
    internal void CheckTrust(Func<string,bool> trusted)
    {
        lock(sync) if(enabled&&owner is not null&&!trusted(owner)) Set(false);
    }
    public void Dispose()
    {
        lock(sync) { if(disposed)return;Set(false);request?.Dispose();request=null;enabled=false;owner=null;disposed=true; }
    }
    [StructLayout(LayoutKind.Explicit,Size=32)]
    internal struct ReasonContext
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public IntPtr SimpleReason;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle,int type);
}
