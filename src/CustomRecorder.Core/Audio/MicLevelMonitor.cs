using CustomRecorder.Core.Util;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CustomRecorder.Core.Audio;

/// <summary>
/// Live microphone level for the settings screen (the sensitivity bar). Opens the microphone only while it exists,
/// and also runs the automatic-threshold estimate so the user sees where "auto" would open the gate.
/// </summary>
public sealed class MicLevelMonitor : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly NoiseFloorTracker _floor;
    private readonly int _channels, _bytesPerSample;
    private readonly bool _float;
    private float _peak;
    private readonly object _lock = new();

    public MicLevelMonitor(string? deviceId)
    {
        using var en = new MMDeviceEnumerator();
        var device = deviceId != null ? en.GetDevice(deviceId) : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _capture = new WasapiCapture(device, true, 20);
        var fmt = _capture.WaveFormat;
        _channels = fmt.Channels;
        _bytesPerSample = fmt.BitsPerSample / 8;
        _float = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                 (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        _floor = new NoiseFloorTracker(fmt.SampleRate);
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    /// <summary>Peak level since the last call (dBFS, -100 = silence).</summary>
    public float TakePeakDb()
    {
        float p;
        lock (_lock) { p = _peak; _peak = 0; }
        return p > 0 ? MathF.Max(-100f, 20f * MathF.Log10(p)) : -100f;
    }

    /// <summary>Where the automatic mode would open the gate right now (dBFS), or null while measuring.</summary>
    public float? AutoThresholdDb { get { lock (_lock) return _floor.AutoThresholdDb; } }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int step = _bytesPerSample * _channels;
        float peak = 0;
        lock (_lock)
        {
            for (int o = 0; o + step <= e.BytesRecorded; o += step)
            {
                float s = Sample(e.Buffer, o);
                peak = MathF.Max(peak, MathF.Abs(s));
                _floor.Add(s);
            }
            _peak = MathF.Max(_peak, peak);
        }
    }

    private float Sample(byte[] b, int o) => _bytesPerSample switch
    {
        4 when _float => BitConverter.ToSingle(b, o),
        4 => BitConverter.ToInt32(b, o) / 2147483648f,
        3 => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f,
        2 => BitConverter.ToInt16(b, o) / 32768f,
        _ => 0f,
    };

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch (Exception ex) { Log.Warn($"마이크 미리보기 중지: {ex.Message}"); }
        _capture.Dispose();
    }
}
