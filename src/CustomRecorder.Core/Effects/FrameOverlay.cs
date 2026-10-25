using System.Numerics;
using System.Runtime.InteropServices;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWFactoryType = Vortice.DirectWrite.FactoryType;

namespace CustomRecorder.Core.Effects;

public enum WatermarkPosition { TopLeft, TopRight, BottomLeft, BottomRight, Center }
public enum WebcamShape { Circle, Rounded }

/// <summary>Effects drawn onto every recorded frame.</summary>
public sealed record OverlayOptions(
    bool CursorHighlight = false,
    bool ClickEffect = false,
    string? WatermarkText = null,
    WatermarkPosition WatermarkPosition = WatermarkPosition.BottomRight,
    float WatermarkOpacity = 0.6f)
{
    public KeyDisplayMode KeyDisplay { get; init; } = KeyDisplayMode.Off;

    /// <summary>Picture-in-picture camera. Null = off; empty string = first camera.</summary>
    public string? WebcamId { get; init; }
    public WatermarkPosition WebcamPosition { get; init; } = WatermarkPosition.BottomRight;
    /// <summary>Camera height as a fraction of the frame height.</summary>
    public float WebcamSize { get; init; } = 0.25f;
    public WebcamShape WebcamShape { get; init; } = WebcamShape.Circle;

    public bool Any => CursorHighlight || ClickEffect || !string.IsNullOrWhiteSpace(WatermarkText)
                       || KeyDisplay != KeyDisplayMode.Off || WebcamId != null;
}

/// <summary>
/// Draws cursor highlight, click ripples and a text watermark straight onto the encoder's frame texture with Direct2D,
/// sharing the capture D3D11 device (no CPU copies). Positions are given in frame pixels.
/// </summary>
public sealed class FrameOverlay : IDisposable
{
    private const long RippleDuration = 4_500_000; // 450 ms

    private readonly OverlayOptions _options;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _dc;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly IDWriteFactory? _dwrite;
    private readonly IDWriteTextLayout? _watermark;
    private readonly float _scale;
    private readonly int _width, _height;
    private readonly List<(long Time, Vector2 Pos, bool Right)> _clicks = new();
    private readonly List<KeyPress> _keys = new();
    private readonly IDWriteTextFormat? _keyFormat;
    private readonly WebcamSource? _webcam;
    private ID2D1Bitmap1? _camBitmap;
    private ID2D1BitmapBrush1? _camBrush;
    private byte[] _camPixels = Array.Empty<byte>();
    private int _camVersion = -1;

    private sealed class KeyPress { public long Time; public string Text = ""; public bool Plain; }
    private const long KeyShow = 15_000_000, KeyFade = 4_000_000; // visible 1.5 s, then fades for 0.4 s

    /// <param name="scale">Frame pixels per captured pixel (output size / capture size), so effects keep their on-screen size.</param>
    public FrameOverlay(D3DContext d3d, OverlayOptions options, int width, int height, float scale)
    {
        _options = options;
        _width = width;
        _height = height;
        _scale = scale;
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.MultiThreaded, DebugLevel.None);
        using (var dxgi = d3d.Device.QueryInterface<IDXGIDevice>())
            _device = _factory.CreateDevice(dxgi);
        _dc = _device.CreateDeviceContext(DeviceContextOptions.None);
        _dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _brush = _dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1), null);

        if (!string.IsNullOrWhiteSpace(options.WatermarkText) || options.KeyDisplay != KeyDisplayMode.Off)
            _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(DWFactoryType.Shared);
        if (options.KeyDisplay != KeyDisplayMode.Off)
            _keyFormat = _dwrite!.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, Math.Max(13f, height * 0.028f));
        if (options.WebcamId != null)
        {
            try { _webcam = new WebcamSource(options.WebcamId.Length == 0 ? null : options.WebcamId); }
            catch (Exception ex) { Log.Warn($"웹캠을 열 수 없습니다: {ex.Message}"); }
        }

        if (!string.IsNullOrWhiteSpace(options.WatermarkText))
        {
            float size = Math.Max(12f, height * 0.032f);
            using var format = _dwrite.CreateTextFormat("Malgun Gothic", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, size);
            _watermark = _dwrite.CreateTextLayout(options.WatermarkText!.Trim(), format, width * 0.9f, height * 0.5f);
        }
    }

    public bool Active => _options.Any;

    /// <summary>Key press shown at the bottom of the frame; plain typed characters are joined into one bubble.</summary>
    public void AddKey(long time, string text, bool plain)
    {
        lock (_keys)
        {
            var last = _keys.Count > 0 ? _keys[^1] : null;
            if (plain && last is { Plain: true } && time - last.Time < 10_000_000 && last.Text.Length < 24)
            {
                last.Text += text == "Space" ? " " : text;
                last.Time = time;
                return;
            }
            _keys.Add(new KeyPress { Time = time, Text = text, Plain = plain });
            if (_keys.Count > 3) _keys.RemoveAt(0);
        }
    }

    public void AddClick(long time, Vector2 pos, bool right)
    {
        if (!_options.ClickEffect) return;
        lock (_clicks) _clicks.Add((time, pos, right));
    }

    /// <summary>Draws onto <paramref name="target"/> (B8G8R8A8, render-target bound). Caller holds the D3D context lock.</summary>
    public void Draw(ID3D11Texture2D target, long time, Vector2? cursor)
    {
        using var surface = target.QueryInterface<IDXGISurface>();
        var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        using var bitmap = _dc.CreateBitmapFromDxgiSurface(surface, props);
        _dc.Target = bitmap;
        _dc.BeginDraw();
        try
        {
            if (_webcam != null) DrawWebcam();

            if (_options.CursorHighlight && cursor is { } c)
            {
                float r = 30 * _scale;
                Fill(c, r, new Color4(1f, 0.86f, 0.15f, 0.32f));
                Stroke(c, r, new Color4(1f, 0.86f, 0.15f, 0.65f), 2 * _scale);
            }

            if (_options.ClickEffect)
            {
                List<(long Time, Vector2 Pos, bool Right)> clicks;
                lock (_clicks)
                {
                    _clicks.RemoveAll(k => time - k.Time > RippleDuration);
                    clicks = _clicks.Where(k => k.Time <= time).ToList();
                }
                foreach (var (t, pos, right) in clicks)
                {
                    float p = (time - t) / (float)RippleDuration;            // 0 → 1
                    float ease = 1 - (1 - p) * (1 - p);
                    var (cr, cg, cb) = right ? (1f, 0.35f, 0.3f) : (0.3f, 0.6f, 1f);
                    Fill(pos, (10 + 8 * ease) * _scale, new Color4(cr, cg, cb, 0.45f * (1 - p)));
                    Stroke(pos, (12 + 34 * ease) * _scale, new Color4(cr, cg, cb, 0.9f * (1 - p)), 3 * _scale);
                }
            }

            if (_watermark != null) DrawWatermark();
            if (_keyFormat != null) DrawKeys(time);
        }
        finally
        {
            var hr = _dc.EndDraw();
            _dc.Target = null;
            if (hr.Failure) Log.Warn($"효과 그리기 실패: 0x{hr.Code:X8}");
        }
    }

    private void DrawWebcam()
    {
        var cam = _webcam!;
        if (_camPixels.Length != cam.Width * cam.Height * 4) _camPixels = new byte[cam.Width * cam.Height * 4];
        if (cam.TryCopyFrame(ref _camVersion, _camPixels))
        {
            if (_camBitmap == null)
            {
                var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.None);
                _camBitmap = _dc.CreateBitmap(new SizeI(cam.Width, cam.Height), IntPtr.Zero, 0, props);
                _camBrush = _dc.CreateBitmapBrush(_camBitmap, new BitmapBrushProperties1 { ExtendModeX = ExtendMode.Clamp, ExtendModeY = ExtendMode.Clamp, InterpolationMode = InterpolationMode.Linear }, null);
            }
            _camBitmap.CopyFromMemory(_camPixels, (uint)(cam.Width * 4));
        }
        if (_camBrush == null) return; // no frame yet

        bool circle = _options.WebcamShape == WebcamShape.Circle;
        float h = _height * Math.Clamp(_options.WebcamSize, 0.1f, 0.5f);
        float w = circle ? h : h * cam.Width / cam.Height;
        float m = _height * 0.03f;
        float x = _options.WebcamPosition is WatermarkPosition.TopLeft or WatermarkPosition.BottomLeft ? m : _width - w - m;
        float y = _options.WebcamPosition is WatermarkPosition.TopLeft or WatermarkPosition.TopRight ? m : _height - h - m;
        if (_options.WebcamPosition == WatermarkPosition.Center) { x = (_width - w) / 2; y = (_height - h) / 2; }

        // Map the camera image so it covers the shape (center crop for the circle).
        float s = circle ? h / Math.Min(cam.Width, cam.Height) : h / cam.Height;
        float ox = x + w / 2 - cam.Width * s / 2, oy = y + h / 2 - cam.Height * s / 2;
        _camBrush.Transform = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(ox, oy);

        var center = new Vector2(x + w / 2, y + h / 2);
        _brush.Color = new Color4(0, 0, 0, 0.35f);
        if (circle)
        {
            _dc.FillEllipse(new Ellipse(center + new Vector2(0, 3 * _scale), w / 2 + 2, h / 2 + 2), _brush); // soft shadow
            _dc.FillEllipse(new Ellipse(center, w / 2, h / 2), _camBrush);
            Stroke(center, w / 2, new Color4(1, 1, 1, 0.9f), Math.Max(2f, 3 * _scale));
        }
        else
        {
            float r = h * 0.08f;
            var rect = new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r);
            _dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y + 3 * _scale, w, h), r, r), _brush);
            _dc.FillRoundedRectangle(rect, _camBrush);
            _brush.Color = new Color4(1, 1, 1, 0.9f);
            _dc.DrawRoundedRectangle(rect, _brush, Math.Max(2f, 3 * _scale));
        }
    }

    private void DrawKeys(long time)
    {
        List<KeyPress> keys;
        lock (_keys)
        {
            _keys.RemoveAll(k => time - k.Time > KeyShow + KeyFade);
            keys = _keys.Where(k => k.Time <= time).Select(k => new KeyPress { Time = k.Time, Text = k.Text, Plain = k.Plain }).ToList();
        }
        float baseY = _height * 0.90f;
        float gap = _height * 0.012f;
        for (int i = keys.Count - 1; i >= 0; i--)
        {
            var k = keys[i];
            long age = time - k.Time;
            float a = age <= KeyShow ? 1f : Math.Max(0f, 1f - (age - KeyShow) / (float)KeyFade);
            using var layout = _dwrite!.CreateTextLayout(k.Text, _keyFormat!, _width * 0.8f, _height * 0.2f);
            var m = layout.Metrics;
            float padX = _height * 0.018f, padY = _height * 0.009f;
            float bw = m.Width + padX * 2, bh = m.Height + padY * 2;
            float bx = (_width - bw) / 2, by = baseY - bh;
            var r = new RoundedRectangle(new System.Drawing.RectangleF(bx, by, bw, bh), bh * 0.28f, bh * 0.28f);
            _brush.Color = new Color4(0.07f, 0.07f, 0.09f, 0.78f * a);
            _dc.FillRoundedRectangle(r, _brush);
            _brush.Color = new Color4(1, 1, 1, 0.16f * a);
            _dc.DrawRoundedRectangle(r, _brush, 1.2f);
            _brush.Color = new Color4(1, 1, 1, a);
            _dc.DrawTextLayout(new Vector2(bx + padX - m.Left, by + padY), layout, _brush);
            baseY = by - gap;
        }
    }

    private void DrawWatermark()
    {
        var m = _watermark!.Metrics;
        float margin = _height * 0.025f;
        float x = _options.WatermarkPosition switch
        {
            WatermarkPosition.TopLeft or WatermarkPosition.BottomLeft => margin,
            WatermarkPosition.Center => (_width - m.Width) / 2,
            _ => _width - m.Width - margin,
        };
        float y = _options.WatermarkPosition switch
        {
            WatermarkPosition.TopLeft or WatermarkPosition.TopRight => margin,
            WatermarkPosition.Center => (_height - m.Height) / 2,
            _ => _height - m.Height - margin,
        };
        x -= m.Left; // layout box may start before the first glyph
        float a = Math.Clamp(_options.WatermarkOpacity, 0.05f, 1f);
        float shadow = Math.Max(1f, _height / 540f);
        _brush.Color = new Color4(0, 0, 0, a * 0.55f);
        _dc.DrawTextLayout(new Vector2(x + shadow, y + shadow), _watermark, _brush);
        _brush.Color = new Color4(1, 1, 1, a);
        _dc.DrawTextLayout(new Vector2(x, y), _watermark, _brush);
    }

    private void Fill(Vector2 c, float r, Color4 color)
    {
        _brush.Color = color;
        _dc.FillEllipse(new Ellipse(c, r, r), _brush);
    }

    private void Stroke(Vector2 c, float r, Color4 color, float width)
    {
        _brush.Color = color;
        _dc.DrawEllipse(new Ellipse(c, r, r), _brush, width);
    }

    public void Dispose()
    {
        _webcam?.Dispose();
        _camBrush?.Dispose();
        _camBitmap?.Dispose();
        _keyFormat?.Dispose();
        _watermark?.Dispose();
        _dwrite?.Dispose();
        _brush.Dispose();
        _dc.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }
}

/// <summary>
/// Global low-level mouse hook on its own thread: reports button presses anywhere on the desktop, including very short
/// clicks that polling would miss. Callbacks run on the hook thread and must stay fast.
/// </summary>
internal sealed class MouseClickHook : IDisposable
{
    private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private readonly Action<int, int, bool> _onPress; // screen x, y, right button
    private readonly HookProc _proc;                   // kept alive for the native hook
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hook;

    public MouseClickHook(Action<int, int, bool> onPress)
    {
        _onPress = onPress;
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Warn($"마우스 후크 설치 실패: {Marshal.GetLastWin32Error()}");
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        }) { Name = "MouseHook", IsBackground = true };
        _thread.Start();
        ready.Wait(2000);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                try { _onPress(info.X, info.Y, msg == WM_RBUTTONDOWN); } catch { /* never break input */ }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
