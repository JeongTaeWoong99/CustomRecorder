using System.Buffers.Binary;
using System.Text;
using CustomRecorder.Core.Encoding;
using Xunit;

/// <summary>
/// Builds small fragmented MP4 files by hand (same layout Media Foundation writes: moov + mvex/trex, then moof/mdat
/// pairs with tfhd base-data-offset and trun v1 carrying duration, size, flags and composition offset), converts them
/// and reads the result's sample tables back.
/// </summary>
public class Fmp4DefragmenterTests
{
    private const uint VideoScale = 600, AudioScale = 48000;

    private sealed record Spec(int Fragments, int VideoPerFragment, int AudioPerFragment);

    /// <summary>Returns the file plus every sample's bytes per track, in order.</summary>
    private static (byte[] File, List<byte[]> Video, List<byte[]> Audio) Build(Spec spec)
    {
        var video = new List<byte[]>();
        var audio = new List<byte[]>();
        var rnd = new Random(7);
        var file = new MemoryStream();
        file.Write(Box("ftyp", Ascii("isom"), U32(0), Ascii("isomiso6")));
        file.Write(Moov());
        for (int f = 0; f < spec.Fragments; f++)
        {
            var v = Enumerable.Range(0, spec.VideoPerFragment).Select(i => RandomBytes(rnd, 50 + rnd.Next(200))).ToList();
            var a = Enumerable.Range(0, spec.AudioPerFragment).Select(i => RandomBytes(rnd, 20 + rnd.Next(30))).ToList();
            video.AddRange(v);
            audio.AddRange(a);
            long moofStart = file.Position;
            // Layout: moof | mdat(header 8) | video run | audio run
            byte[] MakeMoof(long videoData, long audioData) => Box("moof",
                FullBox("mfhd", 0, 0, U32((uint)f + 1)),
                Traf(1, videoData, v.Select((s, i) => (s.Length, 10u, i == 0)).ToList()),
                Traf(2, audioData, a.Select(s => (s.Length, 1024u, true)).ToList()));
            int moofLen = MakeMoof(0, 0).Length;
            long videoStart = moofStart + moofLen + 8;
            long audioStart = videoStart + v.Sum(s => s.Length);
            file.Write(MakeMoof(videoStart, audioStart));
            var mdat = v.Concat(a).SelectMany(x => x).ToArray();
            file.Write(Box("mdat", mdat));
        }
        return (file.ToArray(), video, audio);
    }

    private static byte[] Traf(uint trackId, long baseOffset, List<(int Size, uint Duration, bool Sync)> samples)
    {
        var trun = new List<byte[]> { U32((uint)samples.Count), U32(0) /* data_offset = 0 from base */ };
        foreach (var (size, dur, sync) in samples)
        {
            trun.Add(U32(dur));
            trun.Add(U32((uint)size));
            trun.Add(U32(sync ? 0x02000000u : 0x01010000u)); // depends-on-none + sync / non-sync
            trun.Add(U32(0));
        }
        return Box("traf",
            FullBox("tfhd", 0, 0x1, U32(trackId), U64((ulong)baseOffset)),
            FullBox("trun", 1, 0xF01, trun.ToArray()));
    }

    private static byte[] Moov() => Box("moov",
        FullBox("mvhd", 0, 0, U32(0), U32(0), U32(1000), U32(0), new byte[80]),
        Trak(1, VideoScale, "vide"),
        Trak(2, AudioScale, "soun"),
        Box("mvex",
            FullBox("trex", 0, 0, U32(1), U32(1), U32(0), U32(0), U32(0)),
            FullBox("trex", 0, 0, U32(2), U32(1), U32(0), U32(0), U32(0))));

    private static byte[] Trak(uint id, uint scale, string handler) => Box("trak",
        FullBox("tkhd", 0, 3, U32(0), U32(0), U32(id), U32(0), U32(0), new byte[60]),
        Box("mdia",
            FullBox("mdhd", 0, 0, U32(0), U32(0), U32(scale), U32(0), U32(0)),
            FullBox("hdlr", 0, 0, U32(0), Ascii(handler), new byte[13]),
            Box("minf",
                handler == "vide" ? FullBox("vmhd", 0, 1, new byte[8]) : FullBox("smhd", 0, 0, new byte[4]),
                Box("dinf", FullBox("dref", 0, 0, U32(1), FullBox("url ", 0, 1))),
                Box("stbl",
                    FullBox("stsd", 0, 0, U32(0)),
                    FullBox("stts", 0, 0, U32(0)), FullBox("stsc", 0, 0, U32(0)),
                    FullBox("stsz", 0, 0, U32(0), U32(0)), FullBox("stco", 0, 0, U32(0))))));

    // ---- reading the converted file -----------------------------------------------------------

    private sealed record Table(List<uint> Sizes, List<ulong> ChunkOffsets, List<(uint First, uint PerChunk)> Stsc, List<uint> Sync, ulong Duration, ulong DurationSum);

    private static List<Table> ReadTables(byte[] f)
    {
        var tables = new List<Table>();
        var moov = Find(f, 0, f.Length, "moov");
        foreach (var trak in All(f, moov.Start, moov.End, "trak"))
        {
            var mdia = Find(f, trak.Start, trak.End, "mdia");
            var mdhd = Find(f, mdia.Start, mdia.End, "mdhd");
            ulong duration = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(mdhd.Start + 16));
            var stbl = Find(f, Find(f, mdia.Start, mdia.End, "minf").Start, Find(f, mdia.Start, mdia.End, "minf").End, "stbl");
            var stsz = Find(f, stbl.Start, stbl.End, "stsz");
            int n = (int)U32At(f, stsz.Start + 8);
            var sizes = Enumerable.Range(0, n).Select(i => U32At(f, stsz.Start + 12 + i * 4)).ToList();
            var co = Find(f, stbl.Start, stbl.End, "co64");
            var offsets = Enumerable.Range(0, (int)U32At(f, co.Start + 4)).Select(i => BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(co.Start + 8 + i * 8))).ToList();
            var stsc = Find(f, stbl.Start, stbl.End, "stsc");
            var sc = Enumerable.Range(0, (int)U32At(f, stsc.Start + 4)).Select(i => (U32At(f, stsc.Start + 8 + i * 12), U32At(f, stsc.Start + 12 + i * 12))).ToList();
            var stts = Find(f, stbl.Start, stbl.End, "stts");
            ulong sum = 0;
            for (int i = 0; i < U32At(f, stts.Start + 4); i++) sum += (ulong)U32At(f, stts.Start + 8 + i * 8) * U32At(f, stts.Start + 12 + i * 8);
            var sync = new List<uint>();
            var stss = TryFind(f, stbl.Start, stbl.End, "stss");
            if (stss != null) for (int i = 0; i < U32At(f, stss.Value.Start + 4); i++) sync.Add(U32At(f, stss.Value.Start + 8 + i * 4));
            tables.Add(new Table(sizes, offsets, sc, sync, duration, sum));
        }
        return tables;
    }

    /// <summary>Sample bytes as the converted file's tables describe them.</summary>
    private static List<byte[]> Samples(byte[] f, Table t)
    {
        var result = new List<byte[]>();
        int sample = 0;
        for (int chunk = 0; chunk < t.ChunkOffsets.Count; chunk++)
        {
            uint per = t.Stsc.Last(e => e.First <= chunk + 1).PerChunk;
            long pos = (long)t.ChunkOffsets[chunk];
            for (int k = 0; k < per; k++, sample++)
            {
                result.Add(f.AsSpan((int)pos, (int)t.Sizes[sample]).ToArray());
                pos += t.Sizes[sample];
            }
        }
        return result;
    }

    private static byte[] Convert(byte[] input)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "in.mp4"), input);
            Fmp4Defragmenter.Convert(Path.Combine(dir, "in.mp4"), Path.Combine(dir, "out.mp4"));
            Assert.False(Fmp4Defragmenter.IsFragmented(Path.Combine(dir, "out.mp4")));
            return File.ReadAllBytes(Path.Combine(dir, "out.mp4"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EverySampleIsKeptByteForByte()
    {
        var (file, video, audio) = Build(new Spec(Fragments: 5, VideoPerFragment: 12, AudioPerFragment: 9));
        var output = Convert(file);
        var tables = ReadTables(output);
        Assert.Equal(2, tables.Count);
        Assert.Equal(video, Samples(output, tables[0]));
        Assert.Equal(audio, Samples(output, tables[1]));
    }

    [Fact]
    public void TimingAndKeyframesAreCorrect()
    {
        var (file, _, _) = Build(new Spec(4, 10, 6));
        var t = ReadTables(Convert(file));
        Assert.Equal(40UL * 10, t[0].DurationSum);
        Assert.Equal(t[0].DurationSum, t[0].Duration);           // mdhd duration filled in
        Assert.Equal(new uint[] { 1, 11, 21, 31 }, t[0].Sync);  // one keyframe per fragment (1-based)
        Assert.Equal(24UL * 1024, t[1].DurationSum);
    }

    [Fact]
    public void TruncatedRecordingKeepsCompleteFragments()
    {
        var (file, video, _) = Build(new Spec(5, 12, 9));
        // Cut in the middle of the last fragment's sample data, as a crash would.
        var cut = file.AsSpan(0, file.Length - 100).ToArray();
        var output = Convert(cut);
        var t = ReadTables(output);
        var kept = Samples(output, t[0]);
        Assert.InRange(kept.Count, 1, 59);                        // the damaged tail is gone…
        Assert.Equal(video.Take(kept.Count).ToList(), kept);     // …everything before it is intact
        double videoEnd = t[0].DurationSum / (double)VideoScale, audioEnd = t[1].DurationSum / (double)AudioScale;
        Assert.InRange(videoEnd - audioEnd, -0.06, 0.06);          // tracks end together
    }

    [Fact]
    public void RegularFileIsNotReportedAsFragmented()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Box("ftyp", Ascii("isom")).Concat(Box("moov")).Concat(Box("mdat", new byte[10])).ToArray());
            Assert.False(Fmp4Defragmenter.IsFragmented(path));
        }
        finally { File.Delete(path); }
    }

    // ---- box helpers ---------------------------------------------------------------------------

    private static byte[] RandomBytes(Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }
    private static uint U32At(byte[] f, int pos) => BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(pos));

    private static byte[] Box(string type, params byte[][] parts)
    {
        int len = 8 + parts.Sum(p => p.Length);
        return U32((uint)len).Concat(Ascii(type)).Concat(parts.SelectMany(p => p)).ToArray();
    }

    private static byte[] FullBox(string type, byte version, uint flags, params byte[][] parts)
        => Box(type, new[] { U32(((uint)version << 24) | flags) }.Concat(parts).ToArray());

    private readonly record struct Range(int Start, int End); // Start = first byte after the 8-byte header

    private static IEnumerable<Range> All(byte[] f, int start, int end, string type)
    {
        for (int p = start; p + 8 <= end;)
        {
            long size = U32At(f, p);
            int hdr = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(p + 8)); hdr = 16; }
            if (Encoding.ASCII.GetString(f, p + 4, 4) == type) yield return new Range(p + hdr, (int)(p + size));
            p += (int)size;
        }
    }

    private static Range? TryFind(byte[] f, int start, int end, string type) => All(f, start, end, type).Cast<Range?>().FirstOrDefault();
    private static Range Find(byte[] f, int start, int end, string type) => TryFind(f, start, end, type) ?? throw new Xunit.Sdk.XunitException($"{type} 없음");
}
