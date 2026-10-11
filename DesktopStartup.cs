using Microsoft.Win32;
using System.Security.Principal;

namespace PhoneRemote;

internal static class DesktopStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command(string path) => "\"" + Path.GetFullPath(path) + "\" --startup";
    internal static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("parallexity") as string == Command(AppUpdates.LaunchPath); }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("parallexity", Command(AppUpdates.LaunchPath));
            else key.DeleteValue("parallexity", false);
        }
    }
}

// One instance per Windows user/session, including copies in other folders.
internal sealed class DesktopInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activate;
    private bool owns;
    internal DesktopInstance(string? testName = null)
    {
        var name = @"Local\parallexity-" + (testName ?? WindowsIdentity.GetCurrent().User!.Value);
        mutex = new Mutex(false, name);
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-activate");
    }
    internal bool Acquire(int timeout = 3000)
    {
        try { return owns = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return owns = true; }
    }
    internal void RequestOpen() => activate.Set();
    internal bool OpenRequested() => activate.WaitOne(0);
    public void Dispose() { if (owns) { mutex.ReleaseMutex(); owns = false; } activate.Dispose(); mutex.Dispose(); }
}
