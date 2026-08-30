namespace Jellyfin.Plugin.ScheduleEnforcer.Services;

// Seam for admin-facing alerts on persistent enforcement failure. LoggingNotifier is the only
// implementation for this iteration (spec: Admin error notifications, deferred) -- adding a real
// push channel later (ntfy, Signal, etc.) means a new INotifier implementation plus a DI
// registration swap in ServiceRegistrator, with no change to ScheduleEnforcerTask.
public interface INotifier
{
    void Notify(string message);
}
