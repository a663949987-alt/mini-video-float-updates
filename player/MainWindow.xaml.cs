using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private bool _manuallyHidden;
    private bool _initialized;
    private bool _playbackSyncRunning, _playbackSyncDirty, _resumeAfterAutoPause;
    private DateTime _lastPlaybackCheckUtc;
    private bool _autoFitInProgress, _autoFitSucceeded, _playbackControlUnavailable;
    private DateTime _lastAutoFitUtc;
    private double _brightness = .85;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "settings.json");
    private HwndSource? _source;
    private IntPtr _relayWindow, _relayThumbnail;
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
    private double _videoWidth = 62, _videoHeight = 42;
    private int _aspectWidth = 31, _aspectHeight = 21;
    private double? _savedLeft, _savedTop;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateProgress();
        _imeTimer.Tick += (_, _) => RefreshImePanel();
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
        RefreshImePanel();
        Dispatcher.BeginInvoke(new Action(ConnectRedFruit));
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
        if (msg == 0x02E0) { Dispatcher.BeginInvoke(ApplyVideoSize); }
        return IntPtr.Zero;
    }

    private void Shell_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject element && System.Windows.Media.VisualTreeHelper.GetParent(element) is System.Windows.Controls.Button) return;
        if (e.OriginalSource is System.Windows.Controls.Button || e.OriginalSource is System.Windows.Controls.Primitives.Thumb) return;
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

    private void BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(Menu("打开 / 连接红果", (_, _) => ConnectRedFruit()));
        menu.Items.Add(Menu("暂停 / 继续（Ctrl+Alt+空格）", (_, _) => TogglePlayback()));
        menu.Items.Add(Menu("下一条 / 下一集（Ctrl+Alt+→）", (_, _) => NextRedFruit()));
        menu.Items.Add(Menu("红果静音 / 取消静音", (_, _) => Mute_Click(this, new RoutedEventArgs())));
        menu.Items.Add(Menu("音量增加（Ctrl+Alt+↑）", (_, _) => ChangeRelayVolume(.1f)));
        menu.Items.Add(Menu("音量降低（Ctrl+Alt+↓）", (_, _) => ChangeRelayVolume(-.1f)));
        if (_playbackControlUnavailable)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "红果暂停控制暂不可用", IsEnabled = false });
        var brightnessMenu = new System.Windows.Controls.MenuItem { Header = "画面亮度" };
        foreach (var level in new[] { 100, 85, 70, 55 })
        {
            var item = Menu($"{level}%", (_, _) => SetBrightness(level / 100d));
            item.IsCheckable = true; item.IsChecked = Math.Abs(_brightness - level / 100d) < .001;
            brightnessMenu.Items.Add(item);
        }
        menu.Items.Add(brightnessMenu);
        menu.Items.Add(Menu("隐藏播放器", (_, _) => HidePlayerWindow()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Menu("隐藏转播源窗口（Alt+Tab）", (_, _) => HideRelaySourceFromAltTab()));
        menu.Items.Add(Menu("恢复转播源窗口（Alt+Tab）", (_, _) => RestoreRelaySourceAltTab()));
        menu.Items.Add(Menu("转播窗口移到屏幕外", (_, _) => MoveRelaySourceOffscreen()));
        menu.Items.Add(Menu("恢复转播窗口位置", (_, _) => RestoreRelaySourceWindow()));
        menu.Items.Add(Menu("停止实时转播", (_, _) => StopWindowRelay()));
        menu.Items.Add(Menu("裁切为当前画面比例", (_, _) => SetRelayCropToVideoRatio()));
        menu.Items.Add(Menu("自动识别视频画面", (_, _) => AutoDetectVideoRegion()));
        menu.Items.Add(Menu("自定义转播区域…", (_, _) => ShowRelayCropDialog()));
        menu.Items.Add(Menu("重置裁切（完整窗口）", (_, _) => SetRelayCrop(null)));
        menu.Items.Add(Menu("设置画面尺寸…", (_, _) => ShowVideoSizeDialog()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Menu("退出", (_, _) => Close()));
        Shell.ContextMenu = menu;
    }

    private void OpenSkin_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://pinyin.sogou.com/proxy/inputclient?t=skin&f=windowsMenu") { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "打开搜狗皮肤库"); }
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
        await EnsureRedFruitConnectedAsync();
    }

    private async Task<bool> EnsureRedFruitConnectedAsync()
    {
        if (_relayWindow != IntPtr.Zero && WindowRelay.IsWindow(_relayWindow)) return true;
        var selected = FindRedFruitWindow();
        if (selected is null)
        {
            var packagePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "hongguo-v1.2.17-windows-amd64", "红果短剧.exe"));
            var alreadyRunning = IsRedFruitProcessRunning();
            if (!alreadyRunning && File.Exists(packagePath))
            {
                try { Process.Start(new ProcessStartInfo(packagePath) { UseShellExecute = true }); }
                catch (Exception ex) { System.Windows.MessageBox.Show($"无法启动红果：{ex.Message}", "红果播放器", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
            }
            for (var attempt = 0; attempt < 40 && selected is null; attempt++)
            {
                await Task.Delay(500);
                selected = FindRedFruitWindow();
            }
        }

        if (selected is null)
        {
            System.Windows.MessageBox.Show("没有找到红果窗口。请先打开红果并开始播放，再从右键菜单连接。", "红果播放器", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        StartWindowRelay(selected);
        KeepRelayOffscreen();
        if (!IsVisible) { _relayVisible = false; UpdateRelayThumbnail(); }
        RequestPlaybackSync();
        return _relayWindow != IntPtr.Zero;
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

    private void StartWindowRelay(WindowRelay.WindowInfo selected)
    {
        StopWindowRelay();
        var destination = new WindowInteropHelper(this).Handle;
        var hr = WindowRelay.Register(destination, selected.Handle, out _relayThumbnail);
        if (hr < 0)
        {
            System.Windows.MessageBox.Show("无法转播所选窗口。", "实时转播", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        _relayWindow = selected.Handle; _relayProcessId = selected.ProcessId; _relayVisible = true;
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
            _relayCrop = WindowRelay.ClampCrop(found, _relayThumbnail);
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
        UpdateRelayThumbnail();
    }

    private void UpdateRelayThumbnail()
    {
        if (_relayThumbnail != IntPtr.Zero)
            WindowRelay.Update(_relayThumbnail, 10, 11, 155, 98, _relayVisible,
                _relayCrop is { } crop ? WindowRelay.FitCrop(crop, 155, 98) : null,
                (byte)Math.Round(_brightness * 255));
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
        if (_relayWindow == IntPtr.Zero) { ConnectRedFruit(); return; }
        if (!IsVisible || _playbackSyncRunning) return;
        var source = _relayWindow;
        _playbackSyncRunning = true;
        try
        {
            var paused = await Task.Run(() => RedFruitPlaybackControl.GetPaused(source));
            if (paused is null || source != _relayWindow || !IsVisible) return;
            var result = await Task.Run(() => RedFruitPlaybackControl.SetPaused(source, !paused.Value));
            if (source == _relayWindow && result is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.AlreadySet)
                _resumeAfterAutoPause = false;
        }
        catch (Exception ex) { Debug.WriteLine($"切换红果播放状态失败: {ex.Message}"); }
        finally
        {
            _playbackSyncRunning = false;
            if (_playbackSyncDirty || !IsVisible) RequestPlaybackSync();
        }
    }

    private async void NextRedFruit()
    {
        if (_relayWindow == IntPtr.Zero || !IsVisible) return;
        var source = _relayWindow;
        try { await Task.Run(() => RedFruitPlaybackControl.NextEpisode(source)); }
        catch (Exception ex) { Debug.WriteLine($"切换红果下一条失败: {ex.Message}"); }
    }

    private void ChangeRelayVolume(float delta)
    {
        if (_relayWindow != IntPtr.Zero) AudioSessionControl.ChangeVolume(_relayProcessId, delta);
    }

    private async void RequestPlaybackSync()
    {
        _playbackSyncDirty = true;
        if (_playbackSyncRunning) return;
        _playbackSyncRunning = true;
        try
        {
            while (_playbackSyncDirty)
            {
                _playbackSyncDirty = false;
                var source = _relayWindow;
                if (source == IntPtr.Zero) continue;
                var shouldPause = !IsVisible || !_relayVisible;
                if (!shouldPause && !_resumeAfterAutoPause) continue;
                var result = await Task.Run(() => RedFruitPlaybackControl.SetPaused(source, shouldPause));
                if (source != _relayWindow) continue;
                var unavailable = result is RedFruitPlaybackControl.ChangeResult.Unavailable or RedFruitPlaybackControl.ChangeResult.Unconfirmed;
                if (_playbackControlUnavailable != unavailable) { _playbackControlUnavailable = unavailable; BuildContextMenu(); }
                if (shouldPause && result is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.Unconfirmed)
                    _resumeAfterAutoPause = true;
                else if (!shouldPause && result is RedFruitPlaybackControl.ChangeResult.Changed or RedFruitPlaybackControl.ChangeResult.AlreadySet)
                    _resumeAfterAutoPause = false;
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
            if (_relayMuted) { AudioSessionControl.Restore(_relayAudio); _relayAudio = []; _relayMuted = false; }
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
            if (DateTime.UtcNow - _lastPlaybackCheckUtc >= TimeSpan.FromSeconds(3) && (!IsVisible || !_relayVisible || _resumeAfterAutoPause))
            {
                _lastPlaybackCheckUtc = DateTime.UtcNow;
                RequestPlaybackSync();
            }
            if (IsVisible && _relayVisible && !_relayCropManuallySet && !_autoFitSucceeded && !_autoFitInProgress && DateTime.UtcNow - _lastAutoFitUtc >= TimeSpan.FromSeconds(5))
            {
                _lastAutoFitUtc = DateTime.UtcNow;
                _ = AutoFitRelayCropAsync(_relayWindow);
            }
            return;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopWindowRelay(); SaveSettings(); UnhookMouseShortcut();
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
            _savedLeft = s?.Left; _savedTop = s?.Top; _dark = s?.Dark ?? false;
            _brightness = s?.Brightness is > 0 ? Math.Clamp(s.Brightness, .2, 1) : .85;
            _videoWidth = s?.VideoWidth ?? 62; _videoHeight = s?.VideoHeight ?? 42;
            var gcd = GreatestCommonDivisor((int)_videoWidth, (int)_videoHeight);
            _aspectWidth = s?.AspectWidth > 0 ? s.AspectWidth : (int)_videoWidth / gcd;
            _aspectHeight = s?.AspectHeight > 0 ? s.AspectHeight : (int)_videoHeight / gcd;
        }
        catch { }
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        const int WmXButtonDown = 0x020B;
        if (code >= 0 && message.ToInt32() == WmXButtonDown)
        {
            var hook = Marshal.PtrToStructure<MouseHookData>(data);
            var button = (hook.MouseData >> 16) & 0xFFFF;
            if (button == 2)
            {
                Dispatcher.BeginInvoke(() => { _manuallyHidden = IsVisible; RefreshImePanel(); });
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
        var shouldShow = !_manuallyHidden && anchor is not null;
        var wasVisible = IsVisible;
        SetPlayerVisibility(shouldShow);
        if (shouldShow && anchor is { } location)
            SogouPanelTracker.Place(new WindowInteropHelper(this).Handle, location, !wasVisible);
    }

    private void SetPlayerVisibility(bool show)
    {
        if (show == IsVisible) return;
        if (show) { ShowPlayerWindow(); return; }
        if (_relayWindow != IntPtr.Zero)
        {
            _relayVisible = false;
            KeepRelayOffscreen();
            UpdateRelayThumbnail();
        }
        Hide();
        RequestPlaybackSync();
    }

    private void ShowPlayerWindow()
    {
        Show();
        if (_relayWindow != IntPtr.Zero)
        {
            _relayVisible = true;
            UpdateRelayThumbnail();
            RequestPlaybackSync();
        }
    }

    private void UnhookMouseShortcut()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _mouseHookProc = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public NativePoint Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    private void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!); File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings(Left, Top, _dark, _videoWidth, _videoHeight, _aspectWidth, _aspectHeight, _brightness))); } catch { }
    }
    private sealed record Settings(double Left, double Top, bool Dark, double VideoWidth = 62, double VideoHeight = 42, int AspectWidth = 31, int AspectHeight = 21, double Brightness = .85);
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
}
