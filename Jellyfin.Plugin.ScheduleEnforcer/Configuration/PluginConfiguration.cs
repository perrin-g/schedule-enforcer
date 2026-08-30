using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ScheduleEnforcer.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    // Free-form, not restricted to a fixed dropdown of values — user decision during design.
    public int WarningMinutesBeforeEnd { get; set; } = 10;

    // Supports a {minutes} placeholder, replaced at send time.
    public string WarningMessageTemplate { get; set; } = "Your screen time ends in {minutes} minutes.";

    public string FinalMessageTemplate { get; set; } = "Your screen time is up.";
}
