using CustomRecorder.Core.Util;
using Xunit;

public class TaskbarLayoutTests
{
    private static TaskbarButton App(int l) => new(l, l + 60, "Taskbar.TaskListButtonAutomationPeer");

    [Fact]
    public void TrayButtonsLeftOfTrayWindowAreRecognisedByClass()
    {
        // 4K work PC: TrayNotifyWnd starts at 3423, but chevron / language / network buttons start at 3042.
        var buttons = new List<TaskbarButton> { App(10), App(80), App(150), App(770) };
        buttons.Add(new(3042, 3090, "SystemTray.ChevronButton"));
        buttons.Add(new(3095, 3160, "SystemTray.OmniButton"));
        buttons.Add(new(3410, 3470, "SystemTray.NormalButton"));
        var (apps, tray) = TaskbarLayout.Analyze(buttons, notifyLeft: 3423, packedGap: 18);
        Assert.Equal(830, apps);
        Assert.Equal(3042, tray);
    }

    [Fact]
    public void WithoutClassNamesTheRightmostPackedGroupIsTheTray()
    {
        var buttons = new List<TaskbarButton>
        {
            App(10), App(80), App(770),
            new(3042, 3090, ""), new(3092, 3160, ""), new(3165, 3300, ""), new(3410, 3470, ""), // tray icons, gaps under 120 px
        };
        var (apps, tray) = TaskbarLayout.Analyze(buttons, notifyLeft: 3423, packedGap: 120);
        Assert.Equal(830, apps);
        Assert.Equal(3042, tray);
    }

    [Fact]
    public void TrayWindowWinsWhenItIsFurtherLeft()
    {
        // Home PC: TrayNotifyWnd matches the tray; app buttons end at 1775.
        var buttons = new List<TaskbarButton> { App(1100), App(1715), new(1950, 1990, "SystemTray.ChevronButton") };
        var (apps, tray) = TaskbarLayout.Analyze(buttons, notifyLeft: 1942, packedGap: 15);
        Assert.Equal(1775, apps);
        Assert.Equal(1942, tray);
    }

    [Fact]
    public void NoButtonsFallsBackToTrayWindow()
    {
        var (apps, tray) = TaskbarLayout.Analyze(new List<TaskbarButton>(), notifyLeft: 1942, packedGap: 15);
        Assert.Equal(int.MinValue, apps);
        Assert.Equal(1942, tray);
    }
}
