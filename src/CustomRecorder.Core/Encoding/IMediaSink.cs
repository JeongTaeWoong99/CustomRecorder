using Vortice.Direct3D11;

namespace CustomRecorder.Core.Encoding;

/// <summary>Where the recorder sends encoded-to-be samples. Timestamps are on the recording timeline (100 ns).</summary>
public interface IMediaSink : IDisposable
{
    void WriteVideo(ID3D11Texture2D texture, long time100ns, long duration100ns);
    /// <param name="track">0 = main (mixed) track; 1.. = extra tracks when the sink was created with several.</param>
    void WriteAudio(ReadOnlySpan<byte> pcm, long time100ns, int track = 0);
    void Finish();
}
