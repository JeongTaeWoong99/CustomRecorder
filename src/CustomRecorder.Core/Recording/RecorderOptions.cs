using CustomRecorder.Core.Audio;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Effects;
using CustomRecorder.Core.Encoding;

namespace CustomRecorder.Core.Recording;

public enum QualityPreset
{
    High,      // 고화질
    Balanced,  // 균형
    Small,     // 작은 용량
}

public sealed record RecorderOptions(
    CaptureTarget Target,
    string OutputPath,
    int Fps,
    QualityPreset Quality,
    bool CaptureCursor,
    AudioOptions Audio)
{
    /// <summary>When set, records into a rolling buffer of this length instead of a file (<see cref="OutputPath"/> is the temp folder).</summary>
    public TimeSpan? ReplayLength { get; init; }

    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Manual bitrate; 0 = derived from <see cref="Quality"/>.</summary>
    public int BitrateOverrideBps { get; init; }

    public int KeyframeSeconds { get; init; } = 2;

    /// <summary>Cursor highlight, click effect, watermark. Null = none.</summary>
    public OverlayOptions? Overlay { get; init; }

    /// <summary>Also store system sound and microphone as their own tracks (track 1 stays the mix). Ignored for replay.</summary>
    public bool SeparateAudioTracks { get; init; }

    /// <summary>0 = original size, -1 = half size, otherwise the output height (never upscales).</summary>
    public int OutputHeight { get; init; }

    /// <summary>Encoded size for a captured size, keeping the aspect ratio and even dimensions.</summary>
    public (int Width, int Height) OutputSizeFor(int width, int height)
    {
        int h = OutputHeight switch
        {
            0 => height,
            -1 => height / 2,
            _ => Math.Min(OutputHeight, height),
        };
        if (h >= height) return (width, height);
        int w = (int)Math.Round(width * (double)h / height);
        return (Math.Max(16, w & ~1), Math.Max(16, h & ~1));
    }

    /// <summary>
    /// Bitrate from resolution × fps × bits-per-pixel. Screen content compresses well, so the factors are modest;
    /// 1080p60 gives ≈ 18 / 11 / 6 Mbps.
    /// </summary>
    public static int ComputeBitrate(int width, int height, int fps, QualityPreset quality)
    {
        double bpp = quality switch
        {
            QualityPreset.High => 0.15,
            QualityPreset.Balanced => 0.09,
            _ => 0.05,
        };
        double bps = width * (double)height * fps * bpp;
        return (int)Math.Clamp(bps, 1_000_000, 80_000_000);
    }
}
