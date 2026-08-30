using Jellyfin.Plugin.ScheduleEnforcer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests.Services;

public class LoggingNotifierTests
{
    [Fact]
    public void Notify_LogsAtErrorLevel()
    {
        var logger = new Mock<ILogger<LoggingNotifier>>();
        var notifier = new LoggingNotifier(logger.Object);

        notifier.Notify("session did not stop after 3 retries");

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("session did not stop after 3 retries")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
