using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MiniVideoFloat;

internal static class AutoPauseState
{
    private static readonly string StatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "auto-pause.json");
    private sealed record Token(int ProcessId, long ProcessStartedUtcTicks);

    internal static bool Matches(int processId)
    {
        try
        {
            if (!File.Exists(StatePath)) return false;
            var token = JsonSerializer.Deserialize<Token>(File.ReadAllText(StatePath));
            if (token is null || token.ProcessId != processId) { Clear(); return false; }
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks == token.ProcessStartedUtcTicks) return true;
            Clear();
        }
        catch { Clear(); }
        return false;
    }

    internal static void Remember(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var token = new Token(processId, process.StartTime.ToUniversalTime().Ticks);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(token));
        }
        catch (Exception ex) { Debug.WriteLine($"保存自动暂停状态失败: {ex.Message}"); }
    }

    internal static void Clear()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch (Exception ex) { Debug.WriteLine($"清理自动暂停状态失败: {ex.Message}"); }
    }
}
