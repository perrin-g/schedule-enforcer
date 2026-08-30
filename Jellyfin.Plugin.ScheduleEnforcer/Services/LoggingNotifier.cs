using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

// Two channels, deliberately (spec: Admin error notifications): the ILogger line is what survives
// in the server log for after-the-fact diagnosis, and the IActivityManager entry is what actually
// surfaces the alert to an admin in Jellyfin's Activity Log / dashboard bell without them having to
// go read a log file.
//
// API shapes confirmed by reflection against the installed Jellyfin.Controller/Jellyfin.Model
// 10.11.11 packages, per this project's convention:
//   MediaBrowser.Model.Activity.IActivityManager.CreateAsync(ActivityLog entry) -> Task
//   Jellyfin.Database.Implementations.Entities.ActivityLog(string name, string type, Guid userId)
//     with settable Overview / ShortOverview / LogSeverity (Microsoft.Extensions.Logging.LogLevel).
public class LoggingNotifier : INotifier
{
    // Distinct, greppable Type value so these entries can be filtered out of the activity log.
    private const string ActivityType = "ScheduleEnforcerAlert";

    // Activity entries are truncated hard in the dashboard list; the full text lives in Overview.
    private const int NameMaxLength = 120;

    private readonly ILogger<LoggingNotifier> _logger;
    private readonly IActivityManager _activityManager;

    public LoggingNotifier(ILogger<LoggingNotifier> logger, IActivityManager activityManager)
    {
        _logger = logger;
        _activityManager = activityManager;
    }

    public async Task NotifyAsync(string message)
    {
        _logger.LogError("ScheduleEnforcer: {Message}", message);

        // Guid.Empty: this is a system-level admin alert about enforcement failing, not an entry in
        // some specific user's own activity history.
        var entry = new ActivityLog(Summarise(message), ActivityType, Guid.Empty)
        {
            Overview = message,
            ShortOverview = "Schedule Enforcer",
            LogSeverity = LogLevel.Error
        };

        await _activityManager.CreateAsync(entry).ConfigureAwait(false);
    }

    private static string Summarise(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "Schedule Enforcer alert";
        }

        return message.Length <= NameMaxLength
            ? message
            : message[..(NameMaxLength - 1)] + "…";
    }
}
