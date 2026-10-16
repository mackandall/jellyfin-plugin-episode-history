using System;
using System.Globalization;
using System.IO;
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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.LiveTv;
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
    private readonly ILiveTvManager _liveTvManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly IAuthorizationService _authorization;
    private readonly StallNotes _stalls;
    private readonly RecordingRepair _repair;
    private readonly ScheduleLedger _ledger;
    private readonly Programmes _programmes;
    private readonly RecordingChannels _channels;
    private readonly Airings _airings;
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
        ILiveTvManager liveTvManager,
        IRecordingsManager recordingsManager,
        IAuthorizationService authorization,
        StallNotes stalls,
        RecordingRepair repair,
        ScheduleLedger ledger,
        Programmes programmes,
        RecordingChannels channels,
        Airings airings,
        ILogger<HistoryController> logger)
    {
        _airings = airings;
        _channels = channels;
        _ledger = ledger;
        _programmes = programmes;
        _stalls = stalls;
        _repair = repair;
        _logger = logger;
        _liveTvManager = liveTvManager;
        _recordingsManager = recordingsManager;
        _authorization = authorization;
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

    /// <summary>
    /// What the recordings lists warn about for one recording, and what the caller may do about
    /// it: <c>Partial</c> (doesn't count as recorded), <c>Stalled</c> with the story in
    /// <c>Text</c> (the tuner went quiet while it was being made), <c>CanKeep</c> (count it as
    /// recorded / clear the warning) and <c>CanRecordAgain</c> (schedule the next airing).
    /// </summary>
    [HttpGet("Item/{itemId}/RecordingState")]
    [Authorize]
    public async Task<IActionResult> RecordingState([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item || string.IsNullOrEmpty(item.Path) || _recordings.IsRecordingNow(item))
        {
            return Ok(new { Partial = false, Stalled = false });
        }

        var warning = _repair.Warning(item.Path);
        var partial = _recordings.IsPartial(item, _recordings.HistoryItemIds());
        var pieces = Pieces(item).Count;
        if (warning.Short is null && !partial && pieces < 2)
        {
            return Ok(new { Partial = false, Stalled = false });
        }

        return Ok(new
        {
            Partial = partial,
            Stalled = warning.Short is not null,
            warning.Text,
            Pieces = pieces,
            CanKeep = (await _authorization.AuthorizeAsync(User, Policies.RequiresElevation).ConfigureAwait(false)).Succeeded,
            CanRecordAgain = (await _authorization.AuthorizeAsync(User, Policies.LiveTvManagement).ConfigureAwait(false)).Succeeded
        });
    }

    /// <summary>
    /// "Keep it as it is": a partial recording counts as recorded from now on (guide marks,
    /// series recordings skip it), and a stalled one loses its warning.
    /// </summary>
    /// <summary>
    /// Recordings that produced nothing at all (the last 30 days, newest first), with where each
    /// stands with being recorded again. See <see cref="ScheduleLedger"/>.
    /// </summary>
    [HttpGet("Failed")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Failed()
    {
        var programmes = _programmes.All();
        return Ok(_ledger.Failures().Select(e =>
        {
            // Its own airing on the waiting list, else the incomplete one it was a try at replacing.
            var p = programmes.FirstOrDefault(q => q.Key == e.Key)
                ?? programmes.Where(q => q.Identity is { } x && e.Identity is { } y && x.SameEpisode(y) && q.CreatedUtc <= (e.DecidedUtc ?? DateTime.UtcNow))
                    .OrderByDescending(q => q.CreatedUtc).FirstOrDefault();
            var next = p?.State switch
            {
                Programmes.Waiting => "Waiting for another airing",
                Programmes.Scheduled when p.NextStartUtc is { } start => "Records again " + start.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture)
                    + (p.NextChannel is null ? string.Empty : " (" + p.NextChannel + ")"),
                Programmes.Replaced or Programmes.Complete => "Recorded since",
                Programmes.GaveUp => "No other airing was recorded",
                Programmes.Kept => "Not recorded again",
                _ => "Not recorded again"
            };
            return new
            {
                e.TimerId,
                e.Key,
                e.Title,
                Channel = e.ChannelName is null ? null : (e.ChannelNumber is null ? string.Empty : e.ChannelNumber + " ") + e.ChannelName,
                e.StartUtc,
                e.Reason,
                Next = next,
                CanKeep = p is not null && p.Key == e.Key && p.State is Programmes.Waiting or Programmes.Scheduled
            };
        }));
    }

    /// <summary>
    /// For a client's Home screen: what needs attention (recordings that didn't record, ones that
    /// came out incomplete and are waiting to be recorded again, stalled ones), newest first, and the
    /// recordings drive's space. Open to any signed-in user; nothing here changes anything.
    /// </summary>
    [HttpGet("Home")]
    [Authorize]
    public IActionResult Home()
    {
        var now = DateTime.UtcNow;
        var programmes = _programmes.All();
        static string When(DateTime utc) => utc.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.InvariantCulture);
        static string Next(Programmes.Programme? p) => p?.State switch
        {
            Programmes.Waiting => "waiting for another airing",
            Programmes.Scheduled when p.NextStartUtc is { } s => "records again " + When(s),
            Programmes.GaveUp => "no other airing was recorded",
            _ => "not recorded again"
        };
        Guid? ItemOf(Programmes.Programme p) =>
            p.Files().FirstOrDefault() is { } f && RecordingFiles.ItemAt(_libraryManager, f) is { } item ? item.Id : null;

        var items = new List<(DateTime At, object Item)>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        // Recordings that produced nothing, until another airing is in or someone keeps it as it is.
        foreach (var e in _ledger.Failures().Where(e => now - e.StartUtc < TimeSpan.FromDays(7)))
        {
            var p = programmes.FirstOrDefault(q => q.Key == e.Key)
                ?? programmes.Where(q => q.Identity is { } x && e.Identity is { } y && x.SameEpisode(y)).OrderByDescending(q => q.CreatedUtc).FirstOrDefault();
            if (p?.State is Programmes.Replaced or Programmes.Complete or Programmes.Kept)
            {
                continue;
            }

            items.Add((e.StartUtc, new { Kind = "failed", e.Title, Detail = "Didn't record · " + Next(p), ItemId = (Guid?)null }));
        }

        // Incomplete ones on the list to be recorded again.
        foreach (var p in programmes.Where(p => p.Pieces.Count > 0 && p.State is Programmes.Waiting or Programmes.Scheduled && now - p.CreatedUtc < TimeSpan.FromDays(30)))
        {
            foreach (var f in p.Pieces.Select(x => Path.GetFileNameWithoutExtension(x.Path)).Append(p.JoinedPath is { } j ? Path.GetFileNameWithoutExtension(j) : null).OfType<string>())
            {
                listed.Add(f);
            }

            items.Add((p.ProgramStartUtc, new
            {
                Kind = "incomplete",
                p.Title,
                Detail = string.Format(CultureInfo.InvariantCulture, "{0:F0} of {1:F0} min · {2}", p.CapturedSeconds / 60, p.ScheduledSeconds / 60, Next(p)),
                ItemId = ItemOf(p)
            }));
        }

        // Stalled lately and not kept as it is (keeping one clears its note).
        foreach (var (file, note) in _stalls.Recent(now - TimeSpan.FromDays(7)).Where(kv => !listed.Contains(kv.Key)))
        {
            var p = _programmes.ForFile(file);
            items.Add((note.FirstStallUtc, new { Kind = "stalled", Title = p?.Title ?? file, Detail = note.Short, ItemId = p is null ? null : ItemOf(p) }));
        }

        object? drive = null;
        try
        {
            if (RecordingFiles.Folders(_recordingsManager).FirstOrDefault(Directory.Exists) is { } folder)
            {
                var d = new DriveInfo(folder);
                drive = new { FreeBytes = d.AvailableFreeSpace, TotalBytes = d.TotalSize };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "Couldn't read the recordings drive's space");
        }

        return Ok(new { Attention = items.OrderByDescending(x => x.At).Take(20).Select(x => x.Item), Drive = drive });
    }

    /// <summary>"Don't record it again" for an airing on the failed list.</summary>
    [HttpPost("Failed/Keep")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> KeepFailed([FromQuery] string key, CancellationToken cancellationToken)
    {
        await _repair.KeepAiringAsync(key, cancellationToken).ConfigureAwait(false);
        return Ok(new { Kept = true });
    }

    [HttpPost("Item/{itemId}/KeepRecording")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> KeepRecording([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        var counted = false;
        if (_recordings.IsPartial(item, _recordings.HistoryItemIds()))
        {
            var identity = EpisodeIdentity.FromLibraryItem(item, Marker(), null)
                ?? new EpisodeIdentity(null, item.Name, null, null, null, null, false, null);
            counted = _store.Upsert(HistoryStore.KindRecorded, null, item.Id, identity, "kept", DateTime.UtcNow);
        }

        var cleared = _stalls.Remove(item.Path);
        await _repair.KeepAsync(item.Path, cancellationToken).ConfigureAwait(false);
        if (counted)
        {
            _sweeper.Request("recording kept", TimeSpan.FromSeconds(1));
        }

        _logger.LogInformation("Kept {Name} as it is (counted as recorded: {Counted}, stall warning cleared: {Cleared})", item.Name, counted, cleared);
        return Ok(new { Counted = counted, Cleared = cleared });
    }

    /// <summary>
    /// "Record again": schedules the next airing of a recording's programme found in the guide
    /// (the recording itself is kept; delete it when the new one is in). <c>Scheduled</c> false
    /// with a <c>Message</c> when the guide has no later airing; <c>AlreadyScheduled</c> when
    /// that airing has a recording set already. <paramref name="dryRun"/> only looks.
    /// </summary>
    [HttpPost("Item/{itemId}/RecordAgain")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> RecordAgain([FromRoute] Guid itemId, [FromQuery] bool dryRun, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item
            || EpisodeIdentity.FromLibraryItem(item, Marker(), _store.GetProgramIdForItem(item.Id)) is not { } identity)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var marker = Marker();
        var names = new[] { (item as Episode)?.SeriesName, item.Name, identity.SeriesName, identity.SeriesAlt }
            .Select(EpisodeIdentity.Normalize).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        var next = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.LiveTvProgram },
                MinStartDate = now
            })
            .OfType<LiveTvProgram>()
            .Where(p => names.Contains(EpisodeIdentity.Normalize(marker.Strip(p.Name) ?? p.Name)))
            .Where(p => EpisodeIdentity.FromProgram(p, marker).SameEpisode(identity))
            .OrderBy(p => p.StartDate)
            .FirstOrDefault();
        if (next is null)
        {
            return Ok(new { Scheduled = false, Message = "The guide has no later airing of it yet." });
        }

        var channel = _libraryManager.GetItemById(next.ChannelId)?.Name;
        var programId = next.Id.ToString("N");
        var timers = (await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false)).Items;
        var already = timers.Any(t => string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase)
            && t.Status is not (RecordingStatus.Cancelled or RecordingStatus.Completed or RecordingStatus.Error));
        if (!already && !dryRun)
        {
            var defaults = await _liveTvManager.GetNewTimerDefaults(programId, cancellationToken).ConfigureAwait(false);
            await _liveTvManager.CreateTimer(
                new TimerInfoDto
                {
                    ProgramId = programId,
                    ChannelId = defaults.ChannelId,
                    ServiceName = defaults.ServiceName,
                    Name = defaults.Name,
                    StartDate = defaults.StartDate,
                    EndDate = defaults.EndDate,
                    PrePaddingSeconds = defaults.PrePaddingSeconds,
                    PostPaddingSeconds = defaults.PostPaddingSeconds,
                    IsPrePaddingRequired = defaults.IsPrePaddingRequired,
                    IsPostPaddingRequired = defaults.IsPostPaddingRequired,
                    KeepUntil = defaults.KeepUntil
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Recording {Name} again at {Start:u} on {Channel}", item.Name, next.StartDate, channel);
        }

        if (!dryRun)
        {
            _repair.NoteScheduled(item.Path, programId, next.StartDate, channel, !already);
        }

        return Ok(new { Scheduled = !already && !dryRun, AlreadyScheduled = already, Start = next.StartDate, Channel = channel, item.Name });
    }

    /// <summary>
    /// "Join the pieces": the versions of an interrupted recording still in the recordings
    /// folders ("Name", "Name - 1"…) become one file, in the background (it takes about a minute
    /// per hour of recording). What the automatic join does when it saw the recording being made;
    /// this is for pieces from before, or after it was turned off.
    /// </summary>
    [HttpPost("Item/{itemId}/JoinPieces")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult JoinPieces([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item)
        {
            return NotFound();
        }

        var pieces = Pieces(item);
        if (pieces.Count < 2)
        {
            return Conflict(new { Message = "This recording is in one piece." });
        }

        var keep = (Plugin.Instance?.Configuration.JoinKeepPiecesDays ?? 3) > 0;
        _ = Task.Run(async () =>
        {
            var result = await _repair.JoinFilesAsync(pieces, keep, CancellationToken.None).ConfigureAwait(false);
            if (result.Ok)
            {
                _sweeper.Request("pieces joined", TimeSpan.FromSeconds(30));
            }
            else
            {
                _logger.LogWarning("The pieces of {Name} weren't joined: {Message}", item.Name, result.Message);
            }
        });
        return Accepted(new { Pieces = pieces.Count, Files = pieces.Select(System.IO.Path.GetFileName).ToList() });
    }

    /// <summary>The files an interrupted recording is in: the item's versions in the recordings folders that aren't being recorded, in playing order.</summary>
    private List<string> Pieces(Video item)
    {
        var folders = _recordings.RecordingFolders();
        var versions = Versions(item).ToList();
        if (versions.Any(_recordings.IsRecordingNow))
        {
            return new List<string>();
        }

        return RecordingRepair.OrderPieces(versions
            .Select(v => v.Path)
            .Where(p => !string.IsNullOrEmpty(p) && RecordingFiles.IsUnder(p, folders) && System.IO.File.Exists(p) && !LiveBuffers.IsUnkeptBuffer(p)));
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
            TabLimit = cfg.RecordingsTabLimit,
            TabView = cfg.RecordingsTabView,
            DateLogo = cfg.RecordingsDateLogo
        };

        var cards = _recordings.Cards(user, filter, cfg.RecordingsRowGroupByShow, cfg.RecordingsRowIncludeMovies, limit ?? cfg.RecordingsRowLimit);
        return Ok(new { Settings = settings, Filter = filter, Cards = cards });
    }

    /// <summary>
    /// Every recording for the calling user, newest first, one entry each with the channel it
    /// came from: the list by date (Live TV > Recordings, and JDVR). <paramref name="filter"/> as
    /// for <see cref="Recordings"/>; <c>DateLogo</c> says where clients put the channel logo.
    /// </summary>
    [HttpGet("Recordings/ByDate")]
    [Authorize]
    public IActionResult RecordingsByDate([FromQuery] string? filter, [FromQuery] int? limit)
    {
        var user = _userManager.GetUserById(CurrentUserId());
        if (user is null)
        {
            return Unauthorized();
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        filter = NormalizeFilter(filter) ?? NormalizeFilter(cfg.RecordingsRowDefaultFilter) ?? RecordingsList.FilterAll;
        var entries = _recordings.ByDate(user, filter, true, limit ?? 500);
        return Ok(new { DateLogo = cfg.RecordingsDateLogo, Filter = filter, Recordings = entries });
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

    /// <summary>
    /// The recordings being made right now behind an item: the item itself (or one of its
    /// versions) for an episode or a movie, any of its episodes for a show. Each can be stopped
    /// with <see cref="StopRecording"/>; <c>CanStop</c> says whether the caller may (Live TV
    /// management, as for cancelling a timer).
    /// </summary>
    [HttpGet("Item/{itemId}/ActiveRecordings")]
    [Authorize]
    public async Task<IActionResult> ActiveRecordings([FromRoute] Guid itemId)
    {
        IEnumerable<Video> files = _libraryManager.GetItemById(itemId) switch
        {
            Series series => _recordings.ShowFiles(series.Id) ?? new List<Video>(),
            Video video => Versions(video),
            _ => Array.Empty<Video>()
        };

        var active = files
            .Where(_recordings.IsRecordingNow)
            .DistinctBy(v => v.Id)
            .Select(v => new
            {
                v.Id,
                Name = v is Episode e && !string.IsNullOrEmpty(e.SeriesName) ? e.SeriesName + " \u2013 " + e.Name : v.Name,
                // The Live TV channel it is recorded from, for "Watch live".
                ChannelId = _channels.ByExternalId(_recordingsManager.GetActiveRecordingInfo(v.Path)?.Timer?.ChannelId)?.Id
            })
            .ToList();
        var canStop = active.Count > 0
            && (await _authorization.AuthorizeAsync(User, Policies.LiveTvManagement).ConfigureAwait(false)).Succeeded;
        return Ok(new { CanStop = canStop, Recordings = active });
    }

    /// <summary>
    /// Stops the recording that is writing this item's file, by cancelling its timer — what
    /// "Stop this recording" in the schedule does, reached from the recording instead. What was
    /// recorded so far is kept. 409 when the item isn't being recorded (any more).
    /// </summary>
    [HttpPost("Item/{itemId}/StopRecording")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> StopRecording([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        if (_recordingsManager.GetActiveRecordingInfo(item.Path)?.Timer is not { } timer)
        {
            return Conflict(new { Message = "It isn't being recorded." });
        }

        // CancelTimer wants the timer's API id; the recording knows it by the DVR's own id.
        var timers = (await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false)).Items;
        var dto = timers.FirstOrDefault(t => string.Equals(t.ExternalId, timer.Id, StringComparison.Ordinal));
        if (dto is null)
        {
            return Conflict(new { Message = "The recording's timer wasn't found." });
        }

        await _liveTvManager.CancelTimer(dto.Id).ConfigureAwait(false);
        // From here on it isn't "being recorded" for the lists, and its decision is made right
        // away, so it goes from Recording to Partial (or recorded) without dropping out.
        HistoryEvents.MarkStopping(item.Path);
        HistoryEvents.Current?.PollSoon();
        _logger.LogInformation("Stopped the recording of {Name} ({Path}) on request", item.Name, item.Path);
        return Ok(new { Stopped = true, item.Name });
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

    // ---- Other airings and Re-record -----------------------------------------------------------

    /// <summary>
    /// "Other airings": the airings of a movie or episode still to come in the guide (or on now),
    /// soonest first, each with its channel, whether the channel is commercial-free, a recording
    /// already set for it, and the recordings already set then (<c>Busy</c> of <c>Tuners</c>;
    /// <c>Clash</c> when every tuner is taken). Asked by a library item, a guide entry, or a name
    /// (with <paramref name="year"/> and <paramref name="movie"/>, for titles not in the library).
    /// </summary>
    [HttpGet("Airings")]
    [Authorize]
    public async Task<IActionResult> GetAirings(
        [FromQuery] Guid? itemId,
        [FromQuery] Guid? programId,
        [FromQuery] string? name,
        [FromQuery] int? year,
        [FromQuery] bool movie,
        CancellationToken cancellationToken)
    {
        EpisodeIdentity? identity = null;
        string? title = null;
        if (itemId is { } iid && _libraryManager.GetItemById(iid) is { } item)
        {
            identity = _repair.For(item.Path)?.Identity ?? EpisodeIdentity.FromLibraryItem(item, Marker(), _store.GetProgramIdForItem(item.Id));
            title = item is Episode ep ? ep.SeriesName + " · " + ep.Name : item.Name;
        }
        else if (programId is { } pid && _libraryManager.GetItemById(pid) is LiveTvProgram program)
        {
            identity = EpisodeIdentity.FromProgram(program, Marker());
            title = program.Name + (string.IsNullOrEmpty(program.EpisodeTitle) ? string.Empty : " · " + Marker().Strip(program.EpisodeTitle));
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            identity = new EpisodeIdentity(null, name.Trim(), null, null, null, null, movie, year);
            title = name.Trim();
        }

        if (identity is null)
        {
            return NotFound();
        }

        var (airings, reach) = await _airings.FindWithReachAsync(identity, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            Title = title,
            identity.IsMovie,
            // How far the guide reaches, and the series' first and last episodes in it.
            GuideUntil = reach.UntilUtc,
            GuideShowings = reach.Showings,
            GuideFirst = reach.First is { } f ? new { f.Season, Episode = f.Number, f.Title, Start = f.StartUtc } : null,
            GuideLast = reach.Last is { } l ? new { l.Season, Episode = l.Number, l.Title, Start = l.StartUtc } : null,
            Tuners = airings.Select(a => a.Tuners).DefaultIfEmpty(0).Max(),
            Airings = airings.Select(a => new
            {
                a.ProgramId,
                a.Name,
                a.EpisodeTitle,
                a.Season,
                a.Episode,
                a.Year,
                Start = a.StartUtc,
                End = a.EndUtc,
                a.ChannelId,
                a.ChannelName,
                a.ChannelNumber,
                a.HasLogo,
                a.CommercialFree,
                a.Recording,
                a.Busy,
                a.Tuners,
                a.Clash,
                a.Clashes,
                IsThis = programId is { } p && p == a.ProgramId,
                OnNow = a.StartUtc <= DateTime.UtcNow
            })
        });
    }

    // A recording Jellyfin made (its DVR folders, or followed since: moved to an archive by name,
    // its history row, its channel note); not a file another DVR or a disc backup left in a library.
    private bool IsJellyfinRecording(Video item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        return RecordingFiles.IsUnder(item.Path, _recordings.RecordingFolders())
            || _repair.For(item.Path) is not null
            || _channels.For(item.Path) is not null
            || _store.LoadRecorded().Any(r => r.ItemId == item.Id && r.Source is "recording" or "added");
    }

    /// <summary>
    /// Whether "Re-record…" is offered for an item (<c>Eligible</c>, or <c>Why</c> not), and where
    /// it stands: <c>State</c> waiting / scheduled / replaced / gaveup / kept, with the chip text.
    /// </summary>
    [HttpGet("Item/{itemId}/Rerecord")]
    [Authorize]
    public async Task<IActionResult> RerecordState([FromRoute] Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item || item is not (Movie or Episode))
        {
            return Ok(new { Eligible = false, Why = "Only movies and episodes." });
        }

        var p = _repair.For(item.Path);
        var jellyfin = IsJellyfinRecording(item);
        var identity = p?.Identity ?? EpisodeIdentity.FromLibraryItem(item, Marker(), _store.GetProgramIdForItem(item.Id));
        var why = !jellyfin ? "Not recorded by Jellyfin: look for it with Other airings."
            : !RecordingRepair.Reliable(identity) ? "The guide can't tell this from its other episodes."
            : _recordings.IsRecordingNow(item) ? "It is still recording."
            : null;
        var warning = _repair.Warning(item.Path);
        return Ok(new
        {
            Eligible = why is null,
            Why = why,
            CanManage = (await _authorization.AuthorizeAsync(User, Policies.LiveTvManagement).ConfigureAwait(false)).Succeeded,
            State = p?.State is Programmes.Waiting or Programmes.Scheduled or Programmes.GaveUp ? p.State : p?.Manual == true ? p.State : null,
            Asked = p?.Manual == true,
            p?.Reason,
            NextStart = p?.State == Programmes.Scheduled ? p.NextStartUtc : null,
            NextChannel = p?.State == Programmes.Scheduled ? p.NextChannel : null,
            warning.Short,
            warning.Text
        });
    }

    /// <summary>
    /// "Re-record…": records a flawed Jellyfin recording again, at <paramref name="programId"/>
    /// (an airing from Other airings) or the best airing found (a free tuner, commercial-free,
    /// soonest; searched again every 20 minutes when the guide has none yet). The new recording
    /// replaces this one when it is in full: renamed, then deleted after the keep days, by setting.
    /// </summary>
    [HttpPost("Item/{itemId}/Rerecord")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> Rerecord([FromRoute] Guid itemId, [FromQuery] Guid? programId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item || item is not (Movie or Episode) || !IsJellyfinRecording(item))
        {
            return BadRequest(new { Message = "Only recordings Jellyfin made can be re-recorded; look for others with Other airings." });
        }

        var identity = _repair.For(item.Path)?.Identity ?? EpisodeIdentity.FromLibraryItem(item, Marker(), _store.GetProgramIdForItem(item.Id));
        if (identity is null || !RecordingRepair.Reliable(identity))
        {
            return BadRequest(new { Message = "The guide can't tell this from its other episodes." });
        }

        try
        {
            var p = await _repair.RequestAsync(item, identity, programId, reason, cancellationToken).ConfigureAwait(false);
            var scheduled = p?.State == Programmes.Scheduled;
            return Ok(new
            {
                State = p?.State,
                NextStart = scheduled ? p!.NextStartUtc : null,
                NextChannel = scheduled ? p!.NextChannel : null,
                Message = scheduled
                    ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "Re-records {0:ddd MMM d, h:mm tt}{1}.", p!.NextStartUtc!.Value.ToLocalTime(), p.NextChannel is null ? string.Empty : " on " + p.NextChannel)
                    : "No airing in the guide yet; it goes on looking for " + Math.Clamp((Plugin.Instance?.Configuration ?? new PluginConfiguration()).RerecordGiveUpDays, 1, 365) + " days."
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { ex.Message });
        }
    }

    /// <summary>Calls a re-record off: the recording is kept as it is, and a recording set for it is cancelled.</summary>
    [HttpDelete("Item/{itemId}/Rerecord")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> CancelRerecord([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video item)
        {
            return NotFound();
        }

        await _repair.KeepAsync(item.Path, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
