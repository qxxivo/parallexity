using System.Runtime.InteropServices;

namespace PhoneRemote;

internal static class CommandValidation
{
    internal static readonly Dictionary<string, ushort[]> Keys = new()
    {
        ["enter"] = new ushort[] { 0x0D }, ["escape"] = new ushort[] { 0x1B }, ["tab"] = new ushort[] { 0x09 },
        ["backspace"] = new ushort[] { 0x08 }, ["left"] = new ushort[] { 0x25 }, ["up"] = new ushort[] { 0x26 },
        ["right"] = new ushort[] { 0x27 }, ["down"] = new ushort[] { 0x28 }, ["space"] = new ushort[] { 0x20 },
        ["copy"] = new ushort[] { 0x11, 0x43 }, ["paste"] = new ushort[] { 0x11, 0x56 },
        ["select-all"] = new ushort[] { 0x11, 0x41 }, ["undo"] = new ushort[] { 0x11, 0x5A }
    };
    public static bool IsInput(string action) => action is "pointer" or "click" or "right-click" or "double-click" or "scroll" or "key" or "text";
    public static string? Error(RemoteCommand command)
    {
        if (command.Action is not ("volume" or "mute" or "play" or "pause" or "next" or "previous" or "app-volume" or "app-mute") && !IsInput(command.Action)) return "Неизвестная команда.";
        if (command.Action is "volume" or "app-volume" && (command.Volume is null || command.Volume < 0 || command.Volume > 100)) return "Громкость должна быть от 0 до 100.";
        if (command.Action is "play" or "pause" or "next" or "previous" && (string.IsNullOrEmpty(command.Session) || command.Session.Length > 100)) return "Выбери плеер.";
        if (command.Action is "app-volume" or "app-mute" && (string.IsNullOrEmpty(command.App) || command.App.Length > 100)) return "Выбери приложение.";
        if (command.Action == "pointer" && (command.Dx is null || command.Dy is null || Math.Abs((long)command.Dx.Value) > 200 || Math.Abs((long)command.Dy.Value) > 200)) return "Слишком большое перемещение курсора.";
        if (command.Action == "scroll" && (command.Dy is null || Math.Abs((long)command.Dy.Value) > 1200)) return "Некорректная прокрутка.";
        if (command.Action == "key" && (command.Key is null || !Keys.ContainsKey(command.Key))) return "Эта клавиша не поддерживается.";
        if (command.Action == "text" && (string.IsNullOrEmpty(command.Text) || command.Text.Length > 500 || command.Text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))) return "Введи текст длиной до 500 символов.";
        return null;
    }
}

internal static class NativeInput
{
    internal static INPUT[] Build(RemoteCommand command)
    {
        INPUT Mouse(int dx = 0, int dy = 0, uint flags = 1, uint data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Dx = dx, Dy = dy, Flags = flags, MouseData = data } } };
        INPUT Key(ushort key, ushort scan = 0, uint flags = 0) => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Scan = scan, Flags = flags } } };
        var result = new List<INPUT>();
        void Chord(ushort[] keys)
        {
            foreach (var key in keys) result.Add(Key(key, flags: key is >= 0x25 and <= 0x28 ? 1u : 0));
            foreach (var key in keys.Reverse()) result.Add(Key(key, flags: (key is >= 0x25 and <= 0x28 ? 1u : 0) | 2));
        }
        switch (command.Action)
        {
            case "pointer": result.Add(Mouse(command.Dx!.Value, command.Dy!.Value)); break;
            case "scroll": result.Add(Mouse(flags: 0x0800, data: unchecked((uint)command.Dy!.Value))); break;
            case "click": result.Add(Mouse(flags: 2)); result.Add(Mouse(flags: 4)); break;
            case "right-click": result.Add(Mouse(flags: 8)); result.Add(Mouse(flags: 16)); break;
            case "double-click": for (var i = 0; i < 2; i++) { result.Add(Mouse(flags: 2)); result.Add(Mouse(flags: 4)); } break;
            case "key": Chord(CommandValidation.Keys[command.Key!]); break;
            case "text":
                foreach (var character in command.Text!)
                {
                    if (character == '\r') continue;
                    if (character == '\n') { Chord(new ushort[] { 0x0D }); continue; }
                    if (character == '\t') { Chord(new ushort[] { 0x09 }); continue; }
                    result.Add(Key(0, character, 4)); result.Add(Key(0, character, 6));
                }
                break;
        }
        return result.ToArray();
    }
    public static bool Execute(RemoteCommand command)
    {
        if (CommandValidation.Error(command) is not null) return false;
        var events = Build(command);
        if (events.Length == 0) return false;
        var inserted = SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());
        if (inserted == events.Length) return true;
        // If Windows accepts only part of a batch, make a best-effort release.
        var releases = events.Where(e => (e.Type == 1 && (e.Data.Keyboard.Flags & 2) != 0) ||
            (e.Type == 0 && (e.Data.Mouse.Flags is 4 or 16))).ToArray();
        if (releases.Length > 0) SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
        return false;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public UNION Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct UNION { [FieldOffset(0)] public MOUSE Mouse; [FieldOffset(0)] public KEYBOARD Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSE { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBOARD { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
}
