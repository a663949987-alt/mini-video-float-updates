using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MiniVideoFloat;

// Search and episode metadata only. Playback stays in the existing RedFruit client.
internal static class DramaCatalog
{
    internal sealed record Series(string Id, string Title, int EpisodeCount);
    internal sealed record Episode(int Number, string Id, bool Playable);
    internal sealed record RankedSeries(Series Series, int Position, string Score);
    internal sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor, bool HasMore);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<Page<Series>> SearchAsync(string query, string? cursor, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length is 0 or > 80 || query.Any(char.IsControl)) throw new ArgumentException("请输入 1 至 80 个字的剧名。");
        var args = new List<string> { "search", query, "--json" };
        if (!string.IsNullOrEmpty(cursor)) { args.Add("--cursor"); args.Add(cursor); }
        using var json = await RunAsync(args, token);
        return ParseSearch(json.RootElement);
    }

    internal static Page<Series> ParseSearch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("搜索结果格式无效。");
        var items = new List<Series>();
        if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = Text(item, "id"), title = Text(item, "title");
                int count = Number(item, "episode_count");
                if (IsId(id) && !string.IsNullOrWhiteSpace(title)) items.Add(new Series(id, title, Math.Clamp(count, 0, 10000)));
            }
        return new Page<Series>(items, EmptyToNull(Text(root, "cursor")), Bool(root, "has_more"));
    }

    internal static async Task<Page<RankedSeries>> HotAsync(string? cursor, CancellationToken token)
    {
        var args = new List<string> { "rank", "--shorts", "--content", "all", "--board", "ranklist_hot_play_sc", "--json" };
        if (!string.IsNullOrEmpty(cursor)) { args.Add("--cursor"); args.Add(cursor); }
        using var json = await RunAsync(args, token);
        return ParseHot(json.RootElement);
    }

    internal static Page<RankedSeries> ParseHot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("榜单格式无效。");
        var items = new List<RankedSeries>();
        if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Text(item, "series_id"); var title = Text(item, "title");
                if (IsId(id) && !string.IsNullOrWhiteSpace(title))
                    items.Add(new RankedSeries(new Series(id, title, Math.Clamp(Number(item, "episode_count"), 0, 10000)), Math.Max(0, Number(item, "position")), Text(item, "score")));
            }
        return new Page<RankedSeries>(items, EmptyToNull(Text(root, "cursor")), Bool(root, "has_more"));
    }

    internal static async Task<Page<Episode>> EpisodesAsync(string id, string? cursor, CancellationToken token)
    {
        if (!IsId(id)) throw new ArgumentException("剧集编号无效。");
        var args = new List<string> { "episodes", id, "--json" };
        if (!string.IsNullOrEmpty(cursor)) { args.Add("--cursor"); args.Add(cursor); }
        using var json = await RunAsync(args, token);
        return ParseEpisodes(json.RootElement);
    }

    internal static Page<Episode> ParseEpisodes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("选集结果格式无效。");
        var items = new List<Episode>();
        if (root.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                int number = Number(item, "number"); string episodeId = Text(item, "id");
                if (number is > 0 and <= 10000 && IsId(episodeId)) items.Add(new Episode(number, episodeId, Bool(item, "playable")));
            }
        return new Page<Episode>(items, EmptyToNull(Text(root, "cursor")), Bool(root, "has_more"));
    }

    private static async Task<JsonDocument> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "search", "ohg.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("搜索组件不存在，请重新安装播放器。");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.Environment["OHG_CONFIG_DIR"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "catalog");
            start.Environment.Remove("OHG_API_ORIGIN"); start.Environment.Remove("OHG_REGISTER_ORIGIN"); start.Environment.Remove("OHG_DISTRIBUTION_ORIGIN");
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动搜索组件。");
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            var text = await output; await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("请求失败，可能是网络或红果服务暂不可用。");
            if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("返回结果过大。");
            return JsonDocument.Parse(text);
        }
        finally { Gate.Release(); }
    }

    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int Number(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;
    private static bool Bool(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static bool IsId(string value) => value.Length is > 0 and <= 32 && value.All(c => c is >= '0' and <= '9');
}
