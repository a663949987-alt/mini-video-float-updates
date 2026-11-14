using System.IO;

namespace MiniVideoFloat;

internal static class OfflineCacheStorage
{
    // Keep a buffer for the current downloads because their sizes are unknown
    // until RedFruit starts them. This is an approximate limit, not a hard quota.
    internal const long StopAddingBytes = 9L * 1024 * 1024 * 1024;
    internal const long MinimumFreeBytes = 5L * 1024 * 1024 * 1024;
    internal const long RandomTargetBytes = 5L * 1024 * 1024 * 1024;
    internal static bool ReachedRandomTarget(long size) => size >= RandomTargetBytes;

    internal static long Size(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, "downloads");
        if (!Directory.Exists(directory)) return 0;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid download directory");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", options)) size = checked(size + new FileInfo(file).Length);
        return size;
    }

    internal static bool CanAdd(string dataDirectory)
    {
        try
        {
            if (!File.Exists(Path.Combine(dataDirectory, "hgplayer.db"))) return false;
            var root = Path.GetPathRoot(dataDirectory);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).AvailableFreeSpace < MinimumFreeBytes) return false;
            var directory = Path.Combine(dataDirectory, "downloads");
            if (!Directory.Exists(directory)) return true;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            long size = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                size += new FileInfo(file).Length;
                if (size >= StopAddingBytes) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException) { return false; }
    }
}
