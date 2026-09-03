using System;
using System.Threading;
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
}
