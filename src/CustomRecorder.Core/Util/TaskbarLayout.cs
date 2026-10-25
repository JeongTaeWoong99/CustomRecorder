namespace CustomRecorder.Core.Util;

/// <summary>One button found on the taskbar by UI Automation (screen pixels).</summary>
public readonly record struct TaskbarButton(int Left, int Right, string ClassName);

/// <summary>
/// Finds the free gap between the app buttons and the notification area (tray) on a horizontal taskbar.
/// <para>
/// The tray's own window (TrayNotifyWnd) does not always cover the tray as drawn: on some Windows 11 builds the
/// chevron, input-language and network buttons sit left of it. So the tray is taken from the buttons themselves —
/// by class name ("SystemTray.*") when Windows reports it, otherwise as the group of buttons packed together at the
/// right end.
/// </para>
/// </summary>
public static class TaskbarLayout
{
    /// <param name="notifyLeft">Left edge of TrayNotifyWnd, or int.MaxValue if unknown.</param>
    /// <param name="packedGap">Buttons closer than this belong to the same group.</param>
    /// <returns>AppsRight = right edge of the last app button (int.MinValue if none); TrayLeft = left edge of the tray.</returns>
    public static (int AppsRight, int TrayLeft) Analyze(IReadOnlyList<TaskbarButton> buttons, int notifyLeft, int packedGap)
    {
        int trayLeft = notifyLeft;
        var tray = buttons.Where(IsTray).ToList();
        if (tray.Count > 0)
        {
            trayLeft = Math.Min(trayLeft, tray.Min(b => b.Left));
        }
        else if (buttons.Count > 0)
        {
            // The tray is the cluster of buttons at the right end.
            var sorted = buttons.OrderByDescending(b => b.Right).ToList();
            int left = sorted[0].Left;
            foreach (var b in sorted.Skip(1))
            {
                if (b.Right >= left - packedGap) left = Math.Min(left, b.Left);
                else break;
            }
            trayLeft = Math.Min(trayLeft, left);
        }

        int appsRight = int.MinValue;
        foreach (var b in buttons)
            if (!IsTray(b) && b.Right <= trayLeft) appsRight = Math.Max(appsRight, b.Right);
        return (appsRight, trayLeft);
    }

    private static bool IsTray(TaskbarButton b) => b.ClassName.StartsWith("SystemTray", StringComparison.Ordinal);
}
