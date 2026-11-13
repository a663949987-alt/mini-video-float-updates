namespace MiniVideoFloat;

// Pure planning logic; the download provider must verify the size of each
// episode and enforce the same budget while bytes are being written.
internal static class OfflineCachePlanner
{
    internal const long DefaultBudgetBytes = 10L * 1024 * 1024 * 1024;
    internal const long MinimumFreeBytes = 5L * 1024 * 1024 * 1024;
    internal sealed record Episode(string VideoId, bool Locked, long? SizeBytes);
    internal sealed record Selection(int Index, string VideoId, long SizeBytes);

    internal static IReadOnlyList<Selection> SelectNext(
        IReadOnlyList<Episode> episodes, int currentIndex, ISet<string> existing,
        long usedBytes, long freeBytes, int maximum = 2,
        long budgetBytes = DefaultBudgetBytes, long minimumFreeBytes = MinimumFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(existing);
        if (currentIndex < 0 || currentIndex >= episodes.Count || maximum <= 0 ||
            usedBytes < 0 || freeBytes < 0 || budgetBytes < 0 || minimumFreeBytes < 0)
            return Array.Empty<Selection>();

        long remaining = Math.Min(Math.Max(0, budgetBytes - Math.Min(usedBytes, budgetBytes)),
                                  Math.Max(0, freeBytes - Math.Min(freeBytes, minimumFreeBytes)));
        var result = new List<Selection>();
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        for (var i = currentIndex + 1; i < episodes.Count && result.Count < maximum; i++)
        {
            var episode = episodes[i];
            if (episode.Locked || string.IsNullOrWhiteSpace(episode.VideoId) ||
                !seen.Add(episode.VideoId) || episode.SizeBytes is null or <= 0)
                continue;
            long size = episode.SizeBytes.Value;
            if (size > remaining) continue;
            result.Add(new Selection(i, episode.VideoId, size));
            remaining -= size;
        }
        return result;
    }
}
