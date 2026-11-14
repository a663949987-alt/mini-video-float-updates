using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniVideoFloat;

internal static class WindowRelay
{
    private const long WsExToolWindow = 0x00000080, WsExAppWindow = 0x00040000, WsExNoActivate = 0x08000000;
    internal readonly record struct CropRect(int Left, int Top, int Right, int Bottom);
    internal static (CropRect Top, CropRect BottomLeft) SplitForBadge(CropRect crop, int destinationHeight, int destinationWidth = 155, int badgeHeight = 14, int badgeWidth = 25)
    {
        if (badgeHeight <= 0 || destinationHeight <= badgeHeight || crop.Right - crop.Left < 2 || crop.Bottom - crop.Top < 2) throw new ArgumentException("Video area too small");
        var y = Math.Clamp(crop.Top + (int)Math.Round((crop.Bottom - crop.Top) * (destinationHeight - (double)badgeHeight) / destinationHeight), crop.Top + 1, crop.Bottom - 1);
        var x = Math.Clamp(crop.Left + (int)Math.Round((crop.Right - crop.Left) * (destinationWidth - (double)badgeWidth) / destinationWidth), crop.Left + 1, crop.Right - 1);
        return (new CropRect(crop.Left, crop.Top, crop.Right, y), new CropRect(crop.Left, y, x, crop.Bottom));
    }
    internal static CropRect SubtitleSlice(CropRect crop, int offset, int height, int totalHeight, int displayWidth, int renderedWidth)
    {
        var sourceHeight = Math.Max(1, crop.Bottom - crop.Top);
        var top = Math.Clamp(crop.Top + (int)Math.Round(sourceHeight * (double)offset / totalHeight), crop.Top, crop.Bottom - 1);
        var bottom = Math.Clamp(crop.Top + (int)Math.Round(sourceHeight * (double)(offset + height) / totalHeight), top + 1, crop.Bottom);
        var right = Math.Clamp(crop.Left + (int)Math.Round((crop.Right - crop.Left) * (double)renderedWidth / displayWidth), crop.Left + 1, crop.Right);
        return new CropRect(crop.Left, top, right, bottom);
    }
    internal static bool SubtitleCenterAllowed(CropRect visible, CropRect source, double? centerY)
    {
        if (centerY is not (>= .5 and <= .98) || visible.Bottom <= visible.Top) return false;
        var sourceY = source.Top + (source.Bottom - source.Top) * centerY.Value;
        var position = (sourceY - visible.Top) / (visible.Bottom - visible.Top);
        return position is >= .78 and <= 1.0;
    }
    internal static int SubtitleTop(CropRect visible, CropRect source, double? centerY, int displayHeight, int bandHeight)
    {
        if (!SubtitleCenterAllowed(visible, source, centerY)) return displayHeight - bandHeight;
        var sourceY = source.Top + (source.Bottom - source.Top) * centerY!.Value;
        var displayY = (sourceY - visible.Top) * displayHeight / (visible.Bottom - visible.Top);
        return Math.Clamp((int)Math.Round(displayY - bandHeight / 2d), 0, displayHeight - bandHeight);
    }
    internal static int SubtitleTopInFrame(CropRect visible, CropRect source, double? centerY, int frameHeight, WindowRect placement, int bandHeight, bool anchorBottom)
        => anchorBottom ? frameHeight - bandHeight : placement.Top + SubtitleTop(visible, source, centerY, placement.Height, bandHeight);
    internal static int BottomSubtitleBandHeight(CropRect visible, CropRect source, double? centerY, double? textHeight, int frameHeight, WindowRect placement, int defaultHeight)
    {
        int minimum = Math.Min(defaultHeight, placement.Height);
        if (!SubtitleCenterAllowed(visible, source, centerY) || textHeight is not (> 0 and <= 1)) return minimum;
        double sourceHeight = source.Bottom - source.Top;
        double visibleHeight = Math.Max(1, visible.Bottom - visible.Top);
        double sourceY = source.Top + sourceHeight * centerY!.Value;
        double center = placement.Top + (sourceY - visible.Top) * placement.Height / visibleHeight;
        double originalHeight = sourceHeight * textHeight.Value * placement.Height / visibleHeight;
        int needed = (int)Math.Ceiling(frameHeight - (center - originalHeight / 2 - 3));
        return Math.Clamp(needed, minimum, Math.Min(24, placement.Height));
    }
    internal readonly record struct WindowRect(int Left, int Top, int Width, int Height);
    internal static WindowRect FitInside(CropRect source, int width, int height)
    {
        var sourceWidth = Math.Max(1, source.Right - source.Left);
        var sourceHeight = Math.Max(1, source.Bottom - source.Top);
        var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        int fitWidth = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, width);
        int fitHeight = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, height);
        return new WindowRect((width - fitWidth) / 2, (height - fitHeight) / 2, fitWidth, fitHeight);
    }
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

    internal static bool ResizeWithoutMoving(IntPtr hwnd, int width, int height)
    {
        if (!IsWindow(hwnd) || IsMinimized(hwnd) || width < 1 || height < 1) return false;
        return SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004 | 0x0010 | 0x0200);
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

    internal static bool SetNoActivate(IntPtr hwnd, out bool wasAlreadySet)
    {
        wasAlreadySet = false;
        if (!IsWindow(hwnd)) return false;
        var current = GetWindowLongPtr(hwnd, -20).ToInt64();
        wasAlreadySet = (current & WsExNoActivate) != 0;
        if (wasAlreadySet) return true;
        _ = SetWindowLongPtr(hwnd, -20, new IntPtr(current | WsExNoActivate));
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        return (GetWindowLongPtr(hwnd, -20).ToInt64() & WsExNoActivate) != 0;
    }

    internal static bool TryGetNoActivate(IntPtr hwnd, out bool isSet)
    {
        isSet = false;
        if (!IsWindow(hwnd)) return false;
        isSet = (GetWindowLongPtr(hwnd, -20).ToInt64() & WsExNoActivate) != 0;
        return true;
    }

    internal static void RestoreNoActivate(IntPtr hwnd, bool wasAlreadySet)
    {
        if (wasAlreadySet || !IsWindow(hwnd)) return;
        var current = GetWindowLongPtr(hwnd, -20).ToInt64();
        _ = SetWindowLongPtr(hwnd, -20, new IntPtr(current & ~WsExNoActivate));
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    internal static int Update(IntPtr thumbnail, int left, int top, int width, int height, bool visible, CropRect? sourceCrop = null, byte opacity = 255)
    {
        if (thumbnail == IntPtr.Zero) return unchecked((int)0x80070057);
        var properties = new ThumbnailProperties
        {
            Flags = 0x01 | 0x04 | 0x08 | 0x10 | (sourceCrop.HasValue ? 0x02u : 0u),
            Destination = new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height },
            Source = sourceCrop is { } crop ? new Rect { Left = crop.Left, Top = crop.Top, Right = crop.Right, Bottom = crop.Bottom } : default,
            Opacity = opacity,
            Visible = visible,
            SourceClientAreaOnly = true
        };
        return DwmUpdateThumbnailProperties(thumbnail, ref properties);
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

    // Keep large or long burned-in subtitles inside the available band.
    internal static double AdaptiveSubtitleZoom(CropRect source, int displayWidth, int bandHeight, double requested, double? textWidth, double? textHeight, int badgeWidth = 25)
    {
        if (textWidth is not (> 0 and <= 1) || textHeight is not (> 0 and <= 1) || source.Right <= source.Left || source.Bottom <= source.Top) return requested;
        double widthLimit = Math.Max(1, displayWidth - badgeWidth) / (displayWidth * textWidth.Value * 1.12);
        double nativeHeight = textHeight.Value * (source.Bottom - source.Top) * displayWidth / (source.Right - source.Left);
        double heightLimit = (bandHeight - 2d) / Math.Max(1, nativeHeight * 1.2 * 1.12);
        return Math.Clamp(Math.Min(requested, Math.Min(widthLimit, heightLimit)), 1, requested);
    }

    // Zoom a centred band near the bottom of the detected video; it includes
    // subtitles that are already part of the picture, without OCR.
    internal static CropRect SubtitleBand(CropRect video, int destinationWidth, int destinationHeight, double zoom, double? centerY = null)
    {
        var videoWidth = Math.Max(1, video.Right - video.Left);
        var videoHeight = Math.Max(1, video.Bottom - video.Top);
        var width = Math.Clamp((int)Math.Round(videoWidth / Math.Max(1, zoom)), 1, videoWidth);
        // Stretch vertically without increasing the width occupied by the text.
        var height = Math.Clamp((int)Math.Round(width * destinationHeight / (destinationWidth * 1.2)), 1, videoHeight);
        var left = video.Left + (videoWidth - width) / 2;
        var bottom = video.Bottom - Math.Min(videoHeight - height, Math.Max(0, (int)Math.Round(videoHeight * .02)));
        if (centerY is >= .5 and <= .98)
            bottom = Math.Clamp(video.Top + (int)Math.Round(videoHeight * centerY.Value) + height / 2, video.Top + height, video.Bottom);
        return new CropRect(left, bottom - height, left + width, bottom);
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
