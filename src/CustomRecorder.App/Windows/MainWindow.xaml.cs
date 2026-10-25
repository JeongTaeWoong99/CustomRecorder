using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Audio;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Recording;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Windows;

public partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private D3DContext? _d3d;
    private IRecorder? _recorder;
    private TrayIcon? _tray;
    private QuickButtons? _quick;
    private TaskbarStrip? _strip;
    private bool _exiting;
    private bool _countingDown;
    private bool _cancelCountdown;
    private ScreenRecorder? _replay;
    private HotkeyManager? _hotkeys;
    private BorderWindow? _border;

    private RegionTarget? _region;
    private WindowTarget? _window;

    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _scheduleStart;
    private TimeSpan _scheduleLength;
    private DateTime? _scheduledStopAt;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this, 40);
        ApplyPin();
        _secondTimer.Tick += (_, _) => OnSecond();
        _secondTimer.Start();
        UpdateDisk();

        foreach (var m in MonitorInfo.GetAll()) MonitorCombo.Items.Add(m);
        MonitorCombo.DisplayMemberPath = nameof(MonitorInfo.DisplayName);
        MonitorCombo.SelectedIndex = 0;

        SystemToggle.IsChecked = _settings.RecordSystemAudio;
        MicToggle.IsChecked = _settings.RecordMicrophone;

        _uiTimer.Tick += (_, _) => UpdateLiveStatus();
        SourceInitialized += OnSourceInitialized;
        SourceInitialized += (_, _) => InitUpdates(); // also in tray mode (no Loaded there)
        Loaded += async (_, _) => await RecoverAsync();
        Closing += OnClosing;
        UpdateTargetUi();
    }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        CaptureExclusion.HideFromRecordings = _settings.HideToolbarFromCapture;
        // Never record our own toolbar.
        CaptureExclusion.Apply(Hwnd);

        try { RegisterHotkeys(); }
        catch (Exception ex) { Log.Error("단축키 등록 중 오류", ex); }
        try
        {
            _tray = new TrayIcon(ShowFromTray, () => Record_Click(this, new RoutedEventArgs()),
                () => Screenshot_Click(this, new RoutedEventArgs()), SaveReplay, ExitApp);
        }
        catch (Exception ex) { Log.Error("트레이 아이콘 생성 오류", ex); }
        ApplyQuickButtons();

        try
        {
            _d3d = new D3DContext();
            Log.Info($"준비 완료 — 단축키: 녹화 '{_settings.HotkeyRecord}', 캡처 '{_settings.HotkeyScreenshot}', 리플레이 '{_settings.HotkeySaveReplay}', 그리기 '{_settings.HotkeyDraw}'");
        }
        catch (Exception ex)
        {
            Log.Error("D3D11 초기화 실패", ex);
            MessageBox.Show($"그래픽 장치를 초기화하지 못했습니다.\n{ex.Message}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager(Hwnd);
        _hotkeys.Register("녹화", _settings.HotkeyRecord, () => Record_Click(this, new RoutedEventArgs()));
        _hotkeys.Register("일시정지", _settings.HotkeyPause, () => Pause_Click(this, new RoutedEventArgs()));
        _hotkeys.Register("캡처", _settings.HotkeyScreenshot, () => Screenshot_Click(this, new RoutedEventArgs()));
        _hotkeys.Register("리플레이 저장", _settings.HotkeySaveReplay, SaveReplay);
        _hotkeys.Register("화면에 그리기", _settings.HotkeyDraw, () => Draw_Click(this, new RoutedEventArgs()));

        var hints = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.HotkeyRecord)) hints.Add($"{_settings.HotkeyRecord} 녹화");
        if (!string.IsNullOrWhiteSpace(_settings.HotkeyScreenshot)) hints.Add($"{_settings.HotkeyScreenshot} 캡처");
        if (!string.IsNullOrWhiteSpace(_settings.HotkeySaveReplay)) hints.Add($"{_settings.HotkeySaveReplay} 리플레이 저장");
        HintText.Text = _hotkeys.Failed.Count > 0
            ? $"단축키 사용 불가: {string.Join(", ", _hotkeys.Failed)} (다른 프로그램이 사용 중)"
            : string.Join(" · ", hints);
        static string Key(string k) => string.IsNullOrWhiteSpace(k) ? "단축키 없음" : k;
        RecordButton.ToolTip = $"녹화 시작 / 중지 ({Key(_settings.HotkeyRecord)})";
        PauseButton.ToolTip = $"일시정지 / 재개 ({Key(_settings.HotkeyPause)})";
        ScreenshotButton.ToolTip = $"스크린샷 ({Key(_settings.HotkeyScreenshot)})";
        DrawButton.ToolTip = $"화면에 그리기 ({Key(_settings.HotkeyDraw)}) — 펜·형광펜으로 화면에 표시합니다. 전체 화면·영역 녹화에 함께 찍힙니다";
    }

    // ---------------------------------------------------------------- target selection

    private CaptureTarget? CurrentTarget =>
        ModeRegion.IsChecked == true ? _region :
        ModeWindow.IsChecked == true ? _window :
        MonitorCombo.SelectedItem is MonitorInfo m ? new MonitorTarget(m) : null;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateTargetUi();
        if (sender == ModeRegion && _region == null) Pick_Click(sender, e);
        if (sender == ModeWindow && _window == null) Pick_Click(sender, e);
    }

    private bool SoundOnly => ModeAudio.IsChecked == true;

    private void UpdateTargetUi()
    {
        bool screen = ModeScreen.IsChecked == true;
        MonitorCombo.Visibility = screen ? Visibility.Visible : Visibility.Collapsed;
        PickButton.Visibility = screen || SoundOnly ? Visibility.Collapsed : Visibility.Visible;
        ScreenshotButton.IsEnabled = ReplayButton.IsEnabled = !SoundOnly;
        if (SoundOnly)
        {
            TargetText.Text = $"화면 없이 소리만 녹음 · {AudioFileWriter.Extension(_settings.SoundOnlyFormat).ToUpperInvariant()}";
            return;
        }
        PickButton.Content = ModeRegion.IsChecked == true ? "영역 지정" : "창 선택";
        TargetText.Text = ModeRegion.IsChecked == true ? (_region?.DisplayName ?? "영역을 지정하세요")
                        : ModeWindow.IsChecked == true ? (_window?.Window.Title ?? "창을 선택하세요")
                        : "";
    }

    private void MonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateTargetUi();

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder != null) return;

        if (ModeRegion.IsChecked == true)
        {
            NativeMethods.GetCursorPos(out var pt);
            var monitor = MonitorInfo.FromPoint(pt.X, pt.Y);
            Hide();
            var selector = new RegionSelectorWindow(monitor);
            var ok = selector.ShowDialog() == true;
            Show();
            if (ok && selector.Result is { } r) _region = new RegionTarget(monitor, r);
        }
        else if (ModeWindow.IsChecked == true)
        {
            var picker = new WindowPickerWindow { Owner = this, Topmost = Topmost };
            if (picker.ShowDialog() == true && picker.Selected != null) _window = new WindowTarget(picker.Selected);
        }
        UpdateTargetUi();
    }


    // ---------------------------------------------------------------- screenshot / misc

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_d3d == null) return;
        var target = CurrentTarget;
        if (target == null) return;

        try
        {
            CapturedImage img;
            using (CaptureExclusion.HideTemporarily())
            {
                await Task.Delay(60); // let the window manager apply it before the frame is taken
                img = await Screenshot.CaptureAsync(_d3d, target, _settings.CaptureCursor);
            }
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Stride);
            bmp.Freeze();

            var path = _settings.NewScreenshotPath(TargetName(target));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            BitmapEncoder encoder = _settings.ScreenshotFormat == "jpg"
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            await using (var fs = File.Create(path)) encoder.Save(fs);

            if (_settings.CopyScreenshotToClipboard)
            {
                try { Clipboard.SetImage(bmp); } catch (Exception ex) { Log.Warn($"클립보드 복사 실패: {ex.Message}"); }
            }

            if (_recorder == null) StatusText.Text = $"캡처 저장: {Path.GetFileName(path)}";
            if (!IsActive) Flash("캡처 저장됨", Path.GetFileName(path)); // feedback when triggered from the taskbar
            Log.Info($"스크린샷: {path} {img.Width}x{img.Height}");
        }
        catch (Exception ex)
        {
            Log.Error("스크린샷 실패", ex);
            StatusText.Text = "캡처 실패 — 로그 확인";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Release global hotkeys while the dialog is open so they can be typed into the hotkey boxes.
        _hotkeys?.Dispose();
        _hotkeys = null;
        string? pendingUpdate = null;
        try
        {
            var dlg = new SettingsWindow(_settings.Clone())
            {
                Owner = this, Topmost = Topmost,
                UpdateBlocker = () => UpdateBlocker, KnownRelease = _availableUpdate,
            };
            if (_openSettingsPage != null) { dlg.SelectPage(_openSettingsPage); _openSettingsPage = null; }
            if (dlg.ShowDialog() == true)
            {
                _settings = dlg.Settings;
                _settings.Save();
                CaptureExclusion.HideFromRecordings = _settings.HideToolbarFromCapture;
                CaptureExclusion.Refresh();
                ApplyQuickButtons();
                UpdateTargetUi();
                pendingUpdate = dlg.PendingUpdateExe;
            }
        }
        finally
        {
            RegisterHotkeys();
        }
        if (pendingUpdate != null) InstallUpdate(pendingUpdate);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        NativeMethods.SetForegroundWindow(Hwnd);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }


    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.MinimizeToTray && _tray != null)
        {
            e.Cancel = true;
            Hide();
            _tray.NotifyHidden();
            return;
        }
        if (_recorder != null)
        {
            var answer = MessageBox.Show("녹화 중입니다. 저장하고 종료할까요?", "CustomRecorder", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) { e.Cancel = true; _exiting = false; return; }
            StopRecording(tidy: false);
        }
        if (_tidying > 0)
        {
            // A finished recording is still being tidied; quit right after it is done.
            e.Cancel = true;
            _exitAfterTidy = true;
            StatusText.Text = "파일 마무리가 끝나면 종료합니다…";
            return;
        }
        if (_replay != null) StopReplay();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _quick?.Dispose();
        _strip?.Dispose();
        _d3d?.Dispose();
        _settings.Save();
        Log.Info("종료");
    }
}
