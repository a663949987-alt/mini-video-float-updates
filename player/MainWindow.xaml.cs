using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MiniVideoFloat;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312, WmMouseActivate = 0x0021, MaNoActivate = 3;
    private const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080, WsExAppWindow = 0x00040000;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _imeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _cacheTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(67) };
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly WheelGesture _wheelGesture = new();
    private readonly SemaphoreSlim _sourceUiGate = new(1, 1);
    private readonly RecentVideoFrames _recentFrames = new();
    private readonly Stopwatch _loopClock = new();
    private RecentVideoFrames.Frame[] _loopFrames = [];
    private bool _capturingFrame, _loopActive, _loopManuallyPaused;
    private double? _subtitleCenter, _subtitleCandidate;
    private SubtitleRegionDetector.Detection? _subtitleSize, _subtitleCandidateSize;
    private int _subtitleCandidateCount;
    private DateTime _lastSubtitleDetectUtc;
    private int _subtitleGeneration;
    private void ResetSubtitlePosition() { _subtitleCenter = _subtitleCandidate = null; _subtitleSize = _subtitleCandidateSize = null; _subtitleCandidateCount = 0; _lastSubtitleDetectUtc = DateTime.MinValue; _subtitleGeneration++; }
    private bool _desiredVisible = true;
    private double _captureAverageMs;
    private bool _cacheChecking;
    private bool _randomCaching;
    private CancellationTokenSource? _randomCacheCancellation;
    private string _randomCacheStatus = "未开始随机缓存";
    private string RandomCacheStatus
    {
        get => _randomCacheStatus;
        set
        {
            if (value == _randomCacheStatus) return;
            _randomCacheStatus = value;
            try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "cache-status.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {value}{Environment.NewLine}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private string? _randomSeriesId, _randomSeriesTitle;
    private string? _cacheCheckedVideoId;
    private DateTime _lastCacheSuccessUtc;
    private DateTime _lastCacheAttemptUtc;
    private bool _manuallyHidden;
    private bool _initialized;
    private bool _autoReconnectEnabled = true, _connecting, _closing;
    private bool _watchRestoring;
    private int _watchRestoreGeneration;
    private DateTime _lastReconnectAttemptUtc, _lastPositionPersistUtc;
    private (int X, int Y)? _lastSavedPosition;
    private bool _playbackSyncRunning, _playbackSyncDirty, _resumeAfterAutoPause;
    private DateTime _lastPlaybackCheckUtc;
    private bool _autoFitInProgress, _autoFitSucceeded, _playbackControlUnavailable;
    private DateTime _lastAutoFitUtc;
    private DateTime _lastTrayHideUtc;
    private double _brightness = .85;
    private int _subtitleZoomLevel;
    private bool _subtitleZoomUnavailable;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "settings.json");
    private HwndSource? _source;
    private SelectionWindow? _selectionWindow;
    private IntPtr _relayWindow, _relayThumbnail, _relayBottomThumbnail, _subtitleThumbnail, _subtitleBottomThumbnail, _subtitleFadeFirst, _subtitleFadeSecond;
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseHookProc;
    private WindowRelay.WindowRect? _relayOriginalPosition;
    private int _relayProcessId;
    private long? _relayOriginalExtendedStyle;
    private WindowRelay.CropRect? _relayCrop;
    private List<AudioSessionMuteState> _relayAudio = [];
    private DateTime _lastMuteAttemptUtc;
    private bool _relayVisible = true, _relayMuted = true;
    private bool _relayCropManuallySet;
    private bool _dark;
    private bool _cardSkin, _fullSkin;
    private int VideoW => _cardSkin || _fullSkin ? 241 : 155;
    private int VideoH => _fullSkin ? 123 : _cardSkin ? 110 : 98;
    private int VideoY => _fullSkin ? 9 : 11;
    private int SubtitleHeight => _cardSkin || _fullSkin ? 16 : 12;
    private double _videoWidth = 62, _videoHeight = 42;
    private int _aspectWidth = 31, _aspectHeight = 21;
    private double? _savedLeft, _savedTop;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateProgress();
        _imeTimer.Tick += (_, _) => RefreshImePanel();
        _cacheTimer.Tick += async (_, _) => await CheckOfflineCacheAsync();
        _frameTimer.Tick += async (_, _) => await UpdateRecentFramesAsync();
        _fadeTimer.Tick += (_, _) => UpdateRelayThumbnail();
        Loaded += (_, _) => BuildContextMenu();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        LoadSettings();
        if (_savedLeft is not null && _savedTop is not null) { Left = _savedLeft.Value; Top = _savedTop.Value; }
        else PositionAtBottomRight();
        ApplyVideoSize();
        ApplyTheme();
        ApplySkin();
        EnsureOnScreen();
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
        RegisterHotKey(hwnd, 1, ModControl | ModAlt | ModNoRepeat, 0x20); // Ctrl+Alt+Space
        RegisterHotKey(hwnd, 2, ModControl | ModAlt | ModNoRepeat, 0x27); // Ctrl+Alt+Right
        RegisterHotKey(hwnd, 3, ModControl | ModAlt | ModNoRepeat, 0x26); // Ctrl+Alt+Up
        RegisterHotKey(hwnd, 4, ModControl | ModAlt | ModNoRepeat, 0x28); // Ctrl+Alt+Down
        _mouseHookProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(14, _mouseHookProc, GetModuleHandle(null), 0);
        _imeTimer.Start();
        _timer.Start();
        _cacheTimer.Start();
        _frameTimer.Start();
        RefreshImePanel();
        Dispatcher.BeginInvoke(new Action(ConnectRedFruit));
        if (_randomCaching) StartRandomCache();
        AppUpdater.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        extendedStyle = (extendedStyle & ~WsExAppWindow) | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(hwnd, -20, new nint(extendedStyle));
        HwndSource.FromHwnd(hwnd)?.AddHook(DpiHook);
    }

    private IntPtr DpiHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is 0x02E0 or 0x007E) { Dispatcher.BeginInvoke(() => { ApplyVideoSize(); EnsureOnScreen(); RefreshImePanel(); }); }
        return IntPtr.Zero;
    }

    private void Shell_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Button || element is System.Windows.Controls.Primitives.Thumb) return;
        try { DragMove(); SaveSettings(); } catch (InvalidOperationException) { }
    }

    private void ApplyVideoSize()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 96;
        var scale = Math.Max(1, dpi) / 96d;
        Width = 261 / scale;
        Height = 191 / scale;
        UpdateRelayThumbnail();
    }

    private void PositionAtBottomRight()
    {
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null) { Left = 80; Top = 80; return; }
        var work = screen.WorkingArea;
        var dpi = GetDpiForSystem() / 96d;
        Left = (work.Right - 261 - 28) / dpi;
        Top = (work.Bottom - 191 - 36) / dpi;
    }

    private void EnsureOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        var bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) return;
        PositionAtBottomRight();
        SaveSettings();
    }

    private void BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(Menu("选剧 / 选集…", (_, _) => ShowSelectionWindow()));
        menu.Items.Add(Menu("缓存管理…", (_, _) => ShowCacheWindow()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Menu("暂停 / 继续（Ctrl+Alt+空格）", (_, _) => TogglePlayback()));
        menu.Items.Add(Menu("下一条 / 下一集（Ctrl+Alt+→）", (_, _) => NextRedFruit()));
        menu.Items.Add(Menu("红果静音 / 取消静音", (_, _) => Mute_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Menu("隐藏播放器", (_, _) => HidePlayerWindow()));
        var skins = new System.Windows.Controls.MenuItem { Header = "界面皮肤" };
        foreach (var option in new[] { ("经典皮肤", 0), ("广告卡片", 1), ("完整画面（第三版）", 2) })
        {
            var item = Menu(option.Item1, (_, _) => { _cardSkin = option.Item2 == 1; _fullSkin = option.Item2 == 2; ClearRecentLoop(); ResetSubtitlePosition(); ApplySkin(); SaveSettings(); BuildContextMenu(); });
            item.IsCheckable = true; item.IsChecked = (_fullSkin ? 2 : _cardSkin ? 1 : 0) == option.Item2; skins.Items.Add(item);
        }
        menu.Items.Add(skins);
        var more = new System.Windows.Controls.MenuItem { Header = "更多设置" };
        more.Items.Add(Menu("打开 / 连接红果", (_, _) => ConnectRedFruit()));
        more.Items.Add(Menu("音量增加（Ctrl+Alt+↑）", (_, _) => ChangeRelayVolume(.1f)));
        more.Items.Add(Menu("音量降低（Ctrl+Alt+↓）", (_, _) => ChangeRelayVolume(-.1f)));
        if (_playbackControlUnavailable)
            more.Items.Add(new System.Windows.Controls.MenuItem { Header = "红果暂停控制暂不可用", IsEnabled = false });
        var brightnessMenu = new System.Windows.Controls.MenuItem { Header = "画面亮度" };
        foreach (var level in new[] { 100, 85, 70, 55 })
        {
            var item = Menu($"{level}%", (_, _) => SetBrightness(level / 100d));
            item.IsCheckable = true; item.IsChecked = Math.Abs(_brightness - level / 100d) < .001;
            brightnessMenu.Items.Add(item);
        }
        more.Items.Add(brightnessMenu);
        var subtitleMenu = new System.Windows.Controls.MenuItem { Header = _subtitleZoomUnavailable ? "字幕局部放大（暂不可用）" : "字幕局部放大（试验）" };
        foreach (var option in new[] { ("关闭", 0), ("放大 25%", 1), ("放大 38%", 3), ("放大 50%", 2) })
        {
            var item = Menu(option.Item1, (_, _) => SetSubtitleZoom(option.Item2));
            item.IsCheckable = true; item.IsChecked = _subtitleZoomLevel == option.Item2;
            subtitleMenu.Items.Add(item);
        }
        subtitleMenu.Items.Add(new System.Windows.Controls.Separator());
        subtitleMenu.Items.Add(Menu("重新识别字幕位置", (_, _) => { ResetSubtitlePosition(); UpdateRelayThumbnail(); }));
        more.Items.Add(subtitleMenu);
        more.Items.Add(new System.Windows.Controls.Separator());
        more.Items.Add(Menu("隐藏转播源窗口（Alt+Tab）", (_, _) => HideRelaySourceFromAltTab()));
        more.Items.Add(Menu("恢复转播源窗口（Alt+Tab）", (_, _) => RestoreRelaySourceAltTab()));
        more.Items.Add(Menu("转播窗口移到屏幕外", (_, _) => MoveRelaySourceOffscreen()));
        more.Items.Add(Menu("恢复转播窗口位置", (_, _) => RestoreRelaySourceWindow()));
        more.Items.Add(Menu("停止实时转播", (_, _) => { _autoReconnectEnabled = false; StopWindowRelay(); }));
        more.Items.Add(Menu("裁切为当前画面比例", (_, _) => SetRelayCropToVideoRatio()));
        more.Items.Add(Menu("自动识别视频画面", (_, _) => AutoDetectVideoRegion()));
        more.Items.Add(Menu("自定义转播区域…", (_, _) => ShowRelayCropDialog()));
        more.Items.Add(Menu("重置裁切（完整窗口）", (_, _) => SetRelayCrop(null)));
        more.Items.Add(Menu("设置画面尺寸…", (_, _) => ShowVideoSizeDialog()));
        menu.Items.Add(more);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Menu("退出", (_, _) => Close()));
        Shell.ContextMenu = menu;
    }

    private void OpenSkin_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://pinyin.sogou.com/proxy/inputclient?t=skin&f=windowsMenu") { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "打开搜狗皮肤库"); }
    }

    private void ShowSelectionWindow()
    {
        if (_selectionWindow is not null) { _selectionWindow.Activate(); return; }
        var window = new SelectionWindow(PlaySelectionAsync, TogglePlayback, NextRedFruit, () => Mute_Click(this, new RoutedEventArgs()), ShowSettingsMenu) { Owner = this };
        _selectionWindow = window;
        window.Closed += (_, _) => { if (_selectionWindow == window) _selectionWindow = null; };
        window.Show();
    }

    private void ShowSettingsMenu()
    {
        if (Shell.ContextMenu is null) BuildContextMenu();
        var menu = Shell.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = Shell;
        menu.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (menu.IsOpen && menu.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(item => item.Header?.ToString() == "更多设置") is { } more)
                more.IsSubmenuOpen = true;
        });
    }

    private async Task<string?> PlaySelectionAsync(SelectionWindow.PlaybackRequest request, CancellationToken token)
    {
        if (_relayWindow == IntPtr.Zero && !await EnsureRedFruitConnectedAsync(showErrors: false)) return "红果尚未连接，请稍后重试。";
        token.ThrowIfCancellationRequested();
        CancelWatchRestore();
        ResetSubtitlePosition();
        if (_loopActive) ClearRecentLoop();
        var source = _relayWindow; var sourcePid = _relayProcessId;
        var player = new WindowInteropHelper(this).Handle;
        if (source == IntPtr.Zero) return "红果尚未连接。";
        var historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer", "hgplayer.db");
        var previous = await Task.Run(() => RedFruitProgressReader.ReadLatest(historyPath), token);
        token.ThrowIfCancellationRequested();
        var generation = Interlocked.Increment(ref _watchRestoreGeneration);
        _watchRestoring = true; UpdateRelayThumbnail();
        bool gateHeld = false;
        try
        {
            bool Safe()
            {
                if (token.IsCancellationRequested || Volatile.Read(ref _watchRestoreGeneration) != generation || !WindowRelay.IsWindow(player) || !WindowRelay.IsWindow(source)) return false;
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
                return foregroundPid != sourcePid;
            }
            await _sourceUiGate.WaitAsync(token); gateHeld = true;
            return await Task.Run(() =>
            {
                if (!Safe() || !NavigationStyleState.TryEnter(source, sourcePid, out var wasNoActivate)) return "无法安全操作红果窗口，请重试。";
                try
                {
                    bool Guarded() => Safe() && WindowRelay.TryGetNoActivate(source, out var isSet) && isSet;
                    var result = RedFruitEpisodeNavigator.Play(source, request.SeriesId, request.Title, request.Episode, request.History, Guarded, resumeHistory: request.ResumeHistory);
                    if (result is not null && previous is not null && Guarded() && !result.StartsWith("已请求", StringComparison.Ordinal) && !result.StartsWith("红果已收到", StringComparison.Ordinal))
                        RedFruitWatchRestore.TryRestore(source, previous.SeriesTitle, previous.CurrentIndex, Guarded);
                    return result;
                }
                finally { NavigationStyleState.Leave(source, wasNoActivate); }
            });
        }
        finally
        {
            if (gateHeld) _sourceUiGate.Release();
            if (Volatile.Read(ref _watchRestoreGeneration) == generation && source == _relayWindow)
            {
                _watchRestoring = false; _autoFitSucceeded = false;
                _recentFrames.Clear(); UpdateRelayThumbnail(); RequestPlaybackSync();
                _ = AutoFitRelayCropAsync(source);
            }
        }
    }

    private static System.Windows.Controls.MenuItem Menu(string text, RoutedEventHandler action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = text };
        item.Click += action;
        return item;
    }

    private void SetBrightness(double value)
    {
        _brightness = Math.Clamp(value, .2, 1);
        UpdateRelayThumbnail();
        SaveSettings(); BuildContextMenu();
    }

    private void SetSubtitleZoom(int value)
    {
        _subtitleZoomLevel = value is 1 or 2 or 3 ? value : 0;
        _subtitleZoomUnavailable = false;
        UpdateRelayThumbnail(); SaveSettings(); BuildContextMenu();
    }

    private void ShowVideoSizeDialog()
    {
        var workArea = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var maxVideoWidth = Math.Max(24, Math.Min(2000, workArea.Width - 12));
        var maxVideoHeight = Math.Max(24, Math.Min(2000, workArea.Height - 12));
        using var dialog = new Forms.Form { Text = "视频画面尺寸", Width = 370, Height = 235, StartPosition = Forms.FormStartPosition.CenterScreen, FormBorderStyle = Forms.FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false };
        var ratio = new Forms.ComboBox { Left = 145, Top = 16, Width = 190, DropDownStyle = Forms.ComboBoxStyle.DropDownList };
        ratio.Items.AddRange(["当前比例", "横屏 16:9", "竖屏 9:16"]);
        ratio.SelectedIndex = _aspectWidth == 16 && _aspectHeight == 9 ? 1 : _aspectWidth == 9 && _aspectHeight == 16 ? 2 : 0;
        var width = new Forms.NumericUpDown { Left = 145, Top = 55, Width = 190, Minimum = 24, Maximum = maxVideoWidth, Increment = _aspectWidth, Value = Math.Min((decimal)_videoWidth, maxVideoWidth) };
        var height = new Forms.NumericUpDown { Left = 145, Top = 91, Width = 190, Minimum = 24, Maximum = maxVideoHeight, Increment = _aspectHeight, Value = Math.Min((decimal)_videoHeight, maxVideoHeight) };
        var locked = new Forms.CheckBox { Left = 18, Top = 131, Width = 300, Text = "锁定长宽比（推荐）", Checked = true };
        var ok = new Forms.Button { Text = "应用", Left = 173, Top = 164, Width = 76, DialogResult = Forms.DialogResult.OK };
        var cancel = new Forms.Button { Text = "取消", Left = 259, Top = 164, Width = 76, DialogResult = Forms.DialogResult.Cancel };
        dialog.Controls.AddRange([
            new Forms.Label { Left = 18, Top = 20, Width = 120, Text = "比例" }, ratio,
            new Forms.Label { Left = 18, Top = 59, Width = 120, Text = "画面宽度（像素）" }, width,
            new Forms.Label { Left = 18, Top = 95, Width = 120, Text = "画面高度（像素）" }, height,
            locked, ok, cancel
        ]);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        int activeWidth = _aspectWidth, activeHeight = _aspectHeight;
        bool updating = false;
        void SetFromWidth()
        {
            var kMin = Math.Max((int)Math.Ceiling(24d / activeWidth), (int)Math.Ceiling(24d / activeHeight));
            var kMax = Math.Min((int)(width.Maximum / activeWidth), (int)(height.Maximum / activeHeight));
            var k = Math.Clamp((int)Math.Round((double)width.Value / activeWidth), kMin, kMax);
            updating = true; width.Value = k * activeWidth; height.Value = k * activeHeight; updating = false;
        }
        void SetFromHeight()
        {
            var kMin = Math.Max((int)Math.Ceiling(24d / activeWidth), (int)Math.Ceiling(24d / activeHeight));
            var kMax = Math.Min((int)(width.Maximum / activeWidth), (int)(height.Maximum / activeHeight));
            var k = Math.Clamp((int)Math.Round((double)height.Value / activeHeight), kMin, kMax);
            updating = true; width.Value = k * activeWidth; height.Value = k * activeHeight; updating = false;
        }
        void SetCustomRatio()
        {
            var gcd = GreatestCommonDivisor((int)width.Value, (int)height.Value);
            activeWidth = (int)width.Value / gcd; activeHeight = (int)height.Value / gcd;
            width.Increment = activeWidth; height.Increment = activeHeight;
            ratio.SelectedIndex = 0;
        }
        width.ValueChanged += (_, _) => { if (updating) return; if (locked.Checked) SetFromWidth(); else SetCustomRatio(); };
        height.ValueChanged += (_, _) => { if (updating) return; if (locked.Checked) SetFromHeight(); else SetCustomRatio(); };
        locked.CheckedChanged += (_, _) => { if (locked.Checked) SetFromWidth(); };
        ratio.SelectedIndexChanged += (_, _) =>
        {
            (activeWidth, activeHeight) = ratio.SelectedIndex switch { 1 => (16, 9), 2 => (9, 16), _ => (_aspectWidth, _aspectHeight) };
            width.Increment = activeWidth; height.Increment = activeHeight;
            if (locked.Checked) SetFromWidth();
        };
        if (locked.Checked) SetFromWidth();
        if (dialog.ShowDialog(new NativeWindowOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
        {
            _videoWidth = (double)width.Value; _videoHeight = (double)height.Value;
            if (locked.Checked) { _aspectWidth = activeWidth; _aspectHeight = activeHeight; }
            else { var gcd = GreatestCommonDivisor((int)_videoWidth, (int)_videoHeight); _aspectWidth = (int)_videoWidth / gcd; _aspectHeight = (int)_videoHeight / gcd; }
            ApplyVideoSize(); SaveSettings();
        }
    }

    private static int GreatestCommonDivisor(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Max(1, a); }

    private async void ConnectRedFruit()
    {
        _autoReconnectEnabled = true;
        await EnsureRedFruitConnectedAsync();
    }

    private void ShowCacheWindow()
    {
        var window = new CacheWindow(StartManualCacheAsync, StartRandomCacheFromWindow, StopRandomCache, () => RandomCacheStatus) { Owner = this };
        window.Show();
    }

    private async Task<string> StartManualCacheAsync()
    {
        if (_cacheChecking || _watchRestoring || _relayWindow == IntPtr.Zero || _playbackSyncRunning) return "播放器正忙或红果尚未连接，请稍后再试。";
        var source = _relayWindow; var sourcePid = _relayProcessId; var player = new WindowInteropHelper(this).Handle;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer");
        _cacheChecking = true;
        try
        {
            return await Task.Run(() =>
            {
                if (!_sourceUiGate.Wait(0)) return "播放器正忙，请稍后再试。";
                try
                {
                    var database = Path.Combine(data, "hgplayer.db");
                    var snapshot = RedFruitProgressReader.ReadHistory(database).Select(x => x.Snapshot)
                        .FirstOrDefault(x => RedFruitEpisodeNavigator.IsCurrentStage(source, x.SeriesTitle, x.CurrentIndex + 1));
                    if (snapshot is null) return "无法确认当前播放的短剧和集数，请播放几秒后重试。";
                    if (!OfflineCacheStorage.CanAdd(data)) return "缓存接近上限或磁盘空间不足。";
                    bool Safe() { if (!WindowRelay.IsWindow(player) || !WindowRelay.IsWindow(source) || WindowRelay.IsMinimized(source)) return false; GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid); return foregroundPid != sourcePid; }
                    if (!Safe() || !NavigationStyleState.TryEnter(source, sourcePid, out var wasNoActivate)) return "暂时无法操作红果下载窗口。";
                    try
                    {
                        bool Guarded() => Safe() && WindowRelay.TryGetNoActivate(source, out var active) && active;
                        var paused = RedFruitPlaybackControl.GetPaused(source);
                        if (paused is null) return "无法确认播放状态，请稍后重试。";
                        bool restorePlayback = paused == false;
                        try
                        {
                            if (restorePlayback)
                            {
                                var pause = RedFruitPlaybackControl.SetPaused(source, true);
                                if (pause is not (RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.AlreadySet)) return "暂停视频失败，未提交缓存。";
                            }
                            string? detail = null;
                            var result = RedFruitDownloadDialog.QueueWithRetry(source, snapshot.CurrentIndex, snapshot.SeriesTitle, Guarded, diagnostic: message => detail = message, nextCount: 10);
                            if (result == RedFruitDownloadDialog.Result.NoWork) return "后面没有可新增缓存的集数；可能已下载、正在下载或已到最后一集。";
                            if (result != RedFruitDownloadDialog.Result.Submitted) return $"未确认缓存任务：{detail ?? "红果下载窗口未完成操作"}。";
                            var ids = snapshot.Episodes.Skip(snapshot.CurrentIndex + 1).Take(10).Select(x => x.VideoId).ToHashSet(StringComparer.Ordinal);
                            for (int attempt = 0; attempt < 10; attempt++)
                            {
                                var downloads = RedFruitProgressReader.ReadDownloads(database).Where(x => x.SeriesId == snapshot.SeriesId && ids.Contains(x.VideoId)).ToArray();
                                if (downloads.Length > 0)
                                {
                                    var done = downloads.Count(x => x.State == "done");
                                    var pending = downloads.Count(x => x.State is "queued" or "running");
                                    var failed = downloads.Count(x => x.State == "failed");
                                    return $"后十集中：已完成 {done} 集，下载中或排队 {pending} 集，失败 {failed} 集；共找到 {downloads.Length} 条记录。";
                                }
                                Thread.Sleep(300);
                            }
                            return "已向红果提交下载请求，但暂未读到对应记录；请稍后刷新检查。";
                        }
                        finally { if (restorePlayback && Guarded() && IsWindowVisible(player)) RedFruitPlaybackControl.SetPaused(source, false); }
                    }
                    finally { NavigationStyleState.Leave(source, wasNoActivate); }
                }
                finally { _sourceUiGate.Release(); }
            });
        }
        catch (Exception ex) { Debug.WriteLine(ex); return "读取或提交缓存任务失败。"; }
        finally { _cacheChecking = false; }
    }

    private void StartRandomCacheFromWindow()
    {
        RandomCacheStatus = "随机缓存已开启，正在检查下载状态。";
        StartRandomCache();
    }

    private void StartRandomCache()
    {
        if (_randomCacheCancellation is not null) return;
        _randomCaching = true; SaveSettings();
        _randomCacheCancellation = new CancellationTokenSource();
        _ = RunRandomCacheAsync(_randomCacheCancellation);
    }

    private void StopRandomCache()
    {
        _randomCaching = false; SaveSettings();
        RandomCacheStatus = "正在停止随机缓存；已交给红果的下载任务会继续。";
        _randomCacheCancellation?.Cancel();
    }

    private async Task RunRandomCacheAsync(CancellationTokenSource owner)
    {
        var token = owner.Token;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer");
        var database = Path.Combine(data, "hgplayer.db");
        string[] words = ["爱情", "都市", "重生", "家庭", "古装", "逆袭", "亲情", "悬疑", "甜宠", "传奇"];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var size = await Task.Run(() => OfflineCacheStorage.Size(data), token);
                if (OfflineCacheStorage.ReachedRandomTarget(size) && string.IsNullOrEmpty(_randomSeriesId))
                {
                    RandomCacheStatus = "已达到约 5 GB，停止新增下载。";
                    _randomCaching = false; SaveSettings(); break;
                }
                if (!OfflineCacheStorage.CanAdd(data)) { RandomCacheStatus = "接近缓存上限、磁盘空间不足或下载目录不可用，暂停新增。"; await Task.Delay(TimeSpan.FromMinutes(2), token); continue; }
                var records = await Task.Run(() => RedFruitProgressReader.ReadDownloads(database), token);
                if (records.Any(r => r.State is "pending" or "queued" or "downloading" or "running"))
                { RandomCacheStatus = "红果正在下载，等待本批完成。"; await Task.Delay(TimeSpan.FromSeconds(30), token); continue; }
                if (_relayWindow == IntPtr.Zero || _watchRestoring || _cacheChecking || _playbackSyncRunning)
                { RandomCacheStatus = "等待红果连接或播放器当前操作完成。"; await Task.Delay(TimeSpan.FromSeconds(5), token); continue; }
                DramaCatalog.Series? candidate;
                if (!string.IsNullOrWhiteSpace(_randomSeriesId) && !string.IsNullOrWhiteSpace(_randomSeriesTitle))
                    candidate = new DramaCatalog.Series(_randomSeriesId, _randomSeriesTitle, 0);
                else
                {
                    RandomCacheStatus = "正在寻找未缓存的短剧…";
                    DramaCatalog.Page<DramaCatalog.Series> page;
                    try { page = await DramaCatalog.SearchAsync(words[Random.Shared.Next(words.Length)], null, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { RandomCacheStatus = "获取剧目失败，稍后重试。"; await Task.Delay(TimeSpan.FromMinutes(1), token); continue; }
                    var known = records.Select(r => r.SeriesId).ToHashSet(StringComparer.Ordinal);
                    candidate = page.Items.Where(x => !known.Contains(x.Id)).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
                    if (candidate is null) { RandomCacheStatus = "本次没有找到新剧，稍后继续查找。"; await Task.Delay(TimeSpan.FromSeconds(30), token); continue; }
                    _randomSeriesId = candidate.Id; _randomSeriesTitle = candidate.Title; SaveSettings();
                }
                DramaCatalog.Page<DramaCatalog.Episode> episodes;
                try { episodes = await DramaCatalog.EpisodesAsync(candidate.Id, null, token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { RandomCacheStatus = "获取集数失败，稍后重试。"; await Task.Delay(TimeSpan.FromSeconds(30), token); continue; }
                var episode = episodes.Items.FirstOrDefault(x => x.Number == 1 && x.Playable);
                if (episode is null) { RandomCacheStatus = "该剧首集不可缓存，稍后换一部。"; _randomSeriesId = _randomSeriesTitle = null; SaveSettings(); await Task.Delay(TimeSpan.FromSeconds(15), token); continue; }
                if (_cacheChecking || _relayWindow == IntPtr.Zero) continue;
                var source = _relayWindow; var sourcePid = _relayProcessId; var player = new WindowInteropHelper(this).Handle;
                _cacheChecking = true;
                _watchRestoring = true; UpdateRelayThumbnail();
                try
                {
                    RandomCacheStatus = $"正在缓存《{candidate.Title}》…";
                    var result = await Task.Run(() =>
                    {
                        if (!_sourceUiGate.Wait(0)) return "播放器正忙，稍后重试。";
                        try
                        {
                            bool Safe() { if (!WindowRelay.IsWindow(player) || !WindowRelay.IsWindow(source) || WindowRelay.IsMinimized(source)) return false; GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid); return foregroundPid != sourcePid; }
                            if (token.IsCancellationRequested || !Safe() || !NavigationStyleState.TryEnter(source, sourcePid, out var oldStyle)) return "等待红果窗口可用。";
                            try
                            {
                                bool Guarded() => !token.IsCancellationRequested && Safe() && WindowRelay.TryGetNoActivate(source, out var active) && active;
                                bool CanRestore() => Safe() && WindowRelay.TryGetNoActivate(source, out var active) && active;
                                var previous = RedFruitProgressReader.ReadLatest(database);
                                if (previous is null) return "无法保存当前观看位置，已暂停随机缓存。";
                                try
                                {
                                    var error = RedFruitEpisodeNavigator.Play(source, candidate.Id, candidate.Title, episode, null, Guarded);
                                    if (error is not null) RandomCacheStatus = "自动选剧未确认：" + error;
                                    if (error is not null) return "自动选剧未确认：" + error;
                                    RandomCacheStatus = "已进入待缓存短剧，正在暂停并确认集数。";
                                    if (!Guarded()) return "随机缓存已中断。";
                                    var pause = RedFruitPlaybackControl.ChangeResult.Unavailable;
                                    for (int attempt = 0; attempt < 4 && Guarded(); attempt++)
                                    {
                                        pause = RedFruitPlaybackControl.SetPaused(source, true);
                                        if (pause is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.AlreadySet) break;
                                        Thread.Sleep(500);
                                    }
                                    if (pause is RedFruitPlaybackControl.ChangeResult.Unavailable or RedFruitPlaybackControl.ChangeResult.Unconfirmed) { RandomCacheStatus = "无法确认红果已暂停，未提交下载。"; return RandomCacheStatus; }
                                    var current = RedFruitProgressReader.ReadHistory(database).Select(x => x.Snapshot).FirstOrDefault(x => x.SeriesId == candidate.Id);
                                    if (current is null || current.CurrentVideoId != episode.Id || current.CurrentIndex + 1 != episode.Number) { RandomCacheStatus = "无法确认当前短剧或集数，未提交下载。"; return RandomCacheStatus; }
                                    var submit = RedFruitDownloadDialog.QueueWithRetry(source, current.CurrentIndex, current.SeriesTitle, Guarded, firstEpisodes: true, diagnostic: message => RandomCacheStatus = message);
                                    RandomCacheStatus = $"红果下载窗口返回：{submit}。";
                                    return submit == RedFruitDownloadDialog.Result.Submitted ? "已提交一批下载，等待红果完成。" : submit == RedFruitDownloadDialog.Result.NoWork ? "这部剧已下载，稍后换一部。" : "红果未确认下载任务，稍后重试。";
                                }
                                finally
                                {
                                    var restoreResult = CanRestore() ? RedFruitWatchRestore.TryRestore(source, previous.SeriesTitle, previous.CurrentIndex, CanRestore, message => RandomCacheStatus = message) : RedFruitWatchRestore.Result.Interrupted;
                                    if (restoreResult != RedFruitWatchRestore.Result.Restored)
                                        throw new InvalidOperationException($"无法确认恢复观看记录（{restoreResult}），停止随机缓存。");
                                }
                            }
                            finally { NavigationStyleState.Leave(source, oldStyle); }
                        }
                        finally { _sourceUiGate.Release(); }
                    });
                    RandomCacheStatus = result;
                    if (result == "这部剧已下载，稍后换一部。") { _randomSeriesId = _randomSeriesTitle = null; SaveSettings(); }
                }
                finally { _cacheChecking = false; _watchRestoring = false; UpdateRelayThumbnail(); RequestPlaybackSync(); }
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException) { if (!_randomCaching) RandomCacheStatus = "随机缓存已停止；红果已接收的任务可能仍在下载。"; }
        catch (Exception ex) { Debug.WriteLine(ex); RandomCacheStatus = ex is InvalidOperationException ? ex.Message : "随机缓存遇到错误，请停止后重试。"; _randomCaching = false; SaveSettings(); }
        finally { if (ReferenceEquals(_randomCacheCancellation, owner)) _randomCacheCancellation = null; owner.Dispose(); }
    }

    private async Task CheckOfflineCacheAsync()
    {
        if (_randomCaching || _cacheChecking || _watchRestoring || IsVisible || _relayWindow == IntPtr.Zero || _playbackSyncRunning ||
            DateTime.UtcNow - _lastCacheAttemptUtc < TimeSpan.FromSeconds(60)) return;
        var source = _relayWindow;
        var player = new WindowInteropHelper(this).Handle;
        var sourcePid = _relayProcessId;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer");
        _cacheChecking = true;
        _lastCacheAttemptUtc = DateTime.UtcNow;
        try
        {
            var outcome = await Task.Run(() =>
            {
                if (!_sourceUiGate.Wait(0)) return (RedFruitDownloadDialog.Result.Interrupted, (string?)null);
                try
                {
                var snapshot = RedFruitProgressReader.ReadLatest(Path.Combine(data, "hgplayer.db"));
                if (snapshot is null || (snapshot.CurrentVideoId == _cacheCheckedVideoId && DateTime.UtcNow - _lastCacheSuccessUtc < TimeSpan.FromMinutes(5)) || !OfflineCacheStorage.CanAdd(data))
                    return (RedFruitDownloadDialog.Result.Unavailable, (string?)null);
                bool Safe()
                {
                    if (!WindowRelay.IsWindow(player) || !WindowRelay.IsWindow(source) || IsWindowVisible(player) || WindowRelay.IsMinimized(source)) return false;
                    GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
                    return foregroundPid != sourcePid;
                }
                if (!Safe() || !NavigationStyleState.TryEnter(source, sourcePid, out var wasNoActivate)) return (RedFruitDownloadDialog.Result.Interrupted, (string?)null);
                try
                {
                    bool Guarded() => Safe() && WindowRelay.TryGetNoActivate(source, out var isSet) && isSet;
                    return (RedFruitDownloadDialog.QueueNext(source, snapshot.CurrentIndex, snapshot.SeriesTitle, Guarded), snapshot.CurrentVideoId);
                }
                finally { NavigationStyleState.Leave(source, wasNoActivate); }
                }
                finally { _sourceUiGate.Release(); }
            });
            if (source == _relayWindow && outcome.Item1 is RedFruitDownloadDialog.Result.Submitted or RedFruitDownloadDialog.Result.NoWork)
            {
                _cacheCheckedVideoId = outcome.Item2;
                _lastCacheSuccessUtc = DateTime.UtcNow;
            }
            Debug.WriteLine($"后台缓存检查：{outcome.Item1}");
        }
        catch (Exception ex) { Debug.WriteLine($"后台缓存检查失败：{ex.Message}"); }
        finally { _cacheChecking = false; }
    }

    private async Task<bool> EnsureRedFruitConnectedAsync(bool showErrors = true)
    {
        if (_relayWindow != IntPtr.Zero && WindowRelay.IsWindow(_relayWindow)) return true;
        if (_connecting || _closing) return false;
        _connecting = true;
        RedFruitStartupSize? startupSize = null;
        try
        {
        RedFruitProgressReader.Snapshot? watchSnapshot = null;
        var selected = FindRedFruitWindow();
        if (selected is null)
        {
            var packagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "hongguo-v1.2.17-windows-amd64", "红果短剧.exe"));
            var alreadyRunning = IsRedFruitProcessRunning();
            if (!alreadyRunning && File.Exists(packagePath))
            {
                var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer", "hgplayer.db");
                watchSnapshot = await Task.Run(() => RedFruitProgressReader.ReadLatest(dataPath));
                if (_closing || !_autoReconnectEnabled) return false;
                var windowSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hgplayer", "window.json");
                startupSize = RedFruitStartupSize.Prepare(windowSettingsPath);
                try { Process.Start(new ProcessStartInfo(packagePath) { UseShellExecute = true }); }
                catch (Exception ex) { if (showErrors) System.Windows.MessageBox.Show($"无法启动红果：{ex.Message}", "红果播放器", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
            }
            for (var attempt = 0; attempt < 400 && selected is null; attempt++)
            {
                await Task.Delay(50);
                if (_closing || !_autoReconnectEnabled) return false;
                selected = FindRedFruitWindow();
            }
        }

        if (selected is null)
        {
            if (showErrors) System.Windows.MessageBox.Show("没有找到红果窗口。请先打开红果并开始播放，再从右键菜单连接。", "红果播放器", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        WindowRelay.WindowRect? initialPosition = null;
        if (!WindowRelay.IsMinimized(selected.Handle) && WindowRelay.MoveOffscreen(selected.Handle, out var priorPosition))
            initialPosition = startupSize is null ? priorPosition : priorPosition with { Width = startupSize.OriginalWidth, Height = startupSize.OriginalHeight };
        if (startupSize is not null && initialPosition is not null)
            WindowRelay.ResizeWithoutMoving(selected.Handle, startupSize.OriginalWidth, startupSize.OriginalHeight);
        StartWindowRelay(selected, showErrors);
        if (_relayWindow == selected.Handle && initialPosition is not null) _relayOriginalPosition = initialPosition;
        else if (initialPosition is { } restorePosition) WindowRelay.RestorePosition(selected.Handle, restorePosition);
        KeepRelayOffscreen();
        if (!IsVisible) { _relayVisible = false; UpdateRelayThumbnail(); }
        RequestPlaybackSync();
        if (watchSnapshot is not null && _relayWindow != IntPtr.Zero)
            _ = RestoreWatchAsync(_relayWindow, _relayProcessId, watchSnapshot);
        return _relayWindow != IntPtr.Zero;
        }
        finally { startupSize?.Dispose(); _connecting = false; }
    }

    private async Task RestoreWatchAsync(IntPtr source, int sourcePid, RedFruitProgressReader.Snapshot snapshot)
    {
        var generation = Interlocked.Increment(ref _watchRestoreGeneration);
        var player = new WindowInteropHelper(this).Handle;
        _watchRestoring = true;
        UpdateRelayThumbnail();
        bool gateHeld = false;
        try
        {
            bool Safe()
            {
                if (Volatile.Read(ref _watchRestoreGeneration) != generation || !WindowRelay.IsWindow(player) || !WindowRelay.IsWindow(source)) return false;
                GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
                return foregroundPid != sourcePid;
            }
            await _sourceUiGate.WaitAsync(); gateHeld = true;
            var result = await Task.Run(() =>
            {
                if (!Safe() || !NavigationStyleState.TryEnter(source, sourcePid, out var wasNoActivate)) return RedFruitWatchRestore.Result.Interrupted;
                try
                {
                    bool Guarded() => Safe() && WindowRelay.TryGetNoActivate(source, out var isSet) && isSet;
                    return RedFruitWatchRestore.TryRestore(source, snapshot.SeriesTitle, snapshot.CurrentIndex, Guarded);
                }
                finally { NavigationStyleState.Leave(source, wasNoActivate); }
            });
            Debug.WriteLine($"恢复观看记录：{result}");
        }
        catch (Exception ex) { Debug.WriteLine($"恢复观看记录失败：{ex.Message}"); }
        finally
        {
            if (gateHeld) _sourceUiGate.Release();
            if (Volatile.Read(ref _watchRestoreGeneration) == generation && source == _relayWindow)
            {
                _watchRestoring = false;
                _autoFitSucceeded = false;
                UpdateRelayThumbnail(); RequestPlaybackSync();
                _ = AutoFitRelayCropAsync(source);
            }
        }
    }

    private void CancelWatchRestore()
    {
        if (!_watchRestoring) return;
        Interlocked.Increment(ref _watchRestoreGeneration);
        _watchRestoring = false;
        UpdateRelayThumbnail();
    }

    private static WindowRelay.WindowInfo? FindRedFruitWindow() => WindowRelay.EnumerateWindows().FirstOrDefault(w =>
        w.Title.Contains("红果", StringComparison.OrdinalIgnoreCase)
        || w.ProcessName.Contains("红果", StringComparison.OrdinalIgnoreCase)
        || w.ProcessName.Contains("hongguo", StringComparison.OrdinalIgnoreCase));

    private static bool IsRedFruitProcessRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                if (process.ProcessName.Contains("红果", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Contains("hongguo", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void StartWindowRelay(WindowRelay.WindowInfo selected, bool showErrors)
    {
        StopWindowRelay();
        var destination = new WindowInteropHelper(this).Handle;
        var hr = WindowRelay.Register(destination, selected.Handle, out _relayThumbnail);
        if (hr < 0)
        {
            if (showErrors) System.Windows.MessageBox.Show("无法转播所选窗口。", "实时转播", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        _relayWindow = selected.Handle; _relayProcessId = selected.ProcessId; _relayVisible = true;
        NavigationStyleState.Recover(_relayWindow, _relayProcessId);
        _resumeAfterAutoPause = AutoPauseState.Matches(_relayProcessId);
        RedFruitTrayIcon.Hide(_relayProcessId);
        _lastTrayHideUtc = DateTime.UtcNow;
        if (WindowRelay.IsMinimized(_relayWindow)) WindowRelay.RestoreMinimized(_relayWindow);
        if (WindowRelay.HideFromAltTab(_relayWindow, out var originalStyle))
            _relayOriginalExtendedStyle = originalStyle;
        var defaultCrop = WindowRelay.CenterCrop(_relayThumbnail, _aspectWidth, _aspectHeight);
        _relayCrop = defaultCrop.Right > defaultCrop.Left && defaultCrop.Bottom > defaultCrop.Top ? defaultCrop : null;
        _relayCropManuallySet = false;
        _autoFitSucceeded = false;
        _lastAutoFitUtc = DateTime.UtcNow;
        try { _relayAudio = AudioSessionControl.MuteProcess(_relayProcessId); }
        catch (Exception ex) { Debug.WriteLine($"静音红果音频失败: {ex}"); _relayAudio = []; }
        _relayMuted = true;
        _lastMuteAttemptUtc = DateTime.UtcNow;
        _timer.Start();
        UpdateRelayThumbnail();
        _ = AutoFitRelayCropAsync(_relayWindow);
    }

    private async Task AutoFitRelayCropAsync(IntPtr source)
    {
        if (_autoFitInProgress) return;
        var (width, height) = WindowRelay.GetSourceSize(_relayThumbnail);
        if (width <= 0 || height <= 0) return;
        _autoFitInProgress = true;
        try
        {
            var crop = await Task.Run(() => VideoRegionDetector.Detect(source, width, height, _aspectWidth, _aspectHeight));
            if (_relayWindow != source || _relayCropManuallySet || crop is not { } found) return;
            var updatedCrop = WindowRelay.ClampCrop(found, _relayThumbnail);
            if (_relayCrop != updatedCrop) ResetSubtitlePosition();
            _relayCrop = updatedCrop;
            _autoFitSucceeded = _relayCrop is not null;
            UpdateRelayThumbnail();
        }
        catch (Exception ex) { Debug.WriteLine($"自动适配视频画面失败: {ex}"); }
        finally { _autoFitInProgress = false; }
    }

    private void SetRelayCropToVideoRatio()
    {
        if (_relayThumbnail == IntPtr.Zero) return;
        var crop = WindowRelay.CenterCrop(_relayThumbnail, _aspectWidth, _aspectHeight);
        _relayCrop = crop.Right > crop.Left && crop.Bottom > crop.Top ? crop : null;
        _relayCropManuallySet = true;
        UpdateRelayThumbnail();
    }

    private async void AutoDetectVideoRegion()
    {
        if (_relayThumbnail == IntPtr.Zero) return;
        if (WindowRelay.IsMinimized(_relayWindow))
        {
            System.Windows.MessageBox.Show("红果窗口已最小化并暂停播放。请先恢复窗口、开始播放，再自动识别。", "自动识别视频画面", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (width, height) = WindowRelay.GetSourceSize(_relayThumbnail);
        var source = _relayWindow;
        try
        {
            var crop = await Task.Run(() => VideoRegionDetector.Detect(source, width, height, _aspectWidth, _aspectHeight));
            if (_relayWindow != source) return;
            if (crop is { } found)
            {
                _relayCrop = WindowRelay.ClampCrop(found, _relayThumbnail);
                _relayCropManuallySet = true;
                UpdateRelayThumbnail();
            }
            else
            {
                System.Windows.MessageBox.Show("没检测到明显移动的视频区域。确认红果正在播放动态画面后重试，也可以用“自定义转播区域…”在截图上拖框。", "自动识别视频画面", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            System.Windows.MessageBox.Show("识别视频区域失败。", "自动识别视频画面", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SetRelayCrop(WindowRelay.CropRect? crop)
    {
        if (_relayThumbnail == IntPtr.Zero) return;
        _relayCrop = crop is null ? null : WindowRelay.ClampCrop(crop.Value, _relayThumbnail);
        _relayCropManuallySet = true;
        ResetSubtitlePosition();
        UpdateRelayThumbnail();
    }

    private int UpdateLayerWithBadge(IntPtr primary, IntPtr secondary, WindowRelay.CropRect source, int left, int top, int width, int height, byte opacity, bool show)
    {
        int badgeLeft = 10 + VideoW - 25, badgeTop = VideoY + VideoH - 14;
        int rightCut = Math.Clamp(left + width - badgeLeft, 0, Math.Max(0, width - 1));
        int overlap = rightCut > 0 ? Math.Clamp(top + height - badgeTop, 0, height) : 0;
        if (secondary == IntPtr.Zero || overlap == 0)
        {
            int result = WindowRelay.Update(primary, left, top, width, height, show, source, opacity);
            if (secondary != IntPtr.Zero) WindowRelay.Update(secondary, left, top, width, height, false);
            return result;
        }
        if (overlap < height && source.Bottom - source.Top >= 2 && source.Right - source.Left >= 2)
        {
            var parts = WindowRelay.SplitForBadge(source, height, width, overlap, rightCut);
            int result = WindowRelay.Update(primary, left, top, width, height - overlap, show, parts.Top, opacity);
            WindowRelay.Update(secondary, left, top + height - overlap, width - rightCut, overlap, show, parts.BottomLeft, opacity);
            return result;
        }
        var leftPart = WindowRelay.SubtitleSlice(source, 0, height, height, width, width - rightCut);
        int clipped = WindowRelay.Update(primary, left, top, width - rightCut, height, show, leftPart, opacity);
        WindowRelay.Update(secondary, left, top, width, height, false);
        return clipped;
    }

    private void UpdateRelayThumbnail()
    {
        if (_relayThumbnail == IntPtr.Zero) return;
        var opacity = (byte)Math.Round(Math.Clamp(_brightness * Opacity, 0, 1) * 255);
        var (width, height) = WindowRelay.GetSourceSize(_relayThumbnail);
        if (width <= 0 || height <= 0) return;
        var source = _relayCrop ?? new WindowRelay.CropRect(0, 0, width, height);
        var visible = _fullSkin ? source : WindowRelay.FitCrop(source, VideoW, VideoH);
        var placement = _fullSkin ? WindowRelay.FitInside(visible, VideoW, VideoH) : new WindowRelay.WindowRect(0, 0, VideoW, VideoH);
        int drawLeft = 10 + placement.Left, drawTop = VideoY + placement.Top, drawWidth = placement.Width, drawHeight = placement.Height;
        if (_relayBottomThumbnail == IntPtr.Zero)
            WindowRelay.Register(new WindowInteropHelper(this).Handle, _relayWindow, out _relayBottomThumbnail);
        UpdateLayerWithBadge(_relayThumbnail, _relayBottomThumbnail, visible, drawLeft, drawTop, drawWidth, drawHeight, opacity, _relayVisible && !_loopActive && !_watchRestoring);
        LoopImage.Opacity = _brightness;
        LoopSubtitle.Opacity = _brightness;
        if (_loopActive || _watchRestoring)
        {
            if (_subtitleThumbnail != IntPtr.Zero) WindowRelay.Update(_subtitleThumbnail, 10, 93, 155, 16, false);
            if (_subtitleBottomThumbnail != IntPtr.Zero) WindowRelay.Update(_subtitleBottomThumbnail, 10, 95, 130, 14, false);
            if (_subtitleFadeFirst != IntPtr.Zero) WindowRelay.Update(_subtitleFadeFirst, 10, 95, 130, 1, false);
            if (_subtitleFadeSecond != IntPtr.Zero) WindowRelay.Update(_subtitleFadeSecond, 10, 95, 130, 1, false);
            return;
        }

        if (_subtitleZoomLevel == 0 || drawHeight < 4 || drawWidth < 28)
        {
            if (_subtitleThumbnail != IntPtr.Zero) WindowRelay.Update(_subtitleThumbnail, 10, 93, 155, 16, false);
            if (_subtitleBottomThumbnail != IntPtr.Zero) WindowRelay.Update(_subtitleBottomThumbnail, 10, 95, 130, 14, false);
            if (_subtitleFadeFirst != IntPtr.Zero) WindowRelay.Update(_subtitleFadeFirst, 10, 95, 130, 1, false);
            if (_subtitleFadeSecond != IntPtr.Zero) WindowRelay.Update(_subtitleFadeSecond, 10, 95, 130, 1, false);
            return;
        }
        if (_subtitleThumbnail == IntPtr.Zero)
        {
            var result = WindowRelay.Register(new WindowInteropHelper(this).Handle, _relayWindow, out _subtitleThumbnail);
            if (result < 0 || _subtitleThumbnail == IntPtr.Zero)
            {
                _subtitleZoomUnavailable = true;
                Debug.WriteLine($"字幕局部放大不可用: 0x{result:X8}");
                return;
            }
        }
        // Use the displayed width for the requested zoom; use the original
        // bottom so subtitles remain available even in a cropped portrait video.
        var captionSource = new WindowRelay.CropRect(visible.Left, source.Top, visible.Right, source.Bottom);
        var safeCenter = WindowRelay.SubtitleCenterAllowed(visible, captionSource, _subtitleCenter) ? _subtitleCenter : null;
        var requestedZoom = _subtitleZoomLevel switch { 1 => 1.25, 3 => 1.38, _ => 1.5 };
        var detectedSize = safeCenter is null ? null : _subtitleSize;
        int bandHeight = _fullSkin
            ? WindowRelay.BottomSubtitleBandHeight(visible, captionSource, safeCenter, detectedSize?.HeightFraction, VideoH, placement, SubtitleHeight)
            : Math.Min(SubtitleHeight, drawHeight);
        int subtitleBadgeCut = drawTop + drawHeight > VideoY + VideoH - 14 ? Math.Clamp(drawLeft + drawWidth - (10 + VideoW - 25), 0, drawWidth - 1) : 0;
        var adjustedZoom = WindowRelay.AdaptiveSubtitleZoom(captionSource, drawWidth, bandHeight, requestedZoom, detectedSize?.WidthFraction, detectedSize?.HeightFraction, subtitleBadgeCut);
        var band = WindowRelay.SubtitleBand(captionSource, drawWidth, bandHeight, adjustedZoom, safeCenter);
        if (_subtitleBottomThumbnail == IntPtr.Zero)
            WindowRelay.Register(new WindowInteropHelper(this).Handle, _relayWindow, out _subtitleBottomThumbnail);
        int top = VideoY + WindowRelay.SubtitleTopInFrame(visible, captionSource, safeCenter, VideoH, placement, bandHeight, _fullSkin);
        int badgeTop = VideoY + VideoH - 14, badgeLeft = 10 + VideoW - 25;
        if (_subtitleFadeFirst == IntPtr.Zero) WindowRelay.Register(new WindowInteropHelper(this).Handle, _relayWindow, out _subtitleFadeFirst);
        if (_subtitleFadeSecond == IntPtr.Zero) WindowRelay.Register(new WindowInteropHelper(this).Handle, _relayWindow, out _subtitleFadeSecond);
        int feather = _subtitleFadeFirst != IntPtr.Zero && _subtitleFadeSecond != IntPtr.Zero && bandHeight > 2 ? 2 : 0;
        if (feather == 2)
        {
            int cut = Math.Clamp(drawLeft + drawWidth - badgeLeft, 0, Math.Max(0, drawWidth - 1));
            int firstWidth = top + 1 > badgeTop ? drawWidth - cut : drawWidth;
            int secondWidth = top + 2 > badgeTop ? drawWidth - cut : drawWidth;
            var first = WindowRelay.SubtitleSlice(band, 0, 1, bandHeight, drawWidth, firstWidth);
            var second = WindowRelay.SubtitleSlice(band, 1, 1, bandHeight, drawWidth, secondWidth);
            WindowRelay.Update(_subtitleFadeFirst, drawLeft, top, firstWidth, 1, _relayVisible, first, (byte)(opacity * .3));
            WindowRelay.Update(_subtitleFadeSecond, drawLeft, top + 1, secondWidth, 1, _relayVisible, second, (byte)(opacity * .7));
        }
        else
        {
            if (_subtitleFadeFirst != IntPtr.Zero) WindowRelay.Update(_subtitleFadeFirst, drawLeft, top, drawWidth, 1, false);
            if (_subtitleFadeSecond != IntPtr.Zero) WindowRelay.Update(_subtitleFadeSecond, drawLeft, top, drawWidth, 1, false);
        }
        int bodyTop = top + feather, bodyHeight = bandHeight - feather;
        var bodyBand = WindowRelay.SubtitleSlice(band, feather, bodyHeight, bandHeight, drawWidth, drawWidth);
        int update = UpdateLayerWithBadge(_subtitleThumbnail, _subtitleBottomThumbnail, bodyBand, drawLeft, bodyTop, drawWidth, bodyHeight, opacity, _relayVisible);
        if (update < 0 && !_subtitleZoomUnavailable)
        {
            _subtitleZoomUnavailable = true;
            Debug.WriteLine($"字幕放大更新失败: 0x{update:X8}");
            BuildContextMenu();
        }
    }

    private void ShowRelayCropDialog()
    {
        if (_relayThumbnail == IntPtr.Zero) return;
        var (sourceWidth, sourceHeight) = WindowRelay.GetSourceSize(_relayThumbnail);
        if (sourceWidth <= 0 || sourceHeight <= 0) return;
        var initial = _relayCrop ?? new WindowRelay.CropRect(0, 0, sourceWidth, sourceHeight);
        try
        {
            using var dialog = new CropSelectionForm(_relayWindow, sourceWidth, sourceHeight, initial);
            if (dialog.ShowDialog(new NativeWindowOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
                SetRelayCrop(dialog.SelectedCrop);
        }
        catch (InvalidOperationException ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "自定义转播区域", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void StopWindowRelay()
    {
        CancelWatchRestore();
        ClearRecentLoop();
        _resumeAfterAutoPause = false;
        _autoFitSucceeded = false;
        _playbackControlUnavailable = false;
        if (_relayOriginalExtendedStyle is { } originalStyle)
        {
            try { WindowRelay.RestoreExtendedStyle(_relayWindow, originalStyle); } catch (Exception ex) { Debug.WriteLine(ex); }
            _relayOriginalExtendedStyle = null;
        }
        if (_relayOriginalPosition is { } original)
        {
            try { WindowRelay.RestorePosition(_relayWindow, original); } catch (Exception ex) { Debug.WriteLine(ex); }
            _relayOriginalPosition = null;
        }
        if (_subtitleFadeFirst != IntPtr.Zero) { WindowRelay.Unregister(_subtitleFadeFirst); _subtitleFadeFirst = IntPtr.Zero; }
        if (_subtitleFadeSecond != IntPtr.Zero) { WindowRelay.Unregister(_subtitleFadeSecond); _subtitleFadeSecond = IntPtr.Zero; }
        if (_subtitleBottomThumbnail != IntPtr.Zero) { WindowRelay.Unregister(_subtitleBottomThumbnail); _subtitleBottomThumbnail = IntPtr.Zero; }
        if (_subtitleThumbnail != IntPtr.Zero) { WindowRelay.Unregister(_subtitleThumbnail); _subtitleThumbnail = IntPtr.Zero; }
        if (_relayBottomThumbnail != IntPtr.Zero) { WindowRelay.Unregister(_relayBottomThumbnail); _relayBottomThumbnail = IntPtr.Zero; }
        if (_relayThumbnail != IntPtr.Zero) { WindowRelay.Unregister(_relayThumbnail); _relayThumbnail = IntPtr.Zero; }
        if (_relayAudio.Count > 0) { try { AudioSessionControl.Restore(_relayAudio); } catch (Exception ex) { Debug.WriteLine(ex); } _relayAudio = []; }
        _relayWindow = IntPtr.Zero; _relayProcessId = 0; _relayVisible = true; _relayMuted = true; _relayCrop = null;
    }

    private void HideRelaySourceFromAltTab()
    {
        if (_relayWindow == IntPtr.Zero) return;
        if (_relayOriginalExtendedStyle is not null) return;
        if (WindowRelay.HideFromAltTab(_relayWindow, out var originalStyle))
        {
            _relayOriginalExtendedStyle = originalStyle;
        }
        else
            System.Windows.MessageBox.Show("无法修改转播窗口样式。请确认红果窗口仍然打开。", "隐藏 Alt+Tab 窗口", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreRelaySourceAltTab()
    {
        if (_relayOriginalExtendedStyle is not { } originalStyle || _relayWindow == IntPtr.Zero) return;
        if (WindowRelay.RestoreExtendedStyle(_relayWindow, originalStyle))
        {
            _relayOriginalExtendedStyle = null;
        }
    }

    private void MoveRelaySourceOffscreen()
    {
        if (_relayWindow == IntPtr.Zero) return;
        if (WindowRelay.IsMinimized(_relayWindow))
        {
            System.Windows.MessageBox.Show("请先还原红果窗口并开始播放，再移到屏幕外。最小化状态无法转播。", "移到屏幕外播放", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_relayOriginalPosition is not null) return;
        if (WindowRelay.MoveOffscreen(_relayWindow, out var original))
        {
            _relayOriginalPosition = original;
            UpdateRelayThumbnail();
        }
        else
            System.Windows.MessageBox.Show("移动红果窗口失败。请确认窗口仍然打开且没有最小化。", "移到屏幕外播放", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreRelaySourceWindow()
    {
        if (_relayOriginalPosition is not { } original || _relayWindow == IntPtr.Zero) return;
        if (WindowRelay.RestorePosition(_relayWindow, original))
        {
            _relayOriginalPosition = null;
            UpdateRelayThumbnail();
        }
    }
    private async void TogglePlayback()
    {
        if (_loopActive)
        {
            _loopManuallyPaused = !_loopManuallyPaused;
            if (_loopManuallyPaused) _loopClock.Stop(); else if (IsVisible) _loopClock.Start();
            return;
        }
        CancelWatchRestore();
        if (_relayWindow == IntPtr.Zero) { ConnectRedFruit(); return; }
        if (!IsVisible || _playbackSyncRunning) return;
        var source = _relayWindow;
        _playbackSyncRunning = true;
        bool gateHeld = false;
        try
        {
            await _sourceUiGate.WaitAsync(); gateHeld = true;
            var paused = await Task.Run(() => RedFruitPlaybackControl.GetPaused(source));
            if (paused is null || source != _relayWindow || !IsVisible) return;
            var result = await Task.Run(() => RedFruitPlaybackControl.SetPaused(source, !paused.Value));
            if (source == _relayWindow && result is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.AlreadySet)
            {
                _resumeAfterAutoPause = false;
                AutoPauseState.Clear();
            }
        }
        catch (Exception ex) { Debug.WriteLine($"切换红果播放状态失败: {ex.Message}"); }
        finally
        {
            if (gateHeld) _sourceUiGate.Release();
            _playbackSyncRunning = false;
            if (_playbackSyncDirty || !IsVisible) RequestPlaybackSync();
        }
    }

    private async void NextRedFruit()
    {
        ResetSubtitlePosition();
        CancelWatchRestore();
        if (_loopActive) { ClearRecentLoop(); UpdateRelayThumbnail(); RequestPlaybackSync(); }
        if (_relayWindow == IntPtr.Zero || !IsVisible) return;
        var source = _relayWindow;
        bool gateHeld = false;
        try { await _sourceUiGate.WaitAsync(); gateHeld = true; if (source == _relayWindow && IsVisible) await Task.Run(() => RedFruitPlaybackControl.NextEpisode(source)); }
        catch (Exception ex) { Debug.WriteLine($"切换红果下一条失败: {ex.Message}"); }
        finally { if (gateHeld) _sourceUiGate.Release(); }
    }

    private void ChangeRelayVolume(float delta)
    {
        if (_relayWindow != IntPtr.Zero) AudioSessionControl.ChangeVolume(_relayProcessId, delta);
    }

    private async void RequestPlaybackSync()
    {
        _playbackSyncDirty = true;
        if (_watchRestoring) return;
        if (_playbackSyncRunning) return;
        _playbackSyncRunning = true;
        try
        {
            while (_playbackSyncDirty)
            {
                _playbackSyncDirty = false;
                var source = _relayWindow;
                if (source == IntPtr.Zero) continue;
                var shouldPause = !_desiredVisible || !IsVisible || !_relayVisible || _loopActive;
                if (!shouldPause && !_resumeAfterAutoPause) continue;
                var result = await Task.Run(() => RedFruitPlaybackControl.SetPaused(source, shouldPause));
                if (source != _relayWindow) continue;
                var unavailable = result is RedFruitPlaybackControl.ChangeResult.Unavailable or RedFruitPlaybackControl.ChangeResult.Unconfirmed;
                if (_playbackControlUnavailable != unavailable) { _playbackControlUnavailable = unavailable; BuildContextMenu(); }
                if (shouldPause && result is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.Unconfirmed)
                {
                    _resumeAfterAutoPause = true;
                    AutoPauseState.Remember(_relayProcessId);
                }
                else if (!shouldPause && result is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.AlreadySet)
                {
                    _resumeAfterAutoPause = false;
                    AutoPauseState.Clear();
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"同步红果播放状态失败: {ex.Message}"); }
        finally
        {
            _playbackSyncRunning = false;
            if (_playbackSyncDirty) RequestPlaybackSync();
        }
    }

    private void KeepRelayOffscreen()
    {
        if (_relayWindow == IntPtr.Zero || _relayOriginalPosition is not null || WindowRelay.IsMinimized(_relayWindow)) return;
        if (WindowRelay.MoveOffscreen(_relayWindow, out var original))
        {
            _relayOriginalPosition = original;
            UpdateRelayThumbnail();
        }
        else Debug.WriteLine("无法将红果源窗口移到屏幕外。");
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_relayWindow != IntPtr.Zero)
        {
            if (_relayMuted) { AudioSessionControl.SetProcessMuted(_relayProcessId, false); _relayAudio = []; _relayMuted = false; }
            else { _relayAudio = AudioSessionControl.MuteProcess(_relayProcessId); _relayMuted = true; }
            return;
        }
    }
    private void UpdateProgress()
    {
        if (_relayWindow != IntPtr.Zero)
        {
            if (!WindowRelay.IsWindow(_relayWindow)) StopWindowRelay();
            else if (_relayMuted && _relayAudio.Count == 0 && DateTime.UtcNow - _lastMuteAttemptUtc >= TimeSpan.FromSeconds(1))
            {
                _lastMuteAttemptUtc = DateTime.UtcNow;
                try { _relayAudio = AudioSessionControl.MuteProcess(_relayProcessId); } catch (Exception ex) { Debug.WriteLine(ex); }
            }
            if (_relayProcessId > 0 && DateTime.UtcNow - _lastTrayHideUtc >= TimeSpan.FromSeconds(5))
            {
                _lastTrayHideUtc = DateTime.UtcNow;
                RedFruitTrayIcon.Hide(_relayProcessId);
            }
            if (DateTime.UtcNow - _lastPlaybackCheckUtc >= TimeSpan.FromSeconds(3) && (!IsVisible || !_relayVisible || _resumeAfterAutoPause))
            {
                _lastPlaybackCheckUtc = DateTime.UtcNow;
                RequestPlaybackSync();
            }
            if (IsVisible && _relayVisible && !_watchRestoring && !_relayCropManuallySet && !_autoFitSucceeded && !_autoFitInProgress && DateTime.UtcNow - _lastAutoFitUtc >= TimeSpan.FromSeconds(5))
            {
                _lastAutoFitUtc = DateTime.UtcNow;
                _ = AutoFitRelayCropAsync(_relayWindow);
            }
            return;
        }
        if (_autoReconnectEnabled && !_closing && !_connecting && DateTime.UtcNow - _lastReconnectAttemptUtc >= TimeSpan.FromSeconds(10))
        {
            _lastReconnectAttemptUtc = DateTime.UtcNow;
            _ = EnsureRedFruitConnectedAsync(showErrors: false);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void DismissAd_Click(object sender, RoutedEventArgs e) => HidePlayerWindow();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true; _autoReconnectEnabled = false;
        _selectionWindow?.Close();
        _cacheTimer.Stop();
        _randomCacheCancellation?.Cancel();
        _frameTimer.Stop();
        _fadeTimer.Stop();
        StopWindowRelay(); SaveSettings(); UnhookMouseShortcut();
    }

    private void ApplySkin()
    {
        ClassicHeading.Visibility = ClassicButtons.Visibility = ClassicSkinButton.Visibility = ClassicBottomButton.Visibility = _cardSkin || _fullSkin ? Visibility.Collapsed : Visibility.Visible;
        CardContent.Visibility = _cardSkin ? Visibility.Visible : Visibility.Collapsed;
        FullContent.Visibility = _fullSkin ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in new FrameworkElement[] { VideoFrame, LoopImage, LoopBorder }) { element.Width = VideoW; element.Height = VideoH; element.Margin = new Thickness(10, VideoY, 0, 0); }
        LoopSubtitle.Width = VideoW;
        LoopSubtitle.Height = SubtitleHeight;
        var featherMask = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
        featherMask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(75, 255, 255, 255), 0));
        featherMask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(180, 255, 255, 255), 1d / SubtitleHeight));
        featherMask.GradientStops.Add(new GradientStop(Colors.White, 2d / SubtitleHeight));
        featherMask.GradientStops.Add(new GradientStop(Colors.White, 1));
        LoopSubtitle.OpacityMask = featherMask;
        LoopSubtitle.Margin = new Thickness(10, VideoY + VideoH - SubtitleHeight, 0, 0);
        AdBadge.Margin = new Thickness(10 + VideoW - 25, VideoY + VideoH - 14, 0, 0);
        CloseButton.Margin = _fullSkin ? new Thickness(0, 141, 5, 0) : _cardSkin ? new Thickness(0, 125, 5, 0) : new Thickness(0, 3, 4, 0);
        UpdateRelayThumbnail();
    }

    private void ApplyTheme()
    {
        Shell.Background = Brush("#FFFFFF");
        VideoFrame.Background = Brush("#000000");
    }
    private static SolidColorBrush Brush(string color) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate) { handled = true; return new IntPtr(MaNoActivate); }
        if (msg == WmHotkey)
        {
            switch (wParam.ToInt32()) { case 1: TogglePlayback(); break; case 2: NextRedFruit(); break; case 3: ChangeRelayVolume(.1f); break; case 4: ChangeRelayVolume(-.1f); break; }
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
            _savedLeft = s?.Left; _savedTop = s?.Top; _dark = s?.Dark ?? false; _cardSkin = s?.CardSkin ?? false; _fullSkin = s?.FullSkin ?? false; if (_fullSkin) _cardSkin = false; _randomCaching = s?.RandomCaching ?? false; _randomSeriesId = s?.RandomSeriesId; _randomSeriesTitle = s?.RandomSeriesTitle;
            _brightness = s?.Brightness is > 0 ? Math.Clamp(s.Brightness, .2, 1) : .85;
            _subtitleZoomLevel = s?.SubtitleZoom is 1 or 2 or 3 ? s.SubtitleZoom.Value : 0;
            _videoWidth = s?.VideoWidth ?? 62; _videoHeight = s?.VideoHeight ?? 42;
            var gcd = GreatestCommonDivisor((int)_videoWidth, (int)_videoHeight);
            _aspectWidth = s?.AspectWidth > 0 ? s.AspectWidth : (int)_videoWidth / gcd;
            _aspectHeight = s?.AspectHeight > 0 ? s.AspectHeight : (int)_videoHeight / gcd;
        }
        catch { }
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        const int WmXButtonDown = 0x020B, WmMouseWheel = 0x020A;
        if (code >= 0 && message.ToInt32() == WmMouseWheel)
        {
            var hook = Marshal.PtrToStructure<MouseHookData>(data);
            int delta = unchecked((short)(hook.MouseData >> 16));
            if (_wheelGesture.Feed(delta, Environment.TickCount64)) Dispatcher.BeginInvoke(ToggleRecentLoop);
        }
        if (code >= 0 && message.ToInt32() == WmXButtonDown)
        {
            var hook = Marshal.PtrToStructure<MouseHookData>(data);
            var button = (hook.MouseData >> 16) & 0xFFFF;
            if (button == 2)
            {
                Dispatcher.BeginInvoke(() => { _manuallyHidden = _desiredVisible; RefreshImePanel(); });
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private void HidePlayerWindow()
    {
        _manuallyHidden = true;
        SetPlayerVisibility(false);
    }

    private void RefreshImePanel()
    {
        var anchor = SogouPanelTracker.Find();
        var shouldShow = !_manuallyHidden;
        var wasVisible = IsVisible;
        SetPlayerVisibility(shouldShow);
        if (shouldShow && anchor is { } location)
        {
            var placed = SogouPanelTracker.Place(new WindowInteropHelper(this).Handle, location, !wasVisible);
            if (placed is { } p && p != _lastSavedPosition && DateTime.UtcNow - _lastPositionPersistUtc >= TimeSpan.FromSeconds(2))
            {
                _lastSavedPosition = p; _lastPositionPersistUtc = DateTime.UtcNow;
                SaveSettings();
            }
        }
    }

    private void SetPlayerVisibility(bool show)
    {
        if (show == _desiredVisible) return;
        _desiredVisible = show;
        if (show) { ShowPlayerWindow(); return; }
        _selectionWindow?.Close();
        _wheelGesture.Reset();
        if (_loopActive) _loopClock.Stop(); else _recentFrames.Clear();
        RequestPlaybackSync();
        FadeTo(0, () =>
        {
            if (_desiredVisible) return;
            if (_relayWindow != IntPtr.Zero) { _relayVisible = false; KeepRelayOffscreen(); UpdateRelayThumbnail(); }
            Hide();
        });
    }

    private void ShowPlayerWindow()
    {
        EnsureOnScreen();
        if (!IsVisible) { Opacity = 0; Show(); }
        if (_loopActive && !_loopManuallyPaused) _loopClock.Start();
        if (_relayWindow != IntPtr.Zero)
        {
            _relayVisible = true;
            UpdateRelayThumbnail();
            RequestPlaybackSync();
        }
        FadeTo(1);
    }

    private void FadeTo(double target, Action? completed = null)
    {
        double from = Opacity;
        BeginAnimation(OpacityProperty, null);
        Opacity = from;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(150));
        animation.Completed += (_, _) =>
        {
            if (_desiredVisible != (target > 0)) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            _fadeTimer.Stop();
            UpdateRelayThumbnail();
            completed?.Invoke();
        };
        _fadeTimer.Start();
        BeginAnimation(OpacityProperty, animation);
    }

    private void UnhookMouseShortcut()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _mouseHookProc = null;
    }

    private async Task UpdateRecentFramesAsync()
    {
        if (!_desiredVisible || !IsVisible || !_relayVisible || _watchRestoring || _relayWindow == IntPtr.Zero) return;
        if (_loopActive)
        {
            if (_loopFrames.Length == 0) return;
            var lastInterval = _loopFrames.Length > 1 ? Math.Clamp(_loopFrames[^1].Milliseconds - _loopFrames[^2].Milliseconds, 30, 200) : 67;
            var duration = Math.Max(1, _loopFrames[^1].Milliseconds - _loopFrames[0].Milliseconds + lastInterval);
            var target = _loopFrames[0].Milliseconds + _loopClock.ElapsedMilliseconds % duration;
            var index = Array.FindLastIndex(_loopFrames, f => f.Milliseconds <= target);
            ShowLoopFrame(_loopFrames[Math.Max(0, index)]);
            return;
        }
        if (_capturingFrame || _relayThumbnail == IntPtr.Zero) return;
        var source = _relayWindow;
        var (width, height) = WindowRelay.GetSourceSize(_relayThumbnail);
        if (width <= 0 || height <= 0) return;
        var sourceCrop = _relayCrop ?? new WindowRelay.CropRect(0, 0, width, height);
        var crop = _fullSkin ? sourceCrop : WindowRelay.FitCrop(sourceCrop, VideoW, VideoH);
        var subtitleSource = new WindowRelay.CropRect(crop.Left, sourceCrop.Top, crop.Right, sourceCrop.Bottom);
        _capturingFrame = true;
        var captureWatch = Stopwatch.StartNew();
        var subtitleGeneration = _subtitleGeneration;
        bool detectSubtitle = _subtitleZoomLevel != 0 && _subtitleCenter is null && DateTime.UtcNow - _lastSubtitleDetectUtc >= TimeSpan.FromSeconds(1);
        if (detectSubtitle) _lastSubtitleDetectUtc = DateTime.UtcNow;
        var subtitleCenter = WindowRelay.SubtitleCenterAllowed(crop, subtitleSource, _subtitleCenter) ? _subtitleCenter : null;
        var containFrame = _fullSkin;
        var subtitleSize = subtitleCenter is null ? null : _subtitleSize;
        try
        {
            var frame = await Task.Run(() => RecentVideoFrames.Capture(source, width, height, crop, subtitleSource, VideoW, VideoH, SubtitleHeight, subtitleCenter, detectSubtitle, subtitleSize, containFrame, containFrame));
            captureWatch.Stop();
            _captureAverageMs = _captureAverageMs == 0 ? captureWatch.Elapsed.TotalMilliseconds : _captureAverageMs * .9 + captureWatch.Elapsed.TotalMilliseconds * .1;
            var interval = _captureAverageMs > 70 ? 125 : _captureAverageMs > 35 ? 100 : 67;
            if (_frameTimer.Interval.TotalMilliseconds != interval) _frameTimer.Interval = TimeSpan.FromMilliseconds(interval);
            if (frame is not null && source == _relayWindow && _desiredVisible && IsVisible && _relayVisible && !_loopActive && subtitleGeneration == _subtitleGeneration)
            {
                if (detectSubtitle && frame.DetectedSubtitle is { } candidate && WindowRelay.SubtitleCenterAllowed(crop, subtitleSource, candidate))
                {
                    if (_subtitleCandidate is { } previous && Math.Abs(previous - candidate) < .025) _subtitleCandidateCount++;
                    else { _subtitleCandidateCount = 1; _subtitleCandidateSize = null; }
                    _subtitleCandidate = candidate;
                    if (frame.DetectedSize is { } size)
                    {
                        if (_subtitleCandidateSize is { } priorSize)
                            _subtitleCandidateSize = new SubtitleRegionDetector.Detection(size.CenterY, Math.Max(size.WidthFraction, priorSize.WidthFraction), Math.Max(size.HeightFraction, priorSize.HeightFraction));
                        else _subtitleCandidateSize = size;
                    }
                    if (_subtitleCandidateCount >= 2 && _subtitleCenter is null)
                    { _subtitleCenter = candidate; _subtitleSize = _subtitleCandidateSize; UpdateRelayThumbnail(); }
                }
                _recentFrames.Add(Environment.TickCount64, frame);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"缓存最近画面失败：{ex.Message}"); }
        finally { _capturingFrame = false; }
    }

    private void ToggleRecentLoop()
    {
        if (_loopActive)
        {
            ClearRecentLoop();
        }
        else
        {
            var frames = _recentFrames.Snapshot();
            if (frames.Length == 0) return;
            _loopFrames = frames; _loopActive = true; _loopManuallyPaused = false; _loopClock.Restart();
            ShowLoopFrame(frames[0]); LoopImage.Visibility = Visibility.Visible; LoopBorder.Visibility = Visibility.Visible;
        }
        UpdateRelayThumbnail();
        RequestPlaybackSync();
    }

    private void ClearRecentLoop()
    {
        _loopActive = false; _loopManuallyPaused = false; _loopClock.Reset(); _loopFrames = []; _recentFrames.Clear();
        LoopImage.Source = null; LoopImage.Visibility = Visibility.Collapsed; LoopSubtitle.Source = null; LoopSubtitle.Visibility = Visibility.Collapsed; LoopBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowLoopFrame(RecentVideoFrames.Frame frame)
    {
        LoopImage.Source = frame.Picture.Image;
        var subtitle = frame.Picture.Subtitle(_subtitleZoomLevel);
        LoopSubtitle.Source = subtitle;
        int subtitleWidth = frame.Picture.SubtitleWidth > 0 ? frame.Picture.SubtitleWidth : VideoW;
        int subtitleHeight = frame.Picture.SubtitleHeight > 0 ? frame.Picture.SubtitleHeight : SubtitleHeight;
        LoopSubtitle.Width = subtitleWidth; LoopSubtitle.Height = subtitleHeight;
        LoopSubtitle.Margin = new Thickness(10 + frame.Picture.SubtitleLeft, VideoY + (frame.Picture.SubtitleTop >= 0 ? frame.Picture.SubtitleTop : VideoH - SubtitleHeight), 0, 0);
        LoopSubtitle.Visibility = subtitle is null || subtitleWidth < 28 || subtitleHeight < 4 ? Visibility.Collapsed : Visibility.Visible;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public NativePoint Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(Left, Top, _dark, _videoWidth, _videoHeight, _aspectWidth, _aspectHeight, _brightness, _subtitleZoomLevel, _cardSkin, _randomCaching, _randomSeriesId, _randomSeriesTitle, _fullSkin)));
            File.Move(temporary, _settingsPath, true);
        }
        catch (Exception ex) { Debug.WriteLine($"保存播放器设置失败：{ex.Message}"); }
    }
    private sealed record Settings(double Left, double Top, bool Dark, double VideoWidth = 62, double VideoHeight = 42, int AspectWidth = 31, int AspectHeight = 21, double Brightness = .85, int? SubtitleZoom = null, bool CardSkin = false, bool RandomCaching = false, string? RandomSeriesId = null, string? RandomSeriesTitle = null, bool FullSkin = false);
    private sealed class NativeWindowOwner(IntPtr handle) : Forms.IWin32Window { public IntPtr Handle { get; } = handle; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(IntPtr hWnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
