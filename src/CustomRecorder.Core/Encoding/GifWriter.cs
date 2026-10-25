namespace CustomRecorder.Core.Encoding;

/// <summary>
/// Minimal animated GIF (GIF89a) encoder tuned for screen recordings:
/// each frame only stores the rectangle that changed, unchanged pixels inside it are transparent,
/// and every frame gets its own 255-colour palette (screen content rarely needs dithering).
/// </summary>
public sealed class GifWriter : IDisposable
{
    private const int Transparent = 255;

    private readonly Stream _out;
    private readonly int _width, _height;
    private readonly int[] _shown;          // RGB currently visible per pixel (-1 = nothing yet)
    private byte[]? _pendingFrame;          // encoded frame waiting for its final delay
    private int _pendingDelay;
    private int _pendingDelayPos;

    public GifWriter(Stream output, int width, int height)
    {
        _out = output;
        _width = width;
        _height = height;
        _shown = new int[width * height];
        Array.Fill(_shown, -1);

        Write("GIF89a");
        WriteU16(width);
        WriteU16(height);
        _out.WriteByte(0);      // no global colour table
        _out.WriteByte(0);
        _out.WriteByte(0);
        // Loop forever.
        _out.Write(new byte[] { 0x21, 0xFF, 0x0B });
        Write("NETSCAPE2.0");
        _out.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
    }

    /// <param name="bgra">width × height × 4 bytes, top-down.</param>
    /// <param name="delayMs">How long this frame stays on screen.</param>
    public void AddFrame(byte[] bgra, int delayMs)
    {
        int cs = Math.Max(2, (int)Math.Round(delayMs / 10.0));

        // 1. Quantise to 15-bit colour and find the changed rectangle.
        var rgb = new int[_width * _height];
        int minX = _width, minY = _height, maxX = -1, maxY = -1;
        for (int y = 0, i = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++, i++)
            {
                int o = i * 4;
                int c = ((bgra[o + 2] >> 3) << 10) | ((bgra[o + 1] >> 3) << 5) | (bgra[o] >> 3);
                rgb[i] = c;
                if (c != _shown[i])
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0)
        {
            // Nothing changed: just show the previous frame longer.
            _pendingDelay += cs;
            return;
        }
        FlushPending();

        int rw = maxX - minX + 1, rh = maxY - minY + 1;

        // 2. Palette: the 255 most common colours among the changed pixels.
        var counts = new int[32768];
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                int i = y * _width + x;
                if (rgb[i] != _shown[i]) counts[rgb[i]]++;
            }
        var used = Enumerable.Range(0, 32768).Where(c => counts[c] > 0).OrderByDescending(c => counts[c]).Take(255).ToArray();
        var lut = new short[32768];
        Array.Fill(lut, (short)-1);
        for (int k = 0; k < used.Length; k++) lut[used[k]] = (short)k;

        // 3. Index the rectangle (transparent where the pixel already shows the right colour).
        var indices = new byte[rw * rh];
        for (int y = 0, j = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++, j++)
            {
                int i = (minY + y) * _width + minX + x;
                int c = rgb[i];
                if (c == _shown[i]) { indices[j] = Transparent; continue; }
                int k = lut[c];
                if (k < 0) k = lut[c] = (short)Nearest(c, used);
                indices[j] = (byte)k;
                _shown[i] = used[k];
            }
        }

        // 4. Encode.
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x21, 0xF9, 0x04, (1 << 2) | 1, 0, 0, Transparent, 0 }); // keep previous, transparent index
        _pendingDelayPos = 4;
        ms.WriteByte(0x2C);
        WriteU16(ms, minX); WriteU16(ms, minY); WriteU16(ms, rw); WriteU16(ms, rh);
        ms.WriteByte(0x80 | 7); // local colour table, 256 entries
        for (int k = 0; k < 256; k++)
        {
            int c = k < used.Length ? used[k] : 0;
            ms.WriteByte(Expand(c >> 10)); ms.WriteByte(Expand((c >> 5) & 31)); ms.WriteByte(Expand(c & 31));
        }
        Lzw(ms, indices);
        _pendingFrame = ms.ToArray();
        _pendingDelay = cs;
    }

    private static byte Expand(int v5) => (byte)((v5 << 3) | (v5 >> 2));

    private static int Nearest(int c, int[] palette)
    {
        int r = c >> 10, g = (c >> 5) & 31, b = c & 31, best = 0, bestD = int.MaxValue;
        for (int k = 0; k < palette.Length; k++)
        {
            int p = palette[k];
            int dr = r - (p >> 10), dg = g - ((p >> 5) & 31), db = b - (p & 31);
            int d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    private void FlushPending()
    {
        if (_pendingFrame == null) return;
        int d = Math.Min(_pendingDelay, 65535);
        _pendingFrame[_pendingDelayPos] = (byte)d;
        _pendingFrame[_pendingDelayPos + 1] = (byte)(d >> 8);
        _out.Write(_pendingFrame);
        _pendingFrame = null;
    }

    // ---- LZW (GIF variant, min code size 8) --------------------------------------------------
    [ThreadStatic] private static int[]? _table, _stamp;
    [ThreadStatic] private static int _generation;

    private static void Lzw(Stream s, byte[] data)
    {
        const int minBits = 8, clear = 256, eoi = 257;
        s.WriteByte(minBits);
        var block = new List<byte>(255);
        int bitBuf = 0, bitCount = 0;
        void Emit(int code, int bits)
        {
            bitBuf |= code << bitCount;
            bitCount += bits;
            while (bitCount >= 8)
            {
                block.Add((byte)bitBuf);
                bitBuf >>= 8;
                bitCount -= 8;
                if (block.Count == 255) { s.WriteByte(255); s.Write(block.ToArray()); block.Clear(); }
            }
        }

        // (prefix << 8 | byte) -> code; an entry is valid when its stamp equals the current generation.
        var table = _table ??= new int[4096 * 256];
        var stamp = _stamp ??= new int[4096 * 256];
        int generation = ++_generation;
        int next = eoi + 1, codeBits = minBits + 1;
        Emit(clear, codeBits);
        int prefix = data.Length > 0 ? data[0] : 0;
        for (int i = 1; i < data.Length; i++)
        {
            int k = data[i];
            int key = (prefix << 8) | k;
            if (stamp[key] == generation) { prefix = table[key]; continue; }
            Emit(prefix, codeBits);
            if (next < 4096)
            {
                stamp[key] = generation;
                table[key] = next++;
                if (next > (1 << codeBits) && codeBits < 12) codeBits++;
            }
            else
            {
                Emit(clear, codeBits);
                generation = ++_generation;
                next = eoi + 1;
                codeBits = minBits + 1;
            }
            prefix = k;
        }
        if (data.Length > 0) Emit(prefix, codeBits);
        Emit(eoi, codeBits);
        if (bitCount > 0) block.Add((byte)bitBuf);
        if (block.Count > 0) { s.WriteByte((byte)block.Count); s.Write(block.ToArray()); }
        s.WriteByte(0);
    }

    private void Write(string ascii) { foreach (var ch in ascii) _out.WriteByte((byte)ch); }
    private void WriteU16(int v) => WriteU16(_out, v);
    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    public void Dispose()
    {
        FlushPending();
        _out.WriteByte(0x3B);
        _out.Flush();
    }
}
