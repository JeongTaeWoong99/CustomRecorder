using System.Numerics;
using System.Buffers;
using CustomRecorder.Core.Audio;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Effects;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace CustomRecorder.Core.Recording;

public enum RecorderState { Idle, Recording, Paused, Stopping }

/// <summary>
/// One recording session: WGC capture → latest-frame texture → constant-frame-rate video pump → H.264,
/// and audio engine → clock-paced audio pump → AAC, both muxed into fragmented MP4.
/// </summary>
public sealed class ScreenRecorder : IRecorder
{
    private const long AudioDelay100ns = 1_500_000; // read audio 150 ms behind real time so capture buffers never underrun

    private readonly RecorderOptions _options;
    private readonly D3DContext _d3d;
    private readonly RecordingClock _clock = new();
    private readonly object _frameLock = new();

    private WgcCapture? _capture;
    private IMediaSink? _writer;
    private AudioEngine? _audio;
    private ID3D11Texture2D? _latest;     // last captured frame, cropped to output size
    private bool _hasFrame;
    private PixelRect _crop;              // crop rectangle inside the captured surface
    private int _width, _height;
    private int _encW, _encH;
    private GpuScaler? _scaler;
    private FrameOverlay? _overlay;
    private MouseClickHook? _clickHook;
    private KeyboardHook? _keyHook;

    private Thread? _videoThread, _audioThread;
    private volatile bool _running;
    private long _framesWritten, _framesCaptured;
    private long _framesSkipped, _lateFrames;
    private EncoderTexturePool? _pool;
    private double _frameMsTotal, _frameMsMax;
    private long _frameMsCount;

    /// <summary>Frames lost because the PC stalled for over a second (the gap is skipped, not filled).</summary>
    public long DroppedFrames => Interlocked.Read(ref _framesSkipped);
    /// <summary>Frames written more than two frame-times late (heavy load; the video still keeps every frame).</summary>
    public long LateFrames => Interlocked.Read(ref _lateFrames);

    private void RecordFrameTime(TimeSpan t)
    {
        double ms = t.TotalMilliseconds;
        _frameMsTotal += ms;
        _frameMsCount++;
        if (ms > _frameMsMax) _frameMsMax = ms;
    }
    private long _audioFramesWritten;

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public TimeSpan Elapsed => _clock.Recorded;
    public string OutputPath => _options.OutputPath;

    /// <summary>
    /// The finished file is a fragmented MP4. It is safe against crashes while recording, but some players show a
    /// wrong length or seek badly, so the app rewrites it as a regular MP4 after stopping (<see cref="Mp4Trim.Remux"/>).
    /// </summary>
    public bool NeedsRemux => _writer is Mp4Writer { Fragmented: true };
    public float MicrophonePeak => _audio?.MicrophonePeak ?? 0;
    public float SystemPeak => _audio?.SystemPeak ?? 0;

    /// <summary>Raised on a worker thread when recording stops by itself (window closed, encoder error).</summary>
    public event Action<string>? Faulted;

    public ScreenRecorder(D3DContext d3d, RecorderOptions options)
    {
        _d3d = d3d;
        _options = options;
    }

    public void Start()
    {
        if (State != RecorderState.Idle) throw new InvalidOperationException("이미 녹화 중입니다.");

        _capture = new WgcCapture(_d3d, _options.Target);
        var item = _capture.ItemSize;

        (_crop, _width, _height) = _options.Target switch
        {
            RegionTarget r => CropForRegion(r),
            _ => (new PixelRect(0, 0, item.Width, item.Height).ToEven(), item.Width & ~1, item.Height & ~1),
        };
        if (_width < 16 || _height < 16) throw new InvalidOperationException($"녹화 크기가 너무 작습니다 ({_width}x{_height}).");

        _latest = _d3d.CreateTexture(_width, _height, forEncoder: true); // render-target binding: video processor input views need it on some drivers

        _audio = (_options.Audio.SystemAudio || _options.Audio.Microphone) ? new AudioEngine(_options.Audio) : null;
        if (_audio is { HasSources: false }) { _audio.Dispose(); _audio = null; }

        var (outW, outH) = _options.OutputSizeFor(_width, _height);
        int bitrate = _options.BitrateOverrideBps > 0
            ? _options.BitrateOverrideBps
            : RecorderOptions.ComputeBitrate(outW, outH, _options.Fps, _options.Quality);
        if (_options.Codec == VideoCodec.Hevc && _options.BitrateOverrideBps <= 0) bitrate = bitrate * 6 / 10; // HEVC: same quality at ~60%
        // Downscaling is done here on the GPU video processor, so the encoder's input and output sizes always match.
        if (outW != _width || outH != _height)
        {
            try
            {
                _scaler = new GpuScaler(_d3d, _width, _height, outW, outH, _options.Fps);
                using var probe = _d3d.CreateTexture(outW, outH, forEncoder: true);
                lock (_d3d.ContextLock) _scaler.Scale(_latest, probe); // fail here, not mid-recording
            }
            catch (Exception ex)
            {
                Log.Warn($"GPU 크기 조정 사용 불가 → 원본 크기로 녹화: {ex.Message}");
                _scaler?.Dispose();
                _scaler = null;
                (outW, outH) = (_width, _height);
            }
        }
        _encW = outW; _encH = outH;
        _pool = new EncoderTexturePool(_d3d, outW, outH);

        if (_options.Overlay is { Any: true } overlay)
        {
            try
            {
                _overlay = new FrameOverlay(_d3d, overlay, outW, outH, (float)outW / _width);
                if (overlay.ClickEffect)
                    _clickHook = new MouseClickHook((x, y, right) =>
                    {
                        if (_clock.IsPaused || _overlay == null) return;
                        if (ToFrame(x, y) is { } pos) _overlay.AddClick(_clock.Recorded100ns, pos, right);
                    });
                if (overlay.KeyDisplay != KeyDisplayMode.Off)
                    _keyHook = new KeyboardHook(overlay.KeyDisplay, (text, plain) =>
                    {
                        if (!_clock.IsPaused) _overlay?.AddKey(_clock.Recorded100ns, text, plain);
                    });
            }
            catch (Exception ex) { Log.Warn($"효과 사용 불가 (Direct2D): {ex.Message}"); }
        }
        var video = new VideoEncodeSettings(outW, outH, _options.Fps, bitrate, _options.Codec, KeyframeSeconds: _options.KeyframeSeconds);
        _separateTracks = _audio != null && _options.SeparateAudioTracks && _options.ReplayLength == null
                          && _audio.HasSystem && _audio.HasMicrophone;
        var audio = _audio != null
            ? new AudioEncodeSettings(AudioEngine.SampleRate, AudioEngine.Channels, Tracks: _separateTracks ? 3 : 1)
            : null;
        if (_options.ReplayLength is { } keep)
        {
            _writer = new SegmentedMp4Writer(_options.OutputPath, TimeSpan.FromSeconds(10), keep, audio != null,
                path => new Mp4Writer(path, _d3d, video, null, fragmented: false));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.OutputPath)!);
            _writer = new Mp4Writer(_options.OutputPath, _d3d, video, audio);
        }

        _capture.FrameArrived += OnFrame;
        _capture.ItemClosed += () => Fault("녹화 중인 창이 닫혔습니다.");

        _running = true;
        NativeMethods.timeBeginPeriod(1);
        _clock.Start();
        _capture.Start(_options.CaptureCursor);
        _audio?.Start();

        _videoThread = new Thread(VideoLoop) { Name = "VideoPump", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _videoThread.Start();
        if (_audio != null)
        {
            _audioThread = new Thread(AudioLoop) { Name = "AudioPump", IsBackground = true, Priority = ThreadPriority.Highest };
            _audioThread.Start();
        }

        State = RecorderState.Recording;
        Log.Info($"녹화 시작: {_options.Target.DisplayName} → {_width}x{_height}{(_scaler != null ? $" (출력 {_encW}x{_encH})" : "")}@{_options.Fps} {_options.Codec}, crop={_crop}");
    }

    private static (PixelRect crop, int w, int h) CropForRegion(RegionTarget r)
    {
        var region = r.Region.Intersect(r.Monitor.Bounds).ToEven();
        var crop = new PixelRect(region.X - r.Monitor.Bounds.X, region.Y - r.Monitor.Bounds.Y, region.Width, region.Height);
        return (crop, region.Width, region.Height);
    }

    // Capture thread (WGC free-threaded pool): copy the interesting part into _latest.
    private void OnFrame(ID3D11Texture2D frame, int frameWidth, int frameHeight)
    {
        if (!_running || _latest == null) return;
        Interlocked.Increment(ref _framesCaptured);

        var src = _crop.Intersect(new PixelRect(0, 0, frameWidth, frameHeight));
        if (src.Width <= 0 || src.Height <= 0) return;

        lock (_frameLock)
        lock (_d3d.ContextLock)
        {
            var box = new Box(src.X, src.Y, 0, src.Right, src.Bottom, 1);
            _d3d.Context.CopySubresourceRegion(_latest, 0, 0, 0, 0, frame, 0, box);
            _hasFrame = true;
        }
    }

    /// <summary>Screen point → output-frame pixels, or null when it is outside the recorded area.</summary>
    private Vector2? ToFrame(int screenX, int screenY)
    {
        int ox, oy;
        switch (_options.Target)
        {
            case MonitorTarget m: ox = m.Monitor.Bounds.X; oy = m.Monitor.Bounds.Y; break;
            case RegionTarget r: ox = r.Monitor.Bounds.X; oy = r.Monitor.Bounds.Y; break;
            case WindowTarget w:
                if (NativeMethods.DwmGetWindowAttribute(w.Window.Handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT rc,
                        System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0) return null;
                ox = rc.Left; oy = rc.Top; break;
            default: return null;
        }
        float x = screenX - ox - _crop.X, y = screenY - oy - _crop.Y;
        if (x < 0 || y < 0 || x >= _width || y >= _height) return null;
        float s = (float)_encW / _width;
        return new Vector2(x * s, y * s);
    }

    // Constant frame rate: every tick encodes the most recent frame (duplicates it when the screen did not change).
    private void VideoLoop()
    {
        long frameDuration = 10_000_000L / _options.Fps;
        try
        {
            while (_running)
            {
                if (_clock.IsPaused) { Thread.Sleep(5); continue; }

                long due = _framesWritten * frameDuration;
                long now = _clock.Recorded100ns;
                if (now < due)
                {
                    long waitMs = (due - now) / 10_000;
                    Thread.Sleep(waitMs > 1 ? (int)waitMs - 1 : 0);
                    continue;
                }

                // Fell behind by more than one second (system stall): skip ahead instead of bursting duplicates.
                long behind = (now - due) / frameDuration;
                if (behind > _options.Fps) { _framesWritten += behind; Interlocked.Add(ref _framesSkipped, behind); }
                else if (behind >= 2 && now > 10_000_000) Interlocked.Increment(ref _lateFrames); // the first second is encoder warm-up

                if (!_hasFrame) { _framesWritten++; continue; }

                long frameTime = _framesWritten * frameDuration;
                Vector2? cursor = null;
                if (_overlay != null)
                {
                    cursor = NativeMethods.GetCursorPos(out var cp) ? ToFrame(cp.X, cp.Y) : null;
                }

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var progress = _writer as IEncodeProgress;
                var tex = _pool!.Rent(progress);
                bool submitted = false;
                try
                {
                    lock (_frameLock)
                    lock (_d3d.ContextLock)
                    {
                        if (_scaler != null) _scaler.Scale(_latest!, tex);
                        else _d3d.Context.CopyResource(tex, _latest!);
                        _overlay?.Draw(tex, frameTime, cursor);
                    }
                    long seq = progress?.VideoSubmitted ?? 0;
                    _writer!.WriteVideo(tex, _framesWritten * frameDuration, frameDuration);
                    submitted = true;
                    _pool.Submitted(tex, progress, seq);
                }
                finally
                {
                    if (!submitted) _pool.Return(tex);
                }
                RecordFrameTime(System.Diagnostics.Stopwatch.GetElapsedTime(t0));
                _framesWritten++;
            }
        }
        catch (Exception ex)
        {
            Log.Error("비디오 인코딩 오류", ex);
            Fault($"비디오 인코딩 오류: {ex.Message}");
        }
    }

    // Audio is pulled at the clock's pace: the track always has exactly (recorded time × 48000) samples.
    private const int AudioChunkFrames = AudioEngine.SampleRate / 100; // 10 ms
    private readonly float[] _audioFloats = new float[AudioChunkFrames * AudioEngine.Channels];
    private readonly byte[] _audioPcm = new byte[AudioChunkFrames * AudioEngine.Channels * 2];

    private void AudioLoop()
    {
        try
        {
            while (_running)
            {
                if (_clock.IsPaused) { Thread.Sleep(5); continue; }

                long target = (_clock.Recorded100ns - AudioDelay100ns) * AudioEngine.SampleRate / 10_000_000L;
                if (_audioFramesWritten + AudioChunkFrames > target) { Thread.Sleep(2); continue; }
                WriteAudioChunk();
            }
        }
        catch (Exception ex)
        {
            Log.Error("오디오 인코딩 오류", ex);
            Fault($"오디오 인코딩 오류: {ex.Message}");
        }
    }

    private bool _separateTracks;
    private readonly float[] _systemFloats = new float[AudioChunkFrames * AudioEngine.Channels];
    private readonly float[] _micFloats = new float[AudioChunkFrames * AudioEngine.Channels];

    private void WriteAudioChunk()
    {
        long time = _audioFramesWritten * 10_000_000L / AudioEngine.SampleRate;
        if (_separateTracks)
        {
            _audio!.Read(_audioFloats, _audioFloats.Length, _systemFloats, _micFloats);
            _writer!.WriteAudio(PcmConvert.ToPcm16(_audioFloats, _audioPcm), time, 0);
            _writer.WriteAudio(PcmConvert.ToPcm16(_systemFloats, _audioPcm), time, 1);
            _writer.WriteAudio(PcmConvert.ToPcm16(_micFloats, _audioPcm), time, 2);
        }
        else
        {
            _audio!.Read(_audioFloats, _audioFloats.Length);
            _writer!.WriteAudio(PcmConvert.ToPcm16(_audioFloats, _audioPcm), time);
        }
        _audioFramesWritten += AudioChunkFrames;
    }

    /// <summary>The audio pump runs slightly behind real time; at stop, write the buffered tail so audio ends with the video.</summary>
    private void FlushAudioTail()
    {
        if (_audio == null || _writer == null) return;
        long videoEnd = _framesWritten * 10_000_000L / _options.Fps;
        long endFrames = videoEnd * AudioEngine.SampleRate / 10_000_000L;
        int guard = 0;
        while (_audioFramesWritten < endFrames && guard++ < 100) WriteAudioChunk();
    }

    public bool IsReplay => _options.ReplayLength != null;

    /// <summary>
    /// Saves roughly the last <see cref="RecorderOptions.ReplayLength"/> of footage to <paramref name="outputPath"/>
    /// (rounded up to whole 10-second segments, since segments can only be cut on keyframes).
    /// </summary>
    public async Task SaveReplayAsync(string outputPath)
    {
        if (_writer is not SegmentedMp4Writer seg || _options.ReplayLength is not { } keep)
            throw new InvalidOperationException("리플레이 버퍼가 켜져 있지 않습니다.");

        int closing = seg.ForceRotate();
        // Wait until the segment that was open is fully written (audio lags ~150 ms, finalize takes a moment).
        for (int i = 0; i < 100 && closing >= 0 && !seg.IsClosed(closing); i++) await Task.Delay(50);

        await Task.Delay(250); // the audio pump runs ~150 ms behind; let it catch up to the cut point

        var closed = seg.ClosedSegments();
        if (closed.Count == 0) throw new InvalidOperationException("아직 저장할 영상이 없습니다.");

        var chosen = new List<SegmentedMp4Writer.Segment>();
        long total = 0;
        for (int i = closed.Count - 1; i >= 0 && total < keep.Ticks; i--)
        {
            chosen.Insert(0, closed[i]);
            total += closed[i].Duration;
        }
        long origin = chosen[0].Start;
        long end = chosen[^1].End;
        var parts = chosen.Select(s => new Mp4Concat.Part(s.Path, s.Start - origin)).ToList();
        var audio = seg.HasAudio
            ? new Mp4Concat.PcmTrack(new AudioEncodeSettings(AudioEngine.SampleRate, AudioEngine.Channels),
                seg.GetAudio(origin, end).Select(c => (c.Time - origin, c.Pcm)).ToList())
            : null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await Task.Run(() => Mp4Concat.Join(parts, outputPath, audio));
    }

    public void Pause()
    {
        if (State != RecorderState.Recording) return;
        _clock.Pause();
        State = RecorderState.Paused;
        Log.Info("일시정지");
    }

    public void Resume()
    {
        if (State != RecorderState.Paused) return;
        _audio?.ClearBuffers();
        _clock.Resume();
        State = RecorderState.Recording;
        Log.Info("재개");
    }

    /// <summary>Stops and finalizes the file. Safe to call more than once.</summary>
    public void Stop()
    {
        if (State is RecorderState.Idle or RecorderState.Stopping) return;
        State = RecorderState.Stopping;

        _running = false;
        _capture?.Dispose();
        _videoThread?.Join(3000);
        _audioThread?.Join(3000);

        try { FlushAudioTail(); }
        catch (Exception ex) { Log.Warn($"오디오 끝부분 기록 실패: {ex.Message}"); }

        try { _writer?.Finish(); }
        finally
        {
            NativeMethods.timeEndPeriod(1);
            Log.Info($"녹화 종료: {Elapsed:hh\\:mm\\:ss} frames written={_framesWritten} captured={_framesCaptured} audio={_audioFramesWritten / (double)AudioEngine.SampleRate:F1}s");
            if (_frameMsCount > 0)
                Log.Info($"프레임 처리: 평균 {_frameMsTotal / _frameMsCount:F2}ms, 최대 {_frameMsMax:F1}ms · 텍스처 생성 {_pool?.Created} / 재사용 {_pool?.Reused} · 지연 {LateFrames} · 누락 {DroppedFrames}");
            State = RecorderState.Idle;
        }
    }

    private int _faulted;
    private void Fault(string reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Stop(); } catch (Exception ex) { Log.Error("중지 중 오류", ex); }
            Faulted?.Invoke(reason);
        });
    }

    public void Dispose()
    {
        try { Stop(); } catch (Exception ex) { Log.Error("Dispose 중 오류", ex); }
        _writer?.Dispose();
        _audio?.Dispose();
        _scaler?.Dispose();
        _scaler = null;
        _pool?.Dispose();
        _pool = null;
        _clickHook?.Dispose();
        _clickHook = null;
        _keyHook?.Dispose();
        _keyHook = null;
        _overlay?.Dispose();
        _overlay = null;
        _latest?.Dispose();
    }
}
