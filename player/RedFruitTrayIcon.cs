using System.Runtime.InteropServices;

namespace MiniVideoFloat;

internal static class RedFruitTrayIcon
{
    // This class and ID belong to the currently installed RedFruit desktop app.
    // Verify the owner process before touching the notification icon.
    internal static bool Hide(int processId)
    {
        if (processId <= 0) return false;
        foreach (var parent in new[] { IntPtr.Zero, new IntPtr(-3) })
        {
            var hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowEx(parent, hwnd, "HgPlayerTrayWindow", null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out var owner);
                if (owner != processId) continue;
                var identity = new NotifyIconIdentifier { Size = Marshal.SizeOf<NotifyIconIdentifier>(), Window = hwnd, Id = 1 };
                if (ShellNotifyIconGetRect(ref identity, out _) != 0) return true;
                var data = new NotifyIconData
                {
                    Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = hwnd, Id = 1,
                    Tip = "", Info = "", InfoTitle = ""
                };
                return ShellNotifyIcon(2, ref data);
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public int Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
