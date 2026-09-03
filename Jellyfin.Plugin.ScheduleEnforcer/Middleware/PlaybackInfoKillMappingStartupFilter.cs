using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.ScheduleEnforcer.Middleware;

public class PlaybackInfoKillMappingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<PlaybackInfoKillMappingMiddleware>();
            next(app);
        };
    }
}
