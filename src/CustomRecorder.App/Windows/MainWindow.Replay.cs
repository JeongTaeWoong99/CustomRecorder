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

/// <summary>Replay buffer: keep recent footage and save it on demand.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- replay buffer

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (_replay != null) StopReplay();
        else StartReplay();
    }

    private void StartReplay()
    {
        if (_d3d == null) return;
        var target = CurrentTarget;
        if (target == null) return;

        var options = BuildOptions(target, AppSettings.ReplayTempFolder()) with
        {
            ReplayLength = TimeSpan.FromSeconds(_settings.ReplaySeconds),
        };

        var replay = new ScreenRecorder(_d3d, options);
        replay.Faulted += reason => Dispatcher.BeginInvoke(() =>
        {
            StopReplay();
            MessageBox.Show($"리플레이 버퍼가 꺼졌습니다.\n{reason}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
        try
        {
            replay.Start();
        }
        catch (Exception ex)
        {
            Log.Error("리플레이 시작 실패", ex);
            replay.Dispose();
            MessageBox.Show($"리플레이 버퍼를 켜지 못했습니다.\n{ex.Message}", "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _replay = replay;
        UpdateReplayUi();
        var how = string.IsNullOrWhiteSpace(_settings.HotkeySaveReplay) ? "리플레이 버튼으로" : $"{_settings.HotkeySaveReplay}로";
        if (_recorder == null) StatusText.Text = $"리플레이 대기 중 — {how} 최근 {FormatSeconds(_settings.ReplaySeconds)} 저장";
    }

    private void StopReplay()
    {
        var replay = _replay;
        _replay = null;
        if (replay != null)
        {
            try { replay.Stop(); } catch (Exception ex) { Log.Warn($"리플레이 중지 오류: {ex.Message}"); }
            replay.Dispose();
        }
        UpdateReplayUi();
        if (_recorder == null) StatusText.Text = "대기 중";
    }

    private bool _savingReplay;

    private async void SaveReplay()
    {
        if (_replay == null)
        {
            StatusText.Text = "리플레이 버퍼가 꺼져 있습니다 — 툴바의 리플레이를 먼저 켜세요";
            return;
        }
        if (_savingReplay) return;
        _savingReplay = true;
        var path = _settings.NewReplayPath(CurrentTarget is { } t ? TargetName(t) : "");
        if (_recorder == null) StatusText.Text = "리플레이 저장 중…";
        try
        {
            await _replay.SaveReplayAsync(path);
            if (_recorder == null) StatusText.Text = $"리플레이 저장됨: {Path.GetFileName(path)}";
            Flash("리플레이 저장됨", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error("리플레이 저장 실패", ex);
            if (_recorder == null) StatusText.Text = $"리플레이 저장 실패: {ex.Message}";
            Flash("리플레이 저장 실패", ex.Message);
        }
        finally
        {
            _savingReplay = false;
        }
    }

    private void UpdateReplayUi()
    {
        bool on = _replay != null;
        ReplayIcon.Foreground = on ? (Brush)FindResource("Accent") : (Brush)FindResource("Text");
        ReplayLabel.Text = on ? "리플레이 켜짐" : "리플레이";
        ReplayLabel.Foreground = ReplayIcon.Foreground;
        UpdateQuickButtons();
    }

    private static string FormatSeconds(int s) => s >= 60 && s % 60 == 0 ? $"{s / 60}분" : $"{s}초";
}
