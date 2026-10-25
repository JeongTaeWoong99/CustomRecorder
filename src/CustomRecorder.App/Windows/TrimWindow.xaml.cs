using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Windows;

/// <summary>Pick a start and end point while previewing, then save that part losslessly (<see cref="Mp4Trim"/>).</summary>
public partial class TrimWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private TimeSpan _duration, _start, _end;
    private bool _playing, _dragging, _updating;

    public string? OutputPath { get; private set; }

    public TrimWindow(string path)
    {
        _path = path;
        InitializeComponent();
        WindowTheme.Apply(this, 76);
        SourceInitialized += (_, _) => CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);
        FileText.Text = Path.GetFileName(path);
        Player.Source = new Uri(path);
        Player.Play();
        Player.Pause(); // load the first frame
        _timer.Tick += (_, _) => SyncTimeline();
        Closed += (_, _) => { _timer.Stop(); Player.Close(); };
        PreviewKeyDown += OnKey;
        RangeCanvas.SizeChanged += (_, _) => UpdateRange();
        foreach (var (_, label) in GifSizes) GifSizeCombo.Items.Add(label);
        GifSizeCombo.SelectedIndex = 1;
        foreach (var f in GifFps) GifFpsCombo.Items.Add($"{f} fps");
        GifFpsCombo.SelectedIndex = 1;
    }

    private static readonly (int Width, string Label)[] GifSizes = { (480, "작게 480px"), (640, "보통 640px"), (800, "크게 800px") };
    private static readonly int[] GifFps = { 10, 15, 20 };

    private async void Gif_Click(object sender, RoutedEventArgs e)
    {
        var len = _end - _start;
        if (len < TimeSpan.FromSeconds(0.3)) { StatusText.Text = "남길 구간이 너무 짧습니다."; return; }
        if (len > TimeSpan.FromSeconds(60) &&
            MessageBox.Show(this, $"GIF는 {len.TotalSeconds:F0}초처럼 긴 구간에서 파일이 매우 커집니다 (수십~수백 MB).\n그래도 만들까요?", "CustomRecorder",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetPlaying(false);
        var dir = Path.GetDirectoryName(_path)!;
        var stem = Path.GetFileNameWithoutExtension(_path);
        var output = Path.Combine(dir, $"{stem}.gif");
        for (int i = 2; File.Exists(output); i++) output = Path.Combine(dir, $"{stem}_{i}.gif");

        int width = GifSizes[Math.Max(0, GifSizeCombo.SelectedIndex)].Width, fps = GifFps[Math.Max(0, GifFpsCombo.SelectedIndex)];
        GifButton.IsEnabled = SaveButton.IsEnabled = false;
        var progress = new Progress<double>(p => StatusText.Text = $"GIF 만드는 중… {p:P0}");
        long start = _start.Ticks, end = _end.Ticks;
        try
        {
            await Task.Run(() => VideoToGif.Convert(_path, output, start, end, width, fps, progress));
            var size = new FileInfo(output).Length;
            StatusText.Text = $"GIF 저장됨: {Path.GetFileName(output)} ({size / 1024.0 / 1024.0:F1} MB)";
            OutputPath = output;
        }
        catch (Exception ex)
        {
            Log.Error("GIF 저장 실패", ex);
            try { File.Delete(output); } catch { }
            StatusText.Text = $"GIF를 만들지 못했습니다: {ex.Message}";
        }
        finally { GifButton.IsEnabled = SaveButton.IsEnabled = true; }
    }

    private async void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "길이 확인 중…";
        TimeSpan exact;
        try { exact = await Task.Run(() => Mp4Trim.VideoDuration(_path)); }
        catch (Exception ex) { Log.Warn($"길이 확인 실패: {ex.Message}"); exact = TimeSpan.Zero; }
        if (exact <= TimeSpan.Zero && Player.NaturalDuration.HasTimeSpan) exact = Player.NaturalDuration.TimeSpan;
        if (exact <= TimeSpan.Zero) { StatusText.Text = "영상 길이를 알 수 없습니다."; return; }
        StatusText.Text = "";
        _duration = exact;
        _start = TimeSpan.Zero;
        _end = _duration;
        Timeline.Maximum = _duration.TotalSeconds;
        SaveButton.IsEnabled = GifButton.IsEnabled = true;
        UpdateRange();
        SyncTimeline();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        StatusText.Text = "미리보기를 열 수 없습니다 (HEVC 파일은 Windows의 HEVC 확장이 필요합니다). 자르기는 시간을 알 수 없어 사용할 수 없습니다.";
        Log.Warn($"미리보기 실패: {e.ErrorException?.Message}");
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => SetPlaying(false);

    private void SyncTimeline()
    {
        if (_dragging) return;
        var pos = Player.Position;
        if (_playing && pos >= _end) { SetPlaying(false); Player.Position = _end; pos = _end; }
        _updating = true;
        Timeline.Value = pos.TotalSeconds;
        _updating = false;
        TimeText.Text = $"{Fmt(pos)} / {Fmt(_duration)}";
    }

    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeText.Text = $"{Fmt(Player.Position)} / {Fmt(_duration)}";
    }

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e) => _dragging = true;
    private void Timeline_MouseUp(object sender, MouseButtonEventArgs e) => _dragging = false;

    private void Play_Click(object sender, RoutedEventArgs e) => SetPlaying(!_playing);

    private void SetPlaying(bool play)
    {
        _playing = play;
        if (play)
        {
            if (Player.Position >= _end || Player.Position < _start) Player.Position = _start;
            Player.Play();
            _timer.Start();
        }
        else
        {
            Player.Pause();
            _timer.Stop();
            SyncTimeline();
        }
        PlayIcon.Text = play ? "" : "";
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        _start = Player.Position;
        if (_end <= _start) _end = _duration;
        UpdateRange();
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        _end = Player.Position;
        if (_end <= _start) _start = TimeSpan.Zero;
        UpdateRange();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { SetPlaying(!_playing); e.Handled = true; }
        else if (e.Key == Key.OemOpenBrackets) { SetStart_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets) { SetEnd_Click(this, e); e.Handled = true; }
    }

    private void UpdateRange()
    {
        RangeText.Text = $"남길 구간  {Fmt(_start)} ~ {Fmt(_end)}   (길이 {Fmt(_end - _start)})";
        if (_duration <= TimeSpan.Zero) return;
        double w = RangeCanvas.ActualWidth;
        double x0 = w * _start.TotalSeconds / _duration.TotalSeconds, x1 = w * _end.TotalSeconds / _duration.TotalSeconds;
        System.Windows.Controls.Canvas.SetLeft(RangeBar, x0);
        RangeBar.Width = Math.Max(2, x1 - x0);
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_end - _start < TimeSpan.FromSeconds(0.5)) { StatusText.Text = "남길 구간이 너무 짧습니다."; return; }
        SetPlaying(false);
        var dir = Path.GetDirectoryName(_path)!;
        var stem = Path.GetFileNameWithoutExtension(_path);
        var output = Path.Combine(dir, $"{stem}_잘라냄.mp4");
        for (int i = 2; File.Exists(output); i++) output = Path.Combine(dir, $"{stem}_잘라냄_{i}.mp4");

        SaveButton.IsEnabled = false;
        StatusText.Text = "저장 중…";
        var progress = new Progress<double>(p => StatusText.Text = $"저장 중… {p:P0}");
        long start = _start.Ticks, end = _end.Ticks;
        try
        {
            long actual = await Task.Run(() => Mp4Trim.Trim(_path, output, start, end, progress));
            OutputPath = output;
            var shift = TimeSpan.FromTicks(start - actual);
            MessageBox.Show(this,
                $"저장했습니다.\n{Path.GetFileName(output)}" + (shift > TimeSpan.FromSeconds(0.2) ? $"\n\n키프레임에 맞추느라 {shift.TotalSeconds:F1}초 앞에서 시작합니다." : ""),
                "CustomRecorder", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Log.Error("자르기 실패", ex);
            try { File.Delete(output); } catch { }
            StatusText.Text = $"저장하지 못했습니다: {ex.Message}";
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
