using System.IO;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

// The plugin's settings are one static instance: the tests take turns.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.EpisodeHistory.Tests;

/// <summary>The plugin, loaded once with settings the tests can change (<see cref="Plugin.Instance"/> is what the code reads).</summary>
internal static class TestPlugin
{
    public static readonly PluginConfiguration Config = new();

    static TestPlugin()
    {
        var paths = new Mock<IApplicationPaths>();
        var root = Path.Combine(Path.GetTempPath(), "eh-tests-plugin");
        paths.Setup(p => p.PluginConfigurationsPath).Returns(root);
        paths.Setup(p => p.PluginsPath).Returns(root);
        paths.Setup(p => p.DataPath).Returns(root);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(Config);
        _ = new Plugin(paths.Object, xml.Object).Configuration;
    }

    /// <summary>Trash on, kept 2 days, no free-space purge (the test drive's free space is whatever it is).</summary>
    public static void Reset()
    {
        Config.TrashEnabled = true;
        Config.TrashKeepDays = 2;
        Config.TrashMinFreeGb = 0;
        Config.TrashClearNasRecycle = false;
    }
}
