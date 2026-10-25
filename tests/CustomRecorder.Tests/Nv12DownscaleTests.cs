using CustomRecorder.Core.Encoding;
using Xunit;

public class Nv12DownscaleTests
{
    private static byte[] Solid(int w, int planeH, byte y, byte u, byte v)
    {
        var buf = new byte[w * planeH * 3 / 2];
        Array.Fill(buf, y, 0, w * planeH);
        for (int i = w * planeH; i < buf.Length; i += 2) { buf[i] = u; buf[i + 1] = v; }
        return buf;
    }

    [Theory]
    [InlineData(235, 128, 128, 255, 255, 255)] // white
    [InlineData(16, 128, 128, 0, 0, 0)]        // black
    [InlineData(63, 102, 240, 255, 0, 0)]      // BT.709 red
    [InlineData(173, 42, 26, 0, 255, 0)]       // BT.709 green
    public void Converts_bt709_limited_range(byte y, byte u, byte v, int r, int g, int b)
    {
        var src = Solid(64, 36, y, u, v);
        var dst = new byte[16 * 9 * 4];
        VideoToGif.DownscaleNv12(src, src.Length, 64, 36, dst, 16, 9);
        Assert.InRange(dst[2], r - 3, r + 3);
        Assert.InRange(dst[1], g - 3, g + 3);
        Assert.InRange(dst[0], b - 3, b + 3);
        Assert.Equal(255, dst[3]);
    }

    [Fact]
    public void Handles_padded_plane_height()
    {
        // 64x30 picture in a decoder buffer padded to 32 rows: chroma starts after 32 luma rows.
        var src = Solid(64, 32, 235, 128, 128);
        for (int i = 64 * 32; i < src.Length; i += 2) { src[i] = 102; src[i + 1] = 240; } // red chroma
        for (int i = 0; i < 64 * 32; i++) src[i] = 63;
        var dst = new byte[8 * 4 * 4];
        VideoToGif.DownscaleNv12(src, src.Length, 64, 30, dst, 8, 4);
        Assert.True(dst[2] > 240 && dst[1] < 15 && dst[0] < 15, $"got {dst[2]},{dst[1]},{dst[0]}");
    }

    [Fact]
    public void Averages_area()
    {
        // Left half black, right half white → downscaled to 2 px: one black, one white.
        var src = Solid(8, 4, 16, 128, 128);
        for (int row = 0; row < 4; row++) for (int x = 4; x < 8; x++) src[row * 8 + x] = 235;
        var dst = new byte[2 * 1 * 4];
        VideoToGif.DownscaleNv12(src, src.Length, 8, 4, dst, 2, 1);
        Assert.InRange(dst[0], 0, 2);
        Assert.InRange(dst[4], 253, 255);
    }
}
