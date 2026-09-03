using Jellyfin.Plugin.ScheduleEnforcer.Services;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests.Services;

public class StreamKillRegistryTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string PlaySessionId = "session-1";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryGetOwner_UnknownPlaySessionId_ReturnsFalse()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());

        var found = registry.TryGetOwner(PlaySessionId, out var userId);

        Assert.False(found);
        Assert.Equal(Guid.Empty, userId);
    }

    [Fact]
    public void RecordPlaySessionOwner_ThenTryGetOwner_ReturnsRecordedUserId()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);

        var found = registry.TryGetOwner(PlaySessionId, out var userId);

        Assert.True(found);
        Assert.Equal(UserId, userId);
    }

    [Fact]
    public void IsPlaySessionKilled_UnknownPlaySessionId_ReturnsFalse()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());

        Assert.False(registry.IsPlaySessionKilled(PlaySessionId));
    }

    [Fact]
    public void IsPlaySessionKilled_AfterKillUser_ReturnsTrueForThatUsersPlaySessions()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);

        registry.KillUser(UserId, Now);

        Assert.True(registry.IsPlaySessionKilled(PlaySessionId));
    }

    [Fact]
    public void IsPlaySessionKilled_DifferentUsersPlaySession_StaysFalse()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        var otherUserId = Guid.NewGuid();
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);

        registry.KillUser(otherUserId, Now);

        Assert.False(registry.IsPlaySessionKilled(PlaySessionId));
    }

    [Fact]
    public void KillUser_AbortsAlreadyTrackedActiveRequestForThatUser()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        var aborted = false;
        registry.TrackActiveRequest(PlaySessionId, Guid.NewGuid(), () => aborted = true);

        registry.KillUser(UserId, Now);

        Assert.True(aborted);
    }

    [Fact]
    public void KillUser_DoesNotAbortActiveRequestForDifferentUser()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        var otherUserId = Guid.NewGuid();
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        var aborted = false;
        registry.TrackActiveRequest(PlaySessionId, Guid.NewGuid(), () => aborted = true);

        registry.KillUser(otherUserId, Now);

        Assert.False(aborted);
    }

    [Fact]
    public void UntrackActiveRequest_RemovesIt_SoLaterKillDoesNotAbortIt()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        var aborted = false;
        var trackingId = Guid.NewGuid();
        registry.TrackActiveRequest(PlaySessionId, trackingId, () => aborted = true);
        registry.UntrackActiveRequest(PlaySessionId, trackingId);

        registry.KillUser(UserId, Now);

        Assert.False(aborted);
    }

    [Fact]
    public void TrackActiveRequest_AbortActionThatThrows_DoesNotPreventOtherAbortsOrPropagate()
    {
        // A malformed/already-completed HttpContext.Abort() call must never take down
        // enforcement for every other tracked request on the same tick (same fail-open
        // principle as ScheduleEnforcerTask.ExecuteAsync's per-session try/catch).
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        registry.TrackActiveRequest(PlaySessionId, Guid.NewGuid(), () => throw new InvalidOperationException("already aborted"));
        var secondAborted = false;
        registry.TrackActiveRequest(PlaySessionId, Guid.NewGuid(), () => secondAborted = true);

        var exception = Record.Exception(() => registry.KillUser(UserId, Now));

        Assert.Null(exception);
        Assert.True(secondAborted);
    }

    [Fact]
    public void PruneOlderThan_RemovesStalePlaySessionMapping()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);

        registry.PruneOlderThan(Now.AddHours(1));

        var found = registry.TryGetOwner(PlaySessionId, out _);
        Assert.False(found);
    }

    [Fact]
    public void PruneOlderThan_KeepsRecentPlaySessionMapping()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);

        registry.PruneOlderThan(Now.AddHours(-1));

        var found = registry.TryGetOwner(PlaySessionId, out var userId);
        Assert.True(found);
        Assert.Equal(UserId, userId);
    }

    [Fact]
    public void TouchPlaySession_RefreshesLastSeen_SoLongPlaybackIsNotPrunedMidStream()
    {
        // A Transcode/HLS stream re-enters the streaming middleware once per segment. Without the
        // refresh, the owner entry aged out 2 hours after playback STARTED and enforcement
        // silently stopped working for any long movie.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);

        registry.TouchPlaySession(PlaySessionId, Now.AddHours(3));
        registry.PruneOlderThan(Now.AddHours(1));

        Assert.True(registry.TryGetOwner(PlaySessionId, out var userId));
        Assert.Equal(UserId, userId);
    }

    [Fact]
    public void TouchPlaySession_UnknownPlaySessionId_IsANoOp()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());

        var exception = Record.Exception(() => registry.TouchPlaySession("never-seen", Now));

        Assert.Null(exception);
        Assert.False(registry.TryGetOwner("never-seen", out _));
    }

    [Fact]
    public void PruneOlderThan_EntryWithLiveTrackedRequest_IsKeptEvenPastCutoff()
    {
        // DirectPlay can be a single HTTP request lasting the whole movie, so it never re-enters
        // the middleware to be re-stamped. Pruning it would drop both the ownership mapping and
        // the abort handle -- a later kill would then find nothing to abort.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        var aborted = false;
        registry.TrackActiveRequest(PlaySessionId, Guid.NewGuid(), () => aborted = true);

        registry.PruneOlderThan(Now.AddHours(3));

        Assert.True(registry.TryGetOwner(PlaySessionId, out _));

        // ...and the abort handle survived too, so a kill arriving after the prune still works.
        registry.KillUser(UserId, Now.AddHours(3));
        Assert.True(aborted);
        Assert.True(registry.IsPlaySessionKilled(PlaySessionId));
    }

    [Fact]
    public void PruneOlderThan_EntryWithNoLiveTrackedRequest_IsStillPrunedPastCutoff()
    {
        // The counterpart to the test above: once the tracked request finishes, the stale entry
        // must go, or nothing ever ages out.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        var trackingId = Guid.NewGuid();
        registry.TrackActiveRequest(PlaySessionId, trackingId, () => { });
        registry.UntrackActiveRequest(PlaySessionId, trackingId);

        registry.PruneOlderThan(Now.AddHours(3));

        Assert.False(registry.TryGetOwner(PlaySessionId, out _));
    }

    [Fact]
    public void PruneOlderThan_SweepsFinishedActiveEntriesThatNeverGotAnOwner()
    {
        // TrackActiveRequest creates a bucket for EVERY streaming request carrying a
        // playSessionId, mapped or not. UntrackActiveRequest empties the inner dictionary but
        // leaves the outer key, so without a sweep those keys grow without bound.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        const string UnmappedPlaySessionId = "unmapped-session";
        var trackingId = Guid.NewGuid();
        registry.TrackActiveRequest(UnmappedPlaySessionId, trackingId, () => { });
        registry.UntrackActiveRequest(UnmappedPlaySessionId, trackingId);

        registry.PruneOlderThan(Now.AddHours(3));

        Assert.Equal(0, registry.ActiveBucketCount);
    }

    [Fact]
    public void PruneOlderThan_DoesNotSweepAnUnownedActiveEntryThatIsStillInFlight()
    {
        // An in-flight request whose owner mapping was never recorded (auth resolution failed,
        // or the mapping predates a restart) must not have its abort handle swept out from under
        // it mid-request.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        const string UnmappedPlaySessionId = "unmapped-session";
        registry.TrackActiveRequest(UnmappedPlaySessionId, Guid.NewGuid(), () => { });

        registry.PruneOlderThan(Now.AddHours(3));

        Assert.Equal(1, registry.ActiveBucketCount);
    }

    [Fact]
    public void ClearUser_RemovesTheKill_SoALaterLegitimateSessionIsNotBlocked()
    {
        // A user with a second AccessSchedule window opening within the prune cutoff of the first
        // window's end would otherwise still be marked killed, and their legitimately permitted
        // playback would be killed on sight.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        registry.KillUser(UserId, Now);
        Assert.True(registry.IsPlaySessionKilled(PlaySessionId));

        registry.ClearUser(UserId);

        // A brand-new playback attempt inside the new window is not blocked...
        const string NewPlaySessionId = "session-2";
        registry.RecordPlaySessionOwner(NewPlaySessionId, UserId, Now.AddMinutes(30));
        Assert.False(registry.IsPlaySessionKilled(NewPlaySessionId));

        // ...and neither is the pre-existing mapping.
        Assert.False(registry.IsPlaySessionKilled(PlaySessionId));
    }

    [Fact]
    public void ClearUser_DoesNotClearADifferentUsersKill()
    {
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        var otherUserId = Guid.NewGuid();
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        registry.KillUser(UserId, Now);

        registry.ClearUser(otherUserId);

        Assert.True(registry.IsPlaySessionKilled(PlaySessionId));
    }

    [Fact]
    public void PruneOlderThan_RemovesStaleKillEntry()
    {
        // Verify that kill entries age out and are pruned, allowing the user to be killed again later.
        // This proves that _killedUsers doesn't grow unbounded.
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        registry.KillUser(UserId, Now);

        // Prune with a cutoff after the kill timestamp - should remove the kill entry
        registry.PruneOlderThan(Now.AddHours(1));

        // Record a new PlaySessionId for the same user after the prune
        var newPlaySessionId = "session-2";
        registry.RecordPlaySessionOwner(newPlaySessionId, UserId, Now.AddHours(1));

        // The new session should NOT be killed because the old kill entry was pruned
        Assert.False(registry.IsPlaySessionKilled(newPlaySessionId));
    }

    [Fact]
    public void PruneOlderThan_KeepsRecentKillEntry()
    {
        // Verify that kill entries do NOT get pruned while still "fresh" (cutoff before kill timestamp).
        var registry = new StreamKillRegistry(Mock.Of<Microsoft.Extensions.Logging.ILogger<StreamKillRegistry>>());
        registry.RecordPlaySessionOwner(PlaySessionId, UserId, Now);
        registry.KillUser(UserId, Now);

        // Prune with a cutoff before the kill timestamp - should NOT remove the kill entry
        registry.PruneOlderThan(Now.AddHours(-1));

        // The session should still be marked as killed
        Assert.True(registry.IsPlaySessionKilled(PlaySessionId));
    }
}
