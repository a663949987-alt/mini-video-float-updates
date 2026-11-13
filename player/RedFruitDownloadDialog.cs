using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MiniVideoFloat;

// The closed-source player does not expose a public download API. This helper
// operates only its own download dialog and never activates its window.
// A caller must enforce disk limits and serialize this with playback controls.
internal static class RedFruitDownloadDialog
{
    internal enum Result { Unavailable, Interrupted, NoWork, Submitted, Unconfirmed }

    internal static Result QueueNext(IntPtr source, int expectedIndex, string expectedTitle, Func<bool> mayContinue)
    {
        bool opened = false, submitted = false;
        AutomationElement? root = null;
        IntPtr renderer = IntPtr.Zero;
        try
        {
            if (!mayContinue() || RedFruitPlaybackControl.GetPaused(source) != true) return Result.Interrupted;
            root = AutomationElement.FromHandle(source);
            renderer = FindRenderer(source);
            if (renderer == IntPtr.Zero) return Result.Unavailable;
            var all = Find(root);
            if (FindMask(all) is not null) return Result.Unavailable;
            var button = all.SingleOrDefault(e => Has(e, "rail-item") && e.Current.Name == "下载");
            if (button is null || !mayContinue() || !Click(renderer, button)) return Result.Unavailable;
            opened = true;
            AutomationElement? mask = null;
            for (int attempt = 0; attempt < 20 && mask is null; attempt++)
            {
                Thread.Sleep(100);
                mask = FindMask(Find(root));
            }
            if (mask is null) return Result.Unconfirmed;
            if (!mayContinue()) return Result.Interrupted;
            var elements = Find(mask);
            var title = elements.SingleOrDefault(e => Has(e, "modal-title"));
            if (string.IsNullOrWhiteSpace(expectedTitle) || title is null ||
                !(title.Current.Name.Contains(expectedTitle, StringComparison.Ordinal) || Find(title).Any(e => e.Current.Name.Contains(expectedTitle, StringComparison.Ordinal))))
                return Result.Unavailable;
            var episodes = elements.Where(e => Has(e, "dl-ep")).ToArray();
            int current = Array.FindIndex(episodes, e => Has(e, "cur"));
            if (current != expectedIndex || current < 0 || current + 2 >= episodes.Length) return Result.Unavailable;
            if (episodes.Any(e => Has(e, "on"))) return Result.Unavailable;
            int selected = 0;
            for (int i = current + 1; i <= current + 2; i++)
            {
                var episode = episodes[i];
                // Already downloading or completed entries are disabled by RedFruit.
                if (Has(episode, "off") || Has(episode, "done")) continue;
                if (!mayContinue() || !Click(renderer, episode)) return Result.Interrupted;
                Thread.Sleep(250);
                selected++;
            }
            if (selected == 0) return Result.NoWork;
            elements = Find(mask);
            episodes = elements.Where(e => Has(e, "dl-ep")).ToArray();
            if (episodes.Count(e => Has(e, "on")) != selected ||
                episodes.Where(e => Has(e, "on")).Any(e => !episodes.Skip(current + 1).Take(2).Contains(e)))
                return Result.Unconfirmed;
            var submit = elements.SingleOrDefault(e => e.Current.ControlType == ControlType.Button && Has(e, "btn-primary"));
            if (!mayContinue() || submit is null || !submit.Current.IsEnabled || !Click(renderer, submit)) return Result.Interrupted;
            submitted = true;
            Thread.Sleep(600);
            return FindMask(Find(root)) is null ? Result.Submitted : Result.Unconfirmed;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return Result.Unavailable;
        }
        finally
        {
            if (opened && !submitted && root is not null && renderer != IntPtr.Zero)
            {
                try
                {
                    var mask = FindMask(Find(root));
                    var cancel = mask is null ? null : Find(mask).SingleOrDefault(e => e.Current.ControlType == ControlType.Button && e.Current.Name == "取消");
                    if (cancel is not null) Click(renderer, cancel);
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
            }
        }
    }

    private static AutomationElement[] Find(AutomationElement e) => e.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
    private static bool Has(AutomationElement e, string cls) => e.Current.ClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls, StringComparer.Ordinal);
    private static AutomationElement? FindMask(AutomationElement[] all) => all.SingleOrDefault(e => Has(e, "dl-mask"));
    private static bool Click(IntPtr renderer, AutomationElement element)
    {
        var b = element.Current.BoundingRectangle;
        if (b.IsEmpty) return false;
        var p = new Point { X = (int)(b.Left + b.Width / 2), Y = (int)(b.Top + b.Height / 2) };
        if (!ScreenToClient(renderer, ref p) || !GetClientRect(renderer, out var r) || p.X < 0 || p.Y < 0 || p.X >= r.Right || p.Y >= r.Bottom || p.X > short.MaxValue || p.Y > short.MaxValue) return false;
        var pos = new IntPtr((p.Y << 16) | (p.X & 0xffff));
        return PostMessage(renderer, 0x0200, IntPtr.Zero, pos) && PostMessage(renderer, 0x0201, new IntPtr(1), pos) && PostMessage(renderer, 0x0202, IntPtr.Zero, pos);
    }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    private static IntPtr FindRenderer(IntPtr source)
    {
        var result = IntPtr.Zero;
        EnumChildWindows(source, (h, _) => { var name = new StringBuilder(128); GetClassName(h, name, name.Capacity); if (name.ToString() != "Chrome_RenderWidgetHostHWND") return true; result = h; return false; }, IntPtr.Zero);
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
