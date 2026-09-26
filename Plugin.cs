using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.EpisodeHistory;

/// <summary>
/// Episode history plugin entry point. The dashboard icon comes from meta.json's
/// <c>imagePath</c> (a plain icon.png next to the DLL), not IHasEmbeddedImage, which a
/// side-loaded plugin can't use.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // This GUID must match meta.json and pluginUniqueId in Configuration/configPage.html.
    private static readonly Guid PluginId = Guid.Parse("62b08fb5-2008-4b2b-ace8-e73d3e584405");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Episode History";

    public override string Description =>
        "Remembers what the DVR recorded and what was watched, marks guide entries accordingly, " +
        "and cancels series recordings of episodes already in the library.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
