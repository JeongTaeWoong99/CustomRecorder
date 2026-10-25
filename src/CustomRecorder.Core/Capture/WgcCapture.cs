using System.Runtime.InteropServices;
using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

namespace CustomRecorder.Core.Capture;

/// <summary>
/// Windows.Graphics.Capture wrapper. Frames stay on the GPU; the callback receives the frame texture,
/// which is only valid for the duration of the callback.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    public delegate void FrameHandler(ID3D11Texture2D texture, int width, int height);

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = typeof(ID3D11Texture2D).GUID;

    private readonly D3DContext _d3d;
    private readonly GraphicsCaptureItem _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private volatile bool _disposed;

    public event FrameHandler? FrameArrived;

    /// <summary>Raised when the captured window closes.</summary>
    public event Action? ItemClosed;

    public SizeInt32 ItemSize => _item.Size;

    public WgcCapture(D3DContext d3d, CaptureTarget target)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("이 Windows 버전은 Windows.Graphics.Capture를 지원하지 않습니다 (Windows 10 1903 이상 필요).");

        _d3d = d3d;
        _item = target switch
        {
            MonitorTarget m => CreateForMonitor(m.Monitor.Handle),
            RegionTarget r => CreateForMonitor(r.Monitor.Handle),
            WindowTarget w => CreateForWindow(w.Window.Handle),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        _item.Closed += (_, _) => ItemClosed?.Invoke();
    }

    public void Start(bool captureCursor)
    {
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);

        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            _session.IsCursorCaptureEnabled = captureCursor;

        // Windows 11: hide the yellow capture border when the system allows it.
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception ex) { Log.Warn($"IsBorderRequired=false 실패: {ex.Message}"); }
        }

        _session.StartCapture();
        Log.Info($"WGC 시작: {_item.DisplayName} {_poolSize.Width}x{_poolSize.Height}, cursor={captureCursor}");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        bool resize = false;
        SizeInt32 newSize = default;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            newSize = frame.ContentSize;
            resize = newSize.Width != _poolSize.Width || newSize.Height != _poolSize.Height;

            var access = frame.Surface.As<Interop.IDirect3DDxgiInterfaceAccess>();
            var iid = Texture2DIid;
            var ptr = access.GetInterface(ref iid);
            using var texture = new ID3D11Texture2D(ptr);

            int w = Math.Min(newSize.Width, _poolSize.Width);
            int h = Math.Min(newSize.Height, _poolSize.Height);
            FrameArrived?.Invoke(texture, w, h);
        }
        catch (Exception ex)
        {
            Log.Error("프레임 처리 오류", ex);
        }

        if (resize && !_disposed && newSize.Width > 0 && newSize.Height > 0)
        {
            _poolSize = newSize;
            sender.Recreate(_d3d.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, newSize);
        }
    }

    private static GraphicsCaptureItem CreateForMonitor(IntPtr hmon)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
        if (_pool != null)
        {
            _pool.FrameArrived -= OnFrameArrived;
            _pool.Dispose();
        }
    }
}
