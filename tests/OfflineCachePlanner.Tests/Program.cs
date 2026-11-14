using MiniVideoFloat;
using Episode = MiniVideoFloat.OfflineCachePlanner.Episode;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var episodes = new List<Episode> {
    new("current", false, 2), new("locked", true, 2), new("saved", false, 2),
    new("unknown", false, null), new("next", false, 4), new("next", false, 4),
    new("later", false, 3), new("last", false, 2)
};
var saved = new HashSet<string> { "saved" };
var selected = OfflineCachePlanner.SelectNext(episodes, 0, saved, 3, 20, 2, 10, 5);
Check(selected.Count == 2 && selected[0].VideoId == "next" && selected[1].VideoId == "later", "selection and deduplication");
Check(OfflineCachePlanner.SelectNext(episodes, 0, saved, 10, 20, 2, 10, 5).Count == 0, "budget exhausted");
Check(OfflineCachePlanner.SelectNext(episodes, 0, saved, 0, 5, 2, 10, 5).Count == 0, "free space reserve");
Check(OfflineCachePlanner.SelectNext(episodes, -1, saved, 0, 20, 2, 10, 5).Count == 0, "invalid current index");
Check(!OfflineCacheStorage.ReachedRandomTarget(OfflineCacheStorage.RandomTargetBytes - 1) && OfflineCacheStorage.ReachedRandomTarget(OfflineCacheStorage.RandomTargetBytes), "random cache stops at 5 GiB");
const string json = """{"episodes":[{"videoId":"one","locked":false},{"videoId":"two","locked":true}]}""";
var progress = RedFruitProgressReader.Parse(json, 0, "one");
Check(progress is not null && progress.Episodes.Count == 2 && progress.Episodes[1].Locked, "progress parse");
Check(RedFruitProgressReader.Parse(json, 0, "two") is null, "reject stale progress");
Check(RedFruitProgressReader.Parse("{", 0, "one") is null, "reject corrupt progress");
var gesture = new WheelGesture();
Check(!gesture.Feed(-120, 1000) && gesture.Feed(120, 1500), "down then up triggers");
Check(!gesture.Feed(120, 1600), "up alone does not trigger");
Check(!gesture.Feed(-120, 2000) && !gesture.Feed(120, 2801), "slow gestures do not trigger");
gesture.Feed(-120, 3000); gesture.Reset();
Check(!gesture.Feed(120, 3100), "reset cancels gesture");
using (var result = System.Text.Json.JsonDocument.Parse("""{"items":[{"id":"123","title":"测试剧","episode_count":73},{"id":"bad","title":"invalid","episode_count":2}],"cursor":"next","has_more":true}"""))
{
    var page = DramaCatalog.ParseSearch(result.RootElement);
    Check(page.Items.Count == 1 && page.Items[0].EpisodeCount == 73 && page.HasMore && page.Cursor == "next", "parse real search shape");
}
using (var result = System.Text.Json.JsonDocument.Parse("""{"board":"ranklist_hot_play_sc","items":[{"series_id":"123","title":"热播剧","position":1,"score":"500万热度","episode_count":73},{"series_id":"bad","title":"无效剧","position":2}],"cursor":"next","has_more":true}"""))
{
    var page = DramaCatalog.ParseHot(result.RootElement);
    Check(page.Items.Count == 1 && page.Items[0].Series.Id == "123" && page.Items[0].Position == 1 && page.Items[0].Score == "500万热度" && page.HasMore && page.Cursor == "next", "parse hot ranking and continuation");
}
using (var result = System.Text.Json.JsonDocument.Parse("""{"items":[{"number":1,"id":"456","playable":true},{"number":2,"id":"789","playable":false}],"has_more":false}"""))
{
    var page = DramaCatalog.ParseEpisodes(result.RootElement);
    Check(page.Items.Count == 2 && page.Items[0].Playable && !page.Items[1].Playable && !page.HasMore, "parse locked episode");
}
if (args.Length > 0)
{
    var live = RedFruitProgressReader.ReadLatest(args[0]);
    Check(live is not null, "read actual progress database");
    Console.WriteLine($"Read current episode and {live!.Episodes.Count} episode entries without writing the database.");
    var history = RedFruitProgressReader.ReadHistory(args[0]);
    Check(history.Count > 0 && history[0].Snapshot.CurrentVideoId == live.CurrentVideoId, "history and latest progress agree");
    Console.WriteLine($"Read {history.Count} history entries without writing the database.");
    var downloads = RedFruitProgressReader.ReadDownloads(args[0]);
    Check(downloads.All(x => !string.IsNullOrWhiteSpace(x.SeriesId)), "download records contain a series identifier");
    Console.WriteLine($"Read {downloads.Count} download records without changing them.");
}
Console.WriteLine("Offline cache planner checks passed.");
