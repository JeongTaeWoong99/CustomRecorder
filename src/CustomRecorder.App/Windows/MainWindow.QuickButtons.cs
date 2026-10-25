using System.Windows;
using CustomRecorder.App.Services;

namespace CustomRecorder.App.Windows;

/// <summary>One-click buttons outside the window: tray icons and the taskbar thumbnail toolbar.</summary>
public partial class MainWindow
{
    private bool _themeHooked;

    private void ApplyQuickButtons()
    {
        if (!_themeHooked)
        {
            _themeHooked = true;
            // Light ↔ dark taskbar: redraw the glyphs so they stay visible (the taskbar strip checks by itself).
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
                Dispatcher.BeginInvoke(() => { if (_quick?.RefreshTheme() == true) UpdateQuickButtons(); });
            };
        }

        bool wantTaskbar = _settings.QuickButtons && _settings.QuickButtonsPlace != "tray";
        bool wantTray = _settings.QuickButtons && _settings.QuickButtonsPlace == "tray";

        if (wantTray && _quick == null)
        {
            try
            {
                _quick = new QuickButtons(
                    toggleRecord: () => Record_Click(this, new RoutedEventArgs()),
                    pause: () => Pause_Click(this, new RoutedEventArgs()),
                    capture: () => Screenshot_Click(this, new RoutedEventArgs()),
                    replayToggle: () => Replay_Click(this, new RoutedEventArgs()),
                    replaySave: SaveReplay,
                    openMain: ShowFromTray);
            }
            catch (Exception ex) { CustomRecorder.Core.Util.Log.Error("빠른 버튼 생성 오류", ex); }
        }
        else if (!wantTray && _quick != null)
        {
            _quick.Dispose();
            _quick = null;
        }

        if (wantTaskbar && _strip == null)
        {
            try
            {
                _strip = new TaskbarStrip(new TaskbarStrip.Handlers(
                        Record: () => Record_Click(this, new RoutedEventArgs()),
                        Pause: () => Pause_Click(this, new RoutedEventArgs()),
                        Capture: () => Screenshot_Click(this, new RoutedEventArgs()),
                        ReplayButton: ThumbReplayAction,
                        ReplayToggle: () => Replay_Click(this, new RoutedEventArgs()),
                        ReplaySave: SaveReplay,
                        OpenMain: ShowFromTray),
                    action => Dispatcher.BeginInvoke(action));
            }
            catch (Exception ex) { CustomRecorder.Core.Util.Log.Error("작업 표시줄 버튼 생성 오류", ex); }
        }
        else if (!wantTaskbar && _strip != null)
        {
            _strip.Dispose();
            _strip = null;
        }
        UpdateQuickButtons();
    }

    /// <summary>Own windows are hidden from capture only while something is being recorded (see CaptureExclusion).</summary>
    private void UpdateCaptureExclusion() => CaptureExclusion.SetBusy(_recorder != null || _replay != null || _countingDown);

    private void UpdateQuickButtons()
    {
        UpdateCaptureExclusion();
        bool recording = _recorder != null, replay = _replay != null;
        var length = FormatSeconds(_settings.ReplaySeconds);
        _quick?.SetRecording(recording);
        _quick?.SetReplay(replay, length);
        _strip?.SetState(recording, replay, length, SoundOnly);

        // Buttons under the taskbar thumbnail (hover the taskbar icon).
        ThumbRecord.ImageSource = QuickButtons.Image(recording ? QuickButtons.Kind.Stop : QuickButtons.Kind.Record);
        ThumbRecord.Description = recording ? "녹화 중지" : "녹화 시작";
        ThumbCapture.ImageSource = QuickButtons.Image(QuickButtons.Kind.Capture);
        ThumbCapture.IsEnabled = !SoundOnly;
        ThumbReplay.ImageSource = QuickButtons.Image(replay ? QuickButtons.Kind.ReplayOn : QuickButtons.Kind.ReplayOff);
        ThumbReplay.Description = replay ? $"리플레이 저장 (최근 {length})" : "리플레이 켜기";
        ThumbReplay.IsEnabled = !SoundOnly;
    }

    /// <summary>Small balloon so actions started from the taskbar give feedback without opening the window.</summary>
    private void Flash(string title, string text)
    {
        if (_quick != null) _quick.Flash(title, text);
        else _tray?.Flash(title, text);
    }

    private void ThumbRecord_Click(object? sender, EventArgs e) => Record_Click(this, new RoutedEventArgs());
    private void ThumbCapture_Click(object? sender, EventArgs e) => Screenshot_Click(this, new RoutedEventArgs());
    private void ThumbReplay_Click(object? sender, EventArgs e) => ThumbReplayAction();

    /// <summary>Replay quick button: turns replay on when it is off, saves the recent footage when it is on.</summary>
    private void ThumbReplayAction()
    {
        if (_replay != null) SaveReplay();
        else Replay_Click(this, new RoutedEventArgs());
    }
}
