using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.ScheduleEnforcer.Tests;

public class PluginTests
{
    [Fact]
    public void Constructor_SetsStaticInstance()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "scheduleenforcer-plugintests-" + Guid.NewGuid());
        Directory.CreateDirectory(dataPath);

        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.Setup(p => p.DataPath).Returns(dataPath);
        applicationPaths.Setup(p => p.PluginConfigurationsPath).Returns(dataPath);
        applicationPaths.Setup(p => p.PluginsPath).Returns(dataPath);
        var xmlSerializer = new Mock<IXmlSerializer>(MockBehavior.Loose);
        // BasePlugin<T>.Configuration lazily deserializes the config file on first access
        // and falls back to a fresh default instance only when deserialization throws (e.g.
        // the file doesn't exist yet). A loose mock returns null instead of throwing, which
        // would leave Configuration null, so simulate the real "no config file yet" case.
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Throws<FileNotFoundException>();

        var plugin = new Plugin(applicationPaths.Object, xmlSerializer.Object);

        Assert.Same(plugin, Plugin.Instance);
        Assert.True(plugin.Configuration.Enabled);
        Assert.Equal(10, plugin.Configuration.WarningMinutesBeforeEnd);
    }
}
