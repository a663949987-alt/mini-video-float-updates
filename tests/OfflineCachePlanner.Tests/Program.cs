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
if (args.Length > 0)
{
    var live = RedFruitProgressReader.ReadLatest(args[0]);
    Check(live is not null, "read actual progress database");
    Console.WriteLine($"Read current episode and {live!.Episodes.Count} episode entries without writing the database.");
}
Console.WriteLine("Offline cache planner checks passed.");
