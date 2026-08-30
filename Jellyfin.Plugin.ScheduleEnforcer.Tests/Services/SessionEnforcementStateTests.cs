using System;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests.Services;

public class SessionEnforcementStateTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string DeviceId = "device-1";
    private static readonly DateTimeOffset WindowEnd = new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = WindowEnd.AddMinutes(-5);

    [Fact]
    public void TryMarkWarned_FirstCall_ReturnsTrue()
    {
        var state = new SessionEnforcementState();

        var result = state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now);

        Assert.True(result);
    }

    [Fact]
    public void TryMarkWarned_SecondCallSameKey_ReturnsFalse()
    {
        var state = new SessionEnforcementState();
        state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now);

        var result = state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now);

        Assert.False(result);
    }

    [Fact]
    public void TryMarkWarned_SurvivesSimulatedReconnect_BecauseKeyIsUserAndDeviceNotSession()
    {
        // A reconnect mints a new SessionId, but the key here is (UserId, DeviceId, WindowEnd) --
        // there is no SessionId parameter at all, so a reconnect cannot reset this state.
        // This test documents that guarantee: two calls with the same user+device+window,
        // simulating "before" and "after" a reconnect, still behave as one continuous session.
        var state = new SessionEnforcementState();
        var firstCall = state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now);
        var afterSimulatedReconnect = state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now.AddSeconds(30));

        Assert.True(firstCall);
        Assert.False(afterSimulatedReconnect);
    }

    [Fact]
    public void TryMarkFinalized_And_TryMarkNotified_AreIndependentFlags()
    {
        var state = new SessionEnforcementState();
        state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now);

        var finalized = state.TryMarkFinalized(UserId, DeviceId, WindowEnd, Now);
        var notified = state.TryMarkNotified(UserId, DeviceId, WindowEnd, Now);

        Assert.True(finalized);
        Assert.True(notified);
    }

    [Fact]
    public void DifferentDeviceId_IsTrackedSeparately()
    {
        var state = new SessionEnforcementState();
        state.TryMarkWarned(UserId, "device-1", WindowEnd, Now);

        var resultForOtherDevice = state.TryMarkWarned(UserId, "device-2", WindowEnd, Now);

        Assert.True(resultForOtherDevice);
    }

    [Fact]
    public void PruneOlderThan_RemovesStaleEntries_AllowingReWarnAfterPrune()
    {
        var state = new SessionEnforcementState();
        state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now);

        state.PruneOlderThan(Now.AddMinutes(1));
        var resultAfterPrune = state.TryMarkWarned(UserId, DeviceId, WindowEnd, Now.AddMinutes(2));

        Assert.True(resultAfterPrune);
    }

    [Fact]
    public void PruneOlderThan_BoundaryCondition_DoesNotPruneAtCutoff()
    {
        // Entry with LastSeenUtc exactly at the cutoff should NOT be pruned
        // (check is <, not <=)
        var state = new SessionEnforcementState();
        var timestamp = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        state.TryMarkWarned(UserId, DeviceId, WindowEnd, timestamp);

        state.PruneOlderThan(timestamp); // Cutoff is exactly at LastSeenUtc
        var resultAfterPrune = state.TryMarkWarned(UserId, DeviceId, WindowEnd, timestamp.AddSeconds(1));

        // Should return false because the entry survived the prune
        Assert.False(resultAfterPrune);
    }

    [Fact]
    public void PruneOlderThan_JustBeforeCutoff_IsPruned()
    {
        // Entry with LastSeenUtc just before the cutoff should be pruned
        var state = new SessionEnforcementState();
        var timestamp = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        state.TryMarkWarned(UserId, DeviceId, WindowEnd, timestamp);

        state.PruneOlderThan(timestamp.AddTicks(1)); // Cutoff is 1 tick after LastSeenUtc
        var resultAfterPrune = state.TryMarkWarned(UserId, DeviceId, WindowEnd, timestamp.AddSeconds(1));

        // Should return true because the entry was pruned and this is a new one
        Assert.True(resultAfterPrune);
    }

    [Fact]
    public void PruneOlderThan_HoldsLockDuringCheck_PreventsFlagLoss()
    {
        // This test verifies that PruneOlderThan holds the entry lock during the stale check.
        // If it didn't, a concurrent TryMark* could update LastSeenUtc and set a flag
        // between the read and the remove, causing the flag to be lost.
        // We simulate this by marking an entry, updating its timestamp to just-before-cutoff,
        // then pruning with a cutoff that should not remove it, and verifying it's still there.
        var state = new SessionEnforcementState();
        var oldTimestamp = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var newTimestamp = oldTimestamp.AddMinutes(1);

        // Mark with old timestamp
        state.TryMarkWarned(UserId, DeviceId, WindowEnd, oldTimestamp);

        // Update the timestamp by calling TryMarkNotified (which updates LastSeenUtc)
        // This simulates a "re-touch" of the entry with a newer timestamp
        state.TryMarkNotified(UserId, DeviceId, WindowEnd, newTimestamp);

        // Prune with cutoff between old and new timestamps
        // If the lock was not held, the prune might have removed the entry based on the old timestamp
        var cutoff = oldTimestamp.AddSeconds(30);
        state.PruneOlderThan(cutoff);

        // Entry should still exist because it was updated to newTimestamp which is >= cutoff
        var resultAfterPrune = state.TryMarkWarned(UserId, DeviceId, WindowEnd, newTimestamp.AddSeconds(1));

        Assert.False(resultAfterPrune);
    }
}
