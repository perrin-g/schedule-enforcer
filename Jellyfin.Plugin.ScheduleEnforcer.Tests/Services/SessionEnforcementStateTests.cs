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
}
