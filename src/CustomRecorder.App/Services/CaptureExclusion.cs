using System.Runtime.InteropServices;
using System.Windows.Interop;
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

    /// <summary>Registered windows → whether the exclusion is currently set on that window.</summary>
    private static readonly Dictionary<IntPtr, bool> Windows = new();
    private static bool _applied;

    /// <summary>
    /// Registers a window (call when its handle exists). It follows the current hidden / visible state.
    /// <para>
    /// The affinity is only ever changed on a window that is on screen (or just about to be): calling
    /// SetWindowDisplayAffinity on a hidden window makes DWM compose it, which leaves an empty, unclickable
    /// "ghost" of the window on the desktop (issue #1: toolbar in the tray, recording started and stopped
    /// from the taskbar buttons). A hidden window picks up the current state when it is shown.
    /// </para>
    /// </summary>
    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        bool added;
        lock (Windows) added = Windows.TryAdd(hwnd, false);
        if (added) HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        if (NativeMethods.IsWindowVisible(hwnd)) Sync(hwnd);
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046, WM_NCDESTROY = 0x0082;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // ShowWindow and SetWindowPos(SWP_SHOWWINDOW) both come through here, before the window appears:
            // set the state now so a window shown during a recording is never captured, not even for one frame.
            case WM_WINDOWPOSCHANGING when lParam != IntPtr.Zero
                                           && (Marshal.PtrToStructure<WINDOWPOS>(lParam).flags & SWP_SHOWWINDOW) != 0:
                Sync(hwnd);
                break;
            case WM_NCDESTROY:
                lock (Windows) Windows.Remove(hwnd);
                break;
        }
        return IntPtr.Zero;
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
            foreach (var dead in Windows.Keys.Where(h => !NativeMethods.IsWindow(h)).ToList()) Windows.Remove(dead);
            list = Windows.Keys.ToArray();
        }
        // Hidden windows are left alone (see Apply); they catch up when shown.
        foreach (var h in list)
            if (NativeMethods.IsWindowVisible(h)) Sync(h);
    }

    /// <summary>Brings one window to the current state, if it is not there already.</summary>
    private static void Sync(IntPtr hwnd)
    {
        bool hide = _applied;
        lock (Windows)
        {
            if (!Windows.TryGetValue(hwnd, out bool current) || current == hide) return;
            Windows[hwnd] = hide;
        }
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd, hide ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE) && hide)
            Log.Warn("SetWindowDisplayAffinity 실패 — Windows 10 2004 미만이면 창이 녹화에 찍힐 수 있음");
    }
}
