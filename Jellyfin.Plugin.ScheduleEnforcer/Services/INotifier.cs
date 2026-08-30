using System.Threading.Tasks;

namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

// Seam for admin-facing alerts on persistent enforcement failure. LoggingNotifier is the only
// implementation for this iteration -- adding a real push channel later (ntfy, Signal, etc.) means
// a new INotifier implementation plus a DI registration swap in ServiceRegistrator, with no change
// to ScheduleEnforcerTask.
//
// Async by design (spec: Admin error notifications names NotifyAsync): the shipped implementation
// writes a Jellyfin activity-log entry via IActivityManager.CreateAsync, and any real push channel
// added later would be I/O-bound too. A synchronous surface would force every such implementation
// to block or fire-and-forget.
public interface INotifier
{
    Task NotifyAsync(string message);
}
