using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PhoneRemote;

internal static class AudioMixer
{
    public static AppAudioState[] Read()
    {
        var list = new List<AppAudioState>();
        Visit((control, volume, id) =>
        {
            if (control.GetState(out var state) < 0 || state == 2) return;
            if (volume.GetMasterVolume(out var level) < 0 || volume.GetMute(out var muted) < 0) return;
            control.GetProcessId(out var pid);
            var name = "Приложение";
            if (control.IsSystemSoundsSession() == 0) name = "Системные звуки";
            else
            {
                try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
                catch (ArgumentException) { return; }
                catch (InvalidOperationException) { return; }
            }
            list.Add(new(id, name, (int)Math.Round(level * 100), muted, state == 1));
        });
        return list.OrderByDescending(x => x.Active).ThenBy(x => x.Name).ToArray();
    }
    public static bool Change(string id, int? level)
    {
        var success = false;
        Visit((_, volume, sessionId) =>
        {
            if (id != sessionId) return;
            if (level is not null) success = volume.SetMasterVolume(level.Value / 100f, Guid.Empty) >= 0;
            else if (volume.GetMute(out var muted) >= 0) success = volume.SetMute(!muted, Guid.Empty) >= 0;
        });
        return success;
    }
    private static void Visit(Action<IAudioSessionControl2, ISimpleAudioVolume, string> visitor)
    {
        NativeAudio.WithDevice(device =>
        {
            object? managerObject = null;
            IAudioSessionEnumerator? enumerator = null;
            try
            {
                var iid = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out managerObject));
                var manager = (IAudioSessionManager2)managerObject;
                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out enumerator));
                Marshal.ThrowExceptionForHR(enumerator.GetCount(out var count));
                for (var i = 0; i < count; i++)
                {
                    object? session = null;
                    try
                    {
                        if (enumerator.GetSession(i, out session) < 0) continue;
                        var control = (IAudioSessionControl2)session;
                        if (control.GetSessionInstanceIdentifier(out var identifier) < 0) continue;
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)))[..24];
                        visitor(control, (ISimpleAudioVolume)session, id);
                    }
                    catch (COMException) { }
                    finally { if (session is not null) Marshal.ReleaseComObject(session); }
                }
                return true;
            }
            finally
            {
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
                if (managerObject is not null) Marshal.ReleaseComObject(managerObject);
            }
        }, false);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float volume, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMasterVolume(out float volume);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
