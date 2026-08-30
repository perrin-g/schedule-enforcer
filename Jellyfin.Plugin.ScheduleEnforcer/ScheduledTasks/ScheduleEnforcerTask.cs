using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly INotifier _notifier;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<PluginConfiguration> _getConfig;
    private readonly ILogger<ScheduleEnforcerTask> _logger;
    private readonly TimeSpan _commandTimeout;

    // Tracks consecutive still-live ticks after finalization, keyed the same way as
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
        INotifier notifier,
        TimeZoneInfo timeZone,
        Func<PluginConfiguration> getConfig,
        ILogger<ScheduleEnforcerTask> logger,
        // Optional (trailing, defaulted) so every production/DI call site and the plan's test
        // call sites are unchanged; tests override it to drive the command-timeout path without
        // a real five-second wait.
        TimeSpan? commandTimeout = null)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _windowCalculator = windowCalculator;
        _state = state;
        _notifier = notifier;
        _timeZone = timeZone;
        _getConfig = getConfig;
        _logger = logger;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
    }

    public string Name => "Enforce Access Schedules";

    public string Key => "ScheduleEnforcerTick";

    public string Description => "Warns and stops active sessions when a user's Access Schedule window ends.";

    public string Category => "Library";

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

        // Per-command timeout, linked to the task's own cancellation. `cancellationToken` (the
        // outer token) is passed alongside cts.Token so the OperationCanceledException filters
        // downstream can tell "our 5s timeout fired" from "the whole task was cancelled" -- they
        // cannot distinguish those by inspecting the linked token, which is cancelled in both cases.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_commandTimeout);

        if (!result.HasCoveringWindow)
        {
            // No window covers "now" while the session is live -- treat as immediate-stop, not
            // a no-op (spec: Core loop step 5). Uses the stable sentinel key, not "nowUtc".
            await FinalizeAndStopAsync(session, user.Id, deviceId, NoWindowSentinel, config, cts.Token, cancellationToken).ConfigureAwait(false);
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
            await FinalizeAndStopAsync(session, user.Id, deviceId, windowEndUtc, config, cts.Token, cancellationToken).ConfigureAwait(false);
            return;
        }

        var minutesLeft = (windowEndUtc - nowUtc).TotalMinutes;
        if (minutesLeft <= config.WarningMinutesBeforeEnd &&
            _state.TryMarkWarned(user.Id, deviceId, windowEndUtc, nowUtc))
        {
            var message = config.WarningMessageTemplate.Replace("{minutes}", ((int)Math.Ceiling(minutesLeft)).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            await SendMessageSafeAsync(session, message, cts.Token, cancellationToken).ConfigureAwait(false);
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
        CancellationToken commandToken,
        CancellationToken taskToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var retryKey = (userId, deviceId, windowEndUtc);

        if (_state.TryMarkFinalized(userId, deviceId, windowEndUtc, nowUtc))
        {
            await SendMessageSafeAsync(session, config.FinalMessageTemplate, commandToken, taskToken).ConfigureAwait(false);
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
            // flag between them is therefore safe, not a collision risk.
            if (_state.TryMarkNotified(userId, deviceId, windowEndUtc, nowUtc))
            {
                _notifier.Notify($"Session {session.Id} for user {userId} does not support media control; cannot enforce cutoff.");
            }

            return;
        }

        try
        {
            await _sessionManager.SendPlaystateCommand(string.Empty, session.Id, new PlaystateRequest { Command = PlaystateCommand.Stop }, commandToken).ConfigureAwait(false);

            // Revokes ALL of this user's tokens (not just this device) -- deliberately broad:
            // a scheduled user having another live device at cutoff is itself worth ending, and
            // SessionInfo exposes no access-token property that would allow a narrower per-device
            // revoke without an extra IDeviceManager lookup. Forcing a fresh login means the next
            // attempt to resume is itself blocked by Jellyfin's own native Access Schedule outside
            // the window -- this closes the "just press Play again" loophole (spec: Core loop
            // step 7).
            await _sessionManager.RevokeUserTokens(userId, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!taskToken.IsCancellationRequested)
        {
            // The per-command timeout fired, not the outer task cancellation -- treat as a
            // failed-to-stop retry below, don't rethrow. NOTE: this filter must test the outer
            // task token, never `commandToken`: commandToken is the linked+timeout token and is
            // therefore *always* cancelled when this fires, so filtering on it would rethrow on
            // every timeout and skip the retry accounting entirely.
            _logger.LogWarning("ScheduleEnforcer: stop/revoke for session {SessionId} timed out", session.Id);
        }

        var previous = _stillLiveRetryCounts.TryGetValue(retryKey, out var existing) ? existing.Count : 0;
        var retryCount = previous + 1;
        _stillLiveRetryCounts[retryKey] = (retryCount, nowUtc);

        if (retryCount >= NotifyAfterConsecutiveRetries &&
            _state.TryMarkNotified(userId, deviceId, windowEndUtc, nowUtc))
        {
            _notifier.Notify($"Session {session.Id} for user {userId} has not stopped after {retryCount} consecutive attempts.");
        }
    }

    private void PruneRetryCounts(DateTimeOffset cutoffUtc)
    {
        foreach (var key in _stillLiveRetryCounts.Where(kvp => kvp.Value.LastSeenUtc < cutoffUtc).Select(kvp => kvp.Key).ToList())
        {
            _stillLiveRetryCounts.TryRemove(key, out _);
        }
    }

    private async Task SendMessageSafeAsync(SessionInfo session, string message, CancellationToken commandToken, CancellationToken taskToken)
    {
        try
        {
            await _sessionManager.SendMessageCommand(
                string.Empty,
                session.Id,
                new MessageCommand { Header = "Schedule Enforcer", Text = message },
                commandToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!taskToken.IsCancellationRequested)
        {
            // See the note in FinalizeAndStopAsync: the filter must test the outer task token,
            // not the linked command token.
            _logger.LogWarning("ScheduleEnforcer: message send to session {SessionId} timed out", session.Id);
        }
    }
}
