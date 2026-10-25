using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Audio;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Effects;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Recording;

namespace CustomRecorder.App.Windows;

public partial class SettingsWindow : Window
{
    // ---- choices ------------------------------------------------------------------------------
    private static readonly (int Seconds, string Label)[] Countdowns = { (0, "사용 안 함"), (3, "3초"), (5, "5초"), (10, "10초") };
    private static readonly (int Minutes, string Label)[] AutoStops =
        { (0, "사용 안 함"), (1, "1분 뒤"), (5, "5분 뒤"), (10, "10분 뒤"), (30, "30분 뒤"), (60, "1시간 뒤"), (120, "2시간 뒤") };
    private static readonly (AfterRecordAction Value, string Label)[] Afters =
        { (AfterRecordAction.None, "아무것도 안 함"), (AfterRecordAction.OpenFolder, "저장 폴더 열기"), (AfterRecordAction.ShowFile, "탐색기에서 파일 표시") };
    private static readonly (int Fps, string Label)[] FpsChoices = { (30, "30 FPS · 문서, 강의"), (60, "60 FPS · 게임, 움직임 많은 화면") };
    private static readonly (int Seconds, string Label)[] ReplayChoices = { (30, "최근 30초"), (60, "최근 1분"), (120, "최근 2분"), (300, "최근 5분") };
    private static readonly (QualityPreset Value, string Label)[] Qualities =
        { (QualityPreset.High, "고화질"), (QualityPreset.Balanced, "균형 (추천)"), (QualityPreset.Small, "작은 용량") };
    private static readonly (int Mbps, string Label)[] Bitrates =
        { (0, "자동 (화질 기준)"), (4, "4 Mbps"), (8, "8 Mbps"), (12, "12 Mbps"), (20, "20 Mbps"), (30, "30 Mbps"), (50, "50 Mbps") };
    private static readonly (int Seconds, string Label)[] Keyframes =
        { (1, "1초 · 편집 정확"), (2, "2초 · 추천"), (5, "5초 · 용량 절약") };
    private static readonly (int Height, string Label)[] Resolutions =
        { (0, "화면 크기 그대로"), (1440, "1440p (QHD)"), (1080, "1080p (FHD)"), (720, "720p (HD)"), (-1, "절반 크기") };
    private static readonly (KeyDisplayMode Value, string Label)[] KeyModes =
        { (KeyDisplayMode.Off, "끄기"), (KeyDisplayMode.Shortcuts, "단축키 조합만 (Ctrl·Alt·Win, F키)"), (KeyDisplayMode.All, "모든 키") };
    private static readonly (WatermarkPosition Value, string Label)[] Positions =
    {
        (WatermarkPosition.BottomRight, "오른쪽 아래"), (WatermarkPosition.BottomLeft, "왼쪽 아래"),
        (WatermarkPosition.TopRight, "오른쪽 위"), (WatermarkPosition.TopLeft, "왼쪽 위"), (WatermarkPosition.Center, "가운데"),
    };
    private static readonly (float Size, string Label)[] WebcamSizes = { (0.2f, "작게"), (0.25f, "보통"), (0.33f, "크게") };
    private static readonly (WebcamShape Value, string Label)[] WebcamShapes = { (WebcamShape.Circle, "원"), (WebcamShape.Rounded, "둥근 사각형") };
    private static readonly (AudioFileFormat Value, string Label)[] SoundFormats =
    {
        (AudioFileFormat.M4a, "M4A · 작은 용량 (추천)"), (AudioFileFormat.Mp3, "MP3 · 어디서나 재생"),
        (AudioFileFormat.Flac, "FLAC · 무손실"), (AudioFileFormat.Wav, "WAV · 무손실, 무압축"),
    };

    private static readonly Dictionary<string, (string Title, string Desc)> PageInfo = new()
    {
        ["PageGeneral"] = ("일반", "녹화를 시작하고 끝낼 때의 동작"),
        ["PageRecord"] = ("녹화", "무엇을 얼마나 부드럽게 찍을지"),
        ["PageOutput"] = ("화질 · 파일", "화질과 파일 크기의 균형을 정합니다"),
        ["PageEffects"] = ("효과", "마우스, 키 입력, 웹캠, 워터마크를 영상에 겹쳐 넣습니다"),
        ["PageSound"] = ("소리", "녹음 장치와 소리 크기"),
        ["PageSave"] = ("저장", "파일이 저장될 위치와 이름"),
        ["PageHotkeys"] = ("단축키", "다른 프로그램을 쓰는 중에도 동작하는 키입니다. 필요 없으면 X를 눌러 비워 두세요"),
        ["PageAbout"] = ("정보 · 업데이트", "버전 확인과 업데이트"),
    };

    private readonly bool _hevcAvailable;
    private readonly IReadOnlyList<WebcamDevice> _cameras;

    public AppSettings Settings { get; }

    public SettingsWindow(AppSettings settings)
    {
        Settings = settings;
        InitializeComponent();
        WindowTheme.Apply(this, 44);
        SourceInitialized += (_, _) => CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);

        // 일반
        Fill(CountdownCombo, Countdowns.Select(c => c.Label), Array.FindIndex(Countdowns, c => c.Seconds == settings.CountdownSeconds), 0);
        Fill(AutoStopCombo, AutoStops.Select(a => a.Label), Array.FindIndex(AutoStops, a => a.Minutes == settings.AutoStopMinutes), 0);
        Fill(AfterCombo, Afters.Select(a => a.Label), Array.FindIndex(Afters, a => a.Value == settings.AfterRecording), 0);
        TrayCheck.IsChecked = settings.MinimizeToTray;
        AutostartCheck.IsChecked = Autostart.IsEnabled;
        HideWhileRecordingCheck.IsChecked = settings.HideWhileRecording;
        HideFromCaptureCheck.IsChecked = settings.HideToolbarFromCapture;
        QuickButtonsCheck.IsChecked = settings.QuickButtons;
        QuickPlaceCombo.Items.Add("작업 표시줄 (알림 영역 왼쪽)");
        QuickPlaceCombo.Items.Add("알림 영역 아이콘");
        QuickPlaceCombo.SelectedIndex = settings.QuickButtonsPlace == "tray" ? 1 : 0;
        void QuickPlaceEnabled() => QuickPlaceCombo.IsEnabled = QuickButtonsCheck.IsChecked == true;
        QuickButtonsCheck.Checked += (_, _) => QuickPlaceEnabled();
        QuickButtonsCheck.Unchecked += (_, _) => QuickPlaceEnabled();
        QuickPlaceEnabled();

        // 녹화
        Fill(FpsCombo, FpsChoices.Select(f => f.Label), settings.Fps >= 60 ? 1 : 0, 1);
        CursorCheck.IsChecked = settings.CaptureCursor;
        Fill(ReplayCombo, ReplayChoices.Select(r => r.Label), Array.FindIndex(ReplayChoices, r => r.Seconds == settings.ReplaySeconds), 1);

        // 화질 · 파일
        var h264 = EncoderInfo.HardwareEncoders(VideoCodec.H264);
        var hevc = EncoderInfo.HardwareEncoders(VideoCodec.Hevc);
        _hevcAvailable = hevc.Count > 0 || EncoderInfo.IsAvailable(VideoCodec.Hevc);
        CodecCombo.Items.Add("H.264 · 어디서나 재생");
        CodecCombo.Items.Add(_hevcAvailable ? "HEVC (H.265) · 작은 용량" : "HEVC · 이 PC에서 사용 불가");
        CodecCombo.SelectedIndex = settings.Codec == VideoCodec.Hevc && _hevcAvailable ? 1 : 0;
        EncoderHint.Text = $"그래픽카드 인코더 — H.264: {Describe(h264)}  ·  HEVC: {Describe(hevc)}";
        Fill(QualityCombo, Qualities.Select(q => q.Label), Array.FindIndex(Qualities, q => q.Value == settings.Quality), 1);
        Fill(BitrateCombo, Bitrates.Select(b => b.Label), Array.FindIndex(Bitrates, b => b.Mbps == settings.BitrateMbps), 0);
        Fill(KeyframeCombo, Keyframes.Select(k => k.Label), Array.FindIndex(Keyframes, k => k.Seconds == settings.KeyframeSeconds), 1);
        Fill(ResolutionCombo, Resolutions.Select(r => r.Label), Array.FindIndex(Resolutions, r => r.Height == settings.OutputHeight), 0);
        foreach (var c in new[] { FpsCombo, QualityCombo, BitrateCombo, CodecCombo, ResolutionCombo })
            c.SelectionChanged += (_, _) => UpdateHint();
        UpdateHint();

        // 효과
        HighlightCheck.IsChecked = settings.CursorHighlight;
        ClickCheck.IsChecked = settings.ClickEffect;
        Fill(KeyDisplayCombo, KeyModes.Select(k => k.Label), Array.FindIndex(KeyModes, k => k.Value == settings.KeyDisplay), 0);

        _cameras = WebcamSource.List();
        if (_cameras.Count == 0) WebcamCombo.Items.Add("웹캠을 찾지 못했습니다");
        foreach (var cam in _cameras) WebcamCombo.Items.Add(cam.Name);
        int camIndex = _cameras.ToList().FindIndex(c => c.Id == settings.WebcamId);
        WebcamCombo.SelectedIndex = Math.Max(0, camIndex);
        WebcamCheck.IsChecked = settings.WebcamEnabled && _cameras.Count > 0;
        WebcamCheck.IsEnabled = _cameras.Count > 0;
        if (_cameras.Count == 0)
            WebcamRow.Description = "웹캠을 찾지 못했습니다. 연결을 확인하고, Windows 설정 > 개인 정보 및 보안 > 카메라에서 '데스크톱 앱이 카메라에 액세스하도록 허용'이 켜져 있는지 확인하세요.";
        Fill(WebcamPosCombo, Positions.Select(p => p.Label), Array.FindIndex(Positions, p => p.Value == settings.WebcamPosition), 0);
        Fill(WebcamSizeCombo, WebcamSizes.Select(s => s.Label), Array.FindIndex(WebcamSizes, s => Math.Abs(s.Size - settings.WebcamSize) < 0.01f), 1);
        Fill(WebcamShapeCombo, WebcamShapes.Select(s => s.Label), Array.FindIndex(WebcamShapes, s => s.Value == settings.WebcamShape), 0);
        Bind(WebcamCheck, WebcamPanel);

        WatermarkCheck.IsChecked = settings.WatermarkEnabled;
        WatermarkBox.Text = settings.WatermarkText;
        Fill(WatermarkPosCombo, Positions.Select(p => p.Label), Array.FindIndex(Positions, p => p.Value == settings.WatermarkPosition), 0);
        WatermarkOpacity.Value = settings.WatermarkOpacity;
        Bind(WatermarkCheck, WatermarkPanel);

        // 소리
        MicCombo.Items.Add("Windows 기본 마이크");
        var mics = AudioDevice.GetMicrophones();
        foreach (var m in mics) MicCombo.Items.Add(m);
        var current = mics.FirstOrDefault(m => m.Id == settings.MicrophoneId);
        MicCombo.SelectedItem = current != null ? current : MicCombo.Items[0];
        SystemVolume.Value = settings.SystemVolume;
        MicVolume.Value = settings.MicrophoneVolume;
        SeparateTracksCheck.IsChecked = settings.SeparateAudioTracks;
        NoiseGateCheck.IsChecked = settings.MicNoiseGate;
        InitNoiseGate();
        Loaded += (_, _) => UpdateMonitor();
        Fill(SoundFormatCombo, SoundFormats.Select(f => f.Label), Array.FindIndex(SoundFormats, f => f.Value == settings.SoundOnlyFormat), 0);

        // 저장
        FolderBox.Text = settings.OutputFolder;
        PatternBox.Text = settings.FileNamePattern;
        PatternBox.TextChanged += (_, _) => UpdatePatternPreview();
        UpdatePatternPreview();
        ShotFormatCombo.Items.Add("PNG · 선명함");
        ShotFormatCombo.Items.Add("JPG · 작은 용량");
        ShotFormatCombo.SelectedIndex = settings.ScreenshotFormat == "jpg" ? 1 : 0;
        ClipboardCheck.IsChecked = settings.CopyScreenshotToClipboard;

        // 단축키
        HkRecord.Text = settings.HotkeyRecord;
        HkPause.Text = settings.HotkeyPause;
        HkShot.Text = settings.HotkeyScreenshot;
        HkReplay.Text = settings.HotkeySaveReplay;
        HkDraw.Text = settings.HotkeyDraw;
        foreach (var box in HotkeyBoxes) box.PreviewKeyDown += HotkeyBox_PreviewKeyDown;
        CheckHotkeys();

        InitAbout();
        Nav.SelectedIndex = 1;
    }

    /// <summary>Opens the dialog on a given page (e.g. "PageAbout" from the toolbar's update button).</summary>
    public void SelectPage(string page)
    {
        var item = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == page);
        if (item != null) Nav.SelectedItem = item;
    }

    private static string Describe(IReadOnlyList<string> encoders) => encoders.Count == 0 ? "없음 (CPU 사용)" : string.Join(", ", encoders);

    private static void Fill(ComboBox combo, IEnumerable<string> labels, int index, int fallback)
    {
        foreach (var l in labels) combo.Items.Add(l);
        combo.SelectedIndex = index >= 0 ? index : fallback;
    }

    /// <summary>Shows the detail rows only while the switch is on.</summary>
    private static void Bind(CheckBox toggle, FrameworkElement panel)
    {
        void Update() => panel.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        toggle.Checked += (_, _) => Update();
        toggle.Unchecked += (_, _) => Update();
        Update();
    }

    private FrameworkElement[] Pages => new FrameworkElement[] { PageGeneral, PageRecord, PageOutput, PageEffects, PageSound, PageSave, PageHotkeys, PageAbout };

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageRecord == null) return;
        foreach (var page in Pages) page.Visibility = Visibility.Collapsed;
        if (Nav.SelectedItem is ListBoxItem { Tag: string name } && FindName(name) is FrameworkElement target)
        {
            target.Visibility = Visibility.Visible;
            (PageTitle.Text, PageDesc.Text) = PageInfo[name];
        }
    }

    private void UpdateHint()
    {
        if (QualityHint == null || ResolutionCombo.Items.Count == 0 || BitrateCombo.Items.Count == 0) return;
        int fps = FpsChoices[Math.Max(0, FpsCombo.SelectedIndex)].Fps;
        var (w, h) = new RecorderOptions(null!, "", fps, QualityPreset.Balanced, true, null!)
            { OutputHeight = Resolutions[Math.Max(0, ResolutionCombo.SelectedIndex)].Height }.OutputSizeFor(1920, 1080);
        int manual = Bitrates[Math.Max(0, BitrateCombo.SelectedIndex)].Mbps;
        double mbps = manual > 0
            ? manual
            : RecorderOptions.ComputeBitrate(w, h, fps, Qualities[Math.Max(0, QualityCombo.SelectedIndex)].Value) / 1_000_000.0
              * (CodecCombo.SelectedIndex == 1 ? 0.6 : 1.0);
        QualityHint.Text = $"1080p 화면을 {fps}fps로 녹화하면 1분에 약 {mbps * 60 / 8:F0} MB  ({mbps:F0} Mbps, {w}x{h})";
        QualityCombo.IsEnabled = manual == 0;
        if (CodecCombo.SelectedIndex == 1)
            EncoderHint.Text = "HEVC는 일반 MP4로 저장되어, 녹화 중 프로그램이 강제로 꺼지면 파일이 손상될 수 있습니다.";
        else
            EncoderHint.Text = $"그래픽카드 인코더 — H.264: {Describe(EncoderInfo.HardwareEncoders(VideoCodec.H264))}";
    }

    // ---- hotkeys ------------------------------------------------------------------------------
    private TextBox[] HotkeyBoxes => new[] { HkRecord, HkPause, HkShot, HkReplay, HkDraw };

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete) { box.Text = ""; CheckHotkeys(); return; }
        if (key is Key.Tab or Key.Escape || HotkeyText.IsModifier(key)) return;
        box.Text = HotkeyText.Format(Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift), key);
        CheckHotkeys();
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && FindName(name) is TextBox box) box.Text = "";
        CheckHotkeys();
    }

    private void CheckHotkeys()
    {
        HkRecordHint.Visibility = HkRecord.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HkPauseHint.Visibility = HkPause.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HkShotHint.Visibility = HkShot.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HkReplayHint.Visibility = HkReplay.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HkDrawHint.Visibility = HkDraw.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var dup = HotkeyBoxes.Select(b => b.Text).Where(t => t.Length > 0)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        HotkeyWarning.Text = dup != null ? $"'{dup.Key}' 키가 두 기능에 겹쳐 있습니다. 하나를 바꿔 주세요." : "";
    }

    private void HotkeyDefaults_Click(object sender, RoutedEventArgs e)
    {
        HkRecord.Text = "F2";
        HkPause.Text = "Shift+F2";
        HkShot.Text = "F3";
        HkReplay.Text = "F4";
        HkDraw.Text = "Ctrl+Shift+D";
        CheckHotkeys();
    }

    private void UpdatePatternPreview()
    {
        var name = AppSettings.FormatFileName(PatternBox.Text, "녹화", "모니터1", DateTime.Now);
        PatternPreview.Text = $"예: {name}.mp4";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = FolderBox.Text, UseDescriptionForTitle = true, Description = "저장 폴더 선택" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) FolderBox.Text = dlg.SelectedPath;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CheckHotkeys();
        if (HotkeyWarning.Text.Length > 0) { Nav.SelectedIndex = 6; return; }

        Settings.CountdownSeconds = Countdowns[Math.Max(0, CountdownCombo.SelectedIndex)].Seconds;
        Settings.AutoStopMinutes = AutoStops[Math.Max(0, AutoStopCombo.SelectedIndex)].Minutes;
        Settings.AfterRecording = Afters[Math.Max(0, AfterCombo.SelectedIndex)].Value;
        Settings.MinimizeToTray = TrayCheck.IsChecked == true;
        Settings.HideWhileRecording = HideWhileRecordingCheck.IsChecked == true;
        Settings.HideToolbarFromCapture = HideFromCaptureCheck.IsChecked == true;
        Settings.QuickButtons = QuickButtonsCheck.IsChecked == true;
        Settings.QuickButtonsPlace = QuickPlaceCombo.SelectedIndex == 1 ? "tray" : "taskbar";
        bool autostart = AutostartCheck.IsChecked == true;
        if (autostart != Autostart.IsEnabled) Autostart.Set(autostart);

        Settings.Fps = FpsChoices[Math.Max(0, FpsCombo.SelectedIndex)].Fps;
        Settings.CaptureCursor = CursorCheck.IsChecked == true;
        Settings.ReplaySeconds = ReplayChoices[Math.Max(0, ReplayCombo.SelectedIndex)].Seconds;

        Settings.Codec = CodecCombo.SelectedIndex == 1 && _hevcAvailable ? VideoCodec.Hevc : VideoCodec.H264;
        Settings.Quality = Qualities[Math.Max(0, QualityCombo.SelectedIndex)].Value;
        Settings.BitrateMbps = Bitrates[Math.Max(0, BitrateCombo.SelectedIndex)].Mbps;
        Settings.KeyframeSeconds = Keyframes[Math.Max(0, KeyframeCombo.SelectedIndex)].Seconds;
        Settings.OutputHeight = Resolutions[Math.Max(0, ResolutionCombo.SelectedIndex)].Height;

        Settings.CursorHighlight = HighlightCheck.IsChecked == true;
        Settings.ClickEffect = ClickCheck.IsChecked == true;
        Settings.KeyDisplay = KeyModes[Math.Max(0, KeyDisplayCombo.SelectedIndex)].Value;
        Settings.WebcamEnabled = WebcamCheck.IsChecked == true && _cameras.Count > 0;
        if (_cameras.Count > 0) Settings.WebcamId = _cameras[Math.Max(0, WebcamCombo.SelectedIndex)].Id;
        Settings.WebcamPosition = Positions[Math.Max(0, WebcamPosCombo.SelectedIndex)].Value;
        Settings.WebcamSize = WebcamSizes[Math.Max(0, WebcamSizeCombo.SelectedIndex)].Size;
        Settings.WebcamShape = WebcamShapes[Math.Max(0, WebcamShapeCombo.SelectedIndex)].Value;
        Settings.WatermarkEnabled = WatermarkCheck.IsChecked == true;
        Settings.WatermarkText = WatermarkBox.Text.Trim();
        Settings.WatermarkPosition = Positions[Math.Max(0, WatermarkPosCombo.SelectedIndex)].Value;
        Settings.WatermarkOpacity = (float)WatermarkOpacity.Value;

        Settings.MicrophoneId = (MicCombo.SelectedItem as AudioDevice)?.Id;
        Settings.SystemVolume = (float)SystemVolume.Value;
        Settings.MicrophoneVolume = (float)MicVolume.Value;
        Settings.SeparateAudioTracks = SeparateTracksCheck.IsChecked == true;
        Settings.MicNoiseGate = NoiseGateCheck.IsChecked == true;
        SaveNoiseGate();
        Settings.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
        Settings.UpdatePrereleases = PrereleaseCheck.IsChecked == true;
        Settings.SoundOnlyFormat = SoundFormats[Math.Max(0, SoundFormatCombo.SelectedIndex)].Value;

        Settings.OutputFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? Settings.OutputFolder : FolderBox.Text.Trim();
        Settings.FileNamePattern = string.IsNullOrWhiteSpace(PatternBox.Text) ? AppSettings.DefaultFileNamePattern : PatternBox.Text.Trim();
        Settings.ScreenshotFormat = ShotFormatCombo.SelectedIndex == 1 ? "jpg" : "png";
        Settings.CopyScreenshotToClipboard = ClipboardCheck.IsChecked == true;

        Settings.HotkeyRecord = HkRecord.Text;
        Settings.HotkeyPause = HkPause.Text;
        Settings.HotkeyScreenshot = HkShot.Text;
        Settings.HotkeySaveReplay = HkReplay.Text;
        Settings.HotkeyDraw = HkDraw.Text;
        DialogResult = true;
    }
}
