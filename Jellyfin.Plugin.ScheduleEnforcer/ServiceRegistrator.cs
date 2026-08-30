using Jellyfin.Plugin.ScheduleEnforcer.Configuration;
using Jellyfin.Plugin.ScheduleEnforcer.ScheduledTasks;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
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

        serviceCollection.AddSingleton<INotifier, LoggingNotifier>();

        // Jellyfin's host independently tries to construct every IScheduledTask implementation
        // via its own DI activator, in addition to -- not instead of -- the factory registration
        // below (confirmed live: "Unable to resolve service for type 'System.TimeZoneInfo'" on
        // startup before these two registrations were added). Neither TimeZoneInfo nor
        // Func<PluginConfiguration> has a natural DI registration of its own, so both need an
        // explicit one for that second construction path to succeed. Matches the same gotcha
        // arr-delete-sync's ServiceRegistrator.cs documents for its RetryPolicyOptions.
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

        serviceCollection.AddSingleton<IScheduledTask>(provider =>
        {
            var sessionManager = provider.GetRequiredService<ISessionManager>();
            var userManager = provider.GetRequiredService<IUserManager>();
            var windowCalculator = provider.GetRequiredService<IScheduleWindowCalculator>();
            var state = provider.GetRequiredService<ISessionEnforcementState>();
            var notifier = provider.GetRequiredService<INotifier>();
            var timeZone = provider.GetRequiredService<TimeZoneInfo>();
            var getConfig = provider.GetRequiredService<Func<PluginConfiguration>>();
            var logger = provider.GetRequiredService<ILogger<ScheduleEnforcerTask>>();

            return new ScheduleEnforcerTask(sessionManager, userManager, windowCalculator, state, notifier, timeZone, getConfig, logger);
        });
    }
}
