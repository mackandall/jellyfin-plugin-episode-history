using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.History;
using Jellyfin.Plugin.EpisodeHistory.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Web;

/// <summary>
/// Status and manual controls for the settings page, a per-programme lookup, and the
/// "mark as watched" calls client menus use for guide entries.
/// </summary>
[ApiController]
[Route("EpisodeHistory")]
public sealed class HistoryController : ControllerBase
{
    private readonly HistoryStore _store;
    private readonly GuideSweeper _sweeper;
    private readonly LibraryImporter _importer;
    private readonly ILibraryManager _libraryManager;
    private readonly BackupService _backup;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaSegmentManager _segments;
    private readonly RecordingsList _recordings;
    private readonly ILogger<HistoryController> _logger;

    public HistoryController(
        HistoryStore store,
        GuideSweeper sweeper,
        LibraryImporter importer,
        ILibraryManager libraryManager,
        BackupService backup,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IMediaSegmentManager segments,
        RecordingsList recordings,
        ILogger<HistoryController> logger)
    {
        _logger = logger;
        _recordings = recordings;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _segments = segments;
        _backup = backup;
        _libraryManager = libraryManager;
        _store = store;
        _sweeper = sweeper;
        _importer = importer;
    }

    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Status()
    {
        return Ok(new
        {
            Counts = _store.Counts(),
            LastSweep = _sweeper.LastSweep,
            LastImport = _importer.LastImport,
            _store.DatabasePath
        });
    }

    [HttpGet("Recent")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Recent([FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 500);
        return Ok(new
        {
            History = _store.Recent(limit).Select(r => new
            {
                r.Kind,
                r.UserId,
                r.ItemId,
                r.Identity,
                r.Source,
                r.EventUtc
            }),
            CancelledTimers = _store.RecentCancelled(limit)
        });
    }

    [HttpPost("Sweep")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Sweep(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _sweeper.SweepAsync("manual", cancellationToken).ConfigureAwait(false));
        }
        catch (GuideRefreshingException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>The whole history as a JSON backup document (the settings page saves it as a file).</summary>
    [HttpGet("Backup")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<BackupService.BackupFile> Backup()
    {
        return Ok(_backup.Export());
    }

    /// <summary>
    /// Restores a backup made by <see cref="Backup"/>, on this server or another one. Merges
    /// by default (adds what's missing, keeps everything already here); replace=true empties
    /// the history first. The file is validated before anything is deleted.
    /// </summary>
    [HttpPost("Restore")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(100_000_000)]
    public ActionResult<BackupService.RestoreResult> Restore([FromBody] BackupService.BackupFile file, [FromQuery] bool replace = false)
    {
        try
        {
            var result = _backup.Restore(file, replace);
            _sweeper.Request("history restored");
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("Import")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Import(CancellationToken cancellationToken)
    {
        var result = await _importer.ImportAsync(null, cancellationToken).ConfigureAwait(false);
        _sweeper.Request("library import");
        return Ok(result);
    }

    /// <summary>
    /// What the history knows about one guide entry (internal programme id), plus whether the
    /// calling user has marked it watched by hand (so a client can offer "Unmark").
    /// </summary>
    [HttpGet("Program/{programId}")]
    [Authorize]
    public IActionResult Program([FromRoute] Guid programId)
    {
        if (_sweeper.Lookup(programId) is not { } found)
        {
            return NotFound();
        }

        var userId = CurrentUserId();
        var markedWatched = userId != Guid.Empty && ManualRows(userId, found.Identity).Count > 0;
        var canForget = User.IsInRole("Administrator") && ForgettableRows(found.Identity).Count > 0;
        var copy = BestLibraryCopy(found.LibraryItems, userId);
        return Ok(new
        {
            found.Identity,
            found.Identity.ValidProgramId,
            found.State,
            MarkedWatched = markedWatched,
            CanForget = canForget,
            // "Play from library": the copy of this episode/movie the user can play, and where
            // they left off in it.
            LibraryItemId = copy?.Item.Id.ToString("N"),
            LibraryItemPositionTicks = copy?.PositionTicks ?? 0
        });
    }

    /// <summary>
    /// Of the library items matching a guide entry, the one to play: visible to the user,
    /// preferring a copy with media segments (commercial breaks marked), then the newest.
    /// </summary>
    private (BaseItem Item, long PositionTicks)? BestLibraryCopy(IReadOnlyList<Guid> itemIds, Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        if (user is null || itemIds.Count == 0)
        {
            return null;
        }

        var best = itemIds
            .Select(id => _libraryManager.GetItemById(id))
            .OfType<Video>()
            .Where(item => item.IsVisible(user))
            .OrderByDescending(item => _segments.HasSegments(item.Id))
            .ThenByDescending(item => item.DateCreated)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        return (best, _userDataManager.GetUserData(user, best)?.PlaybackPositionTicks ?? 0);
    }

    /// <summary>
    /// Marks the episode (or movie) a guide entry airs as watched by the calling user, for
    /// something seen outside this server. Stored like a played item, so every airing of that
    /// episode gets the watched mark.
    /// </summary>
    [HttpPost("Program/{programId}/Watched")]
    [Authorize]
    public Task<IActionResult> MarkWatched([FromRoute] Guid programId, CancellationToken cancellationToken) =>
        SetWatched(programId, true, cancellationToken);

    /// <summary>Removes the calling user's hand-made watched mark for that episode, from any airing.</summary>
    [HttpDelete("Program/{programId}/Watched")]
    [Authorize]
    public Task<IActionResult> UnmarkWatched([FromRoute] Guid programId, CancellationToken cancellationToken) =>
        SetWatched(programId, false, cancellationToken);

    /// <summary>
    /// "Forget this episode": deletes what the history remembers about the episode a guide
    /// entry airs, for every user — hand-made watched marks, and watched / recorded rows of
    /// recordings that have since been deleted — so it's unmarked and a series recording
    /// will record it again. Rows of items still in the library are kept (the library
    /// import would only add them back); KeptInLibrary says how many.
    /// </summary>
    [HttpDelete("Program/{programId}/History")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Forget([FromRoute] Guid programId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program)
        {
            return NotFound();
        }

        var identity = EpisodeIdentity.FromProgram(program, Marker());
        var matching = _store.LoadAll().Where(r => r.Identity.SameEpisode(identity)).ToList();
        var forgettable = ForgettableRows(matching);
        var forgotten = _store.DeleteRows(forgettable.Select(r => r.Id));

        var episodeTitle = await _sweeper.ApplyNowAsync(programId, cancellationToken).ConfigureAwait(false);
        if (forgotten > 0)
        {
            _sweeper.Request("forgot episode", TimeSpan.FromSeconds(1));
        }

        return Ok(new { Forgotten = forgotten, KeptInLibrary = matching.Count - forgettable.Count, EpisodeTitle = episodeTitle });
    }

    /// <summary>
    /// "Forget this episode" from a recording (an app's details page): the history no longer
    /// counts the episode as recorded or watched, for every user, so a series recording
    /// records it again. A recording still in the library would count it right back, so
    /// <paramref name="deleteRecording"/> deletes this one first (its file and the files beside
    /// it). Other copies still in the library are kept; KeptInLibrary says how many.
    /// </summary>
    [HttpDelete("Item/{itemId}/History")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult ForgetItem([FromRoute] Guid itemId, [FromQuery] bool deleteRecording)
    {
        if (_libraryManager.GetItemById(itemId) is not { } item || EpisodeIdentity.FromLibraryItem(item, Marker(), null) is not { } identity)
        {
            return NotFound();
        }

        if (deleteRecording)
        {
            _libraryManager.DeleteItem(item, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true }, true);
        }

        var matching = _store.LoadAll().Where(r => r.Identity.SameEpisode(identity)).ToList();
        var forgettable = ForgettableRows(matching);
        var forgotten = _store.DeleteRows(forgettable.Select(r => r.Id));
        if (forgotten > 0 || deleteRecording)
        {
            _sweeper.Request("forgot episode", TimeSpan.FromSeconds(1));
        }

        return Ok(new { Forgotten = forgotten, KeptInLibrary = matching.Count - forgettable.Count, Deleted = deleteRecording });
    }

    /// <summary>
    /// "Forget this show": what <see cref="Forget"/> does for one episode, for every episode of
    /// the show <paramref name="itemId"/> belongs to (a show, an episode or recording of it, or a
    /// guide entry of it), matched by the show's name. Rows of recordings still in the library
    /// are kept (KeptInLibrary). <paramref name="dryRun"/> only counts. Movies aren't shows.
    /// </summary>
    [HttpDelete("Item/{itemId}/ShowHistory")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult ForgetShow([FromRoute] Guid itemId, [FromQuery] bool dryRun)
    {
        var name = _libraryManager.GetItemById(itemId) switch
        {
            MediaBrowser.Controller.Entities.TV.Series s => s.Name,
            MediaBrowser.Controller.Entities.TV.Episode e => e.SeriesName,
            LiveTvProgram p when p.IsMovie != true => p.Name,
            _ => null
        };
        var key = EpisodeIdentity.Normalize(name);
        if (key.Length == 0)
        {
            return NotFound();
        }

        var matching = _store.LoadAll()
            .Where(r => !r.Identity.IsMovie
                && (EpisodeIdentity.Normalize(r.Identity.SeriesName) == key || EpisodeIdentity.Normalize(r.Identity.SeriesAlt) == key))
            .ToList();
        var forgettable = ForgettableRows(matching);
        var episodes = forgettable.Select(r => (r.Identity.Season, r.Identity.Episode, EpisodeIdentity.Normalize(r.Identity.EpisodeTitle))).Distinct().Count();
        var forgotten = 0;
        if (!dryRun)
        {
            forgotten = _store.DeleteRows(forgettable.Select(r => r.Id));
            if (forgotten > 0)
            {
                _logger.LogInformation("Forgot {Rows} history rows of {Show}", forgotten, name);
                _sweeper.Request("forgot show", TimeSpan.FromSeconds(1));
            }
        }

        return Ok(new { Show = name, Rows = forgettable.Count, Episodes = episodes, Forgotten = forgotten, KeptInLibrary = matching.Count - forgettable.Count });
    }

    /// <summary>
    /// Deletes a recording (file and the files beside it). A partial recording takes the
    /// episode's other partial versions with it (the pieces of an interrupted recording,
    /// which Jellyfin groups as versions), so the episode leaves the library and a series
    /// recording records it again; a full version is kept. A full recording is deleted alone,
    /// as Jellyfin does. <paramref name="dryRun"/> only says what would be deleted.
    /// </summary>
    [HttpDelete("Item/{itemId}/Recording")]
    [Authorize]
    public IActionResult DeleteRecording([FromRoute] Guid itemId, [FromQuery] bool dryRun)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item)
        {
            return NotFound();
        }

        var user = _userManager.GetUserById(CurrentUserId());
        if (user is null || !item.CanDelete(user))
        {
            return Forbid();
        }

        var targets = new List<Video> { item };
        var inHistory = _recordings.HistoryItemIds();
        var partial = _recordings.IsPartial(item, inHistory);
        if (partial)
        {
            targets.AddRange(Versions(item).Where(v => !v.Id.Equals(item.Id) && _recordings.IsPartial(v, inHistory)));
        }

        if (!dryRun)
        {
            // Alternates first: deleting a primary would promote one of them to take its place.
            foreach (var v in targets.OrderBy(v => v.PrimaryVersionId.HasValue ? 0 : 1))
            {
                _libraryManager.DeleteItem(v, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true }, true);
            }

            _sweeper.Request("deleted recording", TimeSpan.FromSeconds(1));
        }

        return Ok(new { Partial = partial, Deleted = dryRun ? 0 : targets.Count, Files = targets.Select(v => System.IO.Path.GetFileName(v.Path)).ToList() });
    }

    /// <summary>
    /// Deletes a show's recordings in one go: every episode file of it in the DVR recordings
    /// folders (archived copies elsewhere are kept), partial ones and extra versions included.
    /// Each is checked as a single delete is (the caller may delete it); one being recorded now,
    /// and a live TV buffer, are left alone. The history keeps them as recorded, as a single
    /// delete does. <paramref name="dryRun"/> only counts what would go.
    /// With <paramref name="archive"/> it is the other way round: the show's episode files
    /// outside the recordings folders (the archive copies) go, and its recordings are kept.
    /// </summary>
    [HttpDelete("Recordings/Series/{seriesId}")]
    [Authorize]
    public IActionResult DeleteSeriesRecordings([FromRoute] Guid seriesId, [FromQuery] bool dryRun, [FromQuery] bool archive)
    {
        var user = _userManager.GetUserById(CurrentUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        var files = _recordings.ShowFiles(seriesId, archive);
        if (files is null)
        {
            return NotFound();
        }

        var targets = new List<Video>();
        var skipped = 0;
        foreach (var v in files)
        {
            if (!v.CanDelete(user) || _recordings.IsRecordingNow(v))
            {
                skipped++;
                continue;
            }

            targets.Add(v);
        }

        long bytes = targets.Sum(v => v.Size ?? 0);
        var deleted = 0;
        if (!dryRun)
        {
            // Alternates first: deleting a primary would promote one of them to take its place.
            foreach (var v in targets.OrderBy(v => v.PrimaryVersionId.HasValue ? 0 : 1))
            {
                try
                {
                    var path = v.Path;
                    _libraryManager.DeleteItem(v, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true }, true);
                    deleted++;
                    RecordingTidy.DeleteSideFiles(path, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Couldn't delete recording {Path}", v.Path);
                    skipped++;
                }
            }

            // The shows these were in, before their folders go: an emptied one leaves the library
            // with its folder (Jellyfin only notices by itself where it can watch the folder).
            var shows = targets.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Select(e => e.Series).OfType<MediaBrowser.Controller.Entities.TV.Series>().DistinctBy(x => x.Id).ToList();
            foreach (var dir in targets.Select(v => RecordingTidy.ShowFolder(v.Path)).OfType<string>().Distinct())
            {
                if (RemoveIfNoVideos(dir, archive))
                {
                    foreach (var show in shows.Where(x => string.Equals(System.IO.Path.TrimEndingDirectorySeparator(x.Path ?? string.Empty), System.IO.Path.TrimEndingDirectorySeparator(dir), StringComparison.Ordinal)))
                    {
                        try
                        {
                            _libraryManager.DeleteItem(show, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = false }, true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Couldn't remove the emptied show {Name} from the library", show.Name);
                        }
                    }
                }
            }

            _logger.LogInformation("Deleted {Deleted} {What} of series {SeriesId} ({Skipped} left)", deleted, archive ? "archive copies" : "recordings", seriesId, skipped);
            _sweeper.Request("deleted recordings", TimeSpan.FromSeconds(1));
        }

        return Ok(new { Count = targets.Count, Bytes = bytes, Skipped = skipped, Deleted = deleted, Places = Places(targets) });
    }

    /// <summary>Where files are, for a confirmation: the folder under /mnt (or the like) each is in.</summary>
    private static List<string> Places(IEnumerable<Video> files) =>
        files.Select(v => (v.Path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Length > 2 && p[0] is "mnt" or "media" or "srv" ? p[1] : p.Length > 1 ? p[0] : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Removes an emptied show folder: under the recordings folders, or, for an <paramref name="archive"/> show, under a library's folders.</summary>
    private bool RemoveIfNoVideos(string dir, bool archive)
    {
        var roots = archive
            ? _libraryManager.GetVirtualFolders()
                .SelectMany(f => f.Locations ?? Array.Empty<string>())
                .Select(p => System.IO.Path.TrimEndingDirectorySeparator(p) + System.IO.Path.DirectorySeparatorChar)
                .ToList()
            : _recordings.RecordingFolders();
        return RecordingTidy.RemoveIfNoVideos(dir, roots, _logger);
    }

    /// <summary>An episode's versions: its primary and the primary's alternates (this one included).</summary>
    private IEnumerable<Video> Versions(Video item)
    {
        var primary = item.PrimaryVersionId is { } p && _libraryManager.GetItemById(p) is Video pv ? pv : item;
        return new[] { primary }
            .Concat(_libraryManager.GetLocalAlternateVersionIds(primary).Select(id => _libraryManager.GetItemById(id)).OfType<Video>())
            .Concat(_libraryManager.GetLinkedAlternateVersions(primary))
            .DistinctBy(v => v.Id);
    }

    /// <summary>
    /// Replies with the clicked entry's new episode title (already saved), so the client
    /// can show the mark at once; the episode's other airings follow with the sweep ~1 s later.
    /// </summary>
    private async Task<IActionResult> SetWatched(Guid programId, bool watched, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId == Guid.Empty)
        {
            return Unauthorized();
        }

        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program)
        {
            return NotFound();
        }

        var identity = EpisodeIdentity.FromProgram(program, Marker());
        var existing = ManualRows(userId, identity);
        bool changed;
        if (watched)
        {
            if (identity.ValidProgramId is null && !identity.NameKeys().Any())
            {
                return BadRequest("This guide entry has no program id, episode numbers or title to remember it by.");
            }

            changed = existing.Count == 0
                && _store.Upsert(HistoryStore.KindWatched, userId, programId, identity, ManualSource, DateTime.UtcNow);
        }
        else
        {
            changed = _store.DeleteRows(existing.Select(r => r.Id)) > 0;
        }

        var episodeTitle = await _sweeper.ApplyNowAsync(programId, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            _sweeper.Request(watched ? "marked watched" : "unmarked watched", TimeSpan.FromSeconds(1));
        }

        var canForget = User.IsInRole("Administrator") && ForgettableRows(identity).Count > 0;
        return Ok(new { MarkedWatched = watched, EpisodeTitle = episodeTitle, CanForget = canForget });
    }

    /// <summary>
    /// Guide entries whose marks changed since <paramref name="since"/> (the Version from a
    /// previous call; omit it to get a baseline). Clients showing the guide poll this to
    /// pick up marks made elsewhere without reloading. Reset = reload everything.
    /// </summary>
    [HttpGet("GuideChanges")]
    [Authorize]
    public ActionResult<GuideSweeper.GuideChanges> GuideChanges([FromQuery] long? since)
    {
        return Ok(_sweeper.GetChangesSince(since));
    }

    /// <summary>
    /// The mark symbols currently put in front of guide titles, so the web guide script can
    /// tag marked cells with CSS classes (eh-watched, eh-inlibrary, eh-recordedbefore).
    /// </summary>
    /// <summary>
    /// The recordings views (home row, Live TV > Recordings grid) for the calling user: what
    /// the DVR recorded, newest first, with the views' settings. <paramref name="filter"/> is "all", "current" (still in
    /// the recordings folders) or "archived"; omitted, the configured default applies.
    /// </summary>
    [HttpGet("Recordings")]
    [Authorize]
    public IActionResult Recordings([FromQuery] string? filter, [FromQuery] int? limit)
    {
        var user = _userManager.GetUserById(CurrentUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        filter = NormalizeFilter(filter) ?? NormalizeFilter(cfg.RecordingsRowDefaultFilter) ?? RecordingsList.FilterAll;
        var settings = new
        {
            Enabled = cfg.RecordingsRowEnabled,
            Title = cfg.RecordingsRowTitle,
            Position = cfg.RecordingsRowPosition,
            DefaultFilter = NormalizeFilter(cfg.RecordingsRowDefaultFilter) ?? RecordingsList.FilterAll,
            TabMode = cfg.RecordingsTabMode,
            TabLimit = cfg.RecordingsTabLimit
        };

        var cards = _recordings.Cards(user, filter, cfg.RecordingsRowGroupByShow, cfg.RecordingsRowIncludeMovies, limit ?? cfg.RecordingsRowLimit);
        return Ok(new { Settings = settings, Filter = filter, Cards = cards });
    }

    /// <summary>
    /// A show's recordings for the calling user, newest first, one per recording: the
    /// "Recordings" row on the show's page. Empty when the DVR recorded none of it.
    /// </summary>
    [HttpGet("Recordings/Series/{seriesId}")]
    [Authorize]
    public IActionResult SeriesRecordings([FromRoute] Guid seriesId)
    {
        var user = _userManager.GetUserById(CurrentUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!cfg.ShowPageRecordingsEnabled)
        {
            return Ok(new { Enabled = false, Recordings = Array.Empty<RecordingsList.EpisodeCard>() });
        }

        var cards = _recordings.ShowCards(user, seriesId);
        return cards is null ? NotFound() : Ok(new { Enabled = true, Recordings = cards });
    }

    private static string? NormalizeFilter(string? filter) => filter?.Trim().ToLowerInvariant() switch
    {
        RecordingsList.FilterAll => RecordingsList.FilterAll,
        RecordingsList.FilterCurrent => RecordingsList.FilterCurrent,
        RecordingsList.FilterArchived => RecordingsList.FilterArchived,
        _ => null
    };

    [HttpGet("Symbols")]
    [Authorize]
    public IActionResult Symbols()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(new
        {
            Watched = cfg.WatchedSymbol,
            InLibrary = cfg.InLibrarySymbol,
            RecordedBefore = cfg.RecordedBeforeSymbol,
            Skipped = cfg.SkippedSymbol,
            OtherAiring = cfg.OtherAiringSymbol,
            cfg.EnableGuideMarks
        });
    }

    private const string ManualSource = "manual";

    private List<HistoryStore.Row> ManualRows(Guid userId, EpisodeIdentity identity) =>
        _store.LoadWatched(userId, ManualSource).Where(r => r.Identity.SameEpisode(identity)).ToList();

    private List<HistoryStore.Row> ForgettableRows(EpisodeIdentity identity) =>
        ForgettableRows(_store.LoadAll().Where(r => r.Identity.SameEpisode(identity)));

    // Hand-made marks (their item id is a guide entry) always go; other rows only once
    // their library item is gone.
    private List<HistoryStore.Row> ForgettableRows(IEnumerable<HistoryStore.Row> rows) =>
        rows.Where(r => r.Source == ManualSource || _libraryManager.GetItemById(r.ItemId) is null or LiveTvProgram).ToList();

    // The id Jellyfin's auth handler puts on every authenticated request (its own
    // ClaimsPrincipal.GetUserId reads the same claim; that helper lives in Jellyfin.Api,
    // which plugins shouldn't reference).
    private Guid CurrentUserId() =>
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    private static GuideMarker Marker() => new(Plugin.Instance?.Configuration ?? new PluginConfiguration());
}
