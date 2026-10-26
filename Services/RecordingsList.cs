using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
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
    private readonly RecordingRepair _repair;
    private readonly RecordingChannels _channels;
    private readonly ILogger<RecordingsList> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, DateTime> _lastRelinkTry = new();

    public RecordingsList(
        HistoryStore store,
        ILibraryManager libraryManager,
        IRecordingsManager recordingsManager,
        IUserDataManager userDataManager,
        RecordingRepair repair,
        RecordingChannels channels,
        ILogger<RecordingsList> logger)
    {
        _channels = channels;
        _repair = repair;
        _store = store;
        _libraryManager = libraryManager;
        _recordingsManager = recordingsManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// A recording that is in the library now. <paramref name="Partial"/>: a file in the recordings
    /// folders that doesn't count as recorded (cut short, a failed stub); listed so it can be found
    /// and deleted, but never part of the history (guide marks, duplicate skipping).
    /// <paramref name="InProgress"/>: still being recorded; it joins the history when it finishes.
    /// </summary>
    public sealed record Recording(HistoryStore.RecordedRow Row, Video Item, bool Archived, bool Partial = false, bool InProgress = false);

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
        DateTime RecordedUtc,
        int PartialCount = 0,
        int? PartialMinutes = null,
        int RecordingCount = 0,
        int StalledCount = 0,
        string? StallText = null,
        string? StallTitle = null);

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
        // Programmes without season and episode: movies as far as Jellyfin goes, listed whatever
        // the movies choice is (their library may be hidden, so this can be the only place).
        var other = Plugin.Instance?.Configuration is { RecordingsRowIncludeOther: false }
            ? new List<string>()
            : RecordingFiles.OtherFolders(_recordingsManager);
        var recordings = WithPartials(Resolve())
            .OrderByDescending(r => r.Row.EventUtc)
            .Where(r => r.Item.IsVisible(user))
            .Where(r => includeMovies || r.Item is not Movie || RecordingFiles.IsUnder(r.Item.Path, other))
            .Where(r => filter switch
            {
                FilterCurrent => !r.Archived,
                FilterArchived => r.Archived,
                _ => true
            })
            .ToList();

        // Newest recording first; a show's card sits where its newest recording is.
        var groups = new List<List<Recording>>();
        var byShow = new Dictionary<string, List<Recording>>(StringComparer.Ordinal);
        foreach (var r in recordings)
        {
            if (groupByShow && r.Item is Episode { SeriesId: var seriesId } episode && !seriesId.Equals(Guid.Empty))
            {
                // One card per show, not per folder: a show recorded in one place and moved to
                // others has a series item in each, all with the show's merge key (as the
                // library and the show's page join them).
                var key = episode.FindSeriesPresentationUniqueKey();
                if (string.IsNullOrEmpty(key))
                {
                    key = seriesId.ToString("N");
                }

                if (!byShow.TryGetValue(key, out var group))
                {
                    group = new List<Recording>();
                    byShow[key] = group;
                    groups.Add(group);
                }

                group.Add(r);
            }
            else
            {
                groups.Add(new List<Recording> { r });
            }
        }

        return groups.Take(Math.Clamp(limit, 1, 200)).Select(g => ToCard(g, user, other)).ToList();
    }

    /// <summary>One recording in the list by date (SageTV's and Channels DVR's recent recordings).</summary>
    public sealed record DateEntry(
        Guid Id,
        string Type,
        Guid ImageId,
        string Title,
        string? EpisodeTitle,
        int? Season,
        int? Episode,
        int? Year,
        DateTime RecordedUtc,
        int? Minutes,
        bool Played,
        int PlayedPercent,
        bool Archived,
        bool Recording,
        bool Partial,
        string? StatusShort,
        string? StatusText,
        string? Overview,
        RecordingChannels.Channel? Channel);

    /// <summary>Every recording for one user, newest first, one entry each (no grouping by show).</summary>
    public List<DateEntry> ByDate(User user, string filter, bool includeMovies, int limit)
    {
        var other = Plugin.Instance?.Configuration is { RecordingsRowIncludeOther: false }
            ? new List<string>()
            : RecordingFiles.OtherFolders(_recordingsManager);
        return WithPartials(Resolve())
            .Where(r => r.Item.IsVisible(user))
            .Where(r => includeMovies || r.Item is not Movie || RecordingFiles.IsUnder(r.Item.Path, other))
            .Where(r => filter switch
            {
                FilterCurrent => !r.Archived,
                FilterArchived => r.Archived,
                _ => true
            })
            .Select(r => (Recording: r, At: r.InProgress ? r.Row.EventUtc : StartedAt(r)))
            .OrderByDescending(x => x.Recording.InProgress)
            .ThenByDescending(x => x.At)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(x => ToDateEntry(x.Recording, x.At, user))
            .ToList();
    }

    private DateEntry ToDateEntry(Recording r, DateTime at, User user)
    {
        var item = r.Item;
        var data = _userDataManager.GetUserData(user, item);
        var percent = data is { Played: false, PlaybackPositionTicks: > 0 } && item.RunTimeTicks is > 0 and var runtime
            ? (int)Math.Clamp(data.PlaybackPositionTicks * 100 / runtime, 1, 99)
            : 0;
        var warning = r.InProgress ? default : _repair.Warning(item.Path);
        var imageId = item.HasImage(ImageType.Primary) ? item.Id
            : item is Episode { SeriesId: var sid } && !sid.Equals(Guid.Empty) ? sid
            : item.Id;
        var episode = item as Episode;
        return new DateEntry(
            item.Id,
            item is Episode ? "Episode" : item is Movie ? "Movie" : "Video",
            imageId,
            episode?.SeriesName ?? item.Name,
            episode is null || string.IsNullOrWhiteSpace(episode.Name) ? null : episode.Name,
            episode?.ParentIndexNumber,
            episode?.IndexNumber,
            item.ProductionYear,
            at,
            Minutes(item),
            data?.Played == true,
            percent,
            r.Archived,
            r.InProgress,
            r.Partial,
            warning.Short,
            warning.Text,
            item.Overview,
            _channels.For(item.Path));
    }

    /// <summary>One recording on a show's page.</summary>
    public sealed record EpisodeCard(
        Guid Id,
        string Name,
        int? Season,
        int? Episode,
        bool Played,
        bool Archived,
        DateTime RecordedUtc,
        bool Partial = false,
        int? Minutes = null,
        bool Recording = false,
        string? StallText = null,
        string? StallTitle = null);

    private static int? Minutes(Video item) =>
        item.RunTimeTicks is > 0 and var t ? (int)Math.Max(1, Math.Round(TimeSpan.FromTicks(t).TotalMinutes)) : null;

    /// <summary>The library items the history counts as recorded (a recording that finished).</summary>
    public HashSet<Guid> HistoryItemIds() => Resolve().Select(r => r.Item.Id).ToHashSet();

    /// <summary>
    /// A partial recording: a file in the recordings folders the history never counted (the
    /// recording didn't finish), not one being recorded now and not a live TV buffer. Unlike the
    /// list, an episode's other versions count too.
    /// </summary>
    public bool IsPartial(Video item, IReadOnlySet<Guid> historyItemIds) =>
        !string.IsNullOrEmpty(item.Path)
        && RecordingFiles.IsUnder(item.Path, RecordingFiles.Folders(_recordingsManager))
        && !historyItemIds.Contains(item.Id)
        && !LiveBuffers.IsUnkeptBuffer(item)
        && !BeingRecorded(item.Path);

    /// <summary>
    /// Adds the files in the recordings folders that aren't in the history: recordings still being
    /// made (marked in progress) and partial recordings, each unless turned off. Skips files
    /// written within the settle time (a setting, 5 minutes unless changed) that aren't being recorded (a finished recording is added
    /// to the history shortly after it lands), and unkept live TV buffers.
    /// </summary>
    private List<Recording> WithPartials(List<Recording> recordings)
    {
        var cfg = Plugin.Instance?.Configuration;
        var partials = cfg is not { ShowPartialRecordings: false };
        var inProgress = cfg is not { ShowInProgressRecordings: false };
        if (!partials && !inProgress)
        {
            return recordings;
        }

        var known = recordings.Select(r => r.Item.Id).ToHashSet();
        var now = DateTime.UtcNow;
        var settled = now - TimeSpan.FromMinutes(Math.Clamp(cfg?.PartialRecordingSettleMinutes ?? 5, 0, 60));
        var result = new List<Recording>(recordings);
        foreach (var folderPath in RecordingFiles.Folders(_recordingsManager))
        {
            if (_libraryManager.FindByPath(Path.TrimEndingDirectorySeparator(folderPath), true) is not Folder folder)
            {
                continue;
            }

            // A recording made beside an existing copy (a re-record, or one resumed after a restart:
            // "Name - 1.ts") is an alternate version of that copy, which the folder's children
            // leave out; the ones being made or just ended are added from what is being tracked.
            var pieces = (HistoryEvents.Current?.PendingPaths ?? Array.Empty<string>())
                .Where(p => p.StartsWith(folderPath, StringComparison.Ordinal))
                .Select(p => RecordingFiles.ItemAt(_libraryManager, p))
                .OfType<Video>()
                .Where(v => !v.OwnerId.Equals(Guid.Empty));
            foreach (var item in folder.GetRecursiveChildren(i => i is Video { IsVirtualItem: false } v && v.OwnerId.Equals(Guid.Empty)).OfType<Video>().Concat(pieces))
            {
                if (known.Contains(item.Id) || string.IsNullOrEmpty(item.Path)
                    || LiveBuffers.IsUnkeptBuffer(item))
                {
                    continue;
                }

                // Being recorded now: listed first, as a recording and not as a partial one.
                if (BeingRecorded(item.Path))
                {
                    if (inProgress)
                    {
                        known.Add(item.Id);
                        var recordingIdentity = EpisodeIdentity.FromLibraryItem(item, new GuideMarker(cfg ?? new PluginConfiguration()), null)
                            ?? new EpisodeIdentity(null, item.Name, null, null, null, null, false, null);
                        result.Add(new Recording(
                            new HistoryStore.RecordedRow(-1, item.Id, recordingIdentity, "recording", now, Path.GetFileName(item.Path)),
                            item,
                            false,
                            false,
                            true));
                    }

                    continue;
                }

                // Just ended and not decided yet (counted or not): it stays in the list as a
                // plain recording, so a recording never drops out between stopping and its
                // decision. One that counts is then in the history; one that doesn't is partial.
                var events = HistoryEvents.Current;
                if (inProgress && (events?.IsPending(item.Path) == true || (HistoryEvents.IsStopping(item.Path) && events?.IsDecided(item.Path) != true)))
                {
                    known.Add(item.Id);
                    var endedIdentity = EpisodeIdentity.FromLibraryItem(item, new GuideMarker(cfg ?? new PluginConfiguration()), null)
                        ?? new EpisodeIdentity(null, item.Name, null, null, null, null, false, null);
                    result.Add(new Recording(
                        new HistoryStore.RecordedRow(-1, item.Id, endedIdentity, "recording", now, Path.GetFileName(item.Path)),
                        item,
                        false));
                    continue;
                }

                // Alternate versions only while being made or just ended (the joiner deals with the rest).
                if (!partials || !item.OwnerId.Equals(Guid.Empty))
                {
                    continue;
                }

                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(item.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                // The settle time is for files nothing is known about; a recording the DVR
                // made and already decided not to count is partial right away.
                if (written.Year < 2000 || (written > settled && events?.IsDecided(item.Path) != true))
                {
                    continue;
                }

                known.Add(item.Id);
                var identity = EpisodeIdentity.FromLibraryItem(item, new GuideMarker(cfg ?? new PluginConfiguration()), null)
                    ?? new EpisodeIdentity(null, item.Name, null, null, null, null, false, null);
                result.Add(new Recording(
                    new HistoryStore.RecordedRow(-1, item.Id, identity, "partial", written, Path.GetFileName(item.Path)),
                    item,
                    false,
                    true));
            }
        }

        return result;
    }

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

        return OfShow(series)
            .Where(r => r.Item.IsVisible(user))
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
                    RecordedAt(r),
                    r.Partial,
                    r.Partial ? Minutes(r.Item) : null,
                    r.InProgress,
                    r.InProgress ? null : _repair.Warning(e.Path).Short,
                    r.InProgress ? null : _repair.Warning(e.Path).Text);
            })
            .OrderByDescending(c => c.RecordedUtc)
            .ThenByDescending(c => c.Season ?? 0)
            .ThenByDescending(c => c.Episode ?? 0)
            .ToList();
    }

    /// <summary>
    /// A show's episode files in the DVR recordings folders, whatever the history says (every
    /// version; not live TV buffers that aren't kept), for the show's merge key (see
    /// <see cref="ShowCards"/>). With <paramref name="archive"/>, its episode files everywhere
    /// else in the libraries instead (the archive copies). Null when the id isn't a show.
    /// </summary>
    public List<Video>? ShowFiles(Guid seriesId, bool archive = false)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return null;
        }

        var folders = RecordingFiles.Folders(_recordingsManager);
        var key = series.PresentationUniqueKey;
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        };
        if (string.IsNullOrEmpty(key))
        {
            query.AncestorIds = new[] { series.Id };
        }
        else
        {
            query.SeriesPresentationUniqueKey = key;
        }

        // The query gives one item per episode; its other versions (the pieces of an interrupted
        // recording, a second copy) are files of their own, here or in another folder.
        return _libraryManager.GetItemList(query)
            .OfType<Episode>()
            .Where(e => e.SeriesId.Equals(series.Id) || (!string.IsNullOrEmpty(key) && string.Equals(e.FindSeriesPresentationUniqueKey(), key, StringComparison.Ordinal)))
            .SelectMany(Versions)
            .Where(v => RecordingFiles.IsUnder(v.Path, folders) != archive
                && File.Exists(v.Path)
                && !LiveBuffers.IsUnkeptBuffer(v))
            .DistinctBy(v => v.Id)
            .ToList();
    }

    /// <summary>An episode's versions: its primary and the primary's alternates (this one included).</summary>
    private IEnumerable<Video> Versions(Video item)
    {
        var primary = item.PrimaryVersionId is { } p && _libraryManager.GetItemById(p) is Video pv ? pv : item;
        return new[] { primary, item }
            .Concat(_libraryManager.GetLocalAlternateVersionIds(primary).Select(id => _libraryManager.GetItemById(id)).OfType<Video>());
    }

    /// <summary>The DVR recording folders, each with a trailing separator.</summary>
    public List<string> RecordingFolders() => RecordingFiles.Folders(_recordingsManager);

    /// <summary>Whether the file is being recorded right now.</summary>
    public bool IsRecordingNow(Video item) => BeingRecorded(item.Path);

    /// <summary>Being written by the DVR, and not just asked to stop.</summary>
    private bool BeingRecorded(string? path) =>
        !string.IsNullOrEmpty(path) && _recordingsManager.GetActiveRecordingInfo(path) is not null && !HistoryEvents.IsStopping(path);

    private IEnumerable<Recording> OfShow(Series series)
    {
        var key = series.PresentationUniqueKey;
        return WithPartials(Resolve())
            .Where(r => r.Item is Episode e
                && (e.SeriesId.Equals(series.Id) || (!string.IsNullOrEmpty(key) && string.Equals(e.FindSeriesPresentationUniqueKey(), key, StringComparison.Ordinal))));
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

    // When a finished recording started, as the by-date list shows it (like a DVR's list): the
    // file's last write less its length.
    private static DateTime StartedAt(Recording r)
    {
        var at = RecordedAt(r);
        return r.Item.RunTimeTicks is > 0 and var ticks ? at - TimeSpan.FromTicks(ticks) : at;
    }

    private Card ToCard(List<Recording> group, User user, IReadOnlyList<string> otherFolders)
    {
        var newest = group[0];
        int unplayed = group.Count(r => _userDataManager.GetUserData(user, r.Item)?.Played != true);
        int archived = group.Count(r => r.Archived);
        int partial = group.Count(r => r.Partial);
        int? partialMinutes = group.Count == 1 && newest.Partial ? Minutes(newest.Item) : null;
        int recording = group.Count(r => r.InProgress);
        var stalls = group.Where(r => !r.InProgress).Select(r => _repair.Warning(r.Item.Path)).Where(w => w.Short is not null).ToList();
        var stallText = stalls.Count == 0 ? null : group.Count == 1 ? stalls[0].Short : stalls.Count + " incomplete";
        var stallTitle = stalls.Count == 0 ? null : group.Count == 1 ? stalls[0].Text : stalls.Count + " of these recordings are missing part of the programme.";

        if (newest.Item is Episode episode)
        {
            var seriesId = episode.SeriesId;
            // The show's poster from whichever of its folders has one.
            var imageId = group.Select(r => (r.Item as Episode)?.SeriesId ?? Guid.Empty)
                .Where(id => !id.Equals(Guid.Empty))
                .Distinct()
                .FirstOrDefault(id => _libraryManager.GetItemById(id) is { } series && series.HasImage(ImageType.Primary));
            if (imageId.Equals(Guid.Empty))
            {
                imageId = episode.Id;
            }

            var showName = episode.SeriesName ?? episode.Name;
            if (group.Count > 1)
            {
                return new Card("Series", seriesId, imageId, showName, $"{group.Count} recordings", group.Count, unplayed, archived, newest.Row.EventUtc, partial, null, recording, stalls.Count, stallText, stallTitle);
            }

            return new Card("Episode", episode.Id, imageId, showName, EpisodeLine(episode), 1, unplayed, archived, newest.Row.EventUtc, partial, partialMinutes, recording, stalls.Count, stallText, stallTitle);
        }

        var item = newest.Item;
        // A programme without episode information: told apart by when it was recorded (server time).
        var subtitle = RecordingFiles.IsUnder(item.Path, otherFolders)
            ? RecordedAt(newest).ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
            : item is Movie ? item.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new Card(item is Movie ? "Movie" : "Video", item.Id, item.Id, item.Name, subtitle, 1, unplayed, archived, newest.Row.EventUtc, partial, partialMinutes, recording, stalls.Count, stallText, stallTitle);
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
