using System.Buffers.Binary;
using System.Text;

namespace CustomRecorder.Core.Encoding;

/// <summary>
/// Turns a fragmented MP4 (moov + many moof/mdat pairs, as written while recording for crash safety) into a regular
/// MP4 with one complete sample table, by reading the boxes directly — no decoding, no Media Foundation.
/// Media Foundation's own reader drops samples at fragment boundaries, so it cannot be used for this.
/// </summary>
public static class Fmp4Defragmenter
{
    private sealed class Track
    {
        public uint Id;
        public uint Timescale;
        public byte[] Tkhd = Array.Empty<byte>(), Mdhd = Array.Empty<byte>(), Hdlr = Array.Empty<byte>();
        public byte[] MediaHeader = Array.Empty<byte>(), Dinf = Array.Empty<byte>(), Stsd = Array.Empty<byte>();
        public bool IsVideo;
        public uint DefaultDuration, DefaultSize, DefaultFlags;
        public readonly List<Sample> Samples = new();
        public readonly List<(int FirstSample, int Count, long SourceOffset, long Length)> Runs = new();
    }

    private record struct Sample(uint Size, uint Duration, bool Sync, int Cto);

    /// <summary>True if the file contains movie fragments.</summary>
    public static bool IsFragmented(string path)
    {
        using var f = File.OpenRead(path);
        foreach (var b in TopBoxes(f))
            if (b.Type == "moof" || b.Type == "mvex") return true;
        return false;
    }

    /// <returns>Number of video samples written.</returns>
    public static int Convert(string input, string output)
    {
        using var src = File.OpenRead(input);
        var top = TopBoxes(src).ToList();
        var moovBox = top.FirstOrDefault(b => b.Type == "moov");
        if (moovBox.Type == null) throw new InvalidDataException("moov 상자가 없습니다.");
        byte[] ftyp = top.Where(b => b.Type == "ftyp").Select(b => Read(src, b.Offset, b.Size)).FirstOrDefault() ?? Array.Empty<byte>();
        byte[] moov = Read(src, moovBox.Offset, moovBox.Size);

        // ---- tracks from moov -------------------------------------------------------------
        var tracks = new List<Track>();
        byte[]? mvhd = null;
        uint movieTimescale = 1000;
        foreach (var (type, off, len, hdr) in Children(moov, 8, moov.Length))
        {
            if (type == "mvhd")
            {
                mvhd = moov[off..(off + len)];
                int v = mvhd[8];
                movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(8 + 4 + (v == 1 ? 16 : 8)));
            }
            else if (type == "trak") tracks.Add(ParseTrak(moov, off, len));
            else if (type == "mvex")
            {
                foreach (var (t2, o2, l2, h2) in Children(moov, off + hdr, off + len))
                {
                    if (t2 != "trex") continue;
                    var p = moov.AsSpan(o2 + h2 + 4);
                    uint id = BinaryPrimitives.ReadUInt32BigEndian(p);
                    var tr = tracks.FirstOrDefault(t => t.Id == id);
                    if (tr == null) continue;
                    tr.DefaultDuration = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
                    tr.DefaultSize = BinaryPrimitives.ReadUInt32BigEndian(p[12..]);
                    tr.DefaultFlags = BinaryPrimitives.ReadUInt32BigEndian(p[16..]);
                }
            }
        }
        if (mvhd == null || tracks.Count == 0) throw new InvalidDataException("트랙 정보가 없습니다.");

        // ---- samples from every moof ------------------------------------------------------
        foreach (var box in top.Where(b => b.Type == "moof"))
        {
            var moof = Read(src, box.Offset, box.Size);
            foreach (var (type, off, len, hdr) in Children(moof, 8, moof.Length))
                if (type == "traf") ParseTraf(moof, off, len, hdr, box.Offset, tracks);
        }

        // A recording cut off by a crash can end in the middle of a fragment: drop runs whose data is not all there.
        bool truncated = false;
        foreach (var t in tracks)
        {
            int bad = t.Runs.FindIndex(r => r.SourceOffset < 0 || r.SourceOffset + r.Length > src.Length);
            if (bad < 0) continue;
            truncated = true;
            t.Samples.RemoveRange(t.Runs[bad].FirstSample, t.Samples.Count - t.Runs[bad].FirstSample);
            t.Runs.RemoveRange(bad, t.Runs.Count - bad);
        }
        // After a crash the tracks can end at different points: cut both at the shorter one's end.
        // (Only then — a finished recording is never shortened.)
        if (truncated && tracks.Count > 1) TrimToShortest(tracks);

        // ---- write: ftyp, mdat (copied runs), moov ------------------------------------------
        using var dst = File.Create(output);
        dst.Write(ftyp);
        long mdatStart = dst.Position;
        dst.Write(new byte[16]); // large-size mdat header, filled in later
        var chunkOffsets = tracks.ToDictionary(t => t.Id, _ => new List<long>());
        var buffer = new byte[1 << 20];
        // Copy runs in file order so the result stays interleaved like the original.
        foreach (var (track, run) in tracks.SelectMany(t => t.Runs.Select(r => (t, r))).OrderBy(x => x.r.SourceOffset))
        {
            chunkOffsets[track.Id].Add(dst.Position);
            src.Position = run.SourceOffset;
            long left = run.Length;
            while (left > 0)
            {
                int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new EndOfStreamException("샘플 데이터가 잘렸습니다.");
                dst.Write(buffer, 0, n);
                left -= n;
            }
        }
        long mdatEnd = dst.Position;
        dst.Position = mdatStart;
        var mh = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mh, 1);
        System.Text.Encoding.ASCII.GetBytes("mdat").CopyTo(mh, 4);
        BinaryPrimitives.WriteUInt64BigEndian(mh.AsSpan(8), (ulong)(mdatEnd - mdatStart));
        dst.Write(mh);
        dst.Position = mdatEnd;

        dst.Write(BuildMoov(mvhd, movieTimescale, tracks, chunkOffsets));
        return tracks.Where(t => t.IsVideo).Sum(t => t.Samples.Count);
    }

    private static void TrimToShortest(List<Track> tracks)
    {
        double End(Track t) => t.Timescale == 0 ? 0 : t.Samples.Sum(s => (double)s.Duration) / t.Timescale;
        double shortest = tracks.Where(t => t.Samples.Count > 0).Select(End).DefaultIfEmpty(0).Min();
        foreach (var t in tracks)
        {
            double time = 0;
            int keep = 0;
            while (keep < t.Samples.Count && time < shortest - 1e-9) { time += t.Samples[keep].Duration / (double)t.Timescale; keep++; }
            if (keep >= t.Samples.Count) continue;
            t.Samples.RemoveRange(keep, t.Samples.Count - keep);
            // Shorten / drop runs past the cut.
            for (int i = t.Runs.Count - 1; i >= 0; i--)
            {
                var r = t.Runs[i];
                if (r.FirstSample >= keep) { t.Runs.RemoveAt(i); continue; }
                if (r.FirstSample + r.Count > keep)
                {
                    int n = keep - r.FirstSample;
                    long len = 0;
                    for (int k = 0; k < n; k++) len += t.Samples[r.FirstSample + k].Size;
                    t.Runs[i] = (r.FirstSample, n, r.SourceOffset, len);
                }
            }
        }
    }

    // ---------------------------------------------------------------- parsing

    private static Track ParseTrak(byte[] m, int off, int len)
    {
        var t = new Track();
        void Walk(int start, int end)
        {
            foreach (var (type, o, l, h) in Children(m, start, end))
            {
                switch (type)
                {
                    case "tkhd":
                        t.Tkhd = m[o..(o + l)];
                        t.Id = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(o + h + 4 + (m[o + h] == 1 ? 16 : 8)));
                        break;
                    case "mdhd":
                        t.Mdhd = m[o..(o + l)];
                        t.Timescale = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(o + h + 4 + (m[o + h] == 1 ? 16 : 8)));
                        break;
                    case "hdlr":
                        t.Hdlr = m[o..(o + l)];
                        t.IsVideo = System.Text.Encoding.ASCII.GetString(m, o + h + 8, 4) == "vide";
                        break;
                    case "vmhd": case "smhd": case "nmhd": t.MediaHeader = m[o..(o + l)]; break;
                    case "dinf": t.Dinf = m[o..(o + l)]; break;
                    case "stsd": t.Stsd = m[o..(o + l)]; break;
                    case "mdia": case "minf": case "stbl": Walk(o + h, o + l); break;
                }
            }
        }
        Walk(off + 8, off + len);
        return t;
    }

    private static void ParseTraf(byte[] m, int off, int len, int hdr, long moofOffset, List<Track> tracks)
    {
        Track? track = null;
        long baseOffset = moofOffset;
        uint defDur = 0, defSize = 0, defFlags = 0;
        foreach (var (type, o, l, h) in Children(m, off + hdr, off + len))
        {
            var p = m.AsSpan(o + h);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(p) & 0xFFFFFF;
            byte version = p[0];
            if (type == "tfhd")
            {
                uint id = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
                track = tracks.First(t => t.Id == id);
                defDur = track.DefaultDuration; defSize = track.DefaultSize; defFlags = track.DefaultFlags;
                int i = 8;
                if ((flags & 0x1) != 0) { baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(p[i..]); i += 8; }
                if ((flags & 0x2) != 0) i += 4;
                if ((flags & 0x8) != 0) { defDur = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); i += 4; }
                if ((flags & 0x10) != 0) { defSize = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); i += 4; }
                if ((flags & 0x20) != 0) { defFlags = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); }
            }
            else if (type == "trun" && track != null)
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
                int i = 8;
                long dataPos = baseOffset;
                if ((flags & 0x1) != 0) { dataPos = baseOffset + BinaryPrimitives.ReadInt32BigEndian(p[i..]); i += 4; }
                uint? firstFlags = null;
                if ((flags & 0x4) != 0) { firstFlags = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); i += 4; }
                int first = track.Samples.Count;
                long total = 0;
                for (int k = 0; k < count; k++)
                {
                    uint dur = defDur, size = defSize, sf = k == 0 && firstFlags.HasValue ? firstFlags.Value : defFlags;
                    int cto = 0;
                    if ((flags & 0x100) != 0) { dur = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); i += 4; }
                    if ((flags & 0x200) != 0) { size = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); i += 4; }
                    if ((flags & 0x400) != 0) { sf = BinaryPrimitives.ReadUInt32BigEndian(p[i..]); i += 4; }
                    if ((flags & 0x800) != 0)
                    {
                        cto = version == 0 ? (int)BinaryPrimitives.ReadUInt32BigEndian(p[i..]) : BinaryPrimitives.ReadInt32BigEndian(p[i..]);
                        i += 4;
                    }
                    bool sync = (sf & 0x10000) == 0; // sample_is_non_sync_sample
                    track.Samples.Add(new Sample(size, dur, sync, cto));
                    total += size;
                }
                if (count > 0) track.Runs.Add((first, count, dataPos, total));
            }
        }
    }

    // ---------------------------------------------------------------- writing

    private static byte[] BuildMoov(byte[] mvhd, uint movieTimescale, List<Track> tracks, Dictionary<uint, List<long>> chunkOffsets)
    {
        var traks = new List<byte[]>();
        ulong movieDuration = 0;
        foreach (var t in tracks)
        {
            ulong mediaDur = (ulong)t.Samples.Sum(s => (long)s.Duration);
            ulong movieDur = t.Timescale == 0 ? 0 : mediaDur * movieTimescale / t.Timescale;
            movieDuration = Math.Max(movieDuration, movieDur);

            var stbl = Box("stbl",
                t.Stsd,
                Stts(t.Samples),
                t.IsVideo ? Stss(t.Samples) : Array.Empty<byte>(),
                t.Samples.Any(s => s.Cto != 0) ? Ctts(t.Samples) : Array.Empty<byte>(),
                Stsc(t.Runs),
                Stsz(t.Samples),
                Co64(chunkOffsets[t.Id]));
            var minf = Box("minf", t.MediaHeader, t.Dinf, stbl);
            var mdia = Box("mdia", WithDuration(t.Mdhd, mediaDur, isTkhd: false), t.Hdlr, minf);
            traks.Add(Box("trak", WithDuration(t.Tkhd, movieDur, isTkhd: true), mdia));
        }
        return Box("moov", new[] { WithDuration(mvhd, movieDuration, isTkhd: false) }.Concat(traks).ToArray());
    }

    /// <summary>Copies an mvhd / mdhd / tkhd box with its duration field replaced.</summary>
    private static byte[] WithDuration(byte[] box, ulong duration, bool isTkhd)
    {
        var b = (byte[])box.Clone();
        int v = b[8];
        // after version/flags: creation, modification, then (tkhd: track_ID, reserved | mvhd/mdhd: timescale), then duration
        int pos = 12 + (v == 1 ? 16 : 8) + (isTkhd ? 8 : 4);
        if (v == 1) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(pos), duration);
        else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(pos), (uint)Math.Min(duration, uint.MaxValue));
        return b;
    }

    private static byte[] Stts(List<Sample> s)
    {
        var e = new List<(uint Count, uint Delta)>();
        foreach (var x in s)
        {
            if (e.Count > 0 && e[^1].Delta == x.Duration) e[^1] = (e[^1].Count + 1, x.Duration);
            else e.Add((1, x.Duration));
        }
        return FullBox("stts", 0, w => { w.U32((uint)e.Count); foreach (var (c, d) in e) { w.U32(c); w.U32(d); } });
    }

    private static byte[] Stss(List<Sample> s)
    {
        var sync = s.Select((x, i) => (x, i)).Where(p => p.x.Sync).Select(p => (uint)(p.i + 1)).ToList();
        return FullBox("stss", 0, w => { w.U32((uint)sync.Count); foreach (var n in sync) w.U32(n); });
    }

    private static byte[] Ctts(List<Sample> s)
    {
        var e = new List<(uint Count, int Off)>();
        foreach (var x in s)
        {
            if (e.Count > 0 && e[^1].Off == x.Cto) e[^1] = (e[^1].Count + 1, x.Cto);
            else e.Add((1, x.Cto));
        }
        return FullBox("ctts", 1, w => { w.U32((uint)e.Count); foreach (var (c, o) in e) { w.U32(c); w.U32((uint)o); } });
    }

    private static byte[] Stsc(List<(int FirstSample, int Count, long SourceOffset, long Length)> runs)
    {
        var e = new List<(uint FirstChunk, uint PerChunk)>();
        for (int i = 0; i < runs.Count; i++)
            if (e.Count == 0 || e[^1].PerChunk != (uint)runs[i].Count) e.Add(((uint)(i + 1), (uint)runs[i].Count));
        return FullBox("stsc", 0, w => { w.U32((uint)e.Count); foreach (var (f, n) in e) { w.U32(f); w.U32(n); w.U32(1); } });
    }

    private static byte[] Stsz(List<Sample> s) =>
        FullBox("stsz", 0, w => { w.U32(0); w.U32((uint)s.Count); foreach (var x in s) w.U32(x.Size); });

    private static byte[] Co64(List<long> offsets) =>
        FullBox("co64", 0, w => { w.U32((uint)offsets.Count); foreach (var o in offsets) w.U64((ulong)o); });

    // ---------------------------------------------------------------- box helpers

    private readonly record struct TopBox(string Type, long Offset, long Size);

    private static IEnumerable<TopBox> TopBoxes(Stream s)
    {
        long pos = 0, len = s.Length;
        var h = new byte[16];
        while (pos + 8 <= len)
        {
            s.Position = pos;
            s.ReadExactly(h, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = System.Text.Encoding.ASCII.GetString(h, 4, 4);
            if (size == 1) { s.ReadExactly(h, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8)); }
            else if (size == 0) size = len - pos;
            if (size < 8 || pos + size > len) yield break; // truncated tail (e.g. after a crash)
            yield return new TopBox(type, pos, size);
            pos += size;
        }
    }

    private static byte[] Read(Stream s, long offset, long size)
    {
        var b = new byte[size];
        s.Position = offset;
        s.ReadExactly(b);
        return b;
    }

    private static IEnumerable<(string Type, int Offset, int Length, int Header)> Children(byte[] b, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
            int hdr = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(pos + 8)); hdr = 16; }
            else if (size == 0) size = end - pos;
            if (size < hdr || pos + size > end) yield break;
            yield return (System.Text.Encoding.ASCII.GetString(b, pos + 4, 4), pos, (int)size, hdr);
            pos += (int)size;
        }
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        int len = 8 + children.Sum(c => c.Length);
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)len);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        int p = 8;
        foreach (var c in children) { c.CopyTo(b, p); p += c.Length; }
        return b;
    }

    private sealed class W
    {
        public readonly MemoryStream S = new();
        public void U32(uint v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, v); S.Write(t); }
        public void U64(ulong v) { Span<byte> t = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(t, v); S.Write(t); }
    }

    private static byte[] FullBox(string type, byte version, Action<W> body)
    {
        var w = new W();
        w.U32((uint)version << 24);
        body(w);
        return Box(type, w.S.ToArray());
    }
}
