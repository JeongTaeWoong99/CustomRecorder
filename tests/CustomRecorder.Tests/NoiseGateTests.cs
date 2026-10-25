using CustomRecorder.Core.Audio;
using NAudio.Wave;
using Xunit;

public class NoiseGateTests
{
    /// <summary>Stereo 48 kHz: faint noise all the time, a loud tone between 1 s and 2 s.</summary>
    private sealed class TestSignal(float noiseAmp = 0.002f) : ISampleProvider
    {
        private readonly Random _random = new(1);
        private long _frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i += 2)
            {
                double t = _frame++ / 48000.0;
                float noise = (float)((_random.NextDouble() * 2 - 1) * noiseAmp);           // 0.002 ≈ -54 dBFS peak
                float tone = t is > 1.0 and < 2.0 ? (float)(0.2 * Math.Sin(2 * Math.PI * 220 * t)) : 0; // -14 dBFS
                buffer[offset + i] = buffer[offset + i + 1] = noise + tone;
            }
            return count;
        }
    }

    private static float[] PeaksPer10Ms(int blocks) => PeaksPer10Ms(new NoiseGate(new TestSignal()), blocks);

    private static float[] PeaksPer10Ms(NoiseGate gate, int blocks)
    {
        var buf = new float[480 * 2];
        var peaks = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            gate.Read(buf, 0, buf.Length);
            peaks[b] = buf.Max(MathF.Abs);
        }
        return peaks;
    }

    [Fact]
    public void NoiseAloneIsMuted() => Assert.All(PeaksPer10Ms(90), p => Assert.Equal(0f, p));

    [Fact]
    public void SpeechPassesUntouched()
    {
        var peaks = PeaksPer10Ms(200);
        for (int b = 102; b < 198; b++) Assert.InRange(peaks[b], 0.19f, 0.21f);
    }

    [Fact]
    public void TailIsHeldThenFades()
    {
        var peaks = PeaksPer10Ms(300);
        Assert.True(peaks[210] > 0, "kept open shortly after speech (no clipped word endings)");
        Assert.Equal(0f, peaks[260]);
    }

    [Fact]
    public void AutoAdaptsToLoudFan()
    {
        // Fan noise at -40 dBFS peak would keep a fixed -46 dB gate open; automatic mode learns it and closes.
        var auto = PeaksPer10Ms(new NoiseGate(new TestSignal(0.01f), auto: true), 200);
        for (int b = 30; b < 100; b++) Assert.Equal(0f, auto[b]);
        for (int b = 102; b < 198; b++) Assert.InRange(auto[b], 0.19f, 0.22f);

        var fixedGate = PeaksPer10Ms(new NoiseGate(new TestSignal(0.01f), auto: false, thresholdDb: -46f), 90);
        Assert.True(fixedGate[60] > 0, "fixed threshold below the fan level lets the fan through");
    }

    [Fact]
    public void CustomThresholdIsUsed()
    {
        // Threshold -60 dB: the -54 dB hiss opens it. Threshold -20 dB: the -14 dB voice still passes.
        var low = PeaksPer10Ms(new NoiseGate(new TestSignal(), auto: false, thresholdDb: -60f), 90);
        Assert.True(low[60] > 0);
        var high = PeaksPer10Ms(new NoiseGate(new TestSignal(), auto: false, thresholdDb: -20f), 200);
        Assert.Equal(0f, high[60]);
        Assert.InRange(high[150], 0.19f, 0.21f);
    }

    [Fact]
    public void FloorTrackerIgnoresSpeechAndFollowsRoom()
    {
        var t = new NoiseFloorTracker(48000);
        var rnd = new Random(2);
        for (int i = 0; i < 48000 * 3; i++)
        {
            double sec = i / 48000.0;
            float noise = (float)((rnd.NextDouble() * 2 - 1) * 0.002);
            // speech-like: 300 ms words with 100 ms pauses
            float voice = (sec % 0.4) < 0.3 ? (float)(0.3 * Math.Sin(2 * Math.PI * 180 * sec)) : 0;
            t.Add(noise + voice);
        }
        Assert.InRange(t.FloorDb!.Value, -62f, -56f);    // uniform ±0.002 noise is about -59 dB RMS
        Assert.InRange(t.AutoThresholdDb!.Value, -47f, -41f);
    }
}
