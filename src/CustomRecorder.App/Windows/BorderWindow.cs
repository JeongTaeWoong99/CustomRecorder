using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Interop;

namespace CustomRecorder.App.Windows;

/// <summary>
/// Red frame drawn just outside the recording region. Click-through and excluded from capture,
/// so it never appears in the video or blocks the mouse.
/// </summary>
public sealed class BorderWindow : Window
{
    private const int Thickness = 3;

    public BorderWindow(PixelRect region)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xF2, 0x46, 0x4B)),
            StrokeThickness = Thickness,
            StrokeDashArray = new DoubleCollection { 4, 2 },
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
            CaptureExclusion.Apply(hwnd);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                region.X - Thickness, region.Y - Thickness, region.Width + Thickness * 2, region.Height + Thickness * 2,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        };
    }
}
