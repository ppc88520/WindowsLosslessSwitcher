using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class PlaybackCommandTrackerTests
{
    private static MediaTransportState State(MediaTransportPlaybackStatus status) =>
        new(status, true, true, TimeSpan.Zero, DateTimeOffset.UtcNow);

    [Fact]
    public void StalePausedReaderCannotClearNewPlayingExpectation()
    {
        var tracker = new PlaybackCommandTracker();
        var pause = tracker.Begin(MediaTransportPlaybackStatus.Paused);
        tracker.Complete(pause, true, true);
        tracker.Read(() =>
        {
            // This nested fresh read acknowledges the earlier Pause. A new Play can
            // then start while the outer UI reader still holds its old Paused snapshot.
            tracker.Read(() => State(MediaTransportPlaybackStatus.Paused));
            var play = tracker.Begin(MediaTransportPlaybackStatus.Playing);
            tracker.Complete(play, true, true);
            return State(MediaTransportPlaybackStatus.Paused);
        });
        Assert.False(tracker.IsUncertain);
        Assert.True(tracker.HasPendingExpected);
        tracker.Read(() => State(MediaTransportPlaybackStatus.Paused));
        Assert.True(tracker.HasPendingExpected);
        tracker.Read(() => State(MediaTransportPlaybackStatus.Playing));
        Assert.False(tracker.HasUnconfirmed);
    }

    [Fact]
    public void SameExpectedValueFromLaterCommandStillRequiresItsOwnObservation()
    {
        var tracker = new PlaybackCommandTracker();
        var firstPause = tracker.Begin(MediaTransportPlaybackStatus.Paused);
        tracker.Complete(firstPause, true, true);
        tracker.Read(() =>
        {
            tracker.Read(() => State(MediaTransportPlaybackStatus.Paused));
            var play = tracker.Begin(MediaTransportPlaybackStatus.Playing);
            tracker.Complete(play, true, true);
            tracker.Read(() => State(MediaTransportPlaybackStatus.Playing));
            var laterPause = tracker.Begin(MediaTransportPlaybackStatus.Paused);
            tracker.Complete(laterPause, true, true);
            // The enum value matches the earlier snapshot, but its generation does not.
            return State(MediaTransportPlaybackStatus.Paused);
        });
        Assert.False(tracker.IsUncertain);
        Assert.True(tracker.HasPendingExpected);
        tracker.Read(() => State(MediaTransportPlaybackStatus.Paused));
        Assert.False(tracker.HasUnconfirmed);
    }

    [Fact]
    public void ReadStartedWhileCommandWasInFlightCannotConfirmItsLaterCompletion()
    {
        var tracker = new PlaybackCommandTracker();
        var play = tracker.Begin(MediaTransportPlaybackStatus.Playing);
        tracker.Read(() =>
        {
            tracker.Complete(play, true, true);
            return State(MediaTransportPlaybackStatus.Playing);
        });
        Assert.True(tracker.HasPendingExpected);
        tracker.Read(() => State(MediaTransportPlaybackStatus.Playing));
        Assert.False(tracker.HasUnconfirmed);
    }
}
