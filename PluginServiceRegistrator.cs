using Jellyfin.Plugin.EpisodeHistory.History;
using Jellyfin.Plugin.EpisodeHistory.Services;
using Jellyfin.Plugin.EpisodeHistory.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EpisodeHistory;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<HistoryStore>();
        serviceCollection.AddSingleton<LibraryImporter>();
        serviceCollection.AddSingleton<BackupService>();
        serviceCollection.AddSingleton<StallNotes>();
        serviceCollection.AddSingleton<Programmes>();
        serviceCollection.AddSingleton<RecordingRepair>();
        serviceCollection.AddSingleton<RecordingChannels>();
        serviceCollection.AddSingleton<ScheduleLedger>();
        serviceCollection.AddSingleton<RecordingsList>();
        serviceCollection.AddSingleton<ScheduleOutcomes>();
        serviceCollection.AddSingleton<GuideSweeper>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<GuideSweeper>());
        serviceCollection.AddHostedService<HistoryEvents>();

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(HistoryController).Assembly);
    }
}
