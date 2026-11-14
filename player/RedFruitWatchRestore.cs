using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MiniVideoFloat;

// Used only when this player has just started RedFruit. Existing RedFruit
// sessions must never be navigated away from their current video.
internal static class RedFruitWatchRestore
{
    internal enum Result { Restored, NotFound, Interrupted, Unavailable }

    internal static Result TryRestore(IntPtr window, string title, int episodeIndex, Func<bool> mayContinue, Action<string>? diagnostic = null)
    {
        if (string.IsNullOrWhiteSpace(title) || !mayContinue()) return Result.Interrupted;
        bool historyOpened = false, cardClicked = false;
        IntPtr renderer = IntPtr.Zero;
        try
        {
            renderer = FindRenderer(window);
            if (renderer == IntPtr.Zero) { diagnostic?.Invoke("没有找到红果渲染窗口"); return Result.Unavailable; }
            var root = AutomationElement.FromHandle(window);
            AutomationElement? history = null;
            bool backPressed = false;
            for (var attempt = 0; attempt < 40 && history is null; attempt++)
            {
                if (!mayContinue()) return Result.Interrupted;
                AutomationElement[] elements;
                try
                {
                    elements = All(root);
                    history = elements.SingleOrDefault(e => Has(e, "nav-item") && e.Current.ControlType == ControlType.Button && e.Current.Name.Contains("历史", StringComparison.Ordinal));
                }
                catch (ElementNotAvailableException) { Thread.Sleep(250); continue; }
                if (history is null && !backPressed)
                {
                    var back = elements.FirstOrDefault(e => e.Current.ControlType == ControlType.Button && e.Current.Name == "← 返回");
                    if (back is not null && Click(renderer, back)) backPressed = true;
                }
                if (history is null) Thread.Sleep(250);
            }
            if (history is null || !mayContinue() || (!Has(history, "active") && !Click(renderer, history))) { diagnostic?.Invoke("无法打开历史页"); return Result.Unavailable; }
            historyOpened = true;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (!mayContinue()) return Result.Interrupted;
                AutomationElement[] cards;
                try { cards = FindTitleCards(root, title); }
                catch (ElementNotAvailableException) { Thread.Sleep(250); continue; }
                if (cards.Length > 1) return Result.NotFound;
                if (cards.Length == 1)
                {
                    // RedFruit's visible history may lag behind its database while
                    // the app is running. Open the unique history card and let
                    // RedFruit resume its own position rather than rejecting it.
                    try { if (!ClickTitle(renderer, cards[0], title)) { Thread.Sleep(250); continue; } }
                    catch (ElementNotAvailableException) { Thread.Sleep(250); continue; }
                    cardClicked = true;
                    for (var retry = 0; retry < 40; retry++)
                    {
                        if (!mayContinue()) return Result.Interrupted;
                        try { if (All(root).Any(e => Has(e, "stage") && Has(e, "xgplayer"))) return Result.Restored; }
                        catch (ElementNotAvailableException) { }
                        Thread.Sleep(250);
                    }
                    diagnostic?.Invoke("点击历史后未进入视频画面"); return Result.Unavailable;
                }
                Thread.Sleep(250);
            }
            return Result.NotFound;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { diagnostic?.Invoke($"恢复操作异常：{ex.GetType().Name}"); return Result.Unavailable; }
        finally
        {
            // If the history page was opened but no matching item was selected,
            // return to the prior page rather than leaving the relay on a list.
            if (historyOpened && !cardClicked && renderer != IntPtr.Zero && mayContinue())
            {
                PostMessage(renderer, 0x0104, new IntPtr(0x25), new IntPtr(0x214B0001));
                PostMessage(renderer, 0x0105, new IntPtr(0x25), new IntPtr(unchecked((int)0xE14B0001)));
            }
        }
    }

    internal static AutomationElement[] All(AutomationElement root) => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
    internal static IEnumerable<string> Names(AutomationElement e) => new[] { e.Current.Name }.Concat(All(e).Select(x => x.Current.Name));
    internal static AutomationElement[] FindTitleCards(AutomationElement root, string title) => All(root).Where(e => Has(e, "card") && Names(e).Any(n => string.Equals(n, title, StringComparison.Ordinal))).ToArray();
    internal static bool Has(AutomationElement e, string cls) => e.Current.ClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls, StringComparer.Ordinal);
    internal static bool ClickTitle(IntPtr renderer, AutomationElement card, string title)
    {
        // The middle of a hovered card may be covered by a "details" button.
        // The visible title belongs to the same card and triggers its play action.
        var titles = All(card).Where(e => e.Current.ControlType == ControlType.Text && string.Equals(e.Current.Name, title, StringComparison.Ordinal)).ToArray();
        return titles.Length == 1 && Click(renderer, titles[0]);
    }
    internal static bool Click(IntPtr renderer, AutomationElement element)
    {
        if (element.Current.IsOffscreen) return false;
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty) return false;
        var point = new Point { X = (int)(rect.Left + rect.Width / 2), Y = (int)(rect.Top + rect.Height / 2) };
        if (!ScreenToClient(renderer, ref point) || !GetClientRect(renderer, out var client) || point.X < 0 || point.Y < 0 || point.X >= client.Right || point.Y >= client.Bottom || point.X > short.MaxValue || point.Y > short.MaxValue) return false;
        var position = new IntPtr((point.Y << 16) | (point.X & 0xffff));
        return PostMessage(renderer, 0x0200, IntPtr.Zero, position) && PostMessage(renderer, 0x0201, new IntPtr(1), position) && PostMessage(renderer, 0x0202, IntPtr.Zero, position);
    }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    internal static IntPtr FindRenderer(IntPtr source)
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
