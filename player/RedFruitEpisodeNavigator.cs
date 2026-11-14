using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MiniVideoFloat;

// Drives only RedFruit's off-screen controls after the user picks a specific item.
internal static class RedFruitEpisodeNavigator
{
    internal static string? Play(IntPtr window, string seriesId, string title, DramaCatalog.Episode episode, RedFruitProgressReader.Snapshot? history, Func<bool> mayContinue, Func<string, string, Func<bool>, string?>? verify = null, bool resumeHistory = false)
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!mayContinue()) return "已取消切换。";
                var result = PlayOnce(window, seriesId, title, episode, history, mayContinue, verify, resumeHistory);
                if (result is null || !result.StartsWith("红果界面暂不可用", StringComparison.Ordinal)) return result;
                Thread.Sleep(500);
            }
            return "红果界面仍在刷新，请重试。";
        }
        finally { TryCloseEpisodePanel(window, mayContinue); }
    }

    private static string? PlayOnce(IntPtr window, string seriesId, string title, DramaCatalog.Episode episode, RedFruitProgressReader.Snapshot? history, Func<bool> mayContinue, Func<string, string, Func<bool>, string?>? verify, bool resumeHistory)
    {
        var phase = "准备";
        try
        {
            var verifyProgress = verify ?? VerifyProgress;
            if (!mayContinue()) return "已取消切换。";
            var root = AutomationElement.FromHandle(window);
            string? ConfirmEpisode() { var error = verifyProgress(seriesId, episode.Id, mayContinue); return error is null || (mayContinue() && StageMatches(root, title, episode.Number)) ? null : error; }
            var renderer = RedFruitWatchRestore.FindRenderer(window);
            if (renderer == IntPtr.Zero) return "红果窗口暂不可用。";
            if (history is null && StageMatches(root, title, episode.Number)) return null;
            if (history is not null)
            {
                var restored = RedFruitWatchRestore.TryRestore(window, history.SeriesTitle, history.CurrentIndex, mayContinue);
                if (restored != RedFruitWatchRestore.Result.Restored) return "没有找到对应的观看记录，请重试。";
                if (resumeHistory) return null;
            }
            else
            {
                phase = "打开搜索";
                if (!OpenSearch(root, renderer, mayContinue)) return "无法打开红果搜索，请重试。";
                var elements = RedFruitWatchRestore.All(root);
                phase = "查找搜索框";
                var inputs = elements.Where(e => e.Current.ControlType == ControlType.Edit && RedFruitWatchRestore.Has(e, "input") && e.TryGetCurrentPattern(ValuePattern.Pattern, out _)).ToArray();
                if (inputs.Length != 1 || !mayContinue()) return "没有找到红果搜索框。";
                var input = (ValuePattern)inputs[0].GetCurrentPattern(ValuePattern.Pattern);
                var existing = input.Current.Value ?? "";
                if (existing.Length > 80) return "红果搜索框内容过长，请先清空后重试。";
                if (!RedFruitWatchRestore.Click(renderer, inputs[0]) || !mayContinue()) return "已取消切换。";
                if (existing.Length > 0)
                {
                    PostMessage(renderer, 0x0100, new IntPtr(0x23), new IntPtr(0x014F0001));
                    PostMessage(renderer, 0x0101, new IntPtr(0x23), new IntPtr(unchecked((int)0xC14F0001)));
                    foreach (var _ in existing)
                    {
                        PostMessage(renderer, 0x0100, new IntPtr(0x08), new IntPtr(0x000E0001));
                        PostMessage(renderer, 0x0102, new IntPtr(0x08), new IntPtr(1));
                        PostMessage(renderer, 0x0101, new IntPtr(0x08), new IntPtr(unchecked((int)0xC00E0001)));
                    }
                    Thread.Sleep(250);
                    if (!mayContinue() || !string.IsNullOrEmpty(input.Current.Value)) return "无法清空红果搜索框，请重试。";
                }
                phase = "输入剧名";
                foreach (var character in title)
                    if (!PostMessage(renderer, 0x0102, new IntPtr(character), new IntPtr(1))) return "无法输入红果搜索词。";
                Thread.Sleep(350);
                if (!mayContinue()) return "已取消切换。";
                if (!string.Equals(input.Current.Value, title, StringComparison.Ordinal)) return "红果未接收搜索词，请重试。";
                var search = RedFruitWatchRestore.All(root).FirstOrDefault(e => e.Current.ControlType == ControlType.Button && e.Current.Name == "搜索" && RedFruitWatchRestore.Has(e, "btn-primary"));
                if (search is null || !RedFruitWatchRestore.Click(renderer, search)) return "无法提交红果搜索。";
                phase = "等待搜索结果";
                AutomationElement? target = null;
                for (int i = 0; i < 60 && target is null; i++)
                {
                    if (!mayContinue()) return "已取消切换。";
                    AutomationElement[] matches;
                    try { matches = RedFruitWatchRestore.FindTitleCards(root, title); }
                    catch (ElementNotAvailableException) { Thread.Sleep(250); continue; }
                    if (matches.Length > 1) return "发现同名短剧，无法安全确定要播放哪一部。";
                    target = matches.SingleOrDefault();
                    if (target is null) Thread.Sleep(250);
                }
                phase = "打开搜索结果";
                if (target is null || !mayContinue() || !RedFruitWatchRestore.ClickTitle(renderer, target, title)) return "红果搜索未找到对应短剧。";
                if (!WaitForStage(root, mayContinue)) return "短剧打开超时，请重试。";
            }

            if (!mayContinue()) return "已取消切换。";
            phase = "确认剧目";
            if (!string.IsNullOrEmpty(seriesId) && verifyProgress(seriesId, "", mayContinue) is not null && !StageMatches(root, title, null)) return "无法确认所选短剧，已停止切集。";
            phase = "打开选集";
            if (!OpenEpisodes(root, renderer, mayContinue)) return "无法打开红果选集，请重试。";
            var range = RedFruitWatchRestore.All(root).Where(e => RedFruitWatchRestore.Has(e, "ep-range")).FirstOrDefault(e => InRange(e.Current.Name, episode.Number));
            if (range is not null && mayContinue())
            {
                if (!EnsureVisible(range, mayContinue) || !RedFruitWatchRestore.Click(renderer, range)) return "该集所在范围暂不可访问。";
                Thread.Sleep(350);
            }
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (!mayContinue()) return "已取消切换。";
                var choices = RedFruitWatchRestore.All(root).Where(e => e.Current.ControlType == ControlType.Button && RedFruitWatchRestore.Has(e, "ep") && RedFruitWatchRestore.Names(e).Any(n => n == episode.Number.ToString())).ToArray();
                if (choices.Length == 1)
                {
                    if (!choices[0].Current.IsEnabled || RedFruitWatchRestore.Has(choices[0], "locked")) return "这一集暂不可播放。";
                    if (RedFruitWatchRestore.Has(choices[0], "active")) { CloseEpisodes(root, renderer); return ConfirmEpisode(); }
                    if (!EnsureVisible(choices[0], mayContinue)) return "该集位置暂不可访问。";
                    if (!RedFruitWatchRestore.Click(renderer, choices[0])) return "该集位置暂不可访问。";
                    for (int check = 0; check < 30; check++)
                    {
                        if (!mayContinue()) return "已取消切换。";
                        var visible = RedFruitWatchRestore.All(root);
                        // RedFruit normally closes its episode drawer immediately after a selection.
                        if (!visible.Any(e => RedFruitWatchRestore.Has(e, "ep-close"))) return ConfirmEpisode();
                        var now = visible.Where(e => e.Current.ControlType == ControlType.Button && RedFruitWatchRestore.Has(e, "ep") && RedFruitWatchRestore.Has(e, "active") && RedFruitWatchRestore.Names(e).Any(n => n == episode.Number.ToString())).ToArray();
                        if (now.Length == 1) { CloseEpisodes(root, renderer); return ConfirmEpisode(); }
                        Thread.Sleep(250);
                    }
                    return "已请求切换，但红果尚未确认播放，请检查画面。";
                }
                Thread.Sleep(250);
            }
            return "红果没有显示这一集，请重试。";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { System.Diagnostics.Debug.WriteLine(ex); return $"红果界面暂不可用（{phase}：{ex.GetType().Name}）。"; }
    }

    private static bool OpenSearch(AutomationElement root, IntPtr renderer, Func<bool> mayContinue)
    {
        bool backPressed = false, clicked = false;
        for (int i = 0; i < 40; i++)
        {
            if (!mayContinue()) return false;
            var elements = RedFruitWatchRestore.All(root);
            if (clicked && elements.Any(e => e.Current.ControlType == ControlType.Edit && RedFruitWatchRestore.Has(e, "input")) && elements.Any(e => e.Current.ControlType == ControlType.Button && e.Current.Name == "搜索")) return true;
            if (!clicked)
            {
                var nav = elements.FirstOrDefault(e => RedFruitWatchRestore.Has(e, "nav-item") && e.Current.ControlType == ControlType.Button && e.Current.Name.Contains("找剧", StringComparison.Ordinal));
                if (nav is not null) clicked = RedFruitWatchRestore.Click(renderer, nav);
                else if (!backPressed)
                {
                    var back = elements.FirstOrDefault(e => e.Current.ControlType == ControlType.Button && e.Current.Name == "← 返回");
                    if (back is not null) backPressed = RedFruitWatchRestore.Click(renderer, back);
                }
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool WaitForStage(AutomationElement root, Func<bool> mayContinue)
    {
        for (int i = 0; i < 50; i++) { if (!mayContinue()) return false; if (RedFruitWatchRestore.All(root).Any(e => RedFruitWatchRestore.Has(e, "stage") && RedFruitWatchRestore.Has(e, "xgplayer"))) return true; Thread.Sleep(250); }
        return false;
    }

    private static bool OpenEpisodes(AutomationElement root, IntPtr renderer, Func<bool> mayContinue)
    {
        for (int i = 0; i < 30; i++)
        {
            if (!mayContinue()) return false;
            var elements = RedFruitWatchRestore.All(root);
            if (elements.Any(e => RedFruitWatchRestore.Has(e, "ep-close")) && elements.Any(e => RedFruitWatchRestore.Has(e, "ep"))) return true;
            var button = elements.FirstOrDefault(e => e.Current.ControlType == ControlType.Button && RedFruitWatchRestore.Has(e, "xg-ep"));
            if (button is not null && RedFruitWatchRestore.Click(renderer, button)) { Thread.Sleep(350); continue; }
            if (i == 2) { PostMessage(renderer, 0x0100, new IntPtr(0x45), new IntPtr(0x00120001)); PostMessage(renderer, 0x0101, new IntPtr(0x45), new IntPtr(unchecked((int)0xC0120001))); }
            Thread.Sleep(250);
        }
        return false;
    }

    private static void CloseEpisodes(AutomationElement root, IntPtr renderer)
    {
        var button = RedFruitWatchRestore.All(root).FirstOrDefault(e => e.Current.ControlType == ControlType.Button && RedFruitWatchRestore.Has(e, "ep-close"));
        if (button is not null) RedFruitWatchRestore.Click(renderer, button);
    }

    internal static bool TryCloseEpisodePanel(IntPtr window, Func<bool> mayContinue)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var renderer = RedFruitWatchRestore.FindRenderer(window);
            if (renderer == IntPtr.Zero) return false;
            for (int attempt = 0; attempt < 8 && mayContinue(); attempt++)
            {
                try
                {
                    var button = RedFruitWatchRestore.All(root).FirstOrDefault(e => e.Current.ControlType == ControlType.Button && RedFruitWatchRestore.Has(e, "ep-close"));
                    if (button is null) return true;
                    if (!RedFruitWatchRestore.Click(renderer, button)) return false;
                }
                catch (ElementNotAvailableException) { }
                Thread.Sleep(150);
            }
            return false;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static bool InRange(string text, int number)
    {
        var parts = text.Split('-');
        return parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end) && number >= start && number <= end;
    }

    private static bool EnsureVisible(AutomationElement element, Func<bool> mayContinue)
    {
        if (!element.Current.IsOffscreen) return true;
        if (!mayContinue() || !element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pattern)) return false;
        ((ScrollItemPattern)pattern).ScrollIntoView();
        Thread.Sleep(250);
        return mayContinue() && !element.Current.IsOffscreen;
    }

    private static bool StageMatches(AutomationElement root, string title, int? episode)
    {
        try
        {
            var all = RedFruitWatchRestore.All(root);
            if (!all.Any(e => RedFruitWatchRestore.Has(e, "stage") && RedFruitWatchRestore.Has(e, "xgplayer"))) return false;
            if (!all.Any(e => e.Current.ControlType == ControlType.Text && string.Equals(e.Current.Name, title, StringComparison.Ordinal))) return false;
            return episode is null || all.Any(e => e.Current.ControlType == ControlType.Text && string.Equals(e.Current.Name, $"第{episode}集", StringComparison.Ordinal));
        }
        catch (ElementNotAvailableException) { return false; }
    }

    internal static bool IsCurrentStage(IntPtr window, string title, int episode)
    {
        try { return StageMatches(AutomationElement.FromHandle(window), title, episode); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static string? VerifyProgress(string seriesId, string episodeId, Func<bool> mayContinue)
    {
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer", "hgplayer.db");
        for (int i = 0; i < 10; i++)
        {
            if (!mayContinue()) return "已取消切换。";
            var current = RedFruitProgressReader.ReadLatest(path);
            if (current is not null && (string.IsNullOrEmpty(episodeId) || current.CurrentVideoId == episodeId) && (string.IsNullOrEmpty(seriesId) || current.SeriesId == seriesId)) return null;
            Thread.Sleep(1000);
        }
        return "红果已收到选集请求，但尚未确认实际播放，请检查画面。";
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
