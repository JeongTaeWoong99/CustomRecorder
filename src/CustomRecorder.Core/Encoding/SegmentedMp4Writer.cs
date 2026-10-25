using CustomRecorder.Core.Util;
using Vortice.Direct3D11;

namespace CustomRecorder.Core.Encoding;

/// <summary>
/// Replay-buffer sink: writes the recording as a chain of short MP4 segments and keeps only the most recent ones.
/// Each segment starts with a fresh encoder, so it begins on a keyframe and segments can later be joined
/// without re-encoding (<see cref="Mp4Concat"/>).
///
/// Segments hold video only. Audio is kept as raw PCM in memory and encoded once at save time, cut to exactly
/// the saved video range — joining separately encoded AAC segments is unreliable (AAC's fixed 1024-sample frames
/// overrun each segment boundary, and the MP4 muxer drops the track after the first overlap).
/// </summary>
public sealed class SegmentedMp4Writer : IMediaSink
{
    public sealed class Segment
    {
        public required int Index { get; init; }
        public required string Path { get; init; }
        public required long Start { get; init; }          // recording timeline, 100 ns
        public long End { get; set; } = long.MaxValue;      // set when the video thread moves on
        public Mp4Writer? Writer { get; set; }
        public bool VideoDone { get; set; }
        public bool AudioDone { get; set; }
        public bool Finalized { get; set; }
        public bool Closed { get; set; }  // file fully written
        public int VideoSamples;
        public int AudioSamples;
        public long Duration => End == long.MaxValue ? 0 : End - Start;
    }

    private readonly object _gate = new();
    private readonly Func<string, Mp4Writer> _createWriter;
    private readonly string _folder;
    private readonly long _segmentLength;
    private readonly long _keep;
    private readonly bool _hasAudio;
    private readonly LinkedList<(long Time, byte[] Pcm)> _pcm = new();
    private readonly List<Segment> _segments = new();
    private long _nextBoundary;
    private int _nextIndex;
    private Task<Mp4Writer>? _prepared;
    private string? _preparedPath;
    private bool _finished;

    /// <param name="keep">How much finished footage to keep (replay length).</param>
    public SegmentedMp4Writer(string folder, TimeSpan segmentLength, TimeSpan keep, bool hasAudio, Func<string, Mp4Writer> createWriter)
    {
        _folder = folder;
        _segmentLength = segmentLength.Ticks;
        _keep = keep.Ticks;
        _hasAudio = hasAudio;
        _createWriter = createWriter;
        Directory.CreateDirectory(folder);
        _nextBoundary = 0; // first video frame opens segment 0
    }

    private string PathFor(int index) => System.IO.Path.Combine(_folder, $"seg_{index:D5}.mp4");

    // ---------------------------------------------------------------- video thread

    public void WriteVideo(ID3D11Texture2D texture, long time100ns, long duration100ns)
    {
        Segment seg;
        lock (_gate)
        {
            if (_finished) return;
            if (_segments.Count == 0 || time100ns >= _nextBoundary) seg = OpenSegmentLocked(time100ns);
            else seg = _segments[^1];
        }
        seg.Writer!.WriteVideo(texture, time100ns - seg.Start, duration100ns);
        seg.VideoSamples++;
    }

    private Segment OpenSegmentLocked(long start)
    {
        if (_segments.Count > 0)
        {
            var prev = _segments[^1];
            prev.End = start;
            prev.VideoDone = true;
            prev.AudioDone = true;
            TryFinalizeLocked(prev);
        }

        int index = _nextIndex++;
        var path = PathFor(index);
        Mp4Writer writer;
        if (_prepared != null && _preparedPath == path && _prepared.IsCompletedSuccessfully) writer = _prepared.Result;
        else
        {
            if (_prepared is { IsCompletedSuccessfully: true }) _prepared.Result.Dispose(); // stale (path mismatch)
            writer = _createWriter(path);
        }
        _prepared = null;

        var seg = new Segment { Index = index, Path = path, Start = start, Writer = writer };
        _segments.Add(seg);
        _nextBoundary = start + _segmentLength;

        // Create the next segment's encoder in the background so rotation does not stall the video thread.
        var nextPath = PathFor(_nextIndex);
        _preparedPath = nextPath;
        _prepared = Task.Run(() => _createWriter(nextPath));
        return seg;
    }

    // ---------------------------------------------------------------- audio thread

    public void WriteAudio(ReadOnlySpan<byte> pcm, long time100ns, int track = 0)
    {
        if (!_hasAudio || track != 0) return;
        var copy = pcm.ToArray();
        lock (_pcm)
        {
            _pcm.AddLast((time100ns, copy));
            long oldest = time100ns - _keep - 3 * _segmentLength;
            while (_pcm.First != null && _pcm.First.Value.Time < oldest) _pcm.RemoveFirst();
        }
    }

    public bool HasAudio => _hasAudio;

    /// <summary>Buffered PCM chunks whose start time lies in [from, to).</summary>
    public List<(long Time, byte[] Pcm)> GetAudio(long from, long to)
    {
        lock (_pcm) return _pcm.Where(c => c.Time >= from && c.Time < to).ToList();
    }

    // ---------------------------------------------------------------- finalize / retention

    private void TryFinalizeLocked(Segment seg)
    {
        if (seg.Finalized || !seg.VideoDone || !seg.AudioDone) return;
        seg.Finalized = true;
        var writer = seg.Writer;
        seg.Writer = null;
        Task.Run(() =>
        {
            Log.Info($"리플레이 구간 {seg.Index}: {seg.Start / 10_000}~{seg.End / 10_000}ms video={seg.VideoSamples}");
            try { writer?.Dispose(); }
            catch (Exception ex) { Log.Error($"리플레이 구간 저장 실패: {seg.Path}", ex); }
            lock (_gate)
            {
                seg.Closed = true;
                PruneLocked();
            }
        });
    }

    private void PruneLocked()
    {
        var closed = _segments.Where(s => s.Closed).ToList();
        if (closed.Count == 0) return;
        long newestEnd = closed.Max(s => s.End);
        foreach (var s in closed.Where(s => s.End < newestEnd - _keep - _segmentLength).ToList())
        {
            try { File.Delete(s.Path); } catch { /* still open by a reader; retry next time */ continue; }
            _segments.Remove(s);
        }
    }

    /// <summary>Ends the current segment at the next video frame so everything up to now becomes a finished file.</summary>
    public int ForceRotate()
    {
        lock (_gate)
        {
            _nextBoundary = 0;
            return _segments.Count > 0 ? _segments[^1].Index : -1;
        }
    }

    /// <summary>Finished segments (oldest first), whose files are complete and safe to read.</summary>
    public IReadOnlyList<Segment> ClosedSegments()
    {
        lock (_gate) return _segments.Where(s => s.Closed).OrderBy(s => s.Index).ToList();
    }

    public bool IsClosed(int index)
    {
        lock (_gate) return _segments.Any(s => s.Index == index && s.Closed) || _segments.All(s => s.Index != index);
    }

    public void Finish()
    {
        List<Segment> open;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            open = _segments.Where(s => s.Writer != null).ToList();
        }
        foreach (var s in open)
        {
            var w = s.Writer!;
            s.Writer = null;
            try
            {
                if (s.VideoSamples == 0) w.Abort();
                else w.Dispose();
            }
            catch (Exception ex) { Log.Warn($"리플레이 구간 종료 오류: {ex.Message}"); }
        }
        try { _prepared?.Wait(2000); if (_prepared is { IsCompletedSuccessfully: true }) _prepared.Result.Abort(); } catch { }
    }

    public void Dispose()
    {
        Finish();
        // Give background finalizers a moment, then remove the temp folder.
        for (int i = 0; i < 20; i++)
        {
            lock (_gate) if (_segments.All(s => !s.Finalized || s.Closed)) break;
            Thread.Sleep(100);
        }
        try { Directory.Delete(_folder, true); } catch (Exception ex) { Log.Warn($"리플레이 임시 폴더 삭제 실패: {ex.Message}"); }
    }
}
