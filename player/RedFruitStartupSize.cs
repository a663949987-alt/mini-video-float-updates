using System.IO;
using System.Text.Json.Nodes;

namespace MiniVideoFloat;

// Temporarily reduce RedFruit's saved launch geometry. RedFruit can ignore it
// or impose a minimum window size; never change an already running instance.
internal sealed class RedFruitStartupSize : IDisposable
{
    private readonly string _path, _backup, _written;
    internal int OriginalWidth { get; }
    internal int OriginalHeight { get; }
    private RedFruitStartupSize(string path, int width, int height) { _path = path; _backup = path + ".mini-float-backup"; _written = path + ".mini-float-written"; OriginalWidth = width; OriginalHeight = height; }

    internal static RedFruitStartupSize? Prepare(string path)
    {
        try
        {
            Recover(path);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var original = File.ReadAllBytes(path);
            var root = JsonNode.Parse(original);
            if (root?["main"] is not JsonObject main || main["w"]?.GetValue<int>() is not int width || main["h"]?.GetValue<int>() is not int height || width < 320 || height < 200 || width > 10000 || height > 10000) return null;
            main["w"] = Math.Min(width, 480); main["h"] = Math.Min(height, 270);
            var changed = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
            var state = new RedFruitStartupSize(path, width, height);
            using (var stream = new FileStream(state._backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(original);
            try
            {
                using (var stream = new FileStream(state._written, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(changed);
                var staging = path + ".mini-float-stage";
                File.WriteAllBytes(staging, changed); File.Move(staging, path, true);
                return state;
            }
            catch { state.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    internal static void Recover(string path)
    {
        string backup = path + ".mini-float-backup", written = path + ".mini-float-written";
        try
        {
            if (!File.Exists(backup)) return;
            if (File.Exists(path) && File.Exists(written) && File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(written)))
            {
                var staging = path + ".mini-float-restore";
                File.WriteAllBytes(staging, File.ReadAllBytes(backup)); File.Move(staging, path, true);
            }
            File.Delete(backup); if (File.Exists(written)) File.Delete(written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => Recover(_path);
}
