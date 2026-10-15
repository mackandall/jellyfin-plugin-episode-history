using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Feeds the history from live server events:
/// <list type="bullet">
/// <item>DVR recordings: polled every 30 s from the Live TV "in progress" recordings list.
/// Polling rather than library events because Jellyfin raises no ItemAdded/ItemUpdated for a
/// recording until after it has finished (confirmed live, and the reason the Comskip plugin's
/// channel capture polls too), and by then the timer — which carries the guide's TMS id,
/// title and numbering — is gone. The timer is snapshotted while the recording runs; once it
/// stops, the recording only counts if it ran for
/// <see cref="PluginConfiguration.MinRecordedPercent"/> of the programme.</item>
/// <item>An episode or movie copied into a recordings folder by hand (ItemAdded for a path
/// the DVR capture didn't produce): recorded after a short delay, once Jellyfin has probed
/// its runtime, if it is at least <see cref="PluginConfiguration.MinRecordingMinutes"/> long.</item>
/// <item>A user marks something played / unplayed, or finishes playing it.</item>
/// <item>A recorded item is deleted (it turns from "in library" into "recorded before").</item>
/// </list>
/// </summary>
public sealed class HistoryEvents : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait after a recording stops for Jellyfin to finish adding it to the library.</summary>
    private static readonly TimeSpan LibraryWait = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a hand-added file waits before it is recorded: Jellyfin raises ItemAdded
    /// before it has probed the runtime, and a finished DVR recording's own ItemAdded must
    /// lose the race to the DVR capture, which knows more about it.
    /// </summary>
    private static readonly TimeSpan AddedDelay = TimeSpan.FromMinutes(2);

    private static readonly DtoOptions RecordingDtoOptions = new(false) { Fields = new[] { ItemFields.Path } };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly IUserManager _userManager;
    private readonly HistoryStore _store;
    private readonly GuideSweeper _sweeper;
    private readonly ILogger<HistoryEvents> _logger;

    // Recordings in progress, keyed by file path.
    private readonly ConcurrentDictionary<string, PendingRecording> _pending = new(StringComparer.Ordinal);

    // Paths the DVR capture has finished with (counted or rejected), so their ItemAdded isn't
    // mistaken for a file copied in by hand. Pruned after a few hours.
    private readonly ConcurrentDictionary<string, DateTime> _dvrHandled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _bufferNoted = new(StringComparer.Ordinal);

    // Files that appeared in a recordings folder, waiting out AddedDelay. Keyed by path.
    private readonly ConcurrentDictionary<string, (Guid ItemId, DateTime DueUtc)> _added = new(StringComparer.Ordinal);
    private static readonly TimeSpan TidyDelay = TimeSpan.FromSeconds(3);

    // No growth over more than one poll: no data is arriving.
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(45);
    private readonly StallNotes _stalls;
    private readonly Programmes _programmes;
    private readonly RecordingRepair _repair;
    private readonly ScheduleLedger _ledger;
    private Timer? _poll;
    private int _polling;

    public HistoryEvents(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IRecordingsManager recordingsManager,
        ILiveTvManager liveTvManager,
        IUserManager userManager,
        HistoryStore store,
        GuideSweeper sweeper,
        StallNotes stalls,
        Programmes programmes,
        RecordingRepair repair,
        ScheduleLedger ledger,
        ILogger<HistoryEvents> logger)
    {
        _ledger = ledger;
        _stalls = stalls;
        _programmes = programmes;
        _repair = repair;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _recordingsManager = recordingsManager;
        _liveTvManager = liveTvManager;
        _userManager = userManager;
        _store = store;
        _sweeper = sweeper;
        _logger = logger;
    }

    private sealed record PendingRecording(Guid ItemId, EpisodeIdentity Identity, string Title, DateTime ProgramStart, DateTime ProgramEnd, DateTime FirstSeenUtc)
    {
        /// <summary>The airing it is a piece of (see <see cref="Programmes.KeyFor"/>).</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>The DVR's id of the timer recording it.</summary>
        public string? TimerId { get; init; }

        /// <summary>When the DVR was first seen to have stopped writing it.</summary>
        public DateTime? EndedUtc { get; set; }

        private long _length;
        private DateTime _grewUtc = FirstSeenUtc;
        private DateTime? _stallStartUtc;
        private TimeSpan _stalled;

        /// <summary>When the file first stopped growing while the recording went on (the tuner went quiet).</summary>
        public DateTime? FirstStallUtc { get; private set; }

        /// <summary>
        /// Notes the file's size at a poll. A file that hasn't grown for longer than a poll or
        /// two is stalled from when it last grew, until it grows again.
        /// </summary>
        public void Track(long length, DateTime now)
        {
            if (length > _length)
            {
                if (_stallStartUtc is { } start)
                {
                    _stalled += now - start;
                    _stallStartUtc = null;
                }

                _length = length;
                _grewUtc = now;
            }
            else if (_stallStartUtc is null && now - _grewUtc > StallAfter)
            {
                _stallStartUtc = _grewUtc;
                FirstStallUtc ??= _grewUtc;
            }
        }

        /// <summary>How long the recording ran without receiving anything, up to <paramref name="end"/>.</summary>
        public TimeSpan StalledUntil(DateTime end) =>
            _stalled + (_stallStartUtc is { } start && end > start ? end - start : TimeSpan.Zero);
    }

    /// <summary>The running instance, for the recordings list (which can't take it as a dependency).</summary>
    public static HistoryEvents? Current { get; private set; }

    /// <summary>
    /// Timers cancelled lately, by the DVR's id. Cancelling a one-off recording while it runs
    /// (the clients' "stop recording") leaves its timer "Completed" in Jellyfin 12, so the event
    /// is the only sign that it was stopped rather than cut short.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _cancelledTimers = new(StringComparer.Ordinal);

    /// <summary>Paths a stop was just asked for, until the DVR lets go of the file.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Stopping = new(StringComparer.Ordinal);

    /// <summary>
    /// Notes that a recording was asked to stop: from now on it no longer counts as being
    /// recorded, without waiting the moment Jellyfin takes to close the file.
    /// </summary>
    public static void MarkStopping(string path) => Stopping[path] = DateTime.UtcNow;

    /// <summary>Whether a stop was asked for in the last minute.</summary>
    public static bool IsStopping(string? path) =>
        !string.IsNullOrEmpty(path) && Stopping.TryGetValue(path, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1);

    /// <summary>A recording that ended and is waiting for its counted / not counted decision.</summary>
    public bool IsPending(string path) => _pending.ContainsKey(path);

    /// <summary>A recording whose decision was made (in the history if it counted, partial if not).</summary>
    public bool IsDecided(string path) => _dvrHandled.ContainsKey(path);

    /// <summary>Makes the pending decisions now instead of at the next poll.</summary>
    public void PollSoon() => _poll?.Change(TimeSpan.FromSeconds(2), PollInterval);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Current = this;
        CloseOpenPieces();
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _liveTvManager.TimerCancelled += OnTimerCancelled;
        _poll = new Timer(_ => _ = PollAsync(), null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _liveTvManager.TimerCancelled -= OnTimerCancelled;
        _poll?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _poll?.Dispose();

    /// <summary>Starts tracking recordings the DVR is writing right now.</summary>
    private async Task DiscoverAsync()
    {
        // The recordings query needs a real user (Guid.Empty returns nothing), even though
        // recordings aren't user-scoped.
        var userId = _userManager.GetUsers().FirstOrDefault()?.Id ?? Guid.Empty;
        var result = await _liveTvManager.GetRecordingsAsync(
            new RecordingQuery { UserId = userId, IsInProgress = true, EnableImages = false },
            RecordingDtoOptions).ConfigureAwait(false);

        // A recording going on after a restart is another version ("Name - 1.ts") of the item
        // the first piece made, and the query above lists only that item: Jellyfin's own timer
        // list knows what is really being written.
        var active = result.Items.Select(dto => (Path: (string?)dto.Path, dto.Id))
            .Concat(TimerStatuses().Where(t => t.Value == "InProgress")
                .Select(t => _recordingsManager.GetActiveRecordingPath(t.Key))
                .Select(path => (Path: path, Id: Guid.Empty)))
            .ToList();
        foreach (var (path, itemId) in active)
        {
            if (string.IsNullOrEmpty(path) || _pending.ContainsKey(path)
                || _recordingsManager.GetActiveRecordingInfo(path)?.Timer is not { } timer)
            {
                continue;
            }

            var marker = new GuideMarker(Config);
            var identity = EpisodeIdentity.FromTimer(timer, marker, null);
            if (identity.ValidProgramId is null && FindProgram(timer)?.ShowId is { } showId)
            {
                identity = identity with { ProgramId = showId };
            }

            // Seen within a poll or so of its planned start: it has run since that start (so a
            // short programme recorded without padding isn't a few percent short for nothing).
            var now = DateTime.UtcNow;
            var planned = timer.StartDate.AddSeconds(-Math.Max(0, timer.PrePaddingSeconds));
            var pending = new PendingRecording(
                itemId,
                identity,
                Describe(timer.Name, marker.Strip(timer.EpisodeTitle)),
                timer.StartDate,
                timer.EndDate,
                now >= planned && now - planned <= TimeSpan.FromSeconds(60) ? planned : now)
            {
                Key = Programmes.KeyFor(timer.ChannelId, timer.ProgramId, timer.EndDate),
                TimerId = timer.Id
            };
            if (_pending.TryAdd(path, pending))
            {
                _logger.LogInformation(
                    "Tracking recording {Title} (program id {ProgramId}) at {Path}",
                    pending.Title, pending.Identity.ValidProgramId ?? "none", path);
                NotePieceStarted(path, pending, timer.ChannelId);
            }
        }
    }

    /// <summary>
    /// The guide entry a timer was made from. Jellyfin only copies the programme's ShowId
    /// (the TMS id) into timers created by a series recording, not into one-off ones, so
    /// for those the id has to come from the guide entry itself. Looked up by start time
    /// and Jellyfin's external programme id ("channel_start").
    /// </summary>
    private LiveTvProgram? FindProgram(TimerInfo timer)
    {
        if (string.IsNullOrEmpty(timer.ProgramId))
        {
            return null;
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
                MinStartDate = timer.StartDate,
                MaxStartDate = timer.StartDate
            })
            .OfType<LiveTvProgram>()
            .FirstOrDefault(p => string.Equals(p.ExternalId, timer.ProgramId, StringComparison.Ordinal));
    }

    /// <summary>Finds new recordings and records the ones the DVR has finished.</summary>
    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            try
            {
                await DiscoverAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list in-progress recordings");
            }

            foreach (var (path, pending) in _pending)
            {
                if (_recordingsManager.GetActiveRecordingInfo(path) is not null)
                {
                    pending.Track(FileLength(path), DateTime.UtcNow);
                    continue;
                }

                pending.EndedUtc ??= DateTime.UtcNow;
                try
                {
                    if (TryComplete(path, pending))
                    {
                        _dvrHandled[path] = DateTime.UtcNow;
                        _pending.TryRemove(path, out _);
                    }
                }
                catch (Exception ex)
                {
                    _dvrHandled[path] = DateTime.UtcNow;
                    _pending.TryRemove(path, out _);
                    _logger.LogWarning(ex, "Failed to record history for finished recording {Path}", path);
                }
            }

            ProcessAdded();

            if (Config.WatchFailedRecordings)
            {
                try
                {
                    // Recordings that produced nothing at all: no file, so nothing above sees them.
                    var failed = _ledger.Process(
                        key => _pending.Values.Any(x => x.Key == key) || _programmes.All().Any(p => p.Key == key && p.Pieces.Count > 0),
                        new GuideMarker(Config));
                    await _repair.NoteFailedAsync(failed, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check the schedule for recordings that didn't record");
                }
            }

            try
            {
                await _repair.ProcessAsync(_pending.ContainsKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to look after split or incomplete recordings");
            }

            foreach (var (path, handled) in _dvrHandled)
            {
                if (DateTime.UtcNow - handled > TimeSpan.FromHours(6))
                {
                    _dvrHandled.TryRemove(path, out _);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is not (Episode or Movie) || e.Item.IsVirtualItem || string.IsNullOrEmpty(e.Item.Path))
            {
                return;
            }

            if (Config.CountAllLibraries)
            {
                // Any new episode/movie can change what counts as "in library".
                _sweeper.Request("library item added");
            }

            if (!_pending.ContainsKey(e.Item.Path) && !_dvrHandled.ContainsKey(e.Item.Path)
                && RecordingFiles.IsUnder(e.Item.Path, RecordingFiles.Folders(_recordingsManager)))
            {
                _added[e.Item.Path] = (e.Item.Id, DateTime.UtcNow + AddedDelay);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle added item {Path}", e.Item?.Path);
        }
    }

    /// <summary>Records files copied into a recordings folder once their delay has passed.</summary>
    private void ProcessAdded()
    {
        var changed = false;
        foreach (var (path, (itemId, due)) in _added)
        {
            if (DateTime.UtcNow < due)
            {
                continue;
            }

            _added.TryRemove(path, out _);

            // Still being recorded, or already handled by the DVR capture: not hand-added.
            if (_pending.ContainsKey(path) || _dvrHandled.ContainsKey(path) || _recordingsManager.GetActiveRecordingInfo(path) is not null)
            {
                continue;
            }

            try
            {
                var item = _libraryManager.GetItemById(itemId) ?? RecordingFiles.ItemAt(_libraryManager, path);
                if (item is null)
                {
                    continue;
                }

                if (!RecordingFiles.IsLongEnough(item, Config.MinRecordingMinutes))
                {
                    _logger.LogInformation("Ignoring short file added to recordings: {Path}", path);
                    continue;
                }

                if (EpisodeIdentity.FromLibraryItem(item, new GuideMarker(Config), null) is { } identity
                    && _store.UpsertMany(new[] { (HistoryStore.KindRecorded, (Guid?)null, item.Id, identity, "added", DateTime.UtcNow) }, overwrite: false) > 0)
                {
                    _logger.LogInformation("Recorded in history (added to recordings folder): {Title}", Describe(item is Episode ep ? ep.SeriesName : item.Name, item is Episode ? item.Name : null));
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record added file {Path}", path);
            }
        }

        if (changed)
        {
            _sweeper.Request("recording added");
        }
    }

    /// <returns>False while still waiting for Jellyfin to (re)add the finished recording to the library.</returns>
    private bool TryComplete(string path, PendingRecording pending)
    {
        var item = (pending.ItemId.Equals(Guid.Empty) ? null : _libraryManager.GetItemById(pending.ItemId)) ?? RecordingFiles.ItemAt(_libraryManager, path);
        if (item is null || !File.Exists(path))
        {
            if (DateTime.UtcNow - pending.EndedUtc!.Value < LibraryWait && File.Exists(path))
            {
                return false;
            }

            // The file is there but the library never showed it: still a piece of its airing
            // (to join and to count), just without a history row of its own.
            if (File.Exists(path))
            {
                var kept = pending.StalledUntil(pending.EndedUtc!.Value);
                NotePieceEnded(path, pending, pending.EndedUtc!.Value - pending.FirstSeenUtc - kept, kept);
                _logger.LogInformation("Recording {Title} ended but isn't in the library; kept as a piece of its airing", pending.Title);
                return true;
            }

            _programmes.Change(all =>
            {
                if (all.TryGetValue(pending.Key, out var gone))
                {
                    gone.Pieces.RemoveAll(x => x.Path == path);
                    if (gone.Pieces.Count == 0)
                    {
                        all.Remove(pending.Key);
                    }
                }
            });

            _logger.LogInformation("Recording {Title} ended but its file/item is gone; not recorded in history", pending.Title);
            return true;
        }

        // A JellyDVR Live buffer only counts once someone keeps it; until then keep waiting
        // (deleted unkept, it ends up in the "gone" case above).
        if (LiveBuffers.IsUnkeptBuffer(path) || LiveBuffers.IsUnkeptBuffer(item))
        {
            if (_bufferNoted.TryAdd(path, 0))
            {
                _logger.LogInformation("Recording {Title} is a live TV buffer; not recorded in history unless it's kept", pending.Title);
            }

            return false;
        }

        _bufferNoted.TryRemove(path, out _);

        // Measured from when the poll first saw it (within 30 s of the DVR starting) to when
        // it stopped, against the programme's scheduled length without padding. Time the file
        // didn't grow (the tuner went quiet, Jellyfin kept the recording open) isn't recorded time.
        // An interrupted recording goes on in another file: its pieces count together.
        var stalled = pending.StalledUntil(pending.EndedUtc!.Value);
        var piece = pending.EndedUtc!.Value - pending.FirstSeenUtc - stalled;
        var programme = NotePieceEnded(path, pending, piece, stalled);
        var scheduled = programme is null ? pending.ProgramEnd - pending.ProgramStart : TimeSpan.FromSeconds(programme.ScheduledSeconds);
        var ran = programme is null ? piece : TimeSpan.FromSeconds(programme.CapturedSeconds);
        var percent = scheduled > TimeSpan.Zero ? ran / scheduled * 100 : 100;
        if (Config.StallWarnSeconds > 0 && stalled.TotalSeconds >= Config.StallWarnSeconds && pending.FirstStallUtc is { } firstStall)
        {
            _stalls.Set(path, new StallNotes.Note(
                firstStall,
                (int)Math.Max(1, Math.Round(stalled.TotalMinutes)),
                (int)Math.Round(piece.TotalMinutes),
                (int)Math.Round(scheduled.TotalMinutes),
                DateTime.UtcNow));
            _logger.LogWarning(
                "Recording {Title} stalled: no data from {At:HH:mm:ss} for {Lost:F0} min in all; {Ran:F0} of {Scheduled:F0} minutes are in the file",
                pending.Title, firstStall.ToLocalTime(), stalled.TotalMinutes, ran.TotalMinutes, scheduled.TotalMinutes);
        }

        if (percent < Config.MinRecordedPercent)
        {
            _logger.LogInformation(
                "Recording {Title} ended after {Ran:F0} of {Scheduled:F0} minutes ({Percent:F0}% < {Min}%); not counted as recorded",
                pending.Title, ran.TotalMinutes, scheduled.TotalMinutes, percent, Config.MinRecordedPercent);
            return true;
        }

        // The library may know the series under another name than the guide (metadata
        // provider rename); keep it as the alias so imports and played events line up.
        var marker = new GuideMarker(Config);
        var libraryIdentity = EpisodeIdentity.FromLibraryItem(item, marker, null);
        var identity = pending.Identity with { SeriesAlt = libraryIdentity?.SeriesName };

        _store.Upsert(HistoryStore.KindRecorded, null, item.Id, identity, "recording", DateTime.UtcNow);
        _programmes.Change(all =>
        {
            if (all.TryGetValue(pending.Key, out var counted))
            {
                counted.Counted = true;
            }
        });
        _logger.LogInformation("Recorded in history: {Title} ({Percent:F0}% of scheduled length)", pending.Title, percent);

        StripMarkFromName(item, marker);
        _sweeper.Request("recording finished");
        return true;
    }

    /// <summary>
    /// A recording scheduled while its guide entry carried a mark inherits the marked
    /// episode title as its name (the sweeper unmarks scheduled entries, but a timer that
    /// fired in between keeps the copy). Clean the library item's name.
    /// </summary>
    private void StripMarkFromName(BaseItem item, GuideMarker marker)
    {
        if (!marker.IsMarked(item.Name) || marker.Strip(item.Name) is not { } clean)
        {
            return;
        }

        _logger.LogInformation("Removing guide mark from recording name: {Old} -> {New}", item.Name, clean);
        item.Name = clean;
        _ = item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ContinueWith(
            t => _logger.LogWarning(t.Exception, "Failed to save cleaned recording name for {Path}", item.Path),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Progress/start events fire constantly during playback and never change "played".
        if (e.SaveReason is not (UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed
            or UserDataSaveReason.UpdateUserData or UserDataSaveReason.Import))
        {
            return;
        }

        if (e.Item is not (Episode or Movie) || e.Item.IsVirtualItem || e.UserData is null)
        {
            return;
        }

        try
        {
            bool changed;
            if (e.UserData.Played)
            {
                var identity = EpisodeIdentity.FromLibraryItem(e.Item, new GuideMarker(Config), _store.GetProgramIdForItem(e.Item.Id));
                changed = identity is not null
                    && _store.Upsert(HistoryStore.KindWatched, e.UserId, e.Item.Id, identity, "played", e.UserData.LastPlayedDate ?? DateTime.UtcNow);
            }
            else
            {
                // Stopping part-way (PlaybackFinished with Played=false) isn't "unwatched";
                // only an explicit toggle is.
                changed = e.SaveReason is UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserData
                    && _store.DeleteWatched(e.UserId, e.Item.Id);
            }

            if (changed)
            {
                _logger.LogInformation("Watched state for {Name} changed ({Played})", e.Item.Name, e.UserData.Played ? "watched" : "unwatched");
                _sweeper.Request("watched state changed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update watched history for {Name}", e.Item.Name);
        }
    }

    private void OnTimerCancelled(object? sender, Jellyfin.Data.Events.GenericEventArgs<TimerEventInfo> e)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _cancelledTimers.Where(x => now - x.Value > TimeSpan.FromHours(12)).Select(x => x.Key).ToList())
        {
            _cancelledTimers.TryRemove(old, out _);
        }

        if (e.Argument?.Id is { Length: > 0 } id)
        {
            _cancelledTimers[id] = now;
            _ledger.NoteCancelled(id);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is Video && (_store.HasRecorded(e.Item.Id) || (Config.CountAllLibraries && e.Item is Episode or Movie)))
            {
                _sweeper.Request("recording deleted");
            }

            if (Config.TidyDeletedRecordings && e.Item is Video { Path: { Length: > 0 } path }
                && RecordingFiles.Folders(_recordingsManager) is var roots && RecordingFiles.IsUnder(path, roots))
            {
                _ = TidyAsync(path, roots, (e.Item as Episode)?.Series);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle removal of {Name}", e.Item?.Name);
        }
    }

    /// <summary>
    /// A deleted recording's leftovers (the files beside it, its emptied season and show folders)
    /// go too, whoever deleted it. A moment later, so the delete itself has finished; a show whose
    /// folder went leaves the library with it.
    /// </summary>
    private async Task TidyAsync(string path, System.Collections.Generic.List<string> roots, Series? show)
    {
        try
        {
            await Task.Delay(TidyDelay).ConfigureAwait(false);
            if (RecordingTidy.AfterDelete(path, roots, _logger)
                && show is not null
                && string.Equals(Path.TrimEndingDirectorySeparator(show.Path ?? string.Empty), RecordingTidy.ShowFolder(path), StringComparison.Ordinal)
                && _libraryManager.GetItemById(show.Id) is Series current)
            {
                _libraryManager.DeleteItem(current, new DeleteOptions { DeleteFileLocation = false }, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to tidy up after {Path}", path);
        }
    }

    private void NotePieceStarted(string path, PendingRecording pending, string? channelId) =>
        _programmes.Change(all =>
        {
            if (!all.TryGetValue(pending.Key, out var p))
            {
                // Jellyfin's retry after a failure moves the timer's start: the first piece has the programme's own times.
                all[pending.Key] = p = new Programmes.Programme
                {
                    Key = pending.Key,
                    Title = pending.Title,
                    Identity = pending.Identity,
                    ChannelId = channelId,
                    ProgramStartUtc = pending.ProgramStart,
                    ProgramEndUtc = pending.ProgramEnd,
                    CreatedUtc = DateTime.UtcNow,
                    IsBuffer = LiveBuffers.IsUnkeptBuffer(path)
                };
            }

            if (p.Pieces.All(x => x.Path != path))
            {
                p.Pieces.Add(new Programmes.Piece { Path = path, FirstSeenUtc = pending.FirstSeenUtc });
            }
        });

    private Programmes.Programme? NotePieceEnded(string path, PendingRecording pending, TimeSpan captured, TimeSpan stalled)
    {
        Programmes.Programme? result = null;
        var status = pending.TimerId is { } id && TimerStatuses().TryGetValue(id, out var s) ? s : null;
        _programmes.Change(all =>
        {
            if (!all.TryGetValue(pending.Key, out var p) || p.Pieces.FirstOrDefault(x => x.Path == path) is not { } piece)
            {
                return;
            }

            piece.EndedUtc = pending.EndedUtc;
            piece.CapturedSeconds = Math.Max(0, captured.TotalSeconds);
            piece.StalledSeconds = stalled.TotalSeconds;
            piece.FirstStallUtc = pending.FirstStallUtc;
            piece.StoppedByUser = IsStopping(path);

            // A one-off timer that was cancelled is gone; a series recording's is marked.
            piece.Cancelled = status is null or "Cancelled"
                || (pending.TimerId is { } timerId && _cancelledTimers.ContainsKey(timerId));
            result = p;
        });
        return result;
    }

    /// <summary>
    /// Pieces that were being recorded when the server went down end where their file does (the
    /// recording goes on in a new file when the server is back).
    /// </summary>
    private void CloseOpenPieces()
    {
        try
        {
            _programmes.Change(all =>
            {
                foreach (var piece in all.Values.SelectMany(p => p.Pieces).Where(x => x.EndedUtc is null))
                {
                    var written = File.Exists(piece.Path) ? File.GetLastWriteTimeUtc(piece.Path) : piece.FirstSeenUtc;
                    piece.EndedUtc = written > piece.FirstSeenUtc ? written : piece.FirstSeenUtc;
                    piece.CapturedSeconds = (piece.EndedUtc.Value - piece.FirstSeenUtc).TotalSeconds;
                    piece.ClosedAtStartup = true;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't close the recordings a restart interrupted");
        }
    }

    /// <summary>Jellyfin's timers and their status ("New", "InProgress", "Cancelled"…), by the DVR's id, from its timers.json.</summary>
    private Dictionary<string, string> TimerStatuses()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = Plugin.Instance?.DataPath is { } data ? Path.Combine(data, "livetv", "timers.json") : null;
            if (file is null || !File.Exists(file))
            {
                return result;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.TryGetProperty("Id", out var id) && id.GetString() is { } key && t.TryGetProperty("Status", out var status))
                {
                    result[key] = status.ValueKind == System.Text.Json.JsonValueKind.String ? status.GetString() ?? string.Empty : status.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Can't read Jellyfin's timers.json");
        }

        return result;
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string Describe(string? name, string? episodeTitle) =>
        string.IsNullOrEmpty(episodeTitle) ? name ?? "?" : name + " - " + episodeTitle;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
