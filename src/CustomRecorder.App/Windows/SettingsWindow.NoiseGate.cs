using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CustomRecorder.Core.Audio;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Windows;

/// <summary>
/// Microphone noise reduction: automatic / custom threshold with a live sensitivity bar (like Discord's input
/// sensitivity). The microphone is opened only while the 소리 page is shown and noise reduction is on.
/// </summary>
public partial class SettingsWindow
{
    private const float BarMinDb = -80f, BarMaxDb = 0f;
    private static readonly Brush PassBrush = new SolidColorBrush(Color.FromRgb(61, 214, 140));
    private static readonly Brush BlockBrush = new SolidColorBrush(Color.FromRgb(255, 181, 71));

    private MicLevelMonitor? _micMonitor;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private float _gateDb, _levelDb = -100f;
    private bool _dragging;

    private void InitNoiseGate()
    {
        _gateDb = Math.Clamp(Settings.MicNoiseGateThresholdDb, BarMinDb, BarMaxDb);
        (Settings.MicNoiseGateAuto ? GateAuto : GateCustom).IsChecked = true;
        Bind(NoiseGateCheck, NoiseGatePanel);

        _meterTimer.Tick += (_, _) => UpdateMeter();
        NoiseGateCheck.Checked += (_, _) => UpdateMonitor();
        NoiseGateCheck.Unchecked += (_, _) => UpdateMonitor();
        PageSound.IsVisibleChanged += (_, _) => UpdateMonitor();
        MicCombo.SelectionChanged += (_, _) => { StopMonitor(); UpdateMonitor(); };
        Closed += (_, _) => StopMonitor();
        UpdateGateUi();
    }

    private void SaveNoiseGate()
    {
        Settings.MicNoiseGateAuto = GateAuto.IsChecked == true;
        Settings.MicNoiseGateThresholdDb = MathF.Round(_gateDb);
    }

    private void GateMode_Checked(object sender, RoutedEventArgs e) => UpdateGateUi();

    private bool GateIsAuto => GateAuto.IsChecked == true;

    // ---------------------------------------------------------------- live microphone

    private void UpdateMonitor()
    {
        bool want = IsLoaded && PageSound.IsVisible && NoiseGateCheck.IsChecked == true;
        if (want && _micMonitor == null)
        {
            try
            {
                _micMonitor = new MicLevelMonitor((MicCombo.SelectedItem as AudioDevice)?.Id);
                GateHint.Text = "말을 해 보세요. 막대가 초록색이면 녹음되고, 노란색이면 잡음으로 보고 꺼집니다.";
                _meterTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Warn($"마이크 미리보기를 열 수 없음: {ex.Message}");
                GateHint.Text = "마이크를 열 수 없어 소리 막대를 표시하지 못했습니다. 마이크 연결과 Windows 마이크 권한을 확인하세요.";
            }
        }
        else if (!want) StopMonitor();
    }

    private void StopMonitor()
    {
        _meterTimer.Stop();
        _micMonitor?.Dispose();
        _micMonitor = null;
        _levelDb = -100f;
        UpdateGateUi();
    }

    private void UpdateMeter()
    {
        if (_micMonitor == null) return;
        float peak = _micMonitor.TakePeakDb();
        _levelDb = MathF.Max(peak, _levelDb - 1.5f); // falls ~45 dB/s, rises instantly
        UpdateGateUi();
    }

    private float CurrentThresholdDb => GateIsAuto ? _micMonitor?.AutoThresholdDb ?? NoiseFloorTracker.MaxAutoDb : _gateDb;

    private void UpdateGateUi()
    {
        if (GateBar == null || GateBar.ActualWidth <= 0) return;
        double width = GateBar.ActualWidth;
        float threshold = CurrentThresholdDb;

        GateLevel.Width = Math.Max(0, X(_levelDb, width));
        GateLevel.Background = _levelDb >= threshold ? PassBrush : BlockBrush;
        GateMarker.Margin = new Thickness(Math.Clamp(X(threshold, width) - 2, 0, width - 4), 0, 0, 0);
        GateMarker.Opacity = GateIsAuto ? 0.45 : 1.0;
        GateBar.Cursor = GateIsAuto ? Cursors.Arrow : Cursors.Hand;

        string level = _micMonitor != null && _levelDb > -100f ? $"지금 {_levelDb:F0} dB  ·  " : "";
        GateValueText.Text = GateIsAuto
            ? $"{level}자동 기준 {(_micMonitor?.AutoThresholdDb is float a ? $"{a:F0} dB" : "측정 중")}"
            : $"{level}기준 {_gateDb:F0} dB";
    }

    private static double X(float db, double width) => (Math.Clamp(db, BarMinDb, BarMaxDb) - BarMinDb) / (BarMaxDb - BarMinDb) * width;

    // ---------------------------------------------------------------- dragging the threshold (custom mode)

    private void GateBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GateIsAuto) return;
        _dragging = GateBar.CaptureMouse();
        SetGateFromMouse(e);
    }

    private void GateBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) SetGateFromMouse(e);
    }

    private void GateBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        GateBar.ReleaseMouseCapture();
    }

    private void GateBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateGateUi();

    private void SetGateFromMouse(MouseEventArgs e)
    {
        double x = e.GetPosition(GateBar).X;
        _gateDb = MathF.Round((float)(BarMinDb + Math.Clamp(x / GateBar.ActualWidth, 0, 1) * (BarMaxDb - BarMinDb)));
        UpdateGateUi();
    }
}
