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

/// <summary>Extras: more menu, scheduled recording, disk space, crash recovery, drawing tool, always-on-top.</summary>
public partial class MainWindow
{
    private async Task RecoverAsync()
    {
        var folder = _settings.OutputFolder;
        var unfinished = await Task.Run(() =>
        {
            Recovery.CleanTemp(folder);
            return Recovery.FindUnfinished(folder);
        });
        if (unfinished.Count == 0) return;

        var names = string.Join("\n", unfinished.Take(5).Select(p => "  • " + Path.GetFileName(p)));
        if (unfinished.Count > 5) names += $"\n  … 외 {unfinished.Count - 5}개";
        var answer = MessageBox.Show(this,
            $"마무리되지 못한 녹화 파일이 {unfinished.Count}개 있습니다. (녹화 중 프로그램이나 PC가 꺼진 경우)\n\n{names}\n\n" +
            "지금 복구할까요? 녹화된 부분까지 일반 MP4로 정리해 어디서나 재생되게 합니다.",
            "CustomRecorder — 녹화 복구", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        int ok = 0;
        foreach (var path in unfinished)
        {
            StatusText.Text = $"복구 중… {Path.GetFileName(path)}";
            try { await Task.Run(() => CustomRecorder.Core.Encoding.Mp4Trim.Remux(path)); ok++; }
            catch (Exception ex) { Log.Warn($"복구 실패: {path}: {ex.Message}"); }
        }
        StatusText.Text = ok == unfinished.Count ? $"녹화 {ok}개 복구 완료" : $"녹화 {ok}/{unfinished.Count}개 복구 (나머지는 로그 확인)";
    }


    // ---------------------------------------------------------------- more menu, schedule, disk

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItem("\uE8FD", "최근 녹화 보기 · 자르기", OpenLibrary));
        if (_scheduleStart == null) menu.Items.Add(MenuItem("\uE787", "예약 녹화…", OpenSchedule, enabled: _recorder == null));
        else menu.Items.Add(MenuItem("\uE711", $"예약 취소 ({_scheduleStart:M/d HH:mm})", CancelSchedule));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("\uE838", "저장 폴더 열기", () => OpenFolder_Click(this, new RoutedEventArgs())));
        menu.Items.Add(MenuItem("\uE713", "설정", () => Settings_Click(this, new RoutedEventArgs()), enabled: _recorder == null));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("\uE7E8", "프로그램 종료", ExitApp));
        menu.IsOpen = true;
    }

    private System.Windows.Controls.MenuItem MenuItem(string glyph, string text, Action action, bool enabled = true)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = text,
            IsEnabled = enabled,
            Icon = new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14 },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenLibrary() => new LibraryWindow(_settings.OutputFolder) { Owner = this }.Show();

    private void OpenSchedule()
    {
        var dlg = new ScheduleWindow { Owner = this, Topmost = Topmost };
        if (dlg.ShowDialog() != true) return;
        _scheduleStart = dlg.Start;
        _scheduleLength = dlg.Length;
        StatusText.Text = $"예약됨: {dlg.Start:M월 d일 HH:mm}부터 {FormatLength(dlg.Length)} 녹화";
        Log.Info($"예약 녹화: {dlg.Start} ({dlg.Length})");
    }

    private void CancelSchedule()
    {
        _scheduleStart = null;
        if (_recorder == null) StatusText.Text = "예약을 취소했습니다";
    }

    private static string FormatLength(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간{(t.Minutes > 0 ? $" {t.Minutes}분" : "")}" : $"{t.Minutes}분";

    private void OnSecond()
    {
        if (_scheduleStart is { } start && DateTime.Now >= start)
        {
            _scheduleStart = null;
            if (_recorder == null && !_countingDown)
            {
                StartRecording(skipCountdown: true);
                if (_recorder != null) _scheduledStopAt = DateTime.Now + _scheduleLength;
            }
        }
        if (_scheduledStopAt is { } stop && DateTime.Now >= stop && _recorder != null)
        {
            _scheduledStopAt = null;
            StopRecording();
        }
        if (DateTime.Now.Second % 10 == 0) UpdateDisk();
        CheckDiskWhileRecording();
        RaiseAboveDrawing();
    }

    /// <summary>Stops and saves before the drive fills up (a full disk would corrupt the end of the file).</summary>
    private void CheckDiskWhileRecording()
    {
        if (_recorder == null) return;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_recorder.OutputPath))!);
            if (drive.AvailableFreeSpace >= 512L << 20) return;
            Log.Warn($"저장 공간 부족으로 녹화 자동 종료 ({drive.AvailableFreeSpace >> 20} MB 남음)");
            StopRecording(tidy: false);
            UpdateDisk();
            MessageBox.Show(this, "저장 공간이 거의 없어 녹화를 멈추고 지금까지의 내용을 저장했습니다.\n공간을 비운 뒤 다시 녹화해 주세요.",
                "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* drive info unavailable */ }
    }

    private void UpdateDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.OutputFolder));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            double gb = drive.AvailableFreeSpace / (double)(1L << 30);
            DiskText.Text = $"여유 공간 {gb:F0} GB";
            DiskText.Foreground = gb < 5 ? (Brush)FindResource("Rec") : (Brush)FindResource("TextFaint");
            DiskText.ToolTip = gb < 5 ? "저장 공간이 부족합니다. 긴 녹화는 중간에 실패할 수 있습니다." : $"{drive.Name} 드라이브의 남은 공간";
        }
        catch { DiskText.Text = ""; }
    }

    private DrawToolbarWindow? _draw;

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        if (_draw != null) { _draw.Close(); return; }
        MonitorInfo monitor;
        switch (CurrentTarget)
        {
            case MonitorTarget m: monitor = m.Monitor; break;
            case RegionTarget r: monitor = r.Monitor; break;
            default:
                NativeMethods.GetCursorPos(out var pt);
                monitor = MonitorInfo.FromPoint(pt.X, pt.Y);
                break;
        }
        _draw = new DrawToolbarWindow(monitor);
        _draw.Closed += (_, _) => { _draw = null; ApplyPin(); };
        _draw.LayerTouched += RaiseAboveDrawing;
        _draw.Show();
        // Keep this toolbar clickable above the drawing layer (it is never recorded anyway).
        Topmost = true;
        RaiseAboveDrawing();
        Dispatcher.BeginInvoke(RaiseAboveDrawing, DispatcherPriority.ApplicationIdle);
    }

    private void RaiseAboveDrawing()
    {
        if (_draw == null) return;
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | NativeMethods.SWP_NOACTIVATE);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        _settings.Save();
        ApplyPin();
    }

    /// <summary>"Always on top" on/off; the button shows the current state.</summary>
    private void ApplyPin()
    {
        bool on = _settings.AlwaysOnTop;
        Topmost = on;
        PinIcon.Text = on ? "\uE840" : "\uE718";   // filled pin / outline pin
        PinIcon.Foreground = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Text");
        PinLabel.Text = on ? "고정됨" : "위에 고정";
        PinLabel.Foreground = PinIcon.Foreground;
        PinButton.ToolTip = on
            ? "항상 위에 고정됨 — 누르면 고정을 풀어 다른 창 뒤로 갈 수 있습니다"
            : "누르면 이 창을 다른 창보다 항상 위에 둡니다";
    }
}
