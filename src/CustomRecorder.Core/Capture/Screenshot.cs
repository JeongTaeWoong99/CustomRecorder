using CustomRecorder.Core.Util;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace CustomRecorder.Core.Capture;

/// <summary>BGRA32 pixels of one captured frame (alpha forced to opaque).</summary>
public sealed record CapturedImage(int Width, int Height, int Stride, byte[] Pixels);

public static class Screenshot
{
    /// <summary>Grabs one frame of the target via Windows.Graphics.Capture (same path as recording).</summary>
    public static async Task<CapturedImage> CaptureAsync(D3DContext d3d, CaptureTarget target, bool includeCursor, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<CapturedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new WgcCapture(d3d, target);

        var item = capture.ItemSize;
        PixelRect crop = target is RegionTarget r
            ? new PixelRect(r.Region.X - r.Monitor.Bounds.X, r.Region.Y - r.Monitor.Bounds.Y, r.Region.Width, r.Region.Height)
            : new PixelRect(0, 0, item.Width, item.Height);

        capture.FrameArrived += (frame, fw, fh) =>
        {
            if (tcs.Task.IsCompleted) return;
            try
            {
                var src = crop.Intersect(new PixelRect(0, 0, fw, fh));
                using var staging = d3d.CreateStagingTexture(src.Width, src.Height);
                byte[] pixels;
                int stride = src.Width * 4;
                lock (d3d.ContextLock)
                {
                    d3d.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, frame, 0, new Box(src.X, src.Y, 0, src.Right, src.Bottom, 1));
                    var map = d3d.Context.Map(staging, 0, MapMode.Read, MapFlags.None);
                    try
                    {
                        pixels = new byte[stride * src.Height];
                        unsafe
                        {
                            for (int y = 0; y < src.Height; y++)
                            {
                                var row = new ReadOnlySpan<byte>((byte*)map.DataPointer + y * map.RowPitch, stride);
                                row.CopyTo(pixels.AsSpan(y * stride, stride));
                            }
                        }
                    }
                    finally
                    {
                        d3d.Context.Unmap(staging, 0);
                    }
                }
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                tcs.TrySetResult(new CapturedImage(src.Width, src.Height, stride, pixels));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        capture.Start(includeCursor);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(3)));
        if (done != tcs.Task)
        {
            Log.Warn("스크린샷 시간 초과");
            throw new TimeoutException("화면을 가져오지 못했습니다.");
        }
        return await tcs.Task;
    }
}
