using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MiniVideoFloat;

public partial class SelectionWindow : Window
{
    internal sealed record PlaybackRequest(string SeriesId, string Title, DramaCatalog.Episode Episode, RedFruitProgressReader.Snapshot? History, bool ResumeHistory);
    private sealed record Choice(string Id, string Title, int EpisodeCount, string Description, RedFruitProgressReader.Snapshot? History, bool IsHot = false)
    {
        public string ActionText => History is not null ? "继续看" : IsHot ? "播放" : "选集";
    }
    private readonly Func<PlaybackRequest, CancellationToken, Task<string?>> _play;
    private readonly Action _pause, _next, _mute, _settings;
    private readonly ObservableCollection<Choice> _items = [];
    private readonly List<Choice> _history = [], _results = [], _hot = [];
    private readonly List<DramaCatalog.Episode> _episodes = [];
    private CancellationTokenSource _operation = new();
    private Choice? _chosen;
    private string _query = "";
    private string? _cursor;
    private bool _hasMore, _showingHistory = true, _showingHot, _hotHasMore, _busy;
    private string? _hotCursor;
    private int _page;

    internal SelectionWindow(Func<PlaybackRequest, CancellationToken, Task<string?>> play, Action pause, Action next, Action mute, Action settings)
    {
        _play = play; _pause = pause; _next = next; _mute = mute; _settings = settings;
        InitializeComponent();
        SeriesList.ItemsSource = _items;
        Loaded += async (_, _) => await LoadHistoryAsync();
        Closed += (_, _) => { _operation.Cancel(); _operation.Dispose(); };
    }

    private CancellationToken ResetOperation()
    {
        _operation.Cancel(); _operation.Dispose(); _operation = new CancellationTokenSource();
        return _operation.Token;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SeriesList.IsEnabled = !busy; EpisodeGrid.IsEnabled = !busy; SearchBox.IsEnabled = !busy; MoreButton.IsEnabled = !busy;
    }

    private async Task LoadHistoryAsync()
    {
        var token = ResetOperation();
        StatusText.Text = "正在读取观看历史…";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer", "hgplayer.db");
        IReadOnlyList<RedFruitProgressReader.HistoryEntry> entries;
        try { entries = await Task.Run(() => RedFruitProgressReader.ReadHistory(path), token); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        _history.Clear();
        foreach (var entry in entries)
        {
            var s = entry.Snapshot;
            var t = TimeSpan.FromSeconds(Math.Clamp(entry.PositionSeconds, 0, 7 * 24 * 3600));
            string position = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            _history.Add(new Choice(s.SeriesId, s.SeriesTitle, s.Episodes.Count, $"上次看到第 {s.CurrentIndex + 1} 集 · {position}", s));
        }
        _showingHistory = true; _showingHot = false; ShowList(_history);
        StatusText.Text = _history.Count == 0 ? "还没有可用的观看历史。" : "选择剧目查看选集，或点击继续看。";
    }

    private void ShowList(IEnumerable<Choice> values)
    {
        HistoryTab.Foreground = (Brush)new BrushConverter().ConvertFromString(_showingHistory ? "#E66314" : "#8D8885")!;
        ResultsTab.Foreground = (Brush)new BrushConverter().ConvertFromString(!_showingHistory && !_showingHot ? "#E66314" : "#8D8885")!;
        HotTab.Foreground = (Brush)new BrushConverter().ConvertFromString(_showingHot ? "#E66314" : "#8D8885")!;
        HistoryTab.FontWeight = _showingHistory ? FontWeights.Bold : FontWeights.Normal;
        ResultsTab.FontWeight = !_showingHistory && !_showingHot ? FontWeights.Bold : FontWeights.Normal;
        HotTab.FontWeight = _showingHot ? FontWeights.Bold : FontWeights.Normal;
        SeriesList.SelectedItem = null; _chosen = null; _episodes.Clear(); _page = 0; RenderEpisodes();
        _items.Clear(); foreach (var value in values) _items.Add(value);
        MoreButton.Visibility = (_showingHot ? _hotHasMore && !string.IsNullOrEmpty(_hotCursor) : !_showingHistory && _hasMore && !string.IsNullOrEmpty(_cursor)) ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count > 0) SeriesList.SelectedIndex = 0;
    }

    private async Task SearchAsync(bool append)
    {
        if (_busy) return;
        string query = append ? _query : SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) { StatusText.Text = "请输入短剧名称。"; return; }
        var token = ResetOperation(); SetBusy(true);
        StatusText.Text = "正在搜索…";
        try
        {
            var page = await DramaCatalog.SearchAsync(query, append ? _cursor : null, token);
            if (token.IsCancellationRequested) return;
            if (!append) _results.Clear();
            var ids = _results.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in page.Items)
                if (ids.Add(item.Id)) _results.Add(new Choice(item.Id, item.Title, item.EpisodeCount, item.EpisodeCount > 0 ? $"共 {item.EpisodeCount} 集" : "集数待加载", null));
            _query = query; _cursor = page.Cursor; _hasMore = page.HasMore;
            _showingHistory = false; _showingHot = false; ShowList(_results);
            StatusText.Text = _results.Count == 0 ? "没有找到结果，可以换个关键词。" : page.HasMore && string.IsNullOrEmpty(page.Cursor) ? "部分结果暂不可继续加载。" : $"找到 {_results.Count} 部短剧。";
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) StatusText.Text = "搜索超时，请重试。"; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); StatusText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "搜索暂不可用，请检查网络后重试。"; }
        finally { SetBusy(false); }
    }

    private async Task LoadHotAsync(bool append)
    {
        if (_busy) return;
        var token = ResetOperation(); SetBusy(true); StatusText.Text = "正在加载热播榜…";
        try
        {
            var page = await DramaCatalog.HotAsync(append ? _hotCursor : null, token);
            if (token.IsCancellationRequested) return;
            if (!append) _hot.Clear();
            var ids = _hot.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in page.Items)
            {
                var series = item.Series;
                if (!ids.Add(series.Id)) continue;
                var description = (item.Position > 0 ? $"第 {item.Position} 名" : "热播") + (string.IsNullOrWhiteSpace(item.Score) ? "" : $" · {item.Score}") + (series.EpisodeCount > 0 ? $" · {series.EpisodeCount} 集" : "");
                _hot.Add(new Choice(series.Id, series.Title, series.EpisodeCount, description, null, true));
            }
            _hotCursor = page.Cursor; _hotHasMore = page.HasMore;
            _showingHistory = false; _showingHot = true; ShowList(_hot);
            StatusText.Text = _hot.Count == 0 ? "暂时没有取得热播榜。" : "点击播放可从第一集开始，也可以选择集数。";
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) StatusText.Text = "热播榜加载超时，请重试。"; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); StatusText.Text = "热播榜暂时不可用，请检查网络后重试。"; }
        finally { SetBusy(false); }
    }

    private async Task PlayHotAsync(Choice choice)
    {
        if (_busy) return;
        var token = ResetOperation(); SetBusy(true); StatusText.Text = "正在准备第一集…";
        try
        {
            var page = await DramaCatalog.EpisodesAsync(choice.Id, null, token);
            var first = page.Items.FirstOrDefault(e => e.Number == 1 && e.Playable);
            if (first is null) { if (!token.IsCancellationRequested) StatusText.Text = "第一集暂不可播放，请尝试其他集数。"; return; }
            var error = await _play(new PlaybackRequest(choice.Id, choice.Title, first, null, false), token);
            if (!token.IsCancellationRequested) StatusText.Text = error ?? "已切换到第一集。";
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) StatusText.Text = "播放请求超时，请重试。"; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); if (!token.IsCancellationRequested) StatusText.Text = "无法准备播放，请稍后重试。"; }
        finally { SetBusy(false); }
    }

    private async void SeriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesList.SelectedItem is not Choice choice) return;
        var token = ResetOperation();
        _chosen = choice; _episodes.Clear(); _page = 0; RenderEpisodes();
        if (choice.History is { } history)
        {
            for (int i = 0; i < history.Episodes.Count; i++)
            {
                var item = history.Episodes[i];
                _episodes.Add(new DramaCatalog.Episode(i + 1, item.VideoId, !item.Locked && !string.IsNullOrWhiteSpace(item.VideoId)));
            }
            _page = Math.Max(0, history.CurrentIndex / 18); RenderEpisodes(); return;
        }
        StatusText.Text = "正在加载选集…";
        try
        {
            string? cursor = null; var seen = new HashSet<string>(StringComparer.Ordinal); bool incomplete = false;
            for (int pageNumber = 0; pageNumber < 20; pageNumber++)
            {
                var page = await DramaCatalog.EpisodesAsync(choice.Id, cursor, token);
                if (token.IsCancellationRequested || _chosen != choice) return;
                foreach (var ep in page.Items) if (seen.Add(ep.Id)) _episodes.Add(ep);
                if (!page.HasMore) break;
                if (string.IsNullOrEmpty(page.Cursor) || page.Cursor == cursor || pageNumber == 19) { incomplete = true; break; }
                cursor = page.Cursor;
            }
            _episodes.Sort((a, b) => a.Number.CompareTo(b.Number)); RenderEpisodes();
            StatusText.Text = _episodes.Count == 0 ? "没有取得可用集数。" : incomplete ? "仅加载了部分集数，请稍后重试。" : "选择集数开始播放。";
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) StatusText.Text = "加载选集超时，请重试。"; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); StatusText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "加载选集失败，请检查网络后重试。"; }
    }

    private void RenderEpisodes()
    {
        EpisodeGrid.Children.Clear();
        var pages = Math.Max(1, (_episodes.Count + 17) / 18); _page = Math.Clamp(_page, 0, pages - 1);
        PageText.Text = $"{_page + 1} / {pages}";
        SelectedTitle.Text = _chosen is null ? "" : $"{_chosen.Title} · 共 {_chosen.EpisodeCount} 集";
        foreach (var episode in _episodes.Skip(_page * 18).Take(18))
        {
            var button = new Button { Content = episode.Number.ToString(), Tag = episode, Style = (Style)FindResource("EpisodeButton"), IsEnabled = episode.Playable, ToolTip = episode.Playable ? $"播放第 {episode.Number} 集" : "该集暂不可播放" };
            if (_chosen?.History is { } h && h.CurrentIndex + 1 == episode.Number) { button.Background = (Brush)new BrushConverter().ConvertFromString("#FF7018")!; button.Foreground = Brushes.White; }
            button.Click += Episode_Click; EpisodeGrid.Children.Add(button);
        }
    }

    private async Task PlayAsync(Choice choice, DramaCatalog.Episode episode, bool resumeHistory = false)
    {
        if (_busy) return;
        var token = ResetOperation(); SetBusy(true); StatusText.Text = "正在切换播放…";
        try
        {
            var error = await _play(new PlaybackRequest(choice.Id, choice.Title, episode, choice.History, resumeHistory), token);
            if (!token.IsCancellationRequested) StatusText.Text = error ?? (resumeHistory ? "已打开红果的观看记录。" : $"已切换到第 {episode.Number} 集。");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested) StatusText.Text = "切换失败，请重试。"; }
        finally { SetBusy(false); }
    }

    private async void Episode_Click(object sender, RoutedEventArgs e) { if (_chosen is { } c && sender is Button { Tag: DramaCatalog.Episode ep }) await PlayAsync(c, ep); }
    private async void SeriesAction_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: Choice c }) { SeriesList.SelectedItem = c; if (c.History is { } h) await PlayAsync(c, new DramaCatalog.Episode(h.CurrentIndex + 1, h.CurrentVideoId, true), resumeHistory: true); else if (c.IsHot) await PlayHotAsync(c); } }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(false);
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(false); } }
    private async void History_Click(object sender, RoutedEventArgs e) { if (!_busy) await LoadHistoryAsync(); }
    private void Results_Click(object sender, RoutedEventArgs e) { if (_busy) return; ResetOperation(); _showingHistory = false; _showingHot = false; ShowList(_results); StatusText.Text = _results.Count == 0 ? "输入剧名进行搜索。" : "选择剧目查看选集。"; }
    private async void Hot_Click(object sender, RoutedEventArgs e) => await LoadHotAsync(false);
    private async void More_Click(object sender, RoutedEventArgs e) { if (_showingHot) await LoadHotAsync(true); else await SearchAsync(true); }
    private void Previous_Click(object sender, RoutedEventArgs e) { _page--; RenderEpisodes(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _page++; RenderEpisodes(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Pause_Click(object sender, RoutedEventArgs e) => _pause();
    private void Next_Click(object sender, RoutedEventArgs e) => _next();
    private void Mute_Click(object sender, RoutedEventArgs e) => _mute();
    private void Settings_Click(object sender, RoutedEventArgs e) => _settings();
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is not DependencyObject source || FindAncestorButton(source)) return; try { DragMove(); } catch (InvalidOperationException) { } }
    private static bool FindAncestorButton(DependencyObject element) { for (var p = element; p is not null; p = VisualTreeHelper.GetParent(p)) if (p is Button) return true; return false; }
}
