using CustomRecorder.Core.Util;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Encoding;

/// <summary>Decodes part of a video with Media Foundation and saves it as an animated GIF.</summary>
public static class VideoToGif
{
    private static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public static void Convert(string input, string output, long start100ns, long end100ns, int maxWidth, int fps, IProgress<double>? progress = null)
    {
        MediaFactory.MFStartup(true).CheckError();
        var (readable, cleanup) = Mp4Trim.Readable(input);
        try { ConvertCore(readable, output, start100ns, end100ns, maxWidth, fps, progress); }
        finally { cleanup(); }
    }

    private static void ConvertCore(string input, string output, long start100ns, long end100ns, int maxWidth, int fps, IProgress<double>? progress)
    {
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(EnableVideoProcessing, 1u).CheckError();
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(input, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        // NV12 straight from the decoder: no colour conversion for the frames the GIF skips (most of them at 60 fps).
        // Only the kept frames are converted, already at GIF size. RGB32 via the MF video processor is the fallback.
        bool nv12 = TrySetOutput(reader, VideoFormatGuids.NV12);
        if (!nv12 && !TrySetOutput(reader, VideoFormatGuids.Rgb32))
            throw new InvalidOperationException("이 영상은 GIF로 변환할 수 없는 형식입니다.");
        int srcW, srcH; bool bottomUp;
        using (var t = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            ulong size = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            srcW = (int)(size >> 32); srcH = (int)(size & 0xFFFFFFFF);
            try { bottomUp = (int)t.GetUInt32(DefaultStride) < 0; } catch { bottomUp = false; }
        }
        double scale = Math.Min(1.0, maxWidth / (double)srcW);
        int w = Math.Max(2, (int)(srcW * scale)), h = Math.Max(2, (int)(srcH * scale));

        reader.SetCurrentPosition(start100ns);
        long step = 10_000_000L / fps;
        long nextTime = start100ns;
        var src = nv12 ? Array.Empty<byte>() : new byte[srcW * srcH * 4];
        var dst = new byte[w * h * 4];
        int frames = 0;

        using var file = File.Create(output);
        using var gif = new GifWriter(file, w, h);
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long t);
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0 || t >= end100ns) break;
            if (sample == null || t < nextTime) continue;

            using (var buffer = sample.ConvertToContiguousBuffer())
            {
                buffer.Lock(out var ptr, out _, out int len);
                try
                {
                    if (nv12)
                    {
                        if (src.Length < len) src = new byte[len];
                        System.Runtime.InteropServices.Marshal.Copy(ptr, src, 0, len);
                    }
                    else
                    {
                    int stride = srcW * 4;
                    if (bottomUp)
                        for (int y = 0; y < srcH; y++)
                            System.Runtime.InteropServices.Marshal.Copy(ptr + (srcH - 1 - y) * stride, src, y * stride, stride);
                    else System.Runtime.InteropServices.Marshal.Copy(ptr, src, 0, Math.Min(len, src.Length));
                    }
                    if (nv12) DownscaleNv12(src, len, srcW, srcH, dst, w, h);
                }
                finally { buffer.Unlock(); }
            }
            if (!nv12) Downscale(src, srcW, srcH, dst, w, h);
            // GIF delays are whole centiseconds: spread the rounding (15 fps → 7,7,6,…) so the length stays right.
            long cs0 = (frames * step + 50_000) / 100_000, cs1 = ((frames + 1) * step + 50_000) / 100_000;
            gif.AddFrame(dst, (int)(cs1 - cs0) * 10);
            frames++;
            while (nextTime <= t) nextTime += step;
            progress?.Report(Math.Clamp((t - start100ns) / (double)(end100ns - start100ns), 0, 1));
        }
        Log.Info($"GIF 저장: {output} {w}x{h} {fps}fps {frames}프레임");
        if (frames == 0) throw new InvalidOperationException("선택한 구간에서 프레임을 읽지 못했습니다.");
    }

    private static bool TrySetOutput(IMFSourceReader reader, Guid subtype)
    {
        try
        {
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            return true;
        }
        catch (SharpGenException) { return false; }
    }

    /// <summary>
    /// NV12 (BT.709, limited range) → BGRA at the target size: area-averaged luma, chroma averaged over the same block.
    /// The plane height is derived from the buffer length because decoders may pad it (1080 → 1088).
    /// </summary>
    internal static void DownscaleNv12(byte[] src, int len, int sw, int sh, byte[] dst, int dw, int dh)
    {
        int stride = sw;
        int planeH = len * 2 / (3 * stride);
        if (planeH < sh) { planeH = sh; } // unexpected layout: assume tight packing
        int uvBase = stride * planeH;
        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
            int cy0 = y0 >> 1, cy1 = Math.Max(cy0 + 1, (y1 + 1) >> 1);
            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                int ys = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int o = yy * stride + x0;
                    for (int xx = x0; xx < x1; xx++) { ys += src[o++]; n++; }
                }
                int cx0 = x0 >> 1, cx1 = Math.Max(cx0 + 1, (x1 + 1) >> 1);
                int us = 0, vs = 0, m = 0;
                for (int cy = cy0; cy < cy1; cy++)
                {
                    int o = uvBase + cy * stride + cx0 * 2;
                    for (int cx = cx0; cx < cx1; cx++, o += 2) { us += src[o]; vs += src[o + 1]; m++; }
                }
                double Y = 1.164383 * (ys / (double)n - 16), U = us / (double)m - 128, V = vs / (double)m - 128;
                int d = (y * dw + x) * 4;
                dst[d] = Clamp(Y + 2.112402 * U);
                dst[d + 1] = Clamp(Y - 0.213249 * U - 0.532909 * V);
                dst[d + 2] = Clamp(Y + 1.792741 * V);
                dst[d + 3] = 255;
            }
        }
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    /// <summary>Area-average downscale (sharp text, no shimmering).</summary>
    private static void Downscale(byte[] src, int sw, int sh, byte[] dst, int dw, int dh)
    {
        if (sw == dw && sh == dh) { Buffer.BlockCopy(src, 0, dst, 0, dst.Length); return; }
        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int o = (yy * sw + x0) * 4;
                    for (int xx = x0; xx < x1; xx++, o += 4) { b += src[o]; g += src[o + 1]; r += src[o + 2]; n++; }
                }
                int d = (y * dw + x) * 4;
                dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(r / n); dst[d + 3] = 255;
            }
        }
    }
}
