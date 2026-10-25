using CustomRecorder.Core.Encoding;
using Xunit;

public class GifWriterTests
{
    private const int W = 64, H = 40;

    private static byte[] Frame(int boxX)
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                bool box = x >= boxX && x < boxX + 10 && y >= 10 && y < 30;
                px[o] = (byte)(box ? 255 : x / 8 * 32);   // B (8 levels)
                px[o + 1] = (byte)(box ? 80 : y / 10 * 60); // G (4 levels) — few colours, so the palette is exact
                px[o + 2] = (byte)(box ? 40 : 128);      // R
                px[o + 3] = 255;
            }
        return px;
    }

    private static byte[] Encode(params (byte[] Frame, int Ms)[] frames)
    {
        var ms = new MemoryStream();
        using (var gif = new GifWriter(ms, W, H))
            foreach (var (f, d) in frames) gif.AddFrame(f, d);
        return ms.ToArray();
    }

    [Fact]
    public void DecodedFramesMatchInput()
    {
        var frames = Enumerable.Range(0, 5).Select(i => (Frame(i * 8), 100)).ToArray();
        var decoded = MiniGifDecoder.Decode(Encode(frames));
        Assert.Equal(5, decoded.Count);
        for (int i = 0; i < 5; i++)
        {
            var expected = frames[i].Item1;
            for (int p = 0; p < W * H; p++)
            {
                // GIF stores 5 bits per channel here: compare after the same rounding.
                for (int c = 0; c < 3; c++)
                    Assert.Equal(expected[p * 4 + 2 - c] >> 3, decoded[i].Rgb[p * 3 + c] >> 3);
            }
            Assert.Equal(10, decoded[i].DelayCs);
        }
    }

    [Fact]
    public void UnchangedFramesExtendThePreviousDelay()
    {
        var f = Frame(0);
        var decoded = MiniGifDecoder.Decode(Encode((f, 100), (f, 100), (f, 100), (Frame(20), 100)));
        Assert.Equal(2, decoded.Count);
        Assert.Equal(30, decoded[0].DelayCs);
    }

    [Fact]
    public void LoopsForever() => Assert.True(MiniGifDecoder.Decode(Encode((Frame(0), 50))).Count == 1 &&
                                             Encode((Frame(0), 50)).AsSpan().IndexOf("NETSCAPE2.0"u8) > 0);
}

/// <summary>Small GIF89a decoder (LZW, local colour tables, transparency, "do not dispose") used to check the encoder.</summary>
internal static class MiniGifDecoder
{
    public sealed record Decoded(byte[] Rgb, int DelayCs);

    public static List<Decoded> Decode(byte[] g)
    {
        int w = g[6] | g[7] << 8, h = g[8] | g[9] << 8;
        var canvas = new byte[w * h * 3];
        var result = new List<Decoded>();
        int p = 13 + ((g[10] & 0x80) != 0 ? 3 * (2 << (g[10] & 7)) : 0);
        int delay = 0, transparent = -1;
        while (p < g.Length)
        {
            byte b = g[p++];
            if (b == 0x3B) break;
            if (b == 0x21)
            {
                byte label = g[p++];
                if (label == 0xF9)
                {
                    delay = g[p + 2] | g[p + 3] << 8;
                    transparent = (g[p + 1] & 1) != 0 ? g[p + 4] : -1;
                }
                while (g[p] != 0) p += g[p] + 1;
                p++;
                continue;
            }
            // image descriptor
            int x0 = g[p] | g[p + 1] << 8, y0 = g[p + 2] | g[p + 3] << 8, iw = g[p + 4] | g[p + 5] << 8, ih = g[p + 6] | g[p + 7] << 8;
            byte packed = g[p + 8];
            p += 9;
            int tableSize = 2 << (packed & 7);
            var table = g.AsSpan(p, 3 * tableSize).ToArray();
            p += 3 * tableSize;
            int minCode = g[p++];
            var data = new List<byte>();
            while (g[p] != 0) { data.AddRange(g.AsSpan(p + 1, g[p]).ToArray()); p += g[p] + 1; }
            p++;
            var indices = Lzw(data.ToArray(), minCode, iw * ih);
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] == transparent) continue;
                int dst = ((y0 + i / iw) * w + x0 + i % iw) * 3;
                Array.Copy(table, indices[i] * 3, canvas, dst, 3);
            }
            result.Add(new Decoded((byte[])canvas.Clone(), delay));
        }
        return result;
    }

    private static int[] Lzw(byte[] data, int minCode, int count)
    {
        int clear = 1 << minCode, eoi = clear + 1, size = minCode + 1, next = eoi + 1;
        var prefix = new int[4096]; var suffix = new byte[4096]; var len = new int[4096];
        for (int i = 0; i < clear; i++) { suffix[i] = (byte)i; len[i] = 1; prefix[i] = -1; }
        var output = new List<int>(count);
        int bitPos = 0, prev = -1;
        int Read()
        {
            int v = 0;
            for (int i = 0; i < size; i++, bitPos++)
                if ((data[bitPos >> 3] >> (bitPos & 7) & 1) != 0) v |= 1 << i;
            return v;
        }
        byte First(int c) { while (prefix[c] >= 0) c = prefix[c]; return suffix[c]; }
        void Emit(int c)
        {
            var tmp = new byte[len[c]];
            for (int i = len[c] - 1; i >= 0; i--) { tmp[i] = suffix[c]; c = prefix[c]; }
            foreach (var t in tmp) output.Add(t);
        }
        while (bitPos + size <= data.Length * 8)
        {
            int code = Read();
            if (code == clear) { size = minCode + 1; next = eoi + 1; prev = -1; continue; }
            if (code == eoi) break;
            if (prev < 0) { Emit(code); prev = code; continue; }
            byte k = code < next ? First(code) : First(prev);
            if (next < 4096) { prefix[next] = prev; suffix[next] = k; len[next] = len[prev] + 1; next++; }
            Emit(code);
            prev = code;
            if (next == 1 << size && size < 12) size++;
        }
        return output.ToArray();
    }
}
