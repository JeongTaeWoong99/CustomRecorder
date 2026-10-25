using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace CustomRecorder.App.Services;

/// <summary>
/// One-click buttons in the notification area (taskbar): record / capture / replay, each its own tray icon.
/// Windows puts new icons in the "^" overflow first; drag them next to the clock to keep them on the taskbar.
/// The same images are used for the buttons under the taskbar thumbnail (<see cref="Image"/>).
/// </summary>
public sealed class QuickButtons : IDisposable
{
    public enum Kind { Record, Stop, Capture, ReplayOff, ReplayOn }

    private static readonly Color Red = Color.FromArgb(255, 77, 94), Accent = Color.FromArgb(123, 140, 255),
        Green = Color.FromArgb(61, 214, 140);

    // Glyph colors follow the taskbar theme: a light glyph disappears on the light taskbar and vice versa.
    private static Color Fore(bool light) => light ? Color.FromArgb(32, 33, 38) : Color.FromArgb(236, 237, 242);
    private static Color Dim(bool light) => light ? Color.FromArgb(110, 114, 126) : Color.FromArgb(150, 155, 168);
    private static Color AccentFor(bool light) => light ? Color.FromArgb(70, 90, 230) : Accent;

    /// <summary>True when the taskbar / notification area uses the light theme.</summary>
    public static bool LightTaskbar()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private readonly Forms.NotifyIcon _record, _capture, _replay;
    private readonly Dictionary<Kind, Icon> _icons = new();
    private readonly Forms.ToolStripMenuItem _recordItem, _replayToggleItem, _replaySaveItem;

    public QuickButtons(Action toggleRecord, Action pause, Action capture, Action replayToggle, Action replaySave, Action openMain)
    {
        BuildIcons();

        // Record
        var recordMenu = new Forms.ContextMenuStrip();
        _recordItem = new Forms.ToolStripMenuItem("녹화 시작", null, (_, _) => toggleRecord());
        recordMenu.Items.Add(_recordItem);
        recordMenu.Items.Add("일시정지 / 다시 시작", null, (_, _) => pause());
        recordMenu.Items.Add(new Forms.ToolStripSeparator());
        recordMenu.Items.Add("CustomRecorder 열기", null, (_, _) => openMain());
        _record = Make(_icons[Kind.Record], "녹화 시작 (클릭)", recordMenu, toggleRecord);

        // Capture
        var captureMenu = new Forms.ContextMenuStrip();
        captureMenu.Items.Add("지금 캡처", null, (_, _) => capture());
        captureMenu.Items.Add("CustomRecorder 열기", null, (_, _) => openMain());
        _capture = Make(_icons[Kind.Capture], "화면 캡처 (클릭)", captureMenu, capture);

        // Replay: off → click turns it on; on → click saves the recent footage.
        var replayMenu = new Forms.ContextMenuStrip();
        _replaySaveItem = new Forms.ToolStripMenuItem("최근 장면 저장", null, (_, _) => replaySave()) { Enabled = false };
        _replayToggleItem = new Forms.ToolStripMenuItem("리플레이 켜기", null, (_, _) => replayToggle());
        replayMenu.Items.Add(_replaySaveItem);
        replayMenu.Items.Add(_replayToggleItem);
        replayMenu.Items.Add(new Forms.ToolStripSeparator());
        replayMenu.Items.Add("CustomRecorder 열기", null, (_, _) => openMain());
        _replay = Make(_icons[Kind.ReplayOff], "리플레이 꺼짐 — 클릭하면 켜기", replayMenu, () =>
        {
            if (_replayOn) replaySave(); else replayToggle();
        });
    }

    private bool _replayOn, _recording, _light;

    private void BuildIcons()
    {
        _light = LightTaskbar();
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        var old = _icons.Values.ToList();
        foreach (Kind k in Enum.GetValues<Kind>()) _icons[k] = MakeIcon(k, size, _light);
        foreach (var i in old) i.Dispose();
    }

    /// <summary>Redraws the icons if the Windows theme changed (light ↔ dark taskbar). Returns true when it did.</summary>
    public bool RefreshTheme()
    {
        if (LightTaskbar() == _light) return false;
        BuildIcons();
        _record.Icon = _icons[_recording ? Kind.Stop : Kind.Record];
        _capture.Icon = _icons[Kind.Capture];
        _replay.Icon = _icons[_replayOn ? Kind.ReplayOn : Kind.ReplayOff];
        return true;
    }

    private static Forms.NotifyIcon Make(Icon icon, string tip, Forms.ContextMenuStrip menu, Action click)
    {
        var n = new Forms.NotifyIcon { Icon = icon, Text = tip, ContextMenuStrip = menu, Visible = true };
        n.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) click(); };
        return n;
    }

    public void SetRecording(bool recording, string? elapsed = null)
    {
        _recording = recording;
        _record.Icon = _icons[recording ? Kind.Stop : Kind.Record];
        _record.Text = recording ? $"녹화 중 {elapsed} — 클릭하면 중지".Trim() : "녹화 시작 (클릭)";
        _recordItem.Text = recording ? "녹화 중지" : "녹화 시작";
    }

    public void SetReplay(bool on, string length)
    {
        _replayOn = on;
        _replay.Icon = _icons[on ? Kind.ReplayOn : Kind.ReplayOff];
        _replay.Text = on ? $"리플레이 켜짐 — 클릭하면 최근 {length} 저장" : "리플레이 꺼짐 — 클릭하면 켜기";
        _replaySaveItem.Enabled = on;
        _replaySaveItem.Text = $"최근 {length} 저장";
        _replayToggleItem.Text = on ? "리플레이 끄기" : "리플레이 켜기";
    }

    public void Flash(string title, string text) => _capture.ShowBalloonTip(1500, title, text, Forms.ToolTipIcon.None);

    // ---------------------------------------------------------------- drawing

    /// <summary>The button image as a WPF image (for the taskbar thumbnail toolbar).</summary>
    public static BitmapSource Image(Kind kind)
    {
        using var bmp = Draw(kind, 32, LightTaskbar());
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); // PNG keeps the transparency
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static Icon MakeIcon(Kind kind, int size, bool light)
    {
        using var bmp = Draw(kind, size, light);
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone(); // own copy, so the handle can be destroyed
        DestroyIcon(h);
        return icon;
    }

    internal static Bitmap Draw(Kind kind, int size, bool light)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float s = size;
        switch (kind)
        {
            case Kind.Record:
                using (var b = new SolidBrush(Red)) g.FillEllipse(b, s * 0.14f, s * 0.14f, s * 0.72f, s * 0.72f);
                break;
            case Kind.Stop:
                using (var b = new SolidBrush(Red))
                using (var path = Rounded(s * 0.2f, s * 0.2f, s * 0.6f, s * 0.6f, s * 0.12f))
                    g.FillPath(b, path);
                break;
            case Kind.Capture:
                Glyph(g, "", s, Fore(light));
                break;
            case Kind.ReplayOff:
                Glyph(g, "", s, Dim(light));
                break;
            case Kind.ReplayOn:
                Glyph(g, "", s, AccentFor(light));
                using (var b = new SolidBrush(Green)) g.FillEllipse(b, s * 0.62f, s * 0.62f, s * 0.36f, s * 0.36f);
                break;
        }
        return bmp;
    }

    private static void Glyph(Graphics g, string glyph, float s, Color color)
    {
        using var font = new Font(FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets",
            s * 0.72f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, s, s), fmt);
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    public void Dispose()
    {
        foreach (var n in new[] { _record, _capture, _replay }) { n.Visible = false; n.Dispose(); }
        foreach (var i in _icons.Values) i.Dispose();
    }
}
