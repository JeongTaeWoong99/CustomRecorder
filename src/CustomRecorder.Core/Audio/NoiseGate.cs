using NAudio.Wave;

namespace CustomRecorder.Core.Audio;

/// <summary>
/// Noise gate for the microphone: mutes quiet background sound (fans, keyboard, room hiss) between words and opens
/// smoothly as soon as you speak. A simple level gate — not AI noise removal, so it does not clean noise *during* speech.
/// <para>
/// Automatic mode (like Discord's "automatically determine input sensitivity") measures the room's background level
/// and opens only for sound clearly above it. Custom mode uses a fixed threshold chosen by the user.
/// </para>
/// </summary>
public sealed class NoiseGate : ISampleProvider
{
    /// <summary>Default threshold for custom mode (dBFS, peak).</summary>
    public const float DefaultThresholdDb = -46f;
    public const float MinThresholdDb = -80f, MaxThresholdDb = 0f;
    private const float HysteresisDb = 6f;           // closes this much below the opening level (no chattering)

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly bool _auto;
    private float _openLevel, _closeLevel;            // linear peak thresholds
    private readonly float _attackStep, _releaseStep; // gain change per frame
    private readonly int _holdFrames;
    private readonly float _envelopeDecay;
    private readonly NoiseFloorTracker? _floor;
    private float _gain;
    private int _hold;
    private float _envelope;

    /// <param name="auto">Measure the background level and set the threshold automatically.</param>
    /// <param name="thresholdDb">Custom mode: peak level (dBFS) that opens the gate.</param>
    public NoiseGate(ISampleProvider source, bool auto = true, float thresholdDb = DefaultThresholdDb)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        int rate = source.WaveFormat.SampleRate;
        _auto = auto;
        // Auto starts strict (only clear speech opens it) and relaxes after the first 10 ms of measurement.
        SetThreshold(auto ? NoiseFloorTracker.MaxAutoDb : Math.Clamp(thresholdDb, MinThresholdDb, MaxThresholdDb));
        _attackStep = 1f / (rate * 0.005f);   // 5 ms to open
        _releaseStep = 1f / (rate * 0.150f);  // 150 ms to close
        _holdFrames = (int)(rate * 0.20f);    // stay open 200 ms after the last loud sound
        _envelopeDecay = MathF.Exp(-1f / (rate * 0.010f));
        if (auto) _floor = new NoiseFloorTracker(rate);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The level (dBFS) that currently opens the gate.</summary>
    public float ThresholdDb { get; private set; }

    private void SetThreshold(float openDb)
    {
        ThresholdDb = openDb;
        _openLevel = DbToLinear(openDb);
        _closeLevel = DbToLinear(openDb - HysteresisDb);
    }

    internal static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        for (int i = 0; i < read; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels; c++) peak = MathF.Max(peak, MathF.Abs(buffer[offset + i + c]));
            _envelope = MathF.Max(peak, _envelope * _envelopeDecay);

            if (_floor != null && _floor.Add(buffer[offset + i]) && _floor.AutoThresholdDb is float auto)
                SetThreshold(auto);

            if (_envelope >= _openLevel) _hold = _holdFrames;
            else if (_envelope < _closeLevel && _hold > 0) _hold--;
            bool open = _hold > 0;

            _gain = open ? MathF.Min(1f, _gain + _attackStep) : MathF.Max(0f, _gain - _releaseStep);
            for (int c = 0; c < _channels; c++) buffer[offset + i + c] *= _gain;
        }
        return read;
    }
}

/// <summary>
/// Estimates the background noise level: the quietest 10 ms block (RMS) within the last few seconds.
/// Speech always has short pauses, so the minimum follows the room noise, not the voice.
/// </summary>
public sealed class NoiseFloorTracker
{
    /// <summary>The gate opens this far above the background level.</summary>
    public const float MarginDb = 15f;
    /// <summary>Automatic threshold limits: never so low that hiss opens it, never so high that normal speech is cut.</summary>
    public const float MinAutoDb = -60f, MaxAutoDb = -30f;

    private readonly int _blockFrames;
    private readonly float[] _history;   // block levels (dB) over the window
    private int _historyCount, _historyPos;
    private double _sum;
    private int _frames;

    public NoiseFloorTracker(int sampleRate, double windowSeconds = 3.0)
    {
        _blockFrames = Math.Max(1, sampleRate / 100);
        _history = new float[Math.Max(1, (int)(windowSeconds * 100))];
    }

    /// <summary>Background level (dBFS RMS), or null before the first block.</summary>
    public float? FloorDb { get; private set; }

    public float? AutoThresholdDb => FloorDb is float f ? Math.Clamp(f + MarginDb, MinAutoDb, MaxAutoDb) : null;

    /// <summary>Adds one sample (one channel is enough). Returns true when a block finished and the floor was updated.</summary>
    public bool Add(float sample)
    {
        _sum += sample * (double)sample;
        if (++_frames < _blockFrames) return false;

        float db = (float)(10 * Math.Log10(_sum / _frames + 1e-12));
        _sum = 0;
        _frames = 0;
        _history[_historyPos] = db;
        _historyPos = (_historyPos + 1) % _history.Length;
        if (_historyCount < _history.Length) _historyCount++;

        float min = float.MaxValue;
        for (int i = 0; i < _historyCount; i++) min = MathF.Min(min, _history[i]);
        FloorDb = min;
        return true;
    }
}
