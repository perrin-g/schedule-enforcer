using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ScheduleEnforcer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ScheduleEnforcer;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Schedule Enforcer";

    public override Guid Id => Guid.Parse("5c90bb47-9d60-4b70-9265-3f2d025fcdd8");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace),
                DisplayName = "Schedule Enforcer Settings",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "schedule"
            }
        };
    }
}
