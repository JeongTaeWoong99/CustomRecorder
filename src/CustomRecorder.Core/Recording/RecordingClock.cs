using System.Diagnostics;

namespace CustomRecorder.Core.Recording;

/// <summary>
/// Recording timeline. "Recorded time" excludes paused intervals, so video and audio timestamps
/// both jump over pauses without leaving gaps in the file.
/// </summary>
public sealed class RecordingClock
{
    private readonly Stopwatch _sw = new();
    private readonly object _gate = new();
    private long _pausedTicks;
    private long _pauseStartedAt = -1;

    public bool IsPaused { get { lock (_gate) return _pauseStartedAt >= 0; } }

    public void Start() => _sw.Restart();

    public void Pause()
    {
        lock (_gate)
        {
            if (_pauseStartedAt < 0) _pauseStartedAt = _sw.ElapsedTicks;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_pauseStartedAt < 0) return;
            _pausedTicks += _sw.ElapsedTicks - _pauseStartedAt;
            _pauseStartedAt = -1;
        }
    }

    /// <summary>Recorded time in 100-ns units (Media Foundation timebase).</summary>
    public long Recorded100ns
    {
        get
        {
            lock (_gate)
            {
                long now = _pauseStartedAt >= 0 ? _pauseStartedAt : _sw.ElapsedTicks;
                long ticks = now - _pausedTicks;
                return (long)(ticks * (10_000_000.0 / Stopwatch.Frequency));
            }
        }
    }

    public TimeSpan Recorded => TimeSpan.FromTicks(Recorded100ns);
}
