using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Interop;

namespace CustomRecorder.App.Windows;

/// <summary>Big "3, 2, 1" in the middle of the recorded area. Click-through and never recorded.</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number;
    private readonly PixelRect _area;

    public CountdownWindow(PixelRect area)
    {
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 220;
        Height = 220;
        _number = new TextBlock
        {
            FontSize = 120, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 20, 20, 24)),
            CornerRadius = new CornerRadius(110),
            Child = _number,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            CaptureExclusion.Apply(hwnd);
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
            // Center on the area in physical pixels (works across monitors with different DPI).
            int size = (int)(220 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                _area.X + (_area.Width - size) / 2, _area.Y + (_area.Height - size) / 2, size, size, NativeMethods.SWP_NOACTIVATE);
        };
    }

    /// <summary>Counts down; returns false if <paramref name="cancelled"/> became true meanwhile.</summary>
    public async Task<bool> RunAsync(int seconds, Func<bool> cancelled)
    {
        Show();
        try
        {
            for (int i = seconds; i > 0; i--)
            {
                _number.Text = i.ToString();
                for (int t = 0; t < 10; t++)
                {
                    await Task.Delay(100);
                    if (cancelled()) return false;
                }
            }
            return true;
        }
        finally
        {
            Close();
            await Task.Delay(60); // let the window disappear before the first frame is captured
        }
    }
}
