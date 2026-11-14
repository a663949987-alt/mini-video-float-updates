using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace MiniVideoFloat;

internal sealed class CacheWindow : Window
{
    private readonly string _data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer");
    private readonly ListBox _list = new() { Margin = new Thickness(0, 8, 0, 8), MinHeight = 160 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _manualStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Func<Task<string>> _start;
    private readonly Func<string> _randomStatus;
    internal CacheWindow(Func<Task<string>> start, Action startRandom, Action stopRandom, Func<string> randomStatus)
    {
        _start = start; _randomStatus = randomStatus; Title = "缓存管理"; Width = 500; Height = 420; MinWidth = 460; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var heading = new TextBlock { Text = "已缓存的短剧", FontSize = 18, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var refresh = new Button { Content = "刷新", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => Refresh(); buttons.Children.Add(refresh);
        var startButton = new Button { Content = "缓存当前剧后十集", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        startButton.Click += async (_, _) => { startButton.IsEnabled = false; _manualStatus.Text = "正在确认当前剧并提交缓存任务…"; try { var message = await _start(); Refresh(); _manualStatus.Text = message; } finally { startButton.IsEnabled = true; } }; buttons.Children.Add(startButton);
        var folder = new Button { Content = "打开下载目录", Padding = new Thickness(12, 5, 12, 5) };
        folder.Click += (_, _) => { try { var path = Path.Combine(_data, "downloads"); if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); else _status.Text = "还没有下载目录。"; } catch { _status.Text = "无法打开下载目录。"; } }; buttons.Children.Add(folder);
        bottom.Children.Add(buttons);
        var randomButtons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var randomStart = new Button { Content = "开始随机缓存", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        randomStart.ToolTip = "开始后会暂时切剧并提交下载，随后尝试恢复当前观看记录。";
        randomStart.Click += (_, _) => { startRandom(); Refresh(); }; randomButtons.Children.Add(randomStart);
        var randomStop = new Button { Content = "停止随机缓存", Padding = new Thickness(12, 5, 12, 5) };
        randomStop.Click += (_, _) => { stopRandom(); Refresh(); }; randomButtons.Children.Add(randomStop);
        bottom.Children.Add(randomButtons); bottom.Children.Add(_manualStatus); bottom.Children.Add(_status); root.Children.Add(_list); Refresh();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => Refresh(); timer.Start(); Closed += (_, _) => timer.Stop();
    }
    private void Refresh()
    {
        try
        {
            var records = RedFruitProgressReader.ReadDownloads(Path.Combine(_data, "hgplayer.db"));
            var root = Path.Combine(_data, "downloads");
            _list.Items.Clear(); long total = 0;
            foreach (var group in records.GroupBy(x => x.SeriesId))
            {
                long bytes = 0; var folder = Path.Combine(root, group.Key);
                if (!string.IsNullOrWhiteSpace(group.Key) && group.Key.All(char.IsAsciiDigit) && Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
                    foreach (var file in Directory.EnumerateFiles(folder, "*", opts)) try { bytes += new FileInfo(file).Length; } catch (IOException) { }
                }
                total += bytes;
                var done = group.Count(x => x.State == "done"); var title = group.Select(x => x.Title).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "未命名短剧";
                var pending = group.Count(x => x.State is "queued" or "running");
                var failed = group.Count(x => x.State == "failed");
                _list.Items.Add($"{title}   ·   完成 {done} 集，下载中 {pending} 集，失败 {failed} 集   ·   {FormatSize(bytes)}");
            }
            if (_list.Items.Count == 0) _list.Items.Add("暂无下载记录");
            _status.Text = $"已登记下载文件约占 {FormatSize(total)}。删除请使用红果的下载管理。\n{_randomStatus()}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException) { _status.Text = "读取缓存失败，请稍后刷新。"; }
    }
    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.00} GB" : $"{bytes / (1024d * 1024):0.0} MB";
}
