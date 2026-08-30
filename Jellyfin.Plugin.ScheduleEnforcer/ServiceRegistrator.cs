using Jellyfin.Plugin.ScheduleEnforcer.Configuration;
using Jellyfin.Plugin.ScheduleEnforcer.ScheduledTasks;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IScheduleWindowCalculator, ScheduleWindowCalculator>();

        // Singleton: enforcement state must persist across ticks within the process lifetime
        // (it's explicitly not durable across restarts -- spec accepts this trade-off, see
        // Fault isolation's StartupTrigger mitigation).
        serviceCollection.AddSingleton<ISessionEnforcementState, SessionEnforcementState>();

        // LoggingNotifier's IActivityManager dependency needs no registration of its own -- it is a
        // core Jellyfin service already in the host container, and constructor injection picks it up.
        serviceCollection.AddSingleton<INotifier, LoggingNotifier>();

        // ScheduleEnforcerTask itself is deliberately NOT registered here. Jellyfin discovers
        // scheduled tasks via ApplicationHost.GetExports<IScheduledTask>(), which calls
        // ActivatorUtilities.CreateInstance(ServiceProvider, type) directly against the CONCRETE
        // ScheduleEnforcerTask type -- it never resolves IScheduledTask from this container, so a
        // registration for that interface would simply never be invoked (dead code, and a drift
        // hazard the moment the constructor changes). Confirmed against Jellyfin 10.11.11's own
        // ApplicationHost.cs.
        //
        // The consequence is that every constructor parameter of ScheduleEnforcerTask must be
        // independently resolvable from this container. Neither TimeZoneInfo nor
        // Func<PluginConfiguration> has a natural registration of its own, hence the two below
        // (confirmed live: "Unable to resolve service for type 'System.TimeZoneInfo'" on startup
        // before they were added). Matches the same gotcha arr-delete-sync's ServiceRegistrator.cs
        // documents for its RetryPolicyOptions.
        serviceCollection.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<ScheduleEnforcerTask>>();

            // Resolved once, at registration time, and logged so a wrong container timezone
            // (spec: Timezone correctness -- Docker defaults to UTC) is visible in the server
            // log from the moment the plugin loads, not discovered later via a wrong cutoff.
            var timeZone = TimeZoneInfo.Local;
            logger.LogInformation("ScheduleEnforcer: resolved container timezone is {TimeZoneId}", timeZone.Id);
            return timeZone;
        });

        serviceCollection.AddSingleton<Func<PluginConfiguration>>(_ => () => Plugin.Instance!.Configuration);
    }
}
