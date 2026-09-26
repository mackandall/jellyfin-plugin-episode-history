using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Seeds the history from what the server already has: every episode/movie under a DVR
/// recording folder becomes a "recorded" row, and every episode/movie any user has played
/// becomes a "watched" row. Runs automatically when the history is empty and on demand
/// (scheduled task / settings page). Never overwrites rows captured from a finished
/// recording, which know more (the guide's programme id and numbering).
/// </summary>
public sealed class LibraryImporter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly HistoryStore _store;
    private readonly ILogger<LibraryImporter> _logger;

    public LibraryImporter(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IRecordingsManager recordingsManager,
        HistoryStore store,
        ILogger<LibraryImporter> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _recordingsManager = recordingsManager;
        _store = store;
        _logger = logger;
    }

    public sealed record ImportResult(DateTime FinishedUtc, int Items, int Recordings, int WatchedEntries, int RowsAdded);

    public ImportResult? LastImport { get; private set; }

    public Task<ImportResult> ImportAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Import(progress, cancellationToken), cancellationToken);
    }

    private ImportResult Import(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var marker = new GuideMarker(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        var folders = _recordingsManager.GetRecordingFolders()
            .SelectMany(f => f.Locations)
            .Select(p => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar)
            .ToList();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        });

        var identities = new Dictionary<Guid, EpisodeIdentity>();
        foreach (var item in items)
        {
            if (EpisodeIdentity.FromLibraryItem(item, marker, null) is { } identity)
            {
                identities[item.Id] = identity;
            }
        }

        var rows = new List<(string, Guid?, Guid, EpisodeIdentity, string, DateTime)>();
        int recordings = 0;
        foreach (var item in items)
        {
            if (identities.TryGetValue(item.Id, out var identity) && !string.IsNullOrEmpty(item.Path)
                && folders.Any(f => item.Path.StartsWith(f, StringComparison.Ordinal)))
            {
                rows.Add((HistoryStore.KindRecorded, null, item.Id, identity, "import", item.DateCreated));
                recordings++;
            }
        }

        progress?.Report(30);
        cancellationToken.ThrowIfCancellationRequested();

        int watched = 0;
        var users = _userManager.GetUsers().ToList();
        var candidates = items.Where(i => identities.ContainsKey(i.Id)).ToList();
        for (int u = 0; u < users.Count; u++)
        {
            var data = _userDataManager.GetUserDataBatch(candidates, users[u]);
            foreach (var item in candidates)
            {
                if (data.TryGetValue(item.Id, out var userData) && userData.Played)
                {
                    rows.Add((HistoryStore.KindWatched, users[u].Id, item.Id, identities[item.Id], "import", userData.LastPlayedDate ?? item.DateCreated));
                    watched++;
                }
            }

            progress?.Report(30 + (60.0 * (u + 1) / users.Count));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var added = _store.UpsertMany(rows, overwrite: false);
        progress?.Report(100);

        var result = new ImportResult(DateTime.UtcNow, items.Count, recordings, watched, added);
        LastImport = result;
        _logger.LogInformation(
            "Episode history import: {Items} library episodes/movies, {Recordings} recordings, {Watched} watched entries; {Added} new rows",
            items.Count, recordings, watched, added);
        return result;
    }
}
