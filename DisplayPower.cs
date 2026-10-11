using System.Runtime.InteropServices;

namespace PhoneRemote;

internal static class DisplayPower
{
    // SC_MONITORPOWER, lParam=2 turns off displays; it does not suspend Windows.
    internal static bool TurnOff() => SendMessageTimeout(new IntPtr(0xffff), 0x0112,
        new UIntPtr(0xf170), new IntPtr(2), 0x0002, 1000, out _) != IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        UIntPtr command, IntPtr value, uint flags, uint timeout, out UIntPtr result);
}
