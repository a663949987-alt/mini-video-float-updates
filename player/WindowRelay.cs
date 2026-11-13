using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniVideoFloat;

internal static class WindowRelay
{
    private const long WsExToolWindow = 0x00000080, WsExAppWindow = 0x00040000;
    internal readonly record struct CropRect(int Left, int Top, int Right, int Bottom);
    internal readonly record struct WindowRect(int Left, int Top, int Width, int Height);
    internal sealed record WindowInfo(IntPtr Handle, int ProcessId, string Title, string ProcessName)
    {
        public string DisplayName => $"{Title}  —  {ProcessName}";
        public override string ToString() => DisplayName;
    }

    internal static List<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var text = new StringBuilder(512);
            if (GetWindowText(hwnd, text, text.Capacity) <= 0) return true;
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == Environment.ProcessId) return true;
            var title = text.ToString().Trim();
            try
            {
                using var process = Process.GetProcessById((int)processId);
                result.Add(new WindowInfo(hwnd, (int)processId, title, process.ProcessName));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result.OrderByDescending(w => w.Title.Contains("红果", StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.ProcessName, StringComparer.CurrentCultureIgnoreCase).ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static int Register(IntPtr destination, IntPtr source, out IntPtr thumbnail) => DwmRegisterThumbnail(destination, source, out thumbnail);
    internal static void Unregister(IntPtr thumbnail) { if (thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(thumbnail); }
    internal static bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowNative(hwnd);
    internal static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);
    internal static bool Minimize(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        _ = ShowWindow(hwnd, 7); // SW_SHOWMINNOACTIVE
        return IsMinimized(hwnd);
    }
    internal static bool RestoreMinimized(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        _ = ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        return !IsMinimized(hwnd);
    }

    internal static bool MoveOffscreen(IntPtr hwnd, out WindowRect original)
    {
        original = default;
        if (!IsWindow(hwnd) || IsMinimized(hwnd) || !GetWindowRect(hwnd, out var rect)) return false;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;
        original = new WindowRect(rect.Left, rect.Top, width, height);
        var virtualRight = GetSystemMetrics(76) + GetSystemMetrics(78);
        var virtualTop = GetSystemMetrics(77);
        return SetWindowPos(hwnd, IntPtr.Zero, virtualRight + 64, virtualTop + 32, width, height, 0x0004 | 0x0010 | 0x0200);
    }

    internal static bool RestorePosition(IntPtr hwnd, WindowRect rect)
    {
        if (!IsWindow(hwnd)) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, 0x0004 | 0x0010 | 0x0200);
    }

    internal static bool HideFromAltTab(IntPtr hwnd, out long originalStyle)
    {
        originalStyle = 0;
        if (!IsWindow(hwnd)) return false;
        originalStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        var updatedStyle = (originalStyle & ~WsExAppWindow) | WsExToolWindow;
        if (SetWindowLongPtr(hwnd, -20, new IntPtr(updatedStyle)) == IntPtr.Zero && GetWindowLongPtr(hwnd, -20).ToInt64() != updatedStyle)
            return false;
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        return (GetWindowLongPtr(hwnd, -20).ToInt64() & WsExToolWindow) != 0;
    }

    internal static bool RestoreExtendedStyle(IntPtr hwnd, long style)
    {
        if (!IsWindow(hwnd)) return false;
        _ = SetWindowLongPtr(hwnd, -20, new IntPtr(style));
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        return GetWindowLongPtr(hwnd, -20).ToInt64() == style;
    }

    internal static void Update(IntPtr thumbnail, int left, int top, int width, int height, bool visible, CropRect? sourceCrop = null, byte opacity = 255)
    {
        if (thumbnail == IntPtr.Zero) return;
        var properties = new ThumbnailProperties
        {
            Flags = 0x01 | 0x04 | 0x08 | 0x10 | (sourceCrop.HasValue ? 0x02u : 0u),
            Destination = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height },
            Source = sourceCrop is { } crop ? new Rect { Left = crop.Left, Top = crop.Top, Right = crop.Right, Bottom = crop.Bottom } : default,
            Opacity = opacity,
            Visible = visible,
            SourceClientAreaOnly = true
        };
        DwmUpdateThumbnailProperties(thumbnail, ref properties);
    }

    internal static CropRect CenterCrop(IntPtr thumbnail, int ratioWidth, int ratioHeight)
    {
        if (DwmQueryThumbnailSourceSize(thumbnail, out var size) < 0 || size.Width <= 0 || size.Height <= 0) return default;
        var width = size.Width; var height = size.Height;
        if ((double)width / height > (double)ratioWidth / ratioHeight)
            width = Math.Max(1, (int)Math.Round(height * (double)ratioWidth / ratioHeight));
        else
            height = Math.Max(1, (int)Math.Round(width * (double)ratioHeight / ratioWidth));
        var left = (size.Width - width) / 2; var top = (size.Height - height) / 2;
        return new CropRect(left, top, left + width, top + height);
    }

    internal static CropRect? ClampCrop(CropRect crop, IntPtr thumbnail)
    {
        if (DwmQueryThumbnailSourceSize(thumbnail, out var size) < 0) return null;
        var left = Math.Clamp(crop.Left, 0, Math.Max(0, size.Width - 1));
        var top = Math.Clamp(crop.Top, 0, Math.Max(0, size.Height - 1));
        var right = Math.Clamp(crop.Right, left + 1, size.Width);
        var bottom = Math.Clamp(crop.Bottom, top + 1, size.Height);
        return new CropRect(left, top, right, bottom);
    }

    internal static CropRect FitCrop(CropRect crop, int ratioWidth, int ratioHeight)
    {
        if (ratioWidth <= 0 || ratioHeight <= 0) return crop;
        var width = Math.Max(1, crop.Right - crop.Left);
        var height = Math.Max(1, crop.Bottom - crop.Top);
        var targetRatio = ratioWidth / (double)ratioHeight;
        if (width / (double)height > targetRatio)
        {
            var fittedWidth = Math.Max(1, (int)Math.Round(height * targetRatio));
            var left = crop.Left + (width - fittedWidth) / 2;
            return new CropRect(left, crop.Top, left + fittedWidth, crop.Bottom);
        }
        else
        {
            var fittedHeight = Math.Max(1, (int)Math.Round(width / targetRatio));
            var top = crop.Top + (height - fittedHeight) / 2;
            return new CropRect(crop.Left, top, crop.Right, top + fittedHeight);
        }
    }

    internal static (int Width, int Height) GetSourceSize(IntPtr thumbnail)
    {
        return DwmQueryThumbnailSourceSize(thumbnail, out var size) >= 0 ? (size.Width, size.Height) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    {
        public uint Flags;
        public Rect Destination;
        public Rect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool IsWindowNative(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);
}
