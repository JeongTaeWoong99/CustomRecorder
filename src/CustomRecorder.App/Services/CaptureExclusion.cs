using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

/// <summary>
/// Keeps this app's own windows (toolbar, drawing tools, region border…) out of its recordings and screenshots.
/// <para>
/// Windows hides an excluded window from every capture — including remote desktop (Chrome Remote Desktop etc.),
/// screen sharing and the taskbar thumbnail, where it shows up black. So the windows are hidden only while it
/// matters: during recording, while the replay buffer runs, and for the moment a screenshot is taken.
/// The rest of the time they are normal windows.
/// </para>
/// </summary>
public static class CaptureExclusion
{
    /// <summary>False when started with <c>--visible</c> (development / taking pictures of the app itself).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>User setting "녹화 영상에서 툴바 숨기기".</summary>
    public static bool HideFromRecordings { get; set; } = true;

    private static readonly HashSet<IntPtr> Windows = new();
    private static bool _applied;

    /// <summary>Registers a window (call when its handle exists). It follows the current hidden / visible state.</summary>
    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (Windows) Windows.Add(hwnd);
        if (_applied) Set(hwnd, true);
    }

    private static bool _busy;
    private static int _temporary;

    /// <summary>Recording / replay / countdown running: keep the windows hidden until this is set back to false.</summary>
    public static void SetBusy(bool busy)
    {
        _busy = busy;
        Refresh();
    }

    /// <summary>Hides the windows until the returned object is disposed (screenshots).</summary>
    public static IDisposable HideTemporarily()
    {
        _temporary++;
        Refresh();
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _temporary = Math.Max(0, _temporary - 1);
            Refresh();
        }
    }

    /// <summary>Re-applies after the setting changed.</summary>
    public static void Refresh()
    {
        bool want = Enabled && HideFromRecordings && (_busy || _temporary > 0);
        if (want == _applied) return;
        _applied = want;
        IntPtr[] list;
        lock (Windows)
        {
            Windows.RemoveWhere(h => !NativeMethods.IsWindow(h));
            list = Windows.ToArray();
        }
        foreach (var h in list) Set(h, want);
    }

    private static void Set(IntPtr hwnd, bool hide)
    {
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd, hide ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE) && hide)
            Log.Warn("SetWindowDisplayAffinity 실패 — Windows 10 2004 미만이면 창이 녹화에 찍힐 수 있음");
    }
}
