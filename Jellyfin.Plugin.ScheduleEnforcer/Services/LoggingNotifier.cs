using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

public class LoggingNotifier : INotifier
{
    private readonly ILogger<LoggingNotifier> _logger;

    public LoggingNotifier(ILogger<LoggingNotifier> logger)
    {
        _logger = logger;
    }

    public void Notify(string message)
    {
        _logger.LogError("ScheduleEnforcer: {Message}", message);
    }
}
