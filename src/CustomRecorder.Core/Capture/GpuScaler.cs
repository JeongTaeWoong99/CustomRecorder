using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CustomRecorder.Core.Capture;

/// <summary>
/// Resizes a BGRA texture on the GPU with the D3D11 video processor (the same fixed-function scaler video players use),
/// so the encoder always receives frames at its own size. Caller must hold <see cref="D3DContext.ContextLock"/>.
/// </summary>
public sealed class GpuScaler : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private ID3D11Texture2D? _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;

    public int OutputWidth { get; }
    public int OutputHeight { get; }

    public GpuScaler(D3DContext d3d, int inWidth, int inHeight, int outWidth, int outHeight, int fps)
    {
        OutputWidth = outWidth;
        OutputHeight = outHeight;
        _videoDevice = d3d.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = d3d.Context.QueryInterface<ID3D11VideoContext>();
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)inWidth,
            InputHeight = (uint)inHeight,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outWidth,
            OutputHeight = (uint)outHeight,
            Usage = VideoUsage.OptimalQuality,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
    }

    /// <summary>Scales <paramref name="source"/> into <paramref name="destination"/> (needs RenderTarget binding).</summary>
    public void Scale(ID3D11Texture2D source, ID3D11Texture2D destination)
    {
        if (!ReferenceEquals(source, _inputTexture))
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
            _inputTexture = source;
        }

        using var outputView = _videoDevice.CreateVideoProcessorOutputView(destination, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });
        var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = _inputView } };
        _videoContext.VideoProcessorBlt(_processor, outputView, 0, 1, streams).CheckError();
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
