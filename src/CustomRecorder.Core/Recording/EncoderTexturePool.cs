using CustomRecorder.Core.Capture;
using Vortice.Direct3D11;

namespace CustomRecorder.Core.Recording;

/// <summary>Reports how far the encoder has got, so frame textures can be reused safely.</summary>
public interface IEncodeProgress
{
    /// <summary>Video frames handed to the encoder.</summary>
    long VideoSubmitted { get; }
    /// <summary>Video frames the encoder has finished with (-1 = unknown).</summary>
    long VideoEncoded { get; }
}

/// <summary>
/// Reuses the per-frame GPU textures instead of creating (and zero-filling) a new full-screen texture 60 times a second.
/// A texture goes back into use only after the encoder reports that frame as encoded, so it is never overwritten
/// while the encoder still reads it. Without progress information it simply behaves like plain allocation.
/// </summary>
internal sealed class EncoderTexturePool : IDisposable
{
    private const int SafetyMargin = 2;   // extra frames to wait after "encoded"
    private const int MaxInFlight = 16;   // beyond this, stop tracking the oldest (the encoder keeps its own reference)

    private readonly D3DContext _d3d;
    private readonly int _width, _height;
    private readonly Stack<ID3D11Texture2D> _free = new();
    private readonly Queue<(ID3D11Texture2D Texture, long Seq)> _inFlight = new();

    public long Created { get; private set; }
    public long Reused { get; private set; }

    public EncoderTexturePool(D3DContext d3d, int width, int height)
    {
        _d3d = d3d;
        _width = width;
        _height = height;
    }

    public ID3D11Texture2D Rent(IEncodeProgress? progress)
    {
        if (progress != null)
        {
            long encoded = progress.VideoEncoded;
            while (encoded >= 0 && _inFlight.Count > 0 && encoded >= _inFlight.Peek().Seq + 1 + SafetyMargin)
                _free.Push(_inFlight.Dequeue().Texture);
        }
        if (_free.Count > 0) { Reused++; return _free.Pop(); }
        Created++;
        return _d3d.CreateTexture(_width, _height, forEncoder: true);
    }

    /// <param name="seq">Zero-based index of this frame among frames submitted to the current writer.</param>
    public void Submitted(ID3D11Texture2D texture, IEncodeProgress? progress, long seq)
    {
        if (progress == null) { texture.Dispose(); return; }
        _inFlight.Enqueue((texture, seq));
        while (_inFlight.Count > MaxInFlight) _inFlight.Dequeue().Texture.Dispose();
    }

    /// <summary>The frame could not be submitted: the texture is not in use.</summary>
    public void Return(ID3D11Texture2D texture) => _free.Push(texture);

    public void Dispose()
    {
        foreach (var t in _free) t.Dispose();
        foreach (var (t, _) in _inFlight) t.Dispose();
        _free.Clear();
        _inFlight.Clear();
    }
}
