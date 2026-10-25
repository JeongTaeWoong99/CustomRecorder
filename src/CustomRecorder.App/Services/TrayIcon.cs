using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CustomRecorder.App.Services;

/// <summary>Notification-area icon with a small menu; shows a red dot while recording.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _normal;
    private readonly Icon _recording;
    private readonly Forms.ToolStripMenuItem _recordItem;
    private bool _balloonShown;

    public TrayIcon(Action show, Action toggleRecord, Action screenshot, Action saveReplay, Action exit)
    {
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream)
            _normal = new Icon(stream, 16, 16);
        _recording = MakeRecordingIcon(_normal);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => show());
        _recordItem = new Forms.ToolStripMenuItem("녹화 시작", null, (_, _) => toggleRecord());
        menu.Items.Add(_recordItem);
        menu.Items.Add("캡처", null, (_, _) => screenshot());
        menu.Items.Add("리플레이 저장", null, (_, _) => saveReplay());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon { Icon = _normal, Text = "CustomRecorder", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }

    public void SetRecording(bool recording, string? text = null)
    {
        _icon.Icon = recording ? _recording : _normal;
        _recordItem.Text = recording ? "녹화 중지" : "녹화 시작";
        var tip = text ?? (recording ? "CustomRecorder — 녹화 중" : "CustomRecorder");
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    /// <summary>Tells the user once that the app keeps running in the tray.</summary>
    public void NotifyHidden()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(3000, "CustomRecorder", "알림 영역에서 계속 실행 중입니다. 단축키도 그대로 동작합니다.", Forms.ToolTipIcon.Info);
    }

    /// <summary>Shown once when the toolbar hides itself for recording.</summary>
    private bool _recordingTipShown;
    public void NotifyRecordingHidden(string? hotkey)
    {
        if (_recordingTipShown) return;
        _recordingTipShown = true;
        var how = string.IsNullOrWhiteSpace(hotkey) ? "알림 영역 아이콘을 오른쪽 클릭해 '녹화 중지'를 누르세요." : $"{hotkey} 또는 알림 영역 아이콘 메뉴로 멈출 수 있습니다.";
        _icon.ShowBalloonTip(3000, "녹화 중", $"툴바를 숨겼습니다. {how}", Forms.ToolTipIcon.Info);
    }

    /// <summary>Short notification (e.g. "캡처 저장됨") when the action came from outside the window.</summary>
    public void Flash(string title, string text) => _icon.ShowBalloonTip(1500, title, text, Forms.ToolTipIcon.None);

    private static Icon MakeRecordingIcon(Icon baseIcon)
    {
        using var bmp = baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(Brushes.White, 7, 7, 9, 9);
            using var red = new SolidBrush(Color.FromArgb(242, 70, 75));
            g.FillEllipse(red, 8, 8, 7, 7);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normal.Dispose();
        _recording.Dispose();
    }
}
