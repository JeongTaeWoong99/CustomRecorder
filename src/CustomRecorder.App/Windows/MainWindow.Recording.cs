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

/// <summary>Recording: start/stop, countdown, sound-only, tidy-up after stopping, live status.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- recording

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        Log.Info($"녹화 버튼 (녹화 중={_recorder != null}, 카운트다운={_countingDown}, 장치={_d3d != null})");
        if (_countingDown) { _cancelCountdown = true; return; }
        if (_recorder != null) StopRecording();
        else StartRecording();
    }

    private static string TargetName(CaptureTarget target) => target switch
    {
        WindowTarget w => w.Window.ProcessName,
        RegionTarget => "영역",
        MonitorTarget m => $"모니터{m.Monitor.Index + 1}",
        _ => "",
    };

    private static PixelRect AreaOf(CaptureTarget? target)
    {
        switch (target)
        {
            case RegionTarget r: return r.Region;
            case MonitorTarget m: return m.Monitor.Bounds;
            case WindowTarget w when NativeMethods.DwmGetWindowAttribute(w.Window.Handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out NativeMethods.RECT rc, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0:
                return new PixelRect(rc.Left, rc.Top, rc.Width, rc.Height);
            default:
                var primary = MonitorInfo.GetAll().First();
                return primary.Bounds;
        }
    }

    /// <summary>Optional "3, 2, 1" before recording. Returns false if the user cancelled.</summary>
    private async Task<bool> CountdownAsync(CaptureTarget? target)
    {
        if (_settings.CountdownSeconds <= 0) return true;
        _countingDown = true;
        _cancelCountdown = false;
        UpdateCaptureExclusion();
        StatusText.Text = $"{_settings.CountdownSeconds}초 후 녹화 시작 — {_settings.HotkeyRecord}로 취소";
        try
        {
            bool ok = await new CountdownWindow(AreaOf(target)).RunAsync(_settings.CountdownSeconds, () => _cancelCountdown);
            if (!ok) StatusText.Text = "녹화 취소됨";
            return ok;
        }
        finally
        {
            _countingDown = false;
            UpdateCaptureExclusion();
        }
    }

    private async void StartRecording(bool skipCountdown = false)
    {
        if (SoundOnly)
        {
            await StartSoundRecordingAsync(skipCountdown);
            return;
        }
        if (_d3d == null) return;
        var target = CurrentTarget;
        if (target == null)
        {
            Pick_Click(this, new RoutedEventArgs());
            target = CurrentTarget;
            if (target == null) return;
        }
        if (target is WindowTarget w && !NativeMethods.IsWindow(w.Window.Handle))
        {
            MessageBox.Show("선택한 창이 닫혔습니다. 다시 선택하세요.", "CustomRecorder");
            _window = null;
            UpdateTargetUi();
            return;
        }

        if (!skipCountdown && !await CountdownAsync(target)) return;
        var options = BuildOptions(target, _settings.NewRecordingPath(TargetName(target)));

        var recorder = new ScreenRecorder(_d3d, options);
        recorder.Faulted += reason => Dispatcher.BeginInvoke(() =>
        {
            FinishRecordingUi();
            MessageBox.Show($"녹화가 중지되었습니다.\n{reason}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        // Hide our own windows before the first frame is captured.
        CaptureExclusion.SetBusy(true);
        await Task.Delay(50);
        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            Log.Error("녹화 시작 실패", ex);
            recorder.Dispose();
            UpdateCaptureExclusion();
            MessageBox.Show($"녹화를 시작하지 못했습니다.\n{ex.Message}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _recorder = recorder;
        if (target is RegionTarget rt)
        {
            _border = new BorderWindow(rt.Region);
            _border.Show();
        }

        SetRecordingUi(true);
        _uiTimer.Start();
    }

    private async Task StartSoundRecordingAsync(bool skipCountdown)
    {
        if (SystemToggle.IsChecked != true && MicToggle.IsChecked != true)
        {
            StatusText.Text = "시스템 소리나 마이크를 하나 이상 켜세요";
            return;
        }
        if (!skipCountdown && !await CountdownAsync(null)) return;

        var recorder = new AudioRecorder(AudioOptionsFromUi(), _settings.NewSoundPath(), _settings.SoundOnlyFormat);
        recorder.Faulted += reason => Dispatcher.BeginInvoke(() =>
        {
            FinishRecordingUi();
            MessageBox.Show($"녹음이 중지되었습니다.\n{reason}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        try
        {
            recorder.Start();
        }
        catch (Exception ex)
        {
            Log.Error("녹음 시작 실패", ex);
            recorder.Dispose();
            MessageBox.Show($"녹음을 시작하지 못했습니다.\n{ex.Message}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _recorder = recorder;
        SetRecordingUi(true);
        _uiTimer.Start();
    }

    private AudioOptions AudioOptionsFromUi() =>
        new(SystemToggle.IsChecked == true, MicToggle.IsChecked == true, _settings.MicrophoneId,
            _settings.SystemVolume, _settings.MicrophoneVolume) 
        {
            MicNoiseGate = _settings.MicNoiseGate,
            MicNoiseGateAuto = _settings.MicNoiseGateAuto,
            MicNoiseGateThresholdDb = _settings.MicNoiseGateThresholdDb,
        };

    private RecorderOptions BuildOptions(CaptureTarget target, string outputPath) =>
        new(target, outputPath, _settings.Fps, _settings.Quality, _settings.CaptureCursor, AudioOptionsFromUi())
        {
            SeparateAudioTracks = _settings.SeparateAudioTracks,
            Codec = _settings.Codec,
            BitrateOverrideBps = _settings.BitrateMbps * 1_000_000,
            KeyframeSeconds = _settings.KeyframeSeconds,
            OutputHeight = _settings.OutputHeight,
            Overlay = _settings.ToOverlay(),
        };

    /// <param name="tidy">Rewrite a fragmented MP4 as a regular one afterwards (skipped when the app is closing).</param>
    private void StopRecording(bool tidy = true)
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _uiTimer.Stop();
        StatusText.Text = "저장 중…";

        try
        {
            recorder.Stop();
            var path = recorder.OutputPath;
            if (tidy && recorder is ScreenRecorder { NeedsRemux: true }) _ = TidyAsync(path);
            else
            {
                StatusText.Text = $"저장됨: {Path.GetFileName(path)}";
                RunAfterRecording(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("녹화 저장 실패", ex);
            StatusText.Text = "저장 중 오류 — 로그 확인";
        }
        finally
        {
            recorder.Dispose();
            FinishRecordingUi();
        }
    }

    private int _tidying;
    private bool _exitAfterTidy;

    /// <summary>Background step after a screen recording: fragmented MP4 → regular MP4 (same data, correct length everywhere).</summary>
    private async Task TidyAsync(string path)
    {
        Interlocked.Increment(ref _tidying);
        if (_recorder == null) StatusText.Text = $"마무리 중… {Path.GetFileName(path)}";
        try
        {
            long size = new FileInfo(path).Length;
            var root = Path.GetPathRoot(Path.GetFullPath(path))!;
            if (new DriveInfo(root).AvailableFreeSpace < size + (1L << 30))
                Log.Warn("저장 공간이 부족해 일반 MP4 정리를 건너뜁니다 (파일은 그대로 재생 가능)");
            else
                await Task.Run(() => CustomRecorder.Core.Encoding.Mp4Trim.Remux(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"일반 MP4 정리 실패 (원본 그대로 둠): {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _tidying);
        }
        if (_recorder == null) StatusText.Text = $"저장됨: {Path.GetFileName(path)}";
        RunAfterRecording(path);
        if (_exitAfterTidy && _tidying == 0) { _exiting = true; Close(); }
    }

    /// <summary>After a crash: clean temporary files and offer to repair recordings that were never finished.</summary>

    private void RunAfterRecording(string path)
    {
        try
        {
            switch (_settings.AfterRecording)
            {
                case AfterRecordAction.OpenFolder:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
                    break;
                case AfterRecordAction.ShowFile when File.Exists(path):
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex) { Log.Warn($"녹화 후 동작 실패: {ex.Message}"); }
    }

    private void FinishRecordingUi()
    {
        _uiTimer.Stop();
        if (_recorder != null && _recorder.State == RecorderState.Idle)
        {
            if (!StatusText.Text.StartsWith("저장")) StatusText.Text = $"저장됨: {Path.GetFileName(_recorder.OutputPath)}";
            _recorder.Dispose();
        }
        _recorder = null;
        _border?.Close();
        _border = null;
        SetRecordingUi(false);
    }


    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder == null) return;
        if (_recorder.State == RecorderState.Recording) _recorder.Pause();
        else if (_recorder.State == RecorderState.Paused) _recorder.Resume();
        bool paused = _recorder.State == RecorderState.Paused;
        PauseIcon.Text = paused ? "" : "";
        PauseLabel.Text = paused ? "재개" : "일시정지";
        TitleDot.Fill = paused ? (Brush)FindResource("TextDim") : (Brush)FindResource("Rec");
        TimerText.Foreground = paused ? (Brush)FindResource("TextDim") : (Brush)FindResource("Rec");
        var pulse = (System.Windows.Media.Animation.Storyboard)FindResource("Pulse");
        if (paused) pulse.Pause(this); else pulse.Resume(this);
    }

    private bool _hiddenForRecording;

    private void SetRecordingUi(bool recording)
    {
        // Optional: get the toolbar out of the way while recording; the tray icon (red dot) shows the state.
        if (recording && _settings.HideWhileRecording && _tray != null && IsVisible)
        {
            _hiddenForRecording = true;
            Hide();
            _tray.NotifyRecordingHidden(_settings.HotkeyRecord);
        }
        else if (!recording && _hiddenForRecording)
        {
            _hiddenForRecording = false;
            ShowActivated = false; // come back without stealing focus from what the user is doing
            Show();
            ShowActivated = true;
        }

        RecordIcon.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        StopIcon.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecordLabel.Text = recording ? "중지" : "녹화";
        PauseButton.IsEnabled = recording;
        PauseIcon.Text = "";
        PauseLabel.Text = "일시정지";
        ModeScreen.IsEnabled = ModeRegion.IsEnabled = ModeWindow.IsEnabled = ModeAudio.IsEnabled = !recording;
        _tray?.SetRecording(recording);
        UpdateQuickButtons();
        MonitorCombo.IsEnabled = PickButton.IsEnabled = SettingsButton.IsEnabled = !recording;
        SystemToggle.IsEnabled = MicToggle.IsEnabled = !recording;
        TitleDot.Fill = (Brush)FindResource("Rec");
        RecPill.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        // Level meters only mean something while recording (live level of what goes into the file).
        SystemMeter.Visibility = recording && SystemToggle.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        MicMeter.Visibility = recording && MicToggle.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        TimerText.Text = "00:00:00";
        var pulse = (System.Windows.Media.Animation.Storyboard)FindResource("Pulse");
        if (recording) pulse.Begin(this, true);
        else { pulse.Stop(this); RecRing.Opacity = 1; }
        if (!recording) { SystemMeter.Value = 0; MicMeter.Value = 0; _scheduledStopAt = null; UpdateDisk(); }
    }

    private void UpdateLiveStatus()
    {
        var r = _recorder;
        if (r == null) return;
        long size = 0;
        try { size = new FileInfo(r.OutputPath).Length; } catch { }
        string state = r.State == RecorderState.Paused ? "❚❚ 일시정지" : r is AudioRecorder ? "● 녹음 중" : "● 녹화 중";
        string limit = "";
        if (_settings.AutoStopMinutes > 0 && r.State != RecorderState.Stopping)
        {
            var left = TimeSpan.FromMinutes(_settings.AutoStopMinutes) - r.Elapsed;
            if (left <= TimeSpan.Zero) { StopRecording(); return; }
            limit = $"  ·  {left:hh\\:mm\\:ss} 후 자동 종료";
        }
        if (_scheduledStopAt is { } stopAt)
            limit = $"  ·  예약 녹화 {stopAt:HH:mm}에 종료";
        string load = "";
        if (r is ScreenRecorder sr && sr.DroppedFrames + sr.LateFrames > 0)
            load = $"  ·  ⚠ 끊김 {sr.DroppedFrames + sr.LateFrames}";
        StatusText.Text = $"{state}  ·  {size / 1024.0 / 1024.0:F1} MB{load}{limit}";
        StatusText.ToolTip = load.Length > 0
            ? "PC가 바빠서 제때 기록하지 못한 프레임 수입니다. 화질·프레임을 낮추거나 다른 프로그램을 닫으면 줄어듭니다."
            : null;
        TimerText.Text = r.Elapsed.ToString(@"hh\:mm\:ss");
        _tray?.SetRecording(true, $"CustomRecorder — {state} {r.Elapsed:hh\\:mm\\:ss}");
        _quick?.SetRecording(true, r.Elapsed.ToString(@"hh\:mm\:ss"));
        _strip?.SetElapsed(r.Elapsed);
        SystemMeter.Value = Math.Max(r.SystemPeak, SystemMeter.Value * 0.85);
        MicMeter.Value = Math.Max(r.MicrophonePeak, MicMeter.Value * 0.85);
    }

    private void SoundToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecordSystemAudio = SystemToggle.IsChecked == true;
        _settings.RecordMicrophone = MicToggle.IsChecked == true;
        _settings.Save();
    }
}
