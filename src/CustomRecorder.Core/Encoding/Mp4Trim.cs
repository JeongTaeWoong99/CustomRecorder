using CustomRecorder.Core.Util;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace CustomRecorder.Core.Encoding;

/// <summary>
/// Cuts a part out of an MP4 without re-encoding: compressed samples are copied as they are, so it is fast and
/// lossless. The start snaps back to the keyframe at or before the requested time (the only place a video can begin).
/// </summary>
public static class Mp4Trim
{
    private static readonly Guid SampleDiscontinuity = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");

    /// <summary>
    /// Exact length of the video track, by reading the compressed sample timestamps (no decoding, fast).
    /// Players often misreport the length of fragmented MP4 files, so the trim window uses this instead.
    /// </summary>
    public static TimeSpan VideoDuration(string path)
    {
        MediaFactory.MFStartup(true).CheckError();
        var (readable, cleanup) = Readable(path);
        try { return VideoDurationCore(readable); }
        finally { cleanup(); }
    }

    private static TimeSpan VideoDurationCore(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        long end = 0;
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long t);
            if (sample != null) end = Math.Max(end, t + sample.SampleDuration);
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) break;
        }
        return TimeSpan.FromTicks(end);
    }

    /// <summary>
    /// Rewrites a fragmented MP4 as a regular MP4 with the same compressed samples (correct length and fast seeking in
    /// every player). Uses the box-level converter; writes a temporary file and replaces the original only on success.
    /// </summary>
    public static void Remux(string path)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"~{System.IO.Path.GetFileNameWithoutExtension(path)}.remux.tmp");
        try
        {
            int samples = Fmp4Defragmenter.Convert(path, tmp);
            if (samples == 0) throw new InvalidDataException("영상 샘플이 없습니다.");
            File.Move(tmp, path, overwrite: true);
            Log.Info($"일반 MP4로 정리: {path} (영상 샘플 {samples}개)");
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Media Foundation's reader drops samples at fragment boundaries, so readers work on a regular-MP4 copy of
    /// fragmented files. Returns the path to read and a cleanup action.
    /// </summary>
    public static (string Path, Action Cleanup) Readable(string path)
    {
        if (!Fmp4Defragmenter.IsFragmented(path)) return (path, () => { });
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CustomRecorder_{Guid.NewGuid():N}.mp4");
        Fmp4Defragmenter.Convert(path, tmp);
        return (tmp, () => { try { File.Delete(tmp); } catch { } });
    }

    /// <returns>The actual start time used (keyframe), in 100 ns.</returns>
    public static long Trim(string input, string output, long start100ns, long end100ns, IProgress<double>? progress = null)
    {
        if (end100ns <= start100ns) throw new ArgumentException("끝 지점이 시작 지점보다 뒤여야 합니다.");
        MediaFactory.MFStartup(true).CheckError();
        var (readable, cleanup) = Readable(input);
        try { return TrimCore(readable, output, start100ns, end100ns, progress); }
        finally { cleanup(); }
    }

    private static long TrimCore(string input, string output, long start100ns, long end100ns, IProgress<double>? progress)
    {

        using var reader = MediaFactory.MFCreateSourceReaderFromURL(input, null!);
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4).CheckError();
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(output, null!, attributes);

        var map = new Dictionary<int, int>();
        int videoStream = -1;
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = reader.GetNativeMediaType(i, 0); }
            catch (SharpGenException) { break; }
            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.MajorType) == MediaTypeGuids.Video && videoStream < 0) videoStream = i;
                int ws = writer.AddStream(type);
                writer.SetInputMediaType(ws, type, null!);
                map[i] = ws;
                reader.SetStreamSelection(i, true);
            }
        }
        if (videoStream < 0) throw new InvalidOperationException("영상 트랙이 없는 파일입니다.");

        reader.SetCurrentPosition(start100ns);
        writer.BeginWriting();

        long origin = long.MinValue;
        var ended = new HashSet<int>();
        var lastTime = new Dictionary<int, long>();
        var pending = new List<(int Stream, IMFSample Sample, long Time)>(); // audio read before the first video frame
        long written = 0;

        while (ended.Count < map.Count)
        {
            var sample = reader.ReadSample(SourceReaderIndex.AnyStream, SourceReaderControlFlag.None, out int stream, out var flags, out long t);
            if ((flags & SourceReaderFlag.Error) != 0) { sample?.Dispose(); throw new InvalidOperationException("파일을 읽는 중 오류가 났습니다."); }
            if ((flags & SourceReaderFlag.EndOfStream) != 0) ended.Add(stream);
            if (sample == null) continue;

            if (t >= end100ns) { sample.Dispose(); ended.Add(stream); continue; }
            if (origin == long.MinValue)
            {
                if (stream != videoStream) { pending.Add((stream, sample, t)); continue; }
                origin = t; // first video sample after the seek is a keyframe
                foreach (var p in pending)
                {
                    using (p.Sample) if (p.Time >= origin) Write(p.Stream, p.Sample, p.Time);
                }
                pending.Clear();
            }
            using (sample)
            {
                if (t >= origin) Write(stream, sample, t);
            }
            progress?.Report(Math.Clamp((t - start100ns) / (double)(end100ns - start100ns), 0, 1));
        }
        foreach (var p in pending) p.Sample.Dispose();
        if (written == 0) throw new InvalidOperationException("선택한 구간에 영상이 없습니다.");
        writer.Finalize();
        Log.Info($"자르기 완료: {output} ({origin / 10_000}ms ~ {end100ns / 10_000}ms, 샘플 {written}개)");
        return origin;

        void Write(int stream, IMFSample sample, long time)
        {
            long rel = time - origin;
            if (lastTime.TryGetValue(stream, out var prev) && rel < prev) rel = prev;
            lastTime[stream] = rel;
            sample.SampleTime = rel;
            sample.DeleteItem(SampleDiscontinuity);
            writer.WriteSample(map[stream], sample);
            written++;
        }
    }
}
