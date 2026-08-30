using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests.Services;

public class LoggingNotifierTests
{
    [Fact]
    public async Task NotifyAsync_LogsAtErrorLevel()
    {
        var logger = new Mock<ILogger<LoggingNotifier>>();
        var activityManager = new Mock<IActivityManager>();
        var notifier = new LoggingNotifier(logger.Object, activityManager.Object);

        await notifier.NotifyAsync("session did not stop after 3 retries");

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("session did not stop after 3 retries")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAsync_WritesAdminActivityLogEntry()
    {
        // The spec's "Admin error notifications" decision is ILogger *plus* an IActivityManager
        // entry, so the alert reaches the dashboard bell and not only the server log file.
        var logger = new Mock<ILogger<LoggingNotifier>>();
        var activityManager = new Mock<IActivityManager>();
        var notifier = new LoggingNotifier(logger.Object, activityManager.Object);

        await notifier.NotifyAsync("session did not stop after 3 retries");

        activityManager.Verify(
            a => a.CreateAsync(It.Is<ActivityLog>(e =>
                e.Type == "ScheduleEnforcerAlert" &&
                e.UserId == Guid.Empty &&
                e.LogSeverity == LogLevel.Error &&
                e.Overview == "session did not stop after 3 retries")),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAsync_TruncatesOverlongMessageForTheEntryName()
    {
        // ActivityLog.Name is what the dashboard list renders; the untruncated text stays in
        // Overview so nothing is actually lost.
        var logger = new Mock<ILogger<LoggingNotifier>>();
        var activityManager = new Mock<IActivityManager>();
        var notifier = new LoggingNotifier(logger.Object, activityManager.Object);

        var longMessage = new string('x', 500);
        await notifier.NotifyAsync(longMessage);

        activityManager.Verify(
            a => a.CreateAsync(It.Is<ActivityLog>(e => e.Name.Length <= 120 && e.Overview == longMessage)),
            Times.Once);
    }
}
