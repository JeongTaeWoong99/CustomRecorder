using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Interop;

namespace CustomRecorder.App.Windows;

/// <summary>
/// Full-monitor overlay for dragging a capture region.
/// Positions are read with GetCursorPos (physical pixels) so the result is exact on any DPI scale.
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private readonly MonitorInfo _monitor;
    private NativeMethods.POINT _start;
    private bool _dragging;

    /// <summary>Selected region in physical screen pixels.</summary>
    public PixelRect? Result { get; private set; }

    public RegionSelectorWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            CaptureExclusion.Apply(hwnd);
            var b = monitor.Bounds;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            ShadeFull.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            ShadeHole.Rect = Rect.Empty;
            Canvas.SetLeft(Help, (ActualWidth - Help.ActualWidth) / 2);
            Canvas.SetTop(Help, 40);
        };
        SizeChanged += (_, _) => ShadeFull.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        NativeMethods.GetCursorPos(out _start);
        _dragging = true;
        Root.CaptureMouse();
        Help.Visibility = Visibility.Collapsed;
        Selection.Visibility = Visibility.Visible;
        SizeTag.Visibility = Visibility.Visible;
        UpdateSelection();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) UpdateSelection();
    }

    private void Root_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleaseMouseCapture();
        var rect = CurrentPhysical().Intersect(_monitor.Bounds).ToEven();
        if (rect.Width < 32 || rect.Height < 32)
        {
            // Treat a click as "cancel this drag".
            Selection.Visibility = SizeTag.Visibility = Visibility.Collapsed;
            Help.Visibility = Visibility.Visible;
            ShadeHole.Rect = Rect.Empty;
            return;
        }
        Result = rect;
        DialogResult = true;
    }

    private PixelRect CurrentPhysical()
    {
        NativeMethods.GetCursorPos(out var now);
        int x = Math.Min(_start.X, now.X), y = Math.Min(_start.Y, now.Y);
        return new PixelRect(x, y, Math.Abs(now.X - _start.X), Math.Abs(now.Y - _start.Y));
    }

    private void UpdateSelection()
    {
        var p = CurrentPhysical();
        // Physical screen → this window's device-independent units.
        var tl = PointFromScreen(new Point(p.X, p.Y));
        var br = PointFromScreen(new Point(p.Right, p.Bottom));
        var r = new Rect(tl, br);

        Canvas.SetLeft(Selection, r.X);
        Canvas.SetTop(Selection, r.Y);
        Selection.Width = r.Width;
        Selection.Height = r.Height;
        ShadeHole.Rect = r;

        var even = p.ToEven();
        SizeText.Text = $"{even.Width} × {even.Height}";
        Canvas.SetLeft(SizeTag, r.X);
        Canvas.SetTop(SizeTag, Math.Max(0, r.Y - 26));
    }
}
