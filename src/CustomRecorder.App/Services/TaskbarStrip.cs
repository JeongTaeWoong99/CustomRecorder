using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CustomRecorder.Core.Util;
using Forms = System.Windows.Forms;

namespace CustomRecorder.App.Services;

/// <summary>
/// Record / capture / replay buttons drawn directly on the taskbar, just left of the notification area
/// (and left of other taskbar widgets such as network monitors).
/// <para>
/// The strip is a small layered child window of the taskbar (Shell_TrayWnd). A child of another process's window shares
/// its input queue, so the strip runs on its own thread with its own message loop: whatever the main UI thread is doing,
/// the taskbar never waits on it. Clicks are handed to the main thread asynchronously.
/// </para>
/// </summary>
public sealed class TaskbarStrip : IDisposable
{
    public sealed record Handlers(Action Record, Action Pause, Action Capture, Action ReplayButton,
        Action ReplayToggle, Action ReplaySave, Action OpenMain);

    private readonly Handlers _handlers;
    private readonly Action<Action> _toUi;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private SynchronizationContext? _ctx;
    private StripWindow? _window;
    private MenuOwner? _menuOwner;
    private Forms.Timer? _timer;

    // State shown by the buttons (written by the UI thread, read on the strip thread).
    private readonly object _lock = new();
    private bool _recording, _replayOn, _soundOnly;
    private string _replayLength = "";
    private string _elapsed = "";

    /// <param name="toUi">Runs an action on the main UI thread without waiting (Dispatcher.BeginInvoke).</param>
    public TaskbarStrip(Handlers handlers, Action<Action> toUi)
    {
        _handlers = handlers;
        _toUi = toUi;
        _thread = new Thread(Run) { IsBackground = true, Name = "TaskbarStrip" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    public void SetState(bool recording, bool replayOn, string replayLength, bool soundOnly)
    {
        lock (_lock)
        {
            if (!recording) _elapsed = "";
            _recording = recording;
            _replayOn = replayOn;
            _replayLength = replayLength;
            _soundOnly = soundOnly;
        }
        Post(() => _window?.Render());
    }

    public void SetElapsed(TimeSpan elapsed)
    {
        string text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
        lock (_lock)
        {
            if (!_recording || text == _elapsed) return;
            _elapsed = text;
        }
        Post(() => { _window?.Render(); Place(); });
    }

    private void Post(Action a) => _ctx?.Post(_ => { try { a(); } catch (Exception ex) { Log.Error("작업 표시줄 버튼 오류", ex); } }, null);

    private void Run()
    {
        try
        {
            _ctx = new Forms.WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_ctx);
            _menuOwner = new MenuOwner();
            _timer = new Forms.Timer { Interval = 500 };
            _timer.Tick += (_, _) => { try { Place(); } catch (Exception ex) { Log.Error("작업 표시줄 버튼 배치 오류", ex); } };
            _timer.Start();
            Place();
        }
        catch (Exception ex) { Log.Error("작업 표시줄 버튼 시작 오류", ex); }
        finally { _ready.Set(); }
        Forms.Application.Run();
    }

    // ---------------------------------------------------------------- placement

    private IntPtr _taskbar;
    private string _lastPlacement = "";

    private void Place()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) { _window?.Hide(); return; }
        if (taskbar != _taskbar || _window == null || !IsWindow(_window.Handle))
        {
            // First start, or Explorer restarted (the old window went away with the old taskbar).
            _window?.DestroyHandle();
            _taskbar = taskbar;
            _window = new StripWindow(this, taskbar);
            _window.Render();
        }

        GetWindowRect(taskbar, out var tb);
        if (tb.Width <= tb.Height) { _window.Hide(); return; } // vertical taskbar: no room left of the tray

        int width = _window.Width, height = _window.Height;
        int margin = _window.Scale(4);
        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = notify != IntPtr.Zero && GetWindowRect(notify, out var nr) && nr.Left > tb.Left ? nr.Left : tb.Right - _window.Scale(160);

        // Step left of other programs' widgets docked in the same spot (e.g. a network/CPU monitor).
        GetWindowThreadProcessId(taskbar, out uint explorerPid);
        var foreign = new List<string>();
        for (int pass = 0; pass < 8; pass++)
        {
            bool moved = false;
            for (var child = FindWindowEx(taskbar, IntPtr.Zero, null, null); child != IntPtr.Zero;
                 child = FindWindowEx(taskbar, child, null, null))
            {
                if (child == _window.Handle || !IsWindowVisible(child)) continue;
                GetWindowThreadProcessId(child, out uint pid);
                if (pid == explorerPid || pid == (uint)Environment.ProcessId) continue;
                if (!GetWindowRect(child, out var r) || r.Width <= 0 || r.Height <= 0) continue;
                if (pass == 0) foreign.Add($"{r.Left}-{r.Right}");
                if (r.Left < right && r.Right > right - width - margin * 2) { right = r.Left; moved = true; }
            }
            if (!moved) break;
        }

        // Keep clear of the app buttons (Windows 11 draws them without separate windows: ask UI Automation).
        ProbeAppButtons(taskbar, right);
        int appsRight = _appsRight;
        int leftLimit = appsRight == int.MinValue ? tb.Left : appsRight + _window.Scale(2);
        int available = right - margin - leftLimit;
        bool compact = available < _window.FullWidth; // no room for the elapsed time: icons only
        if (compact != _window.Compact) { _window.Compact = compact; _window.Render(); width = _window.Width; }
        if (available < width)
        {
            _window.Hide();
            string full = $"빈자리 없음 (앱 버튼 끝 {appsRight}, 알림 영역 {right}) → 숨김";
            if (full != _lastPlacement) { _lastPlacement = full; Log.Info($"작업 표시줄 버튼: {full}"); }
            return;
        }

        int x = right - width - margin, y = tb.Top + (tb.Height - height) / 2;
        var p = new POINT { X = x, Y = y };
        ScreenToClient(taskbar, ref p);
        _window.Show(p.X, p.Y);

        string placement = $"taskbar={tb.Left},{tb.Top},{tb.Width}x{tb.Height} tray={(notify != IntPtr.Zero ? right.ToString() : "없음")} 다른 위젯=[{string.Join(", ", foreign)}] 앱 버튼 끝={(appsRight == int.MinValue ? "?" : appsRight.ToString())} → {x},{y} {width}x{height}";
        if (placement != _lastPlacement)
        {
            _lastPlacement = placement;
            Log.Info($"작업 표시줄 버튼 위치: {placement}");
        }
    }

    private volatile int _appsRight = int.MinValue;
    private int _probing;
    private long _lastProbeTicks;
    private bool _probeFailedLogged;

    /// <summary>Finds the right edge of the taskbar's app buttons in the background (UI Automation can take ~100 ms).</summary>
    private void ProbeAppButtons(IntPtr taskbar, int limit)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastProbeTicks) < 1500) return;
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(taskbar);
                var buttons = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Button));
                int max = int.MinValue;
                foreach (System.Windows.Automation.AutomationElement b in buttons)
                {
                    System.Windows.Rect r;
                    try { r = b.Current.BoundingRectangle; } catch { continue; }
                    if (r.IsEmpty || r.Width <= 0 || r.Left >= limit) continue; // tray icons and anything right of our spot
                    max = Math.Max(max, (int)Math.Ceiling(r.Right));
                }
                _appsRight = max;
            }
            catch (Exception ex)
            {
                if (!_probeFailedLogged) { _probeFailedLogged = true; Log.Warn($"작업 표시줄 앱 버튼 위치를 읽지 못함: {ex.Message}"); }
            }
            finally
            {
                Interlocked.Exchange(ref _lastProbeTicks, Environment.TickCount64);
                Interlocked.Exchange(ref _probing, 0);
            }
        });
    }

    // ---------------------------------------------------------------- actions

    private void Click(int index) => _toUi(index switch
    {
        0 => _handlers.Record,
        1 => _handlers.Capture,
        _ => _handlers.ReplayButton,
    });

    private void ShowMenu(int screenX, int screenY)
    {
        bool recording, replayOn, soundOnly; string length;
        lock (_lock) { recording = _recording; replayOn = _replayOn; soundOnly = _soundOnly; length = _replayLength; }

        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1, recording ? "녹화 중지" : "녹화 시작");
        AppendMenu(menu, recording ? 0u : MF_GRAYED, 2, "일시정지 / 다시 시작");
        AppendMenu(menu, soundOnly ? MF_GRAYED : 0u, 3, "화면 캡처");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, soundOnly ? MF_GRAYED : 0u, 4, replayOn ? "리플레이 끄기" : "리플레이 켜기");
        AppendMenu(menu, replayOn ? 0u : MF_GRAYED, 5, $"최근 {length} 저장");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, 0, 6, "CustomRecorder 열기");

        var owner = _menuOwner!.Handle;
        SetForegroundWindow(owner);
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, screenX, screenY, owner, IntPtr.Zero);
        PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL: lets the menu close properly
        DestroyMenu(menu);

        Action? action = cmd switch
        {
            1 => _handlers.Record,
            2 => _handlers.Pause,
            3 => _handlers.Capture,
            4 => _handlers.ReplayToggle,
            5 => _handlers.ReplaySave,
            6 => _handlers.OpenMain,
            _ => null,
        };
        if (action != null) _toUi(action);
    }

    public void Dispose()
    {
        Post(() =>
        {
            _timer?.Stop();
            _window?.DestroyHandle();
            _window = null;
            _menuOwner?.DestroyHandle();
            Forms.Application.ExitThread();
        });
        _thread.Join(2000);
    }

    // ---------------------------------------------------------------- the window

    private sealed class MenuOwner : Forms.NativeWindow
    {
        public MenuOwner() => CreateHandle(new Forms.CreateParams { Style = unchecked((int)0x80000000), ExStyle = 0x80 }); // WS_POPUP, WS_EX_TOOLWINDOW
    }

    private sealed class StripWindow : Forms.NativeWindow
    {
        private readonly TaskbarStrip _owner;
        private readonly IntPtr _taskbar;
        private readonly List<Rectangle> _buttons = new();
        private int _hover = -1, _pressed = -1;
        private bool _tracking;
        private float _scale = 1f;
        private bool _light;

        public int Width { get; private set; } = 1;
        /// <summary>Width including the elapsed time while recording.</summary>
        public int FullWidth { get; private set; } = 1;
        /// <summary>Leave out the elapsed time (not enough room on the taskbar).</summary>
        public bool Compact { get; set; }
        public int Height { get; private set; } = 1;

        public StripWindow(TaskbarStrip owner, IntPtr taskbar)
        {
            _owner = owner;
            _taskbar = taskbar;
            CreateHandle(new Forms.CreateParams
            {
                Parent = taskbar,
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                Width = 1,
                Height = 1,
            });
            _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); if (_hover >= 0) ShowTip(_hover); };
        }

        public int Scale(int px) => (int)Math.Round(px * _scale);

        public void Hide() => ShowWindow(Handle, 0);

        public void Show(int x, int y)
        {
            // HWND_TOP every time: the taskbar's own content layer must not cover the strip.
            SetWindowPos(Handle, IntPtr.Zero, x, y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            bool light = QuickButtons.LightTaskbar();
            if (light != _light || Math.Abs(GetDpiForWindow(_taskbar) / 96f - _scale) > 0.01f) Render();
        }

        // ------------------------------------------------------------ drawing

        public void Render()
        {
            _scale = Math.Max(1f, GetDpiForWindow(_taskbar) / 96f);
            _light = QuickButtons.LightTaskbar();
            bool recording, replayOn, soundOnly; string elapsed, length;
            lock (_owner._lock)
            {
                recording = _owner._recording; replayOn = _owner._replayOn; soundOnly = _owner._soundOnly;
                elapsed = _owner._elapsed; length = _owner._replayLength;
            }

            GetWindowRect(_taskbar, out var tb);
            int h = Math.Min(Scale(40), Math.Max(Scale(24), tb.Height - Scale(8)));
            int btn = Scale(28), gap = Scale(2), icon = Scale(18); // compact: the gap between app buttons and tray is often small
            using var font = new Font("Segoe UI", 11f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

            int timeWidth = 0;
            if (recording && elapsed.Length > 0)
                using (var measure = Graphics.FromHwnd(IntPtr.Zero))
                    timeWidth = (int)Math.Ceiling(measure.MeasureString(elapsed, font, PointF.Empty, StringFormat.GenericTypographic).Width) + Scale(6);
            FullWidth = 3 * btn + 2 * gap + timeWidth;
            if (Compact) timeWidth = 0;

            _buttons.Clear();
            int x = 0;
            _buttons.Add(new Rectangle(x, 0, btn + timeWidth, h)); x += btn + timeWidth + gap;
            _buttons.Add(new Rectangle(x, 0, btn, h)); x += btn + gap;
            _buttons.Add(new Rectangle(x, 0, btn, h)); x += btn;
            Width = x;
            Height = h;

            using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType would break the transparency
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // almost invisible, but keeps the whole strip clickable

                for (int i = 0; i < _buttons.Count; i++)
                {
                    var r = _buttons[i];
                    if (i == _hover || i == _pressed)
                    {
                        int alpha = i == _pressed ? 40 : 24;
                        var back = _light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha + 6, 255, 255, 255);
                        using var path = Rounded(r.X + Scale(1), r.Y + Scale(2), r.Width - Scale(2), r.Height - Scale(4), Scale(4));
                        using var b = new SolidBrush(back);
                        g.FillPath(b, path);
                    }
                    bool disabled = i > 0 && soundOnly;
                    var kind = i switch
                    {
                        0 => recording ? QuickButtons.Kind.Stop : QuickButtons.Kind.Record,
                        1 => QuickButtons.Kind.Capture,
                        _ => replayOn ? QuickButtons.Kind.ReplayOn : QuickButtons.Kind.ReplayOff,
                    };
                    using var img = QuickButtons.Draw(kind, icon, _light);
                    int ix = r.X + (btn - icon) / 2, iy = r.Y + (r.Height - icon) / 2;
                    if (disabled)
                    {
                        using var attrs = new System.Drawing.Imaging.ImageAttributes();
                        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
                        g.DrawImage(img, new Rectangle(ix, iy, icon, icon), 0, 0, icon, icon, GraphicsUnit.Pixel, attrs);
                    }
                    else g.DrawImage(img, ix, iy, icon, icon);

                    if (i == 0 && timeWidth > 0)
                    {
                        var fore = _light ? Color.FromArgb(32, 33, 38) : Color.FromArgb(236, 237, 242);
                        using var tb2 = new SolidBrush(fore);
                        var size = g.MeasureString(elapsed, font, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString(elapsed, font, tb2, r.X + btn - Scale(4), r.Y + (r.Height - size.Height) / 2f, StringFormat.GenericTypographic);
                    }
                }
            }
            Push(bmp);
            UpdateTooltips(recording, replayOn, length, elapsed);
        }

        private void Push(Bitmap bmp) => PushLayered(Handle, bmp, null);


        // ------------------------------------------------------------ tooltip

        private string[] _tips = { "", "", "" };
        private TipWindow? _tipWindow;
        private readonly Forms.Timer _tipTimer = new() { Interval = 450 };
        private int _tipFor = -1;

        private void UpdateTooltips(bool recording, bool replayOn, string length, string elapsed)
        {
            _tips = new[]
            {
                recording ? $"녹화 중 {elapsed} — 클릭하면 중지" : "녹화 시작",
                "화면 캡처",
                replayOn ? $"리플레이 켜짐 — 클릭하면 최근 {length} 저장" : "리플레이 켜기",
            };
            if (_tipFor >= 0 && _tipWindow != null) ShowTip(_tipFor); // live text (elapsed time)
        }

        private void HoverChanged()
        {
            HideTip();
            if (_hover >= 0) { _tipTimer.Stop(); _tipTimer.Start(); }
        }

        private void ShowTip(int index)
        {
            if (index < 0 || index >= _buttons.Count) return;
            _tipWindow ??= new TipWindow();
            GetWindowRect(Handle, out var me);
            var b = _buttons[index];
            _tipWindow.Show(_tips[index] + "\n오른쪽 클릭: 메뉴", me.Left + b.X + b.Width / 2, me.Top - Scale(8), _scale, _light);
            _tipFor = index;
        }

        private void HideTip()
        {
            _tipTimer.Stop();
            _tipFor = -1;
            _tipWindow?.Hide();
        }

        // ------------------------------------------------------------ input

        private int HitTest(IntPtr lParam)
        {
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            return _buttons.FindIndex(b => b.Contains(x, y));
        }

        protected override void WndProc(ref Forms.Message m)
        {
            switch (m.Msg)
            {
                case 0x0021: // WM_MOUSEACTIVATE: don't take focus from whatever the user is doing
                    m.Result = (IntPtr)3; // MA_NOACTIVATE
                    return;
                case 0x0200: // WM_MOUSEMOVE
                {
                    if (!_tracking)
                    {
                        var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = Handle }; // TME_LEAVE
                        _tracking = TrackMouseEvent(ref tme);
                    }
                    int hit = HitTest(m.LParam);
                    if (hit != _hover) { _hover = hit; Render(); HoverChanged(); }
                    break;
                }
                case 0x02A3: // WM_MOUSELEAVE
                    _tracking = false;
                    if (_hover != -1 || _pressed != -1) { _hover = _pressed = -1; Render(); }
                    HideTip();
                    break;
                case 0x0201: // WM_LBUTTONDOWN
                    HideTip();
                    _pressed = HitTest(m.LParam);
                    Render();
                    return;
                case 0x0202: // WM_LBUTTONUP
                {
                    int hit = HitTest(m.LParam);
                    bool click = hit >= 0 && hit == _pressed;
                    _pressed = -1;
                    Render();
                    if (click) _owner.Click(hit);
                    return;
                }
                case 0x0205: // WM_RBUTTONUP
                    GetCursorPos(out var pt);
                    _owner.ShowMenu(pt.X, pt.Y);
                    return;
                case 0x0002: // WM_DESTROY
                    _tipTimer.Dispose();
                    _tipWindow?.DestroyHandle();
                    break;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Puts a 32-bit bitmap on a layered window (per-pixel alpha); optionally moves it (screen/parent coordinates).</summary>
    private static void PushLayered(IntPtr hwnd, Bitmap bmp, POINT? position)
    {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                bool ok;
                if (position is POINT pos)
                {
                    var dst = pos;
                    ok = UpdateLayeredWindowAt(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
                }
                else
                {
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, bmp.Width, bmp.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                    ok = UpdateLayeredWindow(hwnd, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2);
                }
                if (!ok)
                    Log.Warn($"작업 표시줄 버튼 그리기 실패: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
    }

    /// <summary>Small tooltip above the strip (drawn by us: the system tooltip does not work for a window inside the taskbar).</summary>
    private sealed class TipWindow : Forms.NativeWindow
    {
        public TipWindow() => CreateHandle(new Forms.CreateParams
        {
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | 0x20, // + WS_EX_TRANSPARENT
        });

        /// <param name="centerX">Screen x the tip is centred on.</param>
        /// <param name="bottom">Screen y of the tip's bottom edge.</param>
        public void Show(string text, int centerX, int bottom, float scale, bool light)
        {
            using var font = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            SizeF measured;
            using (var g0 = Graphics.FromHwnd(IntPtr.Zero)) measured = g0.MeasureString(text, font);
            int padX = (int)(10 * scale), padY = (int)(6 * scale);
            int w = (int)Math.Ceiling(measured.Width) + padX * 2, h = (int)Math.Ceiling(measured.Height) + padY * 2;

            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var back = light ? Color.FromArgb(250, 249, 249, 251) : Color.FromArgb(250, 44, 45, 50);
                var border = light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255);
                var fore = light ? Color.FromArgb(28, 28, 32) : Color.FromArgb(236, 237, 242);
                using var path = Rounded(0.5f, 0.5f, w - 1, h - 1, 5 * scale);
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
                using var tb = new SolidBrush(fore);
                g.DrawString(text, font, tb, padX, padY);
            }

            // Keep it on the screen that holds the taskbar.
            var screen = Forms.Screen.FromPoint(new Point(centerX, bottom));
            int x = Math.Clamp(centerX - w / 2, screen.Bounds.Left, screen.Bounds.Right - w);
            int y = Math.Max(screen.Bounds.Top, bottom - h);
            PushLayered(Handle, bmp, new POINT { X = x, Y = y });
            SetWindowPos(Handle, (IntPtr)(-1), x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW); // HWND_TOPMOST
        }

        public void Hide() => ShowWindow(Handle, 0);
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

    // ---------------------------------------------------------------- Win32

    private const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UpdateLayeredWindow")]
    private static extern bool UpdateLayeredWindowAt(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
