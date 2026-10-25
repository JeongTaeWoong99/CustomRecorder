namespace CustomRecorder.Core.Recording;

/// <summary>Common surface of the screen recorder and the sound-only recorder (what the UI needs).</summary>
public interface IRecorder : IDisposable
{
    RecorderState State { get; }
    TimeSpan Elapsed { get; }
    string OutputPath { get; }
    float MicrophonePeak { get; }
    float SystemPeak { get; }
    event Action<string>? Faulted;
    void Start();
    void Pause();
    void Resume();
    void Stop();
}
