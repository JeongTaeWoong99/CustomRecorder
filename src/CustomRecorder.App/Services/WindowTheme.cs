using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace CustomRecorder.App.Services;

/// <summary>
/// Native Windows 11 look for our borderless windows: DWM drop shadow, rounded corners and a dark border,
/// while the content draws its own title bar (WindowChrome with no caption).
/// </summary>
public static class WindowTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <param name="draggableHeight">Height of the custom title area that moves the window (0 = none).</param>
    public static void Apply(Window window, double draggableHeight = 0, bool smallCorners = false)
    {
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = draggableHeight,
            GlassFrameThickness = new Thickness(0, 0, 0, 1), // keeps the DWM shadow
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        // SizeToContent measures once with the normal frame; re-run it after the chrome removed the frame,
        // otherwise an unpainted strip is left on the right and bottom.
        window.ContentRendered += (_, _) =>
        {
            if (window.SizeToContent == SizeToContent.Manual) return;
            var mode = window.SizeToContent;
            window.SizeToContent = SizeToContent.Manual;
            window.SizeToContent = mode;
        };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int on = 1, corner = smallCorners ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND, border = 0x00382F2C; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        };
    }
}
