using CustomRecorder.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CustomRecorder.Core.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (기본)" : Name;

    public static IReadOnlyList<AudioDevice> GetMicrophones() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDevice> GetSpeakers() => List(DataFlow.Render);

    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = en.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { /* no device */ }
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId))
            .ToList();
    }
}

public sealed record AudioOptions(bool SystemAudio, bool Microphone, string? MicrophoneId, float SystemVolume = 1f, float MicrophoneVolume = 1f)
{
    /// <summary>Mute background noise on the microphone between words (<see cref="NoiseGate"/>).</summary>
    public bool MicNoiseGate { get; init; }
    /// <summary>Gate threshold set automatically from the room's background level.</summary>
    public bool MicNoiseGateAuto { get; init; } = true;
    /// <summary>Custom mode: level (dBFS) that opens the gate.</summary>
    public float MicNoiseGateThresholdDb { get; init; } = NoiseGate.DefaultThresholdDb;
}

/// <summary>
/// Captures system audio (WASAPI loopback) and microphone, converts both to 48 kHz stereo float and mixes them.
/// The output is pulled at the recording clock's pace, so the audio track has no gaps even while nothing is playing.
/// Silence is played on the output device so WASAPI loopback keeps delivering packets even when nothing else plays.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    private readonly List<Source> _sources = new();
    private WasapiOut? _silence;
    private float[] _tmp = Array.Empty<float>();

    /// <summary>Peak level of the microphone (0..1), for the UI meter.</summary>
    public float MicrophonePeak => _sources.FirstOrDefault(s => s.IsMicrophone)?.Peak ?? 0f;
    public float SystemPeak => _sources.FirstOrDefault(s => !s.IsMicrophone)?.Peak ?? 0f;
    public bool HasSources => _sources.Count > 0;
    public bool HasSystem => _sources.Any(s => !s.IsMicrophone);
    public bool HasMicrophone => _sources.Any(s => s.IsMicrophone);

    public AudioEngine(AudioOptions options)
    {
        using var en = new MMDeviceEnumerator();

        if (options.SystemAudio)
        {
            try
            {
                var render = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                var loopback = new WasapiLoopbackCapture(render);
                AddSource(new Source(loopback, false, options.SystemVolume));

                // Keep the loopback stream alive with silence (otherwise WASAPI sends nothing while the system is quiet).
                _silence = new WasapiOut(render, AudioClientShareMode.Shared, true, 100);
                _silence.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(render.AudioClient.MixFormat.SampleRate, 2)));
                Log.Info($"시스템 소리: {render.FriendlyName} {loopback.WaveFormat}");
            }
            catch (Exception ex)
            {
                Log.Error("시스템 소리 장치를 열 수 없습니다", ex);
            }
        }

        if (options.Microphone)
        {
            try
            {
                var device = options.MicrophoneId != null
                    ? en.GetDevice(options.MicrophoneId)
                    : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                var capture = new WasapiCapture(device, true, 20);
                AddSource(new Source(capture, true, options.MicrophoneVolume,
                    options.MicNoiseGate ? new GateConfig(options.MicNoiseGateAuto, options.MicNoiseGateThresholdDb) : null));
                Log.Info($"마이크: {device.FriendlyName} {capture.WaveFormat}");
            }
            catch (Exception ex)
            {
                Log.Error("마이크를 열 수 없습니다", ex);
            }
        }
    }

    private void AddSource(Source source) => _sources.Add(source);

    public void Start()
    {
        _silence?.Play();
        foreach (var s in _sources) s.Start();
    }

    /// <summary>Drops buffered audio (used when resuming from pause so paused audio is not recorded).</summary>
    public void ClearBuffers()
    {
        foreach (var s in _sources) s.Clear();
    }

    /// <summary>Reads mixed 48 kHz stereo float samples (interleaved). Underruns are filled with silence.</summary>
    public int Read(float[] buffer, int count) => Read(buffer, count, null, null);

    /// <summary>
    /// Reads <paramref name="count"/> interleaved samples of the mix and, optionally, of each source on its own
    /// (for separate system / microphone tracks). Every source is read exactly once per call, so all outputs stay in sync.
    /// </summary>
    public int Read(float[] mix, int count, float[]? system, float[]? microphone)
    {
        Array.Clear(mix, 0, count);
        if (system != null) Array.Clear(system, 0, count);
        if (microphone != null) Array.Clear(microphone, 0, count);
        if (_tmp.Length < count) _tmp = new float[count];

        foreach (var src in _sources)
        {
            int read = src.Output.Read(_tmp, 0, count);
            if (read < count) Array.Clear(_tmp, read, count - read);
            for (int i = 0; i < count; i++) mix[i] += _tmp[i];
            var own = src.IsMicrophone ? microphone : system;
            if (own != null) for (int i = 0; i < count; i++) own[i] += _tmp[i];
        }
        return count;
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
        try { _silence?.Stop(); } catch { }
        _silence?.Dispose();
    }

    private sealed class Source : IDisposable
    {
        private readonly IWaveIn _capture;
        private readonly BufferedWaveProvider _buffer;
        private readonly int _channels;
        private readonly bool _isFloat;

        public bool IsMicrophone { get; }
        public ISampleProvider Output { get; }
        public float Peak { get; private set; }

        public Source(IWaveIn capture, bool isMicrophone, float volume, GateConfig? noiseGate = null)
        {
            _capture = capture;
            IsMicrophone = isMicrophone;
            var fmt = capture.WaveFormat;
            _channels = fmt.Channels;
            _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));

            _buffer = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            capture.DataAvailable += OnData;

            ISampleProvider p = _buffer.ToSampleProvider();
            if (p.WaveFormat.Channels == 1) p = new MonoToStereoSampleProvider(p);
            else if (p.WaveFormat.Channels > 2) p = new FirstTwoChannels(p);
            if (p.WaveFormat.SampleRate != SampleRate) p = new WdlResamplingSampleProvider(p, SampleRate);
            if (noiseGate != null)
            {
                var g = noiseGate.Value;
                p = new NoiseGate(p, g.Auto, g.ThresholdDb);
                Log.Info(g.Auto ? "마이크 잡음 줄이기: 자동" : $"마이크 잡음 줄이기: 직접 설정 {g.ThresholdDb:F0} dB");
            }
            Output = new VolumeSampleProvider(p) { Volume = volume };
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Peak = MeasurePeak(e.Buffer, e.BytesRecorded);
        }

        private float MeasurePeak(byte[] data, int bytes)
        {
            float max = 0;
            if (_isFloat)
            {
                var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data.AsSpan(0, bytes - bytes % 4));
                for (int i = 0; i < span.Length; i += _channels) max = Math.Max(max, Math.Abs(span[i]));
            }
            else
            {
                var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data.AsSpan(0, bytes - bytes % 2));
                for (int i = 0; i < span.Length; i += _channels) max = Math.Max(max, Math.Abs(span[i] / 32768f));
            }
            return max;
        }

        public void Start() => _capture.StartRecording();

        public void Clear() => _buffer.ClearBuffer();

        public void Dispose()
        {
            _capture.DataAvailable -= OnData;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }
    }

    /// <summary>Takes front-left/front-right from multichannel (e.g. 5.1/7.1) devices.</summary>
    private sealed class FirstTwoChannels : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly int _srcChannels;
        private float[] _tmp = Array.Empty<float>();

        public FirstTwoChannels(ISampleProvider src)
        {
            _src = src;
            _srcChannels = src.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            int need = frames * _srcChannels;
            if (_tmp.Length < need) _tmp = new float[need];
            int read = _src.Read(_tmp, 0, need);
            int got = read / _srcChannels;
            for (int f = 0; f < got; f++)
            {
                buffer[offset + f * 2] = _tmp[f * _srcChannels];
                buffer[offset + f * 2 + 1] = _tmp[f * _srcChannels + 1];
            }
            return got * 2;
        }
    }
}

internal readonly record struct GateConfig(bool Auto, float ThresholdDb);
