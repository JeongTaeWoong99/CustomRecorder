using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

/// <summary>
/// Hides this app's own windows from every screen capture (its recordings, other recorders, screenshots).
/// Start with <c>--visible</c> to turn this off, e.g. to take screenshots of the app itself.
/// </summary>
public static class CaptureExclusion
{
    public static bool Enabled { get; set; } = true;

    public static void Apply(IntPtr hwnd)
    {
        if (!Enabled) return;
        if (!NativeMethods.ExcludeFromCapture(hwnd))
            Log.Warn("SetWindowDisplayAffinity 실패 — Windows 10 2004 미만이면 창이 녹화에 찍힐 수 있음");
    }
}
