using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;

namespace MiniVideoFloat;

internal static class AppUpdater
{
    private const string CurrentVersion = "1.0.4";
    private const string ApiUrl = "https://api.github.com/repos/a663949987-alt/mini-video-float-updates/releases/latest";
    private static readonly HttpClient Client = CreateClient();
    private static DispatcherTimer? _timer;
    private static bool _checking;

    internal static void Start()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, ".installed"))) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniVideoFloat/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            using var document = JsonDocument.Parse(await Client.GetStringAsync(ApiUrl));
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v');
            if (!Version.TryParse(tag, out var latest) || latest <= Version.Parse(CurrentVersion)) return;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "MiniVideoFloat-Setup.exe") continue;
                if (!asset.TryGetProperty("digest", out var digestElement)) return;
                var digest = digestElement.GetString();
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71) return;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com") return;
                var file = Path.Combine(Path.GetTempPath(), "MiniVideoFloat-update-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    using (var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength is > 250_000_000) return;
                        await using var target = File.Create(file);
                        await response.Content.CopyToAsync(target);
                    }
                    await using var stream = File.OpenRead(file);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                    if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) { File.Delete(file); return; }
                    Process.Start(new ProcessStartInfo(file, $"--update --wait-pid {Environment.ProcessId}") { UseShellExecute = true });
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
                catch { try { File.Delete(file); } catch { } throw; }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"自动更新检查失败：{ex.Message}"); }
        finally { _checking = false; }
    }
}
