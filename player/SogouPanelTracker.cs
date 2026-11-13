using System.Runtime.InteropServices;
using System.Text;

namespace MiniVideoFloat;

internal static class SogouPanelTracker
{
    internal readonly record struct Bounds(int Left, int Top, int Right, int Bottom);
    private delegate bool EnumProc(nint hwnd, nint data);

    internal static Bounds? Find()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return null;
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        Bounds? status = null, candidate = null;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var process);
            if (process != foregroundProcess) return true;
            var name = new StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            var cls = name.ToString();
            if (cls != "SoPY_Status" && cls != "SoPY_Comp" && cls != "SoPY_Comp2" && cls != "SoPY_Cand") return true;
            if (!GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            var bounds = new Bounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (cls == "SoPY_Status") status = bounds; else candidate ??= bounds;
            return true;
        }, 0);
        return status ?? candidate;
    }

    internal static void Place(nint hwnd, Bounds anchor)
    {
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(anchor.Left, anchor.Top, Math.Max(1, anchor.Right - anchor.Left), Math.Max(1, anchor.Bottom - anchor.Top)));
        var work = screen.WorkingArea;
        var x = Math.Clamp(anchor.Left, work.Left, Math.Max(work.Left, work.Right - 261));
        // 搜狗状态皮肤的窗口顶部带透明留白，向下补偿后贴近可见皮肤。
        var y = anchor.Top - 191 + 27;
        if (y < work.Top) y = anchor.Bottom + 4;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - 191));
        SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
