using System.Diagnostics;
using System.Text;
using CustomRecorder.Core.Interop;

namespace CustomRecorder.Core.Capture;

/// <summary>Rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>H.264 4:2:0 needs even dimensions.</summary>
    public PixelRect ToEven() => this with { Width = Width & ~1, Height = Height & ~1 };

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? new PixelRect(x, y, 0, 0) : new PixelRect(x, y, r - x, b - y);
    }

    public override string ToString() => $"{Width}x{Height} @ ({X},{Y})";
}

/// <summary>What to record.</summary>
public abstract record CaptureTarget
{
    public abstract string DisplayName { get; }
}

/// <summary>A whole monitor.</summary>
public sealed record MonitorTarget(MonitorInfo Monitor) : CaptureTarget
{
    public override string DisplayName => Monitor.DisplayName;
}

/// <summary>A region of one monitor. <see cref="Region"/> is in screen pixels.</summary>
public sealed record RegionTarget(MonitorInfo Monitor, PixelRect Region) : CaptureTarget
{
    public override string DisplayName => $"영역 {Region.Width}x{Region.Height}";
}

/// <summary>A single top-level window. It keeps being captured even when covered by other windows.</summary>
public sealed record WindowTarget(WindowInfo Window) : CaptureTarget
{
    public override string DisplayName => $"창: {Window.Title}";
}

public sealed record MonitorInfo(IntPtr Handle, PixelRect Bounds, bool IsPrimary, int Index)
{
    public string DisplayName => $"모니터 {Index + 1}{(IsPrimary ? " (주)" : "")} — {Bounds.Width}x{Bounds.Height}";

    public override string ToString() => DisplayName;

    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var list = new List<(IntPtr h, NativeMethods.MONITORINFOEX info)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(h, ref info)) list.Add((h, info));
            return true;
        }, IntPtr.Zero);

        // Primary first, then left-to-right.
        return list
            .OrderByDescending(m => (m.info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(m => m.info.rcMonitor.Left)
            .Select((m, i) => new MonitorInfo(
                m.h,
                new PixelRect(m.info.rcMonitor.Left, m.info.rcMonitor.Top, m.info.rcMonitor.Width, m.info.rcMonitor.Height),
                (m.info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                i))
            .ToList();
    }

    public static MonitorInfo FromPoint(int x, int y)
    {
        var handle = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var all = GetAll();
        return all.FirstOrDefault(m => m.Handle == handle) ?? all[0];
    }
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public override string ToString() => $"{Title}  —  {ProcessName}";

    /// <summary>Lists visible, capturable top-level windows (excluding this process).</summary>
    public static IReadOnlyList<WindowInfo> GetCapturable()
    {
        var self = (uint)Environment.ProcessId;
        var shell = NativeMethods.GetShellWindow();
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return true;

            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;

            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch { proc = "?"; }

            result.Add(new WindowInfo(hwnd, sb.ToString(), proc));
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
