using System.Runtime.InteropServices;
using System.Text.Json;

namespace MiniVideoFloat;

// Read-only: do not alter RedFruit's progress, login or download database.
internal static class RedFruitProgressReader
{
    internal sealed record Snapshot(int CurrentIndex, string CurrentVideoId, string SeriesTitle, IReadOnlyList<OfflineCachePlanner.Episode> Episodes);

    internal static Snapshot? ReadLatest(string databasePath)
    {
        if (!System.IO.File.Exists(databasePath)) return null;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(databasePath, out db, 1, IntPtr.Zero) != 0 || db == IntPtr.Zero) return null;
            sqlite3_busy_timeout(db, 1000);
            const string query = "SELECT series, episode_index, video_id FROM progress ORDER BY updated_at DESC LIMIT 1";
            if (sqlite3_prepare_v2(db, query, -1, out statement, IntPtr.Zero) != 0 || sqlite3_step(statement) != 100) return null;
            string? series = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0));
            int index = sqlite3_column_int(statement, 1);
            string? currentId = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 2));
            return series is null ? null : Parse(series, index, currentId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or JsonException)
        {
            return null;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close_v2(db);
        }
    }

    internal static Snapshot? Parse(string json, int currentIndex, string? currentVideoId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) return null;
            string title = document.RootElement.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() ?? "" : "";
            var result = new List<OfflineCachePlanner.Episode>();
            foreach (var item in episodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                string id = item.TryGetProperty("videoId", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() ?? "" : "";
                bool locked = !item.TryGetProperty("locked", out var lockedValue) || lockedValue.ValueKind != JsonValueKind.False;
                result.Add(new OfflineCachePlanner.Episode(id, locked, null));
            }
            if (currentIndex < 0 || currentIndex >= result.Count || string.IsNullOrEmpty(currentVideoId) ||
                !string.Equals(result[currentIndex].VideoId, currentVideoId, StringComparison.Ordinal)) return null;
            return new Snapshot(currentIndex, currentVideoId, title, result);
        }
        catch (JsonException) { return null; }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_int(IntPtr statement, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
}
