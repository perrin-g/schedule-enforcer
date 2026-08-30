using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ScheduleEnforcer.Configuration;
using Jellyfin.Plugin.ScheduleEnforcer.ScheduledTasks;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests.ScheduledTasks;

public class ScheduleEnforcerTaskTests
{
    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    // AccessSchedule/User have no mockable surface worth mocking (User has no parameterless
    // ctor and no virtual members Moq could override) -- construct real instances instead,
    // exactly as confirmed working during this plan's review.
    private static User CreateUser(bool isAdministrator, params AccessSchedule[] schedules)
    {
        var user = new User("test", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")
        {
            Id = Guid.NewGuid()
        };
        user.SetPermission(PermissionKind.IsAdministrator, isAdministrator);
        foreach (var schedule in schedules)
        {
            user.AccessSchedules.Add(schedule);
        }

        return user;
    }

    private static SessionInfo CreateSession(Guid userId, string deviceId, string sessionId = "session-1")
    {
        // Confirmed constructible with null sessionManager/logger for test purposes.
        var session = new SessionInfo(null!, null!)
        {
            Id = sessionId,
            UserId = userId,
            DeviceId = deviceId,
            // Actively playing by default: enforcement treats a cleared NowPlayingItem as
            // "successfully enforced" and stops retrying, so a session that is meant to be
            // enforced in these tests must actually be playing something.
            NowPlayingItem = new BaseItemDto { Name = "Test Item" }
        };
        return session;
    }

    // SessionInfo.SupportsMediaControl (verified by probing the installed 10.11.11 assembly) is
    // true only when BOTH Capabilities.SupportsMediaControl is set AND at least one attached
    // ISessionController reports SupportsMediaControl -- setting only the capability flag leaves
    // it false. That is why controllable test sessions need both halves here.
    private static SessionInfo CreateControllableSession(Guid userId, string deviceId, string sessionId = "session-1")
    {
        var session = CreateSession(userId, deviceId, sessionId);
        session.Capabilities = new ClientCapabilities { SupportsMediaControl = true };
        session.AddController(Mock.Of<ISessionController>(c => c.SupportsMediaControl == true));
        Assert.True(session.SupportsMediaControl, "test setup must produce a media-controllable session");
        return session;
    }

    private static Mock<IScheduleWindowCalculator> WindowEndingAt(DateTimeOffset windowEndUtc)
    {
        var calculator = new Mock<IScheduleWindowCalculator>();
        calculator.Setup(c => c.GetCurrentWindow(It.IsAny<IReadOnlyList<AccessSchedule>>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeZoneInfo>()))
            .Returns(new ScheduleWindowResult { HasCoveringWindow = true, WindowEndUtc = windowEndUtc });
        return calculator;
    }

    private static PluginConfiguration DefaultConfig() => new();

    [Fact]
    public async Task ExecuteAsync_AdministratorSession_IsNeverEnforced()
    {
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var windowCalculator = new Mock<IScheduleWindowCalculator>();
        var state = new Mock<ISessionEnforcementState>();
        var notifier = new Mock<INotifier>();

        var adminUser = CreateUser(isAdministrator: true, new AccessSchedule(DynamicDayOfWeek.Everyday, 0.0, 23.99, Guid.NewGuid()));
        var session = CreateSession(adminUser.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(adminUser.Id)).Returns(adminUser);

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, state.Object, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Proves the admin exclusion happens before schedule lookup, regardless of the admin
        // having a real AccessSchedules row configured.
        windowCalculator.Verify(c => c.GetCurrentWindow(It.IsAny<IReadOnlyList<AccessSchedule>>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeZoneInfo>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OneSessionThrows_OtherSessionsStillProcessed()
    {
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var windowCalculator = new Mock<IScheduleWindowCalculator>();
        var state = new Mock<ISessionEnforcementState>();
        var notifier = new Mock<INotifier>();

        var throwingUserId = Guid.NewGuid();
        var okUser = CreateUser(isAdministrator: false); // no AccessSchedules -> no-op, but proves it was reached
        var throwingSession = CreateSession(throwingUserId, "d1", "s1");
        var okSession = CreateSession(okUser.Id, "d2", "s2");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { throwingSession, okSession });

        userManager.Setup(m => m.GetUserById(throwingUserId)).Throws(new InvalidOperationException("simulated failure"));
        userManager.Setup(m => m.GetUserById(okUser.Id)).Returns(okUser);

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, state.Object, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        userManager.Verify(m => m.GetUserById(okUser.Id), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledToken_ThrowsBeforeProcessingSessions()
    {
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var windowCalculator = new Mock<IScheduleWindowCalculator>();
        var state = new Mock<ISessionEnforcementState>();
        var notifier = new Mock<INotifier>();
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo>());

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, state.Object, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // An empty session list means the per-session loop body never runs, so cancellation
        // must be checked explicitly up front -- this is what proves that check exists.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new Progress<double>(), cts.Token));
    }

    [Fact]
    public void GetDefaultTriggers_IncludesOneMinuteIntervalAndStartupTrigger()
    {
        var task = new ScheduleEnforcerTask(
            Mock.Of<ISessionManager>(), Mock.Of<IUserManager>(), Mock.Of<IScheduleWindowCalculator>(),
            Mock.Of<ISessionEnforcementState>(), Mock.Of<INotifier>(), Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Contains(triggers, t => t.Type == MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromMinutes(1).Ticks);
        Assert.Contains(triggers, t => t.Type == MediaBrowser.Model.Tasks.TaskTriggerInfoType.StartupTrigger);
    }

    [Fact]
    public async Task ExecuteAsync_NoCoveringWindow_FinalizesOnceNotEveryTick()
    {
        // Regression test for a bug caught during this plan's own review: the no-covering-window
        // branch must use a stable synthetic key, not "now", or TryMarkFinalized would return
        // true every single tick and spam the final message forever.
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var windowCalculator = new Mock<IScheduleWindowCalculator>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState(); // real implementation -- this test is about its actual keying behavior, not a mock's call count

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateSession(user.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        windowCalculator.Setup(c => c.GetCurrentWindow(It.IsAny<IReadOnlyList<AccessSchedule>>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeZoneInfo>()))
            .Returns(new ScheduleWindowResult { HasCoveringWindow = false, WindowEndUtc = null });

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // The final message must be sent exactly once across three ticks, not once per tick --
        // this is the concrete regression check for the bug this test is named after.
        sessionManager.Verify(
            m => m.SendMessageCommand(It.IsAny<string>(), session.Id, It.IsAny<MediaBrowser.Model.Session.MessageCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PastWindowEnd_SendsFinalMessageStopsPlaybackAndRevokesTokens()
    {
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState();

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateControllableSession(user.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var windowCalculator = WindowEndingAt(DateTimeOffset.UtcNow.AddMinutes(-1));

        var config = DefaultConfig();
        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland, () => config, Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        sessionManager.Verify(
            m => m.SendMessageCommand(It.IsAny<string>(), session.Id, It.Is<MessageCommand>(c => c.Text == config.FinalMessageTemplate), It.IsAny<CancellationToken>()),
            Times.Once);
        sessionManager.Verify(
            m => m.SendPlaystateCommand(It.IsAny<string>(), session.Id, It.Is<PlaystateRequest>(r => r.Command == PlaystateCommand.Stop), It.IsAny<CancellationToken>()),
            Times.Once);
        sessionManager.Verify(m => m.RevokeUserTokens(user.Id, null), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithinWarningWindow_SendsWarningOnceWithMinutesSubstituted()
    {
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState();

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateControllableSession(user.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var windowCalculator = WindowEndingAt(DateTimeOffset.UtcNow.AddMinutes(5));

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // {minutes} substituted with the real remaining minutes, and warned exactly once across
        // two ticks (the one-shot flag), not once per tick.
        sessionManager.Verify(
            m => m.SendMessageCommand(It.IsAny<string>(), session.Id, It.Is<MessageCommand>(c => c.Text == "Your screen time ends in 5 minutes."), It.IsAny<CancellationToken>()),
            Times.Once);
        // Still inside the window, so nothing may be stopped or revoked yet.
        sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        sessionManager.Verify(m => m.RevokeUserTokens(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SessionWithoutMediaControl_RevokesTokensButNeverSendsStopCommand()
    {
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState();

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateSession(user.Id, "device-1"); // no capabilities/controller -> SupportsMediaControl == false
        Assert.False(session.SupportsMediaControl);
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var windowCalculator = WindowEndingAt(DateTimeOffset.UtcNow.AddMinutes(-1));

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        notifier.Verify(n => n.Notify(It.Is<string>(s => s.Contains("does not support media control", StringComparison.Ordinal))), Times.Once);
        sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // Revocation is a SERVER-side action and must NOT be gated on the client's media-control
        // capability -- otherwise a client Jellyfin cannot remote-control simply keeps streaming
        // past the cutoff with a valid token. This assertion previously read Times.Never, which
        // encoded exactly the hole the enforcement design exists to close.
        sessionManager.Verify(m => m.RevokeUserTokens(user.Id, null), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_StopCommandTimesOut_CountsRetriesAndNotifiesAfterThree()
    {
        // Regression test for a bug found while transcribing this task: the OperationCanceledException
        // filters guarding the per-command timeout originally tested the *linked* command token,
        // which is by definition already cancelled whenever the timeout fires. That made the filter
        // never match, rethrowing on every timeout, skipping the retry accounting entirely, and
        // silently disabling the "session won't stop" admin alert -- the one signal that tells an
        // admin enforcement is failing. The filters must test the outer task token instead.
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState();

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateControllableSession(user.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var windowCalculator = WindowEndingAt(DateTimeOffset.UtcNow.AddMinutes(-1));

        // A client that accepts the stop command but never acknowledges it -- the exact scenario
        // the per-command timeout exists for.
        sessionManager
            .Setup(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, PlaystateRequest _, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct));

        var task = new ScheduleEnforcerTask(
            sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland,
            () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>(),
            commandTimeout: TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 3; i++)
        {
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        }

        notifier.Verify(n => n.Notify(It.Is<string>(s => s.Contains("has not stopped after 3 consecutive attempts", StringComparison.Ordinal))), Times.Once);

        // The revoke must NOT be sequenced behind the Stop command's success. An earlier structure
        // awaited Stop and the revoke inside one try block, so a hanging Stop jumped to the catch
        // and the revoke never ran -- leaving the session both un-stopped AND still authenticated,
        // which is the precise failure mode token revocation was added to cover.
        sessionManager.Verify(m => m.RevokeUserTokens(user.Id, null), Times.Exactly(3));
    }

    [Fact]
    public async Task ExecuteAsync_NowPlayingItemClears_StopsRetryingAndRaisesNoFalseAlert()
    {
        // A stopped session commonly lingers in ISessionManager.Sessions past its idle timeout
        // before Jellyfin garbage-collects it. Counting those ticks as "still live" would raise
        // an "enforcement is failing" alert for a cutoff that actually worked, training the admin
        // to ignore the one alert that matters. NowPlayingItem clearing is the real success signal.
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState();

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateControllableSession(user.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var windowCalculator = WindowEndingAt(DateTimeOffset.UtcNow.AddMinutes(-1));

        var task = new ScheduleEnforcerTask(sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland, () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        // Tick 1: playing, so enforcement runs.
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Playback stopped, but the session object is still listed -- exactly the lingering-session
        // case. Ticks 2 and 3 must treat this as enforced, not as two more failed attempts.
        session.NowPlayingItem = null;
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        notifier.Verify(n => n.Notify(It.IsAny<string>()), Times.Never);

        // Stop is play-state-gated, so it is only sent on the tick where something was playing...
        sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // ...but the revoke is not gated on play state and is retried on every tick past cutoff,
        // so that a revoke which failed transiently is never left un-retried just because the
        // client stopped reporting a NowPlayingItem. See
        // ExecuteAsync_RevokeFailsThenPlaybackPauses_RevokeIsStillRetried.
        sessionManager.Verify(m => m.RevokeUserTokens(user.Id, null), Times.Exactly(3));
    }

    [Fact]
    public async Task ExecuteAsync_RevokeFailsThenPlaybackPauses_RevokeIsStillRetried()
    {
        // Regression test for the "pause to dodge a failed revoke" hole: several clients null out
        // NowPlayingItem while merely PAUSED, not stopped. If the revoke retry sat behind the
        // stillPlaying gate, a revoke that failed transiently on one tick would never be retried
        // once the user paused -- and un-pausing later would resume playback on tokens that were
        // never actually revoked. Revocation is server-side, so it must retry every tick past
        // cutoff regardless of what the client reports about play state.
        var sessionManager = new Mock<ISessionManager>();
        var userManager = new Mock<IUserManager>();
        var notifier = new Mock<INotifier>();
        var realState = new SessionEnforcementState();

        var user = CreateUser(isAdministrator: false, new AccessSchedule(DynamicDayOfWeek.Everyday, 13.0, 17.0, Guid.NewGuid()));
        var session = CreateControllableSession(user.Id, "device-1");
        sessionManager.Setup(m => m.Sessions).Returns(new List<SessionInfo> { session });
        userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        var windowCalculator = WindowEndingAt(DateTimeOffset.UtcNow.AddMinutes(-1));

        // A revoke that never completes -- the transient server-side failure this retries for.
        // RevokeUserTokens takes no CancellationToken, so the task is bounded by WaitAsync.
        sessionManager
            .Setup(m => m.RevokeUserTokens(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(() => Task.Delay(Timeout.InfiniteTimeSpan));

        var task = new ScheduleEnforcerTask(
            sessionManager.Object, userManager.Object, windowCalculator.Object, realState, notifier.Object, Auckland,
            () => DefaultConfig(), Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>(),
            commandTimeout: TimeSpan.FromMilliseconds(50));

        // Tick 1: playing, revoke attempted and times out.
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // User pauses; the client nulls out NowPlayingItem. Tokens are still live at this point.
        session.NowPlayingItem = null;
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // The revoke must have been retried on the paused tick, not skipped.
        sessionManager.Verify(m => m.RevokeUserTokens(user.Id, null), Times.Exactly(2));

        // ...while the play-state-dependent actions correctly stay gated: Stop was only sent on
        // the tick where something was actually playing, and no false alert was raised.
        sessionManager.Verify(m => m.SendPlaystateCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlaystateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.Notify(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotEvenEnumerateSessions()
    {
        var sessionManager = new Mock<ISessionManager>();
        var task = new ScheduleEnforcerTask(
            sessionManager.Object, Mock.Of<IUserManager>(), Mock.Of<IScheduleWindowCalculator>(),
            Mock.Of<ISessionEnforcementState>(), Mock.Of<INotifier>(), Auckland,
            () => new PluginConfiguration { Enabled = false }, Mock.Of<Microsoft.Extensions.Logging.ILogger<ScheduleEnforcerTask>>());

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        sessionManager.Verify(m => m.Sessions, Times.Never);
    }
}
