using CustomRecorder.Core.Audio;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Util;

namespace CustomRecorder.Core.Recording;

/// <summary>Records only sound (system and/or microphone) into M4A / MP3 / FLAC / WAV.</summary>
public sealed class AudioRecorder : IRecorder
{
    private const int ChunkFrames = AudioEngine.SampleRate / 100;   // 10 ms
    private const long Delay100ns = 1_000_000;                      // stay 100 ms behind so device buffers are filled

    private readonly AudioOptions _audioOptions;
    private readonly AudioFileFormat _format;
    private readonly RecordingClock _clock = new();
    private readonly float[] _floats = new float[ChunkFrames * AudioEngine.Channels];
    private readonly byte[] _pcm = new byte[ChunkFrames * AudioEngine.Channels * 2];
    private AudioEngine? _engine;
    private AudioFileWriter? _writer;
    private Thread? _thread;
    private volatile bool _running;
    private long _framesWritten;
    private int _faulted;

    public AudioRecorder(AudioOptions audio, string outputPath, AudioFileFormat format)
    {
        _audioOptions = audio;
        _format = format;
        OutputPath = outputPath;
    }

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public TimeSpan Elapsed => _clock.Recorded;
    public string OutputPath { get; }
    public float MicrophonePeak => _engine?.MicrophonePeak ?? 0;
    public float SystemPeak => _engine?.SystemPeak ?? 0;
    public event Action<string>? Faulted;

    public void Start()
    {
        if (State != RecorderState.Idle) throw new InvalidOperationException("이미 녹음 중입니다.");
        _engine = new AudioEngine(_audioOptions);
        if (!_engine.HasSources)
        {
            _engine.Dispose();
            _engine = null;
            throw new InvalidOperationException("녹음할 소리가 없습니다. 시스템 소리나 마이크를 켜세요.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        _writer = new AudioFileWriter(OutputPath, _format, new AudioEncodeSettings(AudioEngine.SampleRate, AudioEngine.Channels));

        _running = true;
        _clock.Start();
        _engine.Start();
        _thread = new Thread(Pump) { Name = "AudioOnlyPump", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        State = RecorderState.Recording;
    }

    private void Pump()
    {
        try
        {
            while (_running)
            {
                if (_clock.IsPaused) { Thread.Sleep(5); continue; }
                long target = (_clock.Recorded100ns - Delay100ns) * AudioEngine.SampleRate / 10_000_000L;
                if (_framesWritten + ChunkFrames > target) { Thread.Sleep(2); continue; }
                WriteChunk();
            }
        }
        catch (Exception ex)
        {
            Log.Error("녹음 오류", ex);
            if (Interlocked.Exchange(ref _faulted, 1) == 0)
                ThreadPool.QueueUserWorkItem(_ => { try { Stop(); } catch { } Faulted?.Invoke(ex.Message); });
        }
    }

    private void WriteChunk()
    {
        _engine!.Read(_floats, _floats.Length);
        _writer!.Write(PcmConvert.ToPcm16(_floats, _pcm), _framesWritten * 10_000_000L / AudioEngine.SampleRate);
        _framesWritten += ChunkFrames;
    }

    public void Pause()
    {
        if (State != RecorderState.Recording) return;
        _clock.Pause();
        State = RecorderState.Paused;
    }

    public void Resume()
    {
        if (State != RecorderState.Paused) return;
        _engine?.ClearBuffers();
        _clock.Resume();
        State = RecorderState.Recording;
    }

    public void Stop()
    {
        if (State is RecorderState.Idle or RecorderState.Stopping) return;
        State = RecorderState.Stopping;
        _running = false;
        _thread?.Join(3000);
        try
        {
            long end = _clock.Recorded100ns * AudioEngine.SampleRate / 10_000_000L;
            for (int guard = 0; _framesWritten < end && guard < 100; guard++) WriteChunk();
        }
        catch (Exception ex) { Log.Warn($"녹음 끝부분 기록 실패: {ex.Message}"); }
        try { _writer?.Finish(); }
        finally
        {
            Log.Info($"녹음 종료: {OutputPath} {_framesWritten / (double)AudioEngine.SampleRate:F1}s");
            State = RecorderState.Idle;
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch (Exception ex) { Log.Error("녹음 종료 오류", ex); }
        _writer?.Dispose();
        _engine?.Dispose();
    }
}
