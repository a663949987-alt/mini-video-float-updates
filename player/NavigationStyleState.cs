using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MiniVideoFloat;

// Records only our temporary no-activation change so it can be undone after a crash.
internal static class NavigationStyleState
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "navigation-style.json");
    private sealed record Token(int ProcessId, long StartedUtcTicks);

    internal static bool TryEnter(IntPtr window, int processId, out bool wasAlreadySet)
    {
        wasAlreadySet = false;
        if (!WindowRelay.TryGetNoActivate(window, out var existing)) return false;
        if (existing) { wasAlreadySet = true; return true; }
        try
        {
            using var process = Process.GetProcessById(processId);
            var token = new Token(processId, process.StartTime.ToUniversalTime().Ticks);
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            var temporary = PathName + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(token));
            File.Move(temporary, PathName, true);
            if (WindowRelay.SetNoActivate(window, out var actualPrevious)) { wasAlreadySet = actualPrevious; if (actualPrevious) Clear(); return true; }
            Clear(); return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Debug.WriteLine($"保存窗口恢复状态失败: {ex.Message}"); return false; }
    }

    internal static void Leave(IntPtr window, bool wasAlreadySet)
    {
        WindowRelay.RestoreNoActivate(window, wasAlreadySet);
        if (!wasAlreadySet) Clear();
    }

    internal static void Recover(IntPtr window, int processId)
    {
        try
        {
            if (!File.Exists(PathName)) return;
            var token = JsonSerializer.Deserialize<Token>(File.ReadAllText(PathName));
            if (token is not null && token.ProcessId == processId)
            {
                using var process = Process.GetProcessById(processId);
                if (process.StartTime.ToUniversalTime().Ticks == token.StartedUtcTicks) WindowRelay.RestoreNoActivate(window, false);
            }
            Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { Debug.WriteLine($"恢复窗口状态失败: {ex.Message}"); }
    }

    private static void Clear()
    {
        try { if (File.Exists(PathName)) File.Delete(PathName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine($"清理窗口恢复状态失败: {ex.Message}"); }
    }
}
