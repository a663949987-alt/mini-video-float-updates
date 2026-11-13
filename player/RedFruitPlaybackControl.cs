using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniVideoFloat;

internal static class RedFruitPlaybackControl
{
    internal enum ChangeResult { Unavailable, AlreadySet, Changed, Unconfirmed }

    // RedFruit's default shortcut for the next item is ArrowDown. This posts
    // messages only to its own WebView and does not send a system-wide key.
    internal static bool NextEpisode(IntPtr window)
    {
        try
        {
            if (FindStage(AutomationElement.FromHandle(window)) is null) return false;
            var renderer = FindRenderer(window);
            if (renderer == IntPtr.Zero) return false;
            var keyDown = new IntPtr(0x01500001); // extended ArrowDown, scan code 0x50
            var keyUp = new IntPtr(unchecked((int)0xc1500001));
            return PostMessage(renderer, 0x0100, new IntPtr(0x28), keyDown) &&
                   PostMessage(renderer, 0x0101, new IntPtr(0x28), keyUp);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    internal static bool? GetPaused(IntPtr window)
    {
        try
        {
            var stage = FindStage(AutomationElement.FromHandle(window));
            return stage is null ? null : ReadPaused(stage);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // Read the actual state first; post a click to the WebView without moving the
    // system pointer or activating RedFruit. InvokePattern would steal focus.
    internal static ChangeResult SetPaused(IntPtr window, bool paused)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var stage = FindStage(root);
            if (stage is null) return ChangeResult.Unavailable;
            var state = ReadPaused(stage);
            if (state is null) return ChangeResult.Unavailable;
            if (state.Value == paused) return ChangeResult.AlreadySet;
            var bounds = stage.Current.BoundingRectangle;
            var renderer = FindRenderer(window);
            if (renderer == IntPtr.Zero || bounds.IsEmpty || bounds.Width < 10 || bounds.Height < 10) return ChangeResult.Unavailable;
            var point = new Point { X = (int)Math.Round(bounds.Left + bounds.Width / 2), Y = (int)Math.Round(bounds.Top + bounds.Height / 2) };
            if (!ScreenToClient(renderer, ref point) || !GetClientRect(renderer, out var client) ||
                point.X < 0 || point.Y < 0 || point.X >= client.Right || point.Y >= client.Bottom || point.X > short.MaxValue || point.Y > short.MaxValue)
                return ChangeResult.Unavailable;
            var position = new IntPtr((point.Y << 16) | (point.X & 0xffff));
            if (!PostMessage(renderer, 0x0200, IntPtr.Zero, position) ||
                !PostMessage(renderer, 0x0201, new IntPtr(1), position) ||
                !PostMessage(renderer, 0x0202, IntPtr.Zero, position)) return ChangeResult.Unconfirmed;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(100);
                var current = GetPaused(window);
                if (current == paused) return ChangeResult.Changed;
            }
            return ChangeResult.Unconfirmed;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return ChangeResult.Unavailable;
        }
    }

    private static AutomationElement? FindStage(AutomationElement root)
    {
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement element in elements)
        {
            var classes = element.Current.ClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (classes.Contains("xgplayer", StringComparer.Ordinal) && classes.Contains("stage", StringComparer.Ordinal)) return element;
        }
        return null;
    }

    private static bool? ReadPaused(AutomationElement stage)
    {
        var classes = stage.Current.ClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes.Contains("xgplayer-pause", StringComparer.Ordinal)) return true;
        if (classes.Contains("xgplayer-playing", StringComparer.Ordinal)) return false;
        return null;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    private static IntPtr FindRenderer(IntPtr parent)
    {
        var result = IntPtr.Zero;
        EnumChildWindows(parent, (hwnd, _) =>
        {
            var name = new StringBuilder(128);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "Chrome_RenderWidgetHostHWND") return true;
            result = hwnd; return false;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
