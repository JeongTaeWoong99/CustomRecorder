namespace CustomRecorder.Core.Audio;

public static class PcmConvert
{
    /// <summary>Float samples (-1..1) → 16-bit little-endian PCM in <paramref name="pcm"/> (clipped).</summary>
    public static ReadOnlySpan<byte> ToPcm16(float[] samples, byte[] pcm)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        return pcm.AsSpan(0, samples.Length * 2);
    }
}
