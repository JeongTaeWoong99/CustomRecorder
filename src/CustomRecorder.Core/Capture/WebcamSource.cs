using CustomRecorder.Core.Util;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Capture;

public sealed record WebcamDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Reads frames from a webcam with the Media Foundation source reader (converted to 32-bit BGRA by MF's video
/// processor) on a background thread, and keeps only the newest frame for the overlay.
/// </summary>
public sealed class WebcamSource : IDisposable
{
    private static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static readonly Guid SourceTypeVidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    private static readonly Guid SymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    private readonly object _gate = new();
    private readonly IMFMediaSource _source;
    private readonly IMFSourceReader _reader;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private byte[] _frame = Array.Empty<byte>();
    private int _version;

    public int Width { get; }
    public int Height { get; }

    public static IReadOnlyList<WebcamDevice> List()
    {
        var list = new List<WebcamDevice>();
        try
        {
            MediaFactory.MFStartup(true).CheckError();
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.Set(SourceType, SourceTypeVidcap).CheckError();
            foreach (var a in MediaFactory.MFEnumDeviceSources(attrs))
            {
                using (a) list.Add(new WebcamDevice(a.GetAllocatedString(SymbolicLink), a.GetAllocatedString(FriendlyName)));
            }
        }
        catch (Exception ex) { Log.Warn($"웹캠 목록 조회 실패: {ex.Message}"); }
        return list;
    }

    /// <param name="deviceId">Symbolic link from <see cref="List"/>; null = first camera.</param>
    public WebcamSource(string? deviceId)
    {
        MediaFactory.MFStartup(true).CheckError();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(SourceType, SourceTypeVidcap).CheckError();
        IMFActivate? chosen = null;
        foreach (var a in MediaFactory.MFEnumDeviceSources(attrs))
        {
            if (chosen == null && (deviceId == null || a.GetAllocatedString(SymbolicLink) == deviceId)) chosen = a;
            else a.Dispose();
        }
        if (chosen == null) throw new InvalidOperationException("웹캠을 찾을 수 없습니다.");

        string name = chosen.GetAllocatedString(FriendlyName);
        _source = chosen.ActivateObject<IMFMediaSource>();
        chosen.Dispose();

        using (var readerAttrs = MediaFactory.MFCreateAttributes(1))
        {
            readerAttrs.Set(EnableVideoProcessing, 1u).CheckError();
            _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, readerAttrs);
        }

        // Pick the largest native mode up to 1280 px wide (enough for a picture-in-picture).
        IMFMediaType? best = null;
        long bestPixels = 0;
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _reader.GetNativeMediaType(0, i); }
            catch (SharpGenException) { break; }
            var (w, h) = FrameSize(t);
            long px = (long)w * h;
            if (w <= 1280 && px > bestPixels) { best?.Dispose(); best = t; bestPixels = px; }
            else t.Dispose();
        }
        if (best != null)
        {
            try { _reader.SetCurrentMediaType(0, best); } catch (Exception ex) { Log.Warn($"웹캠 해상도 선택 실패: {ex.Message}"); }
            best.Dispose();
        }

        using (var native = _reader.GetCurrentMediaType(0))
        using (var rgb = MediaFactory.MFCreateMediaType())
        {
            var (w, h) = FrameSize(native);
            rgb.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            rgb.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            MediaFactory.MFSetAttributeSize(rgb, MediaTypeAttributeKeys.FrameSize, (uint)w, (uint)h).CheckError();
            _reader.SetCurrentMediaType(0, rgb);
        }
        using (var current = _reader.GetCurrentMediaType(0))
        {
            (Width, Height) = FrameSize(current);
            try { _bottomUp = (int)current.GetUInt32(DefaultStride) < 0; } catch { _bottomUp = false; }
        }

        _thread = new Thread(ReadLoop) { Name = "Webcam", IsBackground = true };
        _thread.Start();
        Log.Info($"웹캠 시작: {name} {Width}x{Height}{(_bottomUp ? " (bottom-up)" : "")}");
    }

    private readonly bool _bottomUp;

    private static (int W, int H) FrameSize(IMFMediaType type)
    {
        ulong packed = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        return ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
    }

    private void ReadLoop()
    {
        int stride = Width * 4;
        var tmp = new byte[stride * Height];
        while (_running)
        {
            try
            {
                using var sample = _reader.ReadSample(0, SourceReaderControlFlag.None, out _, out var flags, out _);
                if ((flags & (SourceReaderFlag.Error | SourceReaderFlag.EndOfStream)) != 0) break;
                if (sample == null) continue;
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var ptr, out _, out int length);
                try
                {
                    int bytes = Math.Min(length, tmp.Length);
                    if (_bottomUp)
                    {
                        for (int y = 0; y < Height; y++)
                            System.Runtime.InteropServices.Marshal.Copy(ptr + (Height - 1 - y) * stride, tmp, y * stride, stride);
                    }
                    else System.Runtime.InteropServices.Marshal.Copy(ptr, tmp, 0, bytes);
                }
                finally { buffer.Unlock(); }

                // RGB32 leaves the alpha byte undefined: make the frame opaque.
                for (int i = 3; i < tmp.Length; i += 4) tmp[i] = 255;
                lock (_gate)
                {
                    if (_frame.Length != tmp.Length) _frame = new byte[tmp.Length];
                    Buffer.BlockCopy(tmp, 0, _frame, 0, tmp.Length);
                    _version++;
                }
            }
            catch (Exception ex)
            {
                if (_running) Log.Warn($"웹캠 읽기 오류: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>Copies the newest frame into <paramref name="dest"/> if it changed since <paramref name="version"/>.</summary>
    public bool TryCopyFrame(ref int version, byte[] dest)
    {
        lock (_gate)
        {
            if (_version == version || _frame.Length != dest.Length) return false;
            Buffer.BlockCopy(_frame, 0, dest, 0, dest.Length);
            version = _version;
            return true;
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1500);
        _reader.Dispose();
        try { _source.Shutdown(); } catch { }
        _source.Dispose();
    }
}
