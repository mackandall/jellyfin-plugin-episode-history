using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// What the DVR recorded, for the home screen's recordings row (SageTV's Recordings view,
/// Channels DVR's Recordings section): every "recorded" history row whose file is still in
/// the library, whether it's still in the recordings folders ("current") or has been moved
/// to an archive folder ("archived", e.g. by JellyDVR Space).
///
/// A moved file is a new library item, so a row whose item is gone is looked up again by
/// the file name it was recorded under (moves keep the name) and re-pointed at the new item.
/// That also keeps the guide's "in library" marks right after a move.
/// </summary>
public sealed class RecordingsList
{
    public const string FilterAll = "all";
    public const string FilterCurrent = "current";
    public const string FilterArchived = "archived";

    // A row whose recording can't be found (deleted) is looked up again at most this often.
    private static readonly TimeSpan RelinkRetry = TimeSpan.FromMinutes(30);

    private readonly HistoryStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<RecordingsList> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, DateTime> _lastRelinkTry = new();

    public RecordingsList(
        HistoryStore store,
        ILibraryManager libraryManager,
        IRecordingsManager recordingsManager,
        IUserDataManager userDataManager,
        ILogger<RecordingsList> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _recordingsManager = recordingsManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>A recording that is in the library now.</summary>
    public sealed record Recording(HistoryStore.RecordedRow Row, Video Item, bool Archived);

    /// <summary>One card of the row: a show (several recordings), an episode, or a movie.</summary>
    public sealed record Card(
        string Type,
        Guid Id,
        Guid ImageId,
        string Name,
        string? Subtitle,
        int Count,
        int UnplayedCount,
        int ArchivedCount,
        DateTime RecordedUtc);

    /// <summary>
    /// Every recording still in the library, newest first. Records file names of rows that
    /// lack one and re-points rows whose recording was moved.
    /// </summary>
    public List<Recording> Resolve()
    {
        lock (_lock)
        {
            var folders = RecordingFiles.Folders(_recordingsManager);
            var now = DateTime.UtcNow;
            var names = new List<(long, string)>();
            var result = new List<Recording>();
            var seen = new HashSet<Guid>();

            foreach (var row in _store.LoadRecorded())
            {
                var item = _libraryManager.GetItemById(row.ItemId) as Video;
                if (item is null || string.IsNullOrEmpty(item.Path))
                {
                    item = FindMoved(row, now);
                    if (item is null)
                    {
                        continue;
                    }
                }

                var fileName = Path.GetFileName(item.Path);
                if (row.FileName is null && !string.IsNullOrEmpty(fileName))
                {
                    names.Add((row.Id, fileName));
                }

                if (seen.Add(item.Id))
                {
                    result.Add(new Recording(row, item, !RecordingFiles.IsUnder(item.Path, folders)));
                }
            }

            if (names.Count > 0)
            {
                _store.SetFileNames(names);
            }

            return result;
        }
    }

    /// <summary>The row's cards for one user.</summary>
    public List<Card> Cards(User user, string filter, bool groupByShow, bool includeMovies, int limit)
    {
        var recordings = Resolve()
            .Where(r => r.Item.IsVisible(user))
            .Where(r => includeMovies || r.Item is not Movie)
            .Where(r => filter switch
            {
                FilterCurrent => !r.Archived,
                FilterArchived => r.Archived,
                _ => true
            })
            .ToList();

        // Newest recording first; a show's card sits where its newest recording is.
        var groups = new List<List<Recording>>();
        var byShow = new Dictionary<Guid, List<Recording>>();
        foreach (var r in recordings)
        {
            if (groupByShow && r.Item is Episode { SeriesId: var seriesId } && !seriesId.Equals(Guid.Empty))
            {
                if (!byShow.TryGetValue(seriesId, out var group))
                {
                    group = new List<Recording>();
                    byShow[seriesId] = group;
                    groups.Add(group);
                }

                group.Add(r);
            }
            else
            {
                groups.Add(new List<Recording> { r });
            }
        }

        return groups.Take(Math.Clamp(limit, 1, 200)).Select(g => ToCard(g, user)).ToList();
    }

    /// <summary>One recording on a show's page.</summary>
    public sealed record EpisodeCard(
        Guid Id,
        string Name,
        int? Season,
        int? Episode,
        bool Played,
        bool Archived,
        DateTime RecordedUtc);

    /// <summary>
    /// A show's recordings for one user, newest first, one card per recording. Matched on the
    /// show's merge key, so a show whose folders the library merges (recordings folder plus
    /// archive folders) lists all its recordings whichever folder's series page is open.
    /// </summary>
    public List<EpisodeCard>? ShowCards(User user, Guid seriesId)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return null;
        }

        var key = series.PresentationUniqueKey;
        return Resolve()
            .Where(r => r.Item is Episode e
                && (e.SeriesId.Equals(seriesId) || (!string.IsNullOrEmpty(key) && string.Equals(e.FindSeriesPresentationUniqueKey(), key, StringComparison.Ordinal)))
                && r.Item.IsVisible(user))
            .Select(r =>
            {
                var e = (Episode)r.Item;
                return new EpisodeCard(
                    e.Id,
                    EpisodeLine(e) ?? e.Name,
                    e.ParentIndexNumber,
                    e.IndexNumber,
                    _userDataManager.GetUserData(user, e)?.Played == true,
                    r.Archived,
                    RecordedAt(r));
            })
            .OrderByDescending(c => c.RecordedUtc)
            .ThenByDescending(c => c.Season ?? 0)
            .ThenByDescending(c => c.Episode ?? 0)
            .ToList();
    }

    /// <summary>
    /// When a recording was made. The history's time is the finish time for recordings the
    /// plugin saw being made, but for imported rows it is the library's "date added", which a
    /// rescan can reset; the file's last write (the moment recording stopped) is earlier then.
    /// </summary>
    private static DateTime RecordedAt(Recording r)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(r.Item.Path);
            if (written.Year > 2000 && written < r.Row.EventUtc)
            {
                return written;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return r.Row.EventUtc;
    }

    private Card ToCard(List<Recording> group, User user)
    {
        var newest = group[0];
        int unplayed = group.Count(r => _userDataManager.GetUserData(user, r.Item)?.Played != true);
        int archived = group.Count(r => r.Archived);

        if (newest.Item is Episode episode)
        {
            var seriesId = episode.SeriesId;
            var imageId = !seriesId.Equals(Guid.Empty) && _libraryManager.GetItemById(seriesId) is { } series && series.HasImage(ImageType.Primary)
                ? seriesId
                : episode.Id;
            var showName = episode.SeriesName ?? episode.Name;
            if (group.Count > 1)
            {
                return new Card("Series", seriesId, imageId, showName, $"{group.Count} recordings", group.Count, unplayed, archived, newest.Row.EventUtc);
            }

            return new Card("Episode", episode.Id, imageId, showName, EpisodeLine(episode), 1, unplayed, archived, newest.Row.EventUtc);
        }

        var item = newest.Item;
        var subtitle = item is Movie ? item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new Card(item is Movie ? "Movie" : "Video", item.Id, item.Id, item.Name, subtitle, 1, unplayed, archived, newest.Row.EventUtc);
    }

    private static string? EpisodeLine(Episode episode)
    {
        var number = episode.ParentIndexNumber is { } s && episode.IndexNumber is { } e ? $"S{s}:E{e}" : null;
        var title = string.IsNullOrWhiteSpace(episode.Name) ? null : episode.Name;
        return number is not null && title is not null ? $"{number} - {title}" : number ?? title;
    }

    /// <summary>
    /// The library item a row's recording lives under after a move: same file name, and for
    /// episodes the same season/episode numbers (movies: same title). Null if deleted.
    /// </summary>
    private Video? FindMoved(HistoryStore.RecordedRow row, DateTime now)
    {
        if (row.FileName is null
            || (_lastRelinkTry.TryGetValue(row.Id, out var last) && now - last < RelinkRetry))
        {
            return null;
        }

        _lastRelinkTry[row.Id] = now;
        var id = row.Identity;
        InternalItemsQuery query;
        if (id.IsMovie && !string.IsNullOrEmpty(id.SeriesName))
        {
            query = new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie }, Name = id.SeriesName };
        }
        else if (id.Season is { } season && id.Episode is { } number)
        {
            query = new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode }, ParentIndexNumber = season, IndexNumber = number };
        }
        else if (!string.IsNullOrEmpty(id.EpisodeTitle))
        {
            query = new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Video }, Name = id.EpisodeTitle };
        }
        else
        {
            return null;
        }

        query.IsVirtualItem = false;
        query.Recursive = true;
        var moved = _libraryManager.GetItemList(query)
            .OfType<Video>()
            .FirstOrDefault(v => string.Equals(Path.GetFileName(v.Path), row.FileName, StringComparison.Ordinal));
        if (moved is null)
        {
            return null;
        }

        if (_store.Relink(row.Id, moved.Id))
        {
            _logger.LogInformation("Recording {File} moved to {Path}; history follows it", row.FileName, moved.Path);
        }

        _lastRelinkTry.Remove(row.Id);
        return moved;
    }
}
