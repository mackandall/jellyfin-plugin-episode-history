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
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public HistoryController(HistoryStore store, GuideSweeper sweeper, LibraryImporter importer, ILibraryManager libraryManager, BackupService backup)
    {
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
        return Ok(new { found.Identity, found.Identity.ValidProgramId, found.State, MarkedWatched = markedWatched });
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

        return Ok(new { MarkedWatched = watched, EpisodeTitle = episodeTitle });
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

    private const string ManualSource = "manual";

    private List<HistoryStore.Row> ManualRows(Guid userId, EpisodeIdentity identity) =>
        _store.LoadWatched(userId, ManualSource).Where(r => r.Identity.SameEpisode(identity)).ToList();

    // The id Jellyfin's auth handler puts on every authenticated request (its own
    // ClaimsPrincipal.GetUserId reads the same claim; that helper lives in Jellyfin.Api,
    // which plugins shouldn't reference).
    private Guid CurrentUserId() =>
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    private static GuideMarker Marker() => new(Plugin.Instance?.Configuration ?? new PluginConfiguration());
}
