using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ScheduleEnforcer.Configuration;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.ScheduledTasks;

public class ScheduleEnforcerTask : IScheduledTask
{
    private const int NotifyAfterConsecutiveRetries = 3;

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(5);

    // Synthetic, stable stand-in for "window end" in the no-covering-window case, where there is
    // no real window to key state by. Using DateTimeOffset.MinValue (a fixed value, never equal
    // to a real computed window end) keeps TryMarkFinalized/TryMarkNotified one-shot per session
    // for this case too -- an earlier draft of this method passed the ever-changing "nowUtc" as
    // the key here, which made the final message re-send every single tick.
    private static readonly DateTimeOffset NoWindowSentinel = DateTimeOffset.MinValue;

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly IScheduleWindowCalculator _windowCalculator;
    private readonly ISessionEnforcementState _state;
    private readonly IStreamKillRegistry _streamKillRegistry;
    private readonly INotifier _notifier;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<PluginConfiguration> _getConfig;
    private readonly ILogger<ScheduleEnforcerTask> _logger;
    private readonly TimeSpan _commandTimeout;

    // Tracks consecutive still-playing ticks after finalization, keyed the same way as
    // ISessionEnforcementState (user, device, window end) -- not just (user, device) -- so a
    // stale count from a previous day's window can never leak into a new window's first tick
    // and trigger a false "3 retries" notification immediately. Concurrent because Jellyfin's
    // task manager is the only thing serialising runs of this task; a ConcurrentDictionary keeps
    // an unexpected overlapping run from corrupting the map (matching SessionEnforcementState).
    private readonly ConcurrentDictionary<(Guid UserId, string DeviceId, DateTimeOffset WindowEndUtc), (int Count, DateTimeOffset LastSeenUtc)> _stillLiveRetryCounts = new();

    public ScheduleEnforcerTask(
        ISessionManager sessionManager,
        IUserManager userManager,
        IScheduleWindowCalculator windowCalculator,
        ISessionEnforcementState state,
        IStreamKillRegistry streamKillRegistry,
        INotifier notifier,
        TimeZoneInfo timeZone,
        Func<PluginConfiguration> getConfig,
        ILogger<ScheduleEnforcerTask> logger,
        // Optional (trailing, defaulted) so every production/DI call site and the plan's test
        // call sites are unchanged; tests override it to drive the command-timeout path without
        // a real five-second wait. Applied per command, not as one shared budget for the tick.
        TimeSpan? commandTimeout = null)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _windowCalculator = windowCalculator;
        _state = state;
        _streamKillRegistry = streamKillRegistry;
        _notifier = notifier;
        _timeZone = timeZone;
        _getConfig = getConfig;
        _logger = logger;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    public string Name => "Enforce Access Schedules";

    public string Key => "ScheduleEnforcerTick";

    public string Description => "Warns and stops active sessions when a user's Access Schedule window ends.";

    // Its own category rather than "Library": this is a session/user-policy job, and Jellyfin's
    // Scheduled Tasks UI groups purely by this free-form string, so a plugin-specific heading is
    // both accurate and easy for an admin to find. None of Jellyfin's built-in categories
    // ("Library", "Maintenance", "Application", "Live TV") describe session enforcement.
    public string Category => "Schedule Enforcer";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(1).Ticks };
        // Catches the case where a Jellyfin restart shortly before a window's end would otherwise
        // push the first interval-triggered run past the cutoff (spec: Fault isolation).
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Checked up front, not just inside the per-session loop: an empty or already-drained
        // session list would otherwise never observe a cancellation request at all.
        cancellationToken.ThrowIfCancellationRequested();

        var config = _getConfig();
        if (!config.Enabled)
        {
            progress.Report(100);
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        _state.PruneOlderThan(nowUtc.AddHours(-2));
        _streamKillRegistry.PruneOlderThan(nowUtc.AddHours(-2));
        PruneRetryCounts(nowUtc.AddHours(-2));

        var sessions = _sessionManager.Sessions.ToList();

        for (var i = 0; i < sessions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await EnforceSessionAsync(sessions[i], nowUtc, config, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail-open: log and move on to the next session. One malformed session must
                // never take down enforcement for everyone else (spec: Fault isolation).
                _logger.LogError(ex, "ScheduleEnforcer: error enforcing session {SessionId}", sessions[i].Id);
            }

            progress.Report((i + 1) * 100.0 / sessions.Count);
        }

        progress.Report(100);
    }

    private async Task EnforceSessionAsync(SessionInfo session, DateTimeOffset nowUtc, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (session.UserId == Guid.Empty)
        {
            return;
        }

        var user = _userManager.GetUserById(session.UserId);

        // Admin exclusion is evaluated before any schedule lookup, not merely before the stop
        // action, so an administrator is never subject to enforcement even if they happen to have
        // AccessSchedules rows configured (spec: Core loop step 2).
        if (user is null || user.HasPermission(PermissionKind.IsAdministrator))
        {
            return;
        }

        var schedules = user.AccessSchedules;
        if (schedules is null || schedules.Count == 0)
        {
            return;
        }

        var deviceId = session.DeviceId ?? string.Empty;
        var result = _windowCalculator.GetCurrentWindow(schedules.ToList(), nowUtc, _timeZone);

        if (!result.HasCoveringWindow)
        {
            // No window covers "now" while the session is live -- treat as immediate-stop, not
            // a no-op (spec: Core loop step 5). Uses the stable sentinel key, not "nowUtc".
            await FinalizeAndStopAsync(session, user.Id, deviceId, NoWindowSentinel, config, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.WindowEndUtc is null)
        {
            // Contract violation from the calculator (covering window with no end). Do NOT fall
            // through to a stop: an internal inconsistency must not cut a child off mid-show.
            _logger.LogWarning(
                "ScheduleEnforcer: window calculator reported a covering window with no end for session {SessionId}; skipping",
                session.Id);
            return;
        }

        var windowEndUtc = result.WindowEndUtc.Value;

        if (nowUtc >= windowEndUtc)
        {
            await FinalizeAndStopAsync(session, user.Id, deviceId, windowEndUtc, config, cancellationToken).ConfigureAwait(false);
            return;
        }

        var minutesLeft = (windowEndUtc - nowUtc).TotalMinutes;
        if (minutesLeft <= config.WarningMinutesBeforeEnd &&
            _state.TryMarkWarned(user.Id, deviceId, windowEndUtc, nowUtc))
        {
            var message = config.WarningMessageTemplate.Replace(
                "{minutes}",
                ((int)Math.Ceiling(minutesLeft)).ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            await SendMessageSafeAsync(session, message, cancellationToken).ConfigureAwait(false);
        }
    }

    // windowEndUtc is either a real window end or NoWindowSentinel -- both are stable across
    // ticks for the same underlying condition, which is what keeps every action in here one-shot
    // per condition rather than re-firing every minute.
    private async Task FinalizeAndStopAsync(
        SessionInfo session,
        Guid userId,
        string deviceId,
        DateTimeOffset windowEndUtc,
        PluginConfiguration config,
        CancellationToken taskToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var retryKey = (userId, deviceId, windowEndUtc);
        if (_state.TryMarkFinalized(userId, deviceId, windowEndUtc, nowUtc))
        {
            await SendMessageSafeAsync(session, config.FinalMessageTemplate, taskToken).ConfigureAwait(false);
        }

        // Attempted on EVERY tick past cutoff, before any play-state gate, independently of the
        // Stop command below, and regardless of whether the client supports media control.
        // Revocation is a server-side action whose success does not depend on anything the client
        // reports, and it is THE enforcement mechanism that must still work when Stop cannot reach
        // the client at all -- Stop alone only clears NowPlayingItem, leaving the client
        // authenticated and free to resume seconds later (spec: Core loop step 7).
        //
        // Three separate gates have had to be removed from in front of this call, all of which
        // made it unreachable in precisely the failure modes it exists to cover:
        //   1. below the SupportsMediaControl early return -- unreachable for non-controllable
        //      clients;
        //   2. nested after SendPlaystateCommand inside a shared try -- unreachable whenever Stop
        //      timed out;
        //   3. below the !stillPlaying early return -- a transient revoke failure on a tick where
        //      the client happened to be paused (several clients null out NowPlayingItem while
        //      paused) would never be retried, so un-pausing later resumed playback on tokens
        //      that were never actually revoked.
        //
        // Revokes ALL of this user's tokens (not just this device) -- deliberately broad: a
        // scheduled user having another live device at cutoff is itself worth ending, and
        // SessionInfo exposes no access-token property that would allow a narrower per-device
        // revoke without an extra IDeviceManager lookup. Forcing a fresh login means the next
        // attempt to resume is blocked by Jellyfin's own native Access Schedule outside the window.
        //
        // RevokeUserTokens(Guid, string) takes no CancellationToken (confirmed by reflection
        // against 10.11.11), so WaitAsync bounds it with the same per-command timeout as the rest.
        var revoked = await TryRunCommandAsync(ct => _sessionManager.RevokeUserTokens(userId, null).WaitAsync(ct), taskToken).ConfigureAwait(false);
        if (!revoked)
        {
            _logger.LogWarning("ScheduleEnforcer: token revoke for user {UserId} did not complete", userId);
        }

        // Same unconditional, every-tick treatment as the revoke call above (see the comment on
        // RevokeUserTokens's call site for why three prior gates had to be removed from in front of
        // it) -- this closes the gap that revoke alone leaves open: an already-open stream that
        // revoke does not touch, confirmed live 2026-09-01.
        _streamKillRegistry.KillUser(userId, nowUtc);

        // Everything BELOW this point is gated on the session actually still playing something.
        // "Still listed in ISessionManager.Sessions" is NOT evidence that enforcement failed --
        // Jellyfin keeps a SessionInfo around for a while after playback ends, so counting those
        // ticks would raise a false "enforcement is failing" alert for a cutoff that worked and
        // train the admin to ignore the one alert that matters. Sending Stop to a session that
        // isn't playing anything is likewise meaningless. The revoke above is deliberately NOT
        // part of this gate.
        var stillPlaying = session.NowPlayingItem is not null;
        if (!stillPlaying)
        {
            _stillLiveRetryCounts.TryRemove(retryKey, out _);
            return;
        }

        // SupportsMediaControl is the only capability signal Jellyfin actually exposes for this
        // -- SupportedCommands is a GeneralCommandType list, and GeneralCommandType has no Stop
        // member at all (Stop is a PlaystateCommand, a different enum). Confirmed via reflection
        // against the installed 10.11.11 package during this plan's review.
        if (!session.SupportsMediaControl)
        {
            // Mutually exclusive with the retry-count notify below by construction: this branch
            // always returns, so a given (userId, deviceId, windowEndUtc) key can only ever reach
            // one of the two TryMarkNotified call sites, never both -- sharing the same notified
            // flag between them is therefore safe, not a collision risk. Only reached while media
            // is actually still playing (guarded above): tokens are revoked either way, so a
            // non-controllable session playing nothing is not worth waking an admin for.
            if (_state.TryMarkNotified(userId, deviceId, windowEndUtc, nowUtc))
            {
                await _notifier.NotifyAsync($"Session {session.Id} for user {userId} does not support media control; tokens revoked, but Stop could not be sent.").ConfigureAwait(false);
            }

            return;
        }

        var stopped = await TryRunCommandAsync(
            ct => _sessionManager.SendPlaystateCommand(string.Empty, session.Id, new PlaystateRequest { Command = PlaystateCommand.Stop }, ct),
            taskToken).ConfigureAwait(false);
        if (!stopped)
        {
            _logger.LogWarning("ScheduleEnforcer: stop command for session {SessionId} did not complete", session.Id);
        }

        var previous = _stillLiveRetryCounts.TryGetValue(retryKey, out var existing) ? existing.Count : 0;
        var retryCount = previous + 1;
        _stillLiveRetryCounts[retryKey] = (retryCount, nowUtc);

        if (retryCount >= NotifyAfterConsecutiveRetries &&
            _state.TryMarkNotified(userId, deviceId, windowEndUtc, nowUtc))
        {
            await _notifier.NotifyAsync($"Session {session.Id} for user {userId} has not stopped after {retryCount} consecutive attempts.").ConfigureAwait(false);
        }
    }

    private void PruneRetryCounts(DateTimeOffset cutoffUtc)
    {
        foreach (var key in _stillLiveRetryCounts.Where(kvp => kvp.Value.LastSeenUtc < cutoffUtc).Select(kvp => kvp.Key).ToList())
        {
            _stillLiveRetryCounts.TryRemove(key, out _);
        }
    }

    private async Task SendMessageSafeAsync(SessionInfo session, string message, CancellationToken taskToken)
    {
        var sent = await TryRunCommandAsync(
            ct => _sessionManager.SendMessageCommand(string.Empty, session.Id, new MessageCommand { Header = "Schedule Enforcer", Text = message }, ct),
            taskToken).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogWarning("ScheduleEnforcer: message send to session {SessionId} did not complete", session.Id);
        }
    }

    // Runs one Jellyfin call under its own per-command timeout. Returns true if the call
    // completed, false if it did NOT complete for any reason -- timed out, or threw.
    //
    // Catching every exception here (not just OperationCanceledException) is what actually makes
    // the three commands in FinalizeAndStopAsync independent, as their comments claim. With a
    // narrower catch, a non-cancellation throw out of RevokeUserTokens (a DB error, say) escaped
    // to the per-session catch in ExecuteAsync and skipped that tick's Stop attempt entirely; and
    // because the final message is sent BEFORE the revoke -- with TryMarkFinalized already
    // flipped -- a throw from SendMessageCommand lost that message forever AND preempted the
    // revoke. Both are covered by ExecuteAsync_*ThrowsNonCancellationException_* regression tests.
    //
    // The one rethrow case is a genuine cancellation of the whole scheduled task. That filter MUST
    // test `taskToken`, never the linked `cts.Token`: the linked token is by definition already
    // cancelled whenever the per-command timeout fires, so filtering on it would match on every
    // timeout, rethrow, and skip all the retry/notify accounting downstream. (That was a real bug
    // in an earlier draft; ExecuteAsync_StopCommandTimesOut_... is its regression test.) An
    // OperationCanceledException that is NOT driven by task cancellation is just another failure
    // and falls through to the general catch below.
    private async Task<bool> TryRunCommandAsync(Func<CancellationToken, Task> command, CancellationToken taskToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(taskToken);
        cts.CancelAfter(_commandTimeout);

        try
        {
            await command(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (taskToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduleEnforcer: command did not complete");
            return false;
        }
    }
}
