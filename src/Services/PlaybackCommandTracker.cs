using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Serializes command bookkeeping, never the remote calls themselves. A reader may clear
/// a pending observation only for the exact command generation captured before its read.
/// </summary>
internal sealed class PlaybackCommandTracker
{
    private readonly object _sync = new();
    private long _generation;
    private int _inFlight;
    private MediaTransportPlaybackStatus _expected;
    private bool _uncertain;

    internal bool IsUncertain { get { lock (_sync) return _uncertain; } }
    internal bool HasPendingExpected { get { lock (_sync) return _expected != MediaTransportPlaybackStatus.Unknown; } }
    internal bool HasUnconfirmed { get { lock (_sync) return _uncertain || _inFlight != 0 || _expected != MediaTransportPlaybackStatus.Unknown; } }

    internal long Begin(MediaTransportPlaybackStatus expected)
    {
        lock (_sync)
        {
            if (_inFlight != 0 || _expected != MediaTransportPlaybackStatus.Unknown) _uncertain = true;
            _inFlight++;
            _expected = expected;
            return ++_generation;
        }
    }

    internal void Complete(long generation, bool accepted, bool outcomeKnown)
    {
        lock (_sync)
        {
            _inFlight--;
            if (!outcomeKnown) _uncertain = true;
            if (generation == _generation && outcomeKnown && !accepted)
            {
                _expected = MediaTransportPlaybackStatus.Unknown;
                _generation++;
            }
        }
    }

    internal MediaTransportState Read(Func<MediaTransportState> read)
    {
        long generation;
        MediaTransportPlaybackStatus expected;
        int inFlight;
        lock (_sync)
        {
            generation = _generation;
            expected = _expected;
            inFlight = _inFlight;
        }
        // GSMTC can marshal across apartments. Never invoke it while holding _sync.
        var state = read();
        lock (_sync)
        {
            if (generation == _generation && inFlight == 0 && _inFlight == 0 &&
                ((expected == MediaTransportPlaybackStatus.Playing && state.IsPlaying) ||
                 (expected == MediaTransportPlaybackStatus.Paused && state.PlaybackStatus is
                     MediaTransportPlaybackStatus.Paused or MediaTransportPlaybackStatus.Stopped or MediaTransportPlaybackStatus.Closed)))
            {
                _expected = MediaTransportPlaybackStatus.Unknown;
                _generation++;
            }
        }
        return state;
    }
}
