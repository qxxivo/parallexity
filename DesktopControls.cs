using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Media.Control;

namespace PhoneRemote;

public record AudioState(bool Available, int Volume, bool Muted);
public record MediaState(string Id, string App, string Title, string Artist, bool Playing,
    bool CanPlay, bool CanPause, bool CanNext, bool CanPrevious);
public record AppAudioState(string Id, string Name, int Volume, bool Muted, bool Active);
public record RemoteState(string Computer, AudioState Audio, MediaState[] Media, string? MediaError,
    AppAudioState[]? Apps = null, bool InputEnabled = false, PcTimerState? Timer = null, BrowserState? Browser = null, bool ViewingMode = false, ComputerState? ComputerPanel = null);
public record RemoteCommand(string Action, int? Volume = null, string? Session = null,
    string? App = null, int? Dx = null, int? Dy = null, string? Key = null, string? Text = null, string? Drag = null);

public interface IDesktopControls
{
    Task<RemoteState> ReadAsync();
    Task<bool> ExecuteAsync(RemoteCommand command);
}

public sealed class DesktopControls : IDesktopControls
{
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> sessions = new();

    public async Task<RemoteState> ReadAsync()
    {
        await gate.WaitAsync();
        try
        {
            var list = new List<MediaState>();
            string? error = null;
            sessions.Clear();
            try
            {
                manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var counts = new Dictionary<string, int>();
                foreach (var session in manager.GetSessions())
                {
                    try
                    {
                        var source = session.SourceAppUserModelId;
                        counts.TryGetValue(source, out var index);
                        counts[source] = index + 1;
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + ":" + index)))[..24];
                        var properties = await session.TryGetMediaPropertiesAsync();
                        var playback = session.GetPlaybackInfo();
                        var controls = playback.Controls;
                        sessions[id] = session;
                        list.Add(new(id, FriendlyName(source), properties.Title, properties.Artist,
                            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                            controls.IsPlayEnabled, controls.IsPauseEnabled,
                            controls.IsNextEnabled, controls.IsPreviousEnabled));
                    }
                    catch (Exception) { /* A player can disappear while it is being queried. */ }
                }
            }
            catch (Exception)
            {
                error = "Windows пока не передаёт управление плеерами. Громкость работает отдельно.";
                manager = null;
            }
            return new(Environment.MachineName, NativeAudio.Read(), list.ToArray(), error, AudioMixer.Read());
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ExecuteAsync(RemoteCommand command)
    {
        if (command.Action == "display-off") return await Task.Run(DisplayPower.TurnOff);
        if (command.Action == "volume") return NativeAudio.SetVolume(command.Volume!.Value);
        if (command.Action == "mute") return NativeAudio.ToggleMute();
        if (command.Action == "app-volume") return AudioMixer.Change(command.App!, command.Volume);
        if (command.Action == "app-mute") return AudioMixer.Change(command.App!, null);
        if (CommandValidation.IsInput(command.Action)) return NativeInput.Execute(command);
        await gate.WaitAsync();
        try
        {
            if (command.Session is null || !sessions.TryGetValue(command.Session, out var session)) return false;
            return command.Action switch
            {
                "play" => await session.TryPlayAsync(),
                "pause" => await session.TryPauseAsync(),
                "next" => await session.TrySkipNextAsync(),
                "previous" => await session.TrySkipPreviousAsync(),
                _ => false
            };
        }
        catch (Exception) { return false; }
        finally { gate.Release(); }
    }

    private static string FriendlyName(string id)
    {
        if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (id.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (id.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC";
        var file = Path.GetFileNameWithoutExtension(id);
        return file.Length > 48 ? file[..48] + "…" : file;
    }
}

public static class NativeAudio
{
    private static readonly object Sync = new();
    public static AudioState Read() => WithEndpoint(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var volume));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
        return new AudioState(true, (int)Math.Round(volume * 100), muted);
    }, new AudioState(false, 0, false));

    public static bool SetVolume(int value) => WithEndpoint(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 100) / 100f, Guid.Empty));
        return true;
    }, false);

    public static bool ToggleMute() => WithEndpoint(endpoint =>
    {
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
        Marshal.ThrowExceptionForHR(endpoint.SetMute(!muted, Guid.Empty));
        return true;
    }, false);

    private static T WithEndpoint<T>(Func<IAudioEndpointVolume, T> action, T fallback)
    {
        lock (Sync)
        {
            object? enumerator = null, device = null, endpoint = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(0, 1, out var selected));
                device = selected;
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(selected.Activate(ref iid, 23, IntPtr.Zero, out var activated));
                endpoint = activated;
                return action((IAudioEndpointVolume)activated);
            }
            catch (COMException) { return fallback; }
            finally
            {
                foreach (var item in new[] { endpoint, device, enumerator })
                    if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }
    internal static T WithDevice<T>(Func<IMMDevice, T> action, T fallback)
    {
        lock (Sync)
        {
            object? enumerator = null, device = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(0, 1, out var selected));
                device = selected;
                return action(selected);
            }
            catch (COMException) { return fallback; }
            finally
            {
                if (device is not null) Marshal.ReleaseComObject(device);
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            }
        }
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
