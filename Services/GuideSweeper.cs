using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Applies the history to the guide and the DVR schedule. One sweep:
/// <list type="number">
/// <item>Cancels each upcoming series-recording episode whose recorded copy is currently in
/// the library (duplicate skipping). Uses ILiveTvManager.CancelTimer, which Jellyfin
/// records as a manual cancellation, so its series-timer refresh won't reschedule it. A
/// timer is only ever cancelled once, so re-enabling it by hand sticks. One-off recordings
/// a JellyDVR Schedule favourite scheduled (with its "skip episodes in the library") are
/// cancelled the same way, once per airing.</item>
/// <item>Marks guide entries by rewriting their episode title ("✓● What's in the Box?").
/// Jellyfin's guide refresh leaves an entry alone while its XMLTV data is unchanged
/// (programme etag), so marks survive refreshes; changed entries get re-marked on the
/// sweep that follows every "Refresh Guide" run.</item>
/// <item>Keeps marks out of recordings: a scheduled timer copies its entry's episode title
/// into the recording's file name and NFO, so entries with an active timer are never
/// marked, and a timer created while its entry was marked is re-created (one-off) or
/// has its series timer refreshed so it re-copies the now-clean title.</item>
/// </list>
/// Sweeps are debounced and never overlap, and wait while "Refresh Guide" is running so
/// they can't save a stale copy of a programme the refresh is updating.
/// </summary>
public sealed class GuideSweeper : IHostedService, IDisposable
{
    private const string RefreshGuideTaskKey = "RefreshGuide";
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Periodic = TimeSpan.FromMinutes(30);

    private readonly ILibraryManager _libraryManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ITaskManager _taskManager;
    private readonly HistoryStore _store;
    private readonly LibraryImporter _importer;
    private readonly RecordingsList _recordings;
    private readonly ScheduleOutcomes _outcomes;
    private readonly ScheduleFavourites _favourites;
    private readonly Programmes _programmes;
    private readonly ILogger<GuideSweeper> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly HashSet<string> _disarmed = new(StringComparer.Ordinal);
    private HashSet<Guid>? _lastScheduled;
    private readonly Timer _debounce;
    private readonly Timer _periodic;
    private volatile string _pendingReason = "startup";
    private bool _firstRun = true;

    public GuideSweeper(
        ILibraryManager libraryManager,
        ILiveTvManager liveTvManager,
        ITaskManager taskManager,
        HistoryStore store,
        LibraryImporter importer,
        RecordingsList recordings,
        ScheduleOutcomes outcomes,
        ScheduleFavourites favourites,
        Programmes programmes,
        ILogger<GuideSweeper> logger)
    {
        _programmes = programmes;
        _outcomes = outcomes;
        _favourites = favourites;
        _recordings = recordings;
        _libraryManager = libraryManager;
        _liveTvManager = liveTvManager;
        _taskManager = taskManager;
        _store = store;
        _importer = importer;
        _logger = logger;
        _debounce = new Timer(_ => _ = RunScheduledAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new Timer(_ => Request("periodic"), null, Timeout.Infinite, Timeout.Infinite);
    }

    public sealed record SweepResult(
        DateTime FinishedUtc,
        string Reason,
        long DurationMs,
        int Programs,
        int Watched,
        int InLibrary,
        int RecordedBefore,
        int ProgramsUpdated,
        int TimersCancelled,
        int TimersFixed,
        IReadOnlyList<string> Cancelled,
        string? Error);

    public SweepResult? LastSweep { get; private set; }

    /*
     * Change feed for clients that keep the guide on screen (jellyfin-web script,
     * Wholphin): every guide entry a sweep re-titles gets the next version number, and a
     * client polls "what changed since version N?" instead of reloading the whole guide.
     * Kept in memory only; a client that asks for a version older than what's retained (or
     * from before a server restart) is told to reload everything.
     */
    private const int MaxChanges = 20000;
    private readonly object _changesLock = new();
    private readonly LinkedList<(long Version, Guid ProgramId)> _changes = new();
    private long _version;
    private static readonly IReadOnlyDictionary<string, string?> EmptyTitles = new Dictionary<string, string?>();

    /// <param name="Titles">Each changed entry's current episode title (null = none), keyed by
    /// the id in the same dash-less form Jellyfin uses for item ids, so a client can patch its
    /// loaded guide without fetching the items.</param>
    public sealed record GuideChanges(long Version, bool Reset, IReadOnlyList<Guid> ProgramIds, IReadOnlyDictionary<string, string?> Titles);

    /// <summary>
    /// Guide entries re-titled since <paramref name="since"/> (a Version from an earlier
    /// call). Null <paramref name="since"/> just returns the current version, as a baseline.
    /// </summary>
    public GuideChanges GetChangesSince(long? since)
    {
        lock (_changesLock)
        {
            if (since is null || since.Value == _version)
            {
                return new GuideChanges(_version, false, Array.Empty<Guid>(), EmptyTitles);
            }

            // Asked about versions no longer retained, or from a previous server run.
            var oldest = _changes.First?.Value.Version ?? _version + 1;
            if (since.Value > _version || since.Value < oldest - 1)
            {
                return new GuideChanges(_version, true, Array.Empty<Guid>(), EmptyTitles);
            }

            var ids = _changes.Where(c => c.Version > since.Value).Select(c => c.ProgramId).Distinct().ToList();
            var titles = new Dictionary<string, string?>(ids.Count);
            foreach (var id in ids)
            {
                if (_libraryManager.GetItemById(id) is LiveTvProgram program)
                {
                    titles[id.ToString("N")] = program.EpisodeTitle;
                }
            }

            return new GuideChanges(_version, false, ids, titles);
        }
    }

    private void RecordChanges(IEnumerable<Guid> programIds)
    {
        lock (_changesLock)
        {
            foreach (var id in programIds)
            {
                _changes.AddLast((++_version, id));
            }

            while (_changes.Count > MaxChanges)
            {
                _changes.RemoveFirst();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _liveTvManager.TimerCreated += OnTimerChanged;
        _liveTvManager.SeriesTimerCreated += OnTimerChanged;
        _liveTvManager.TimerCancelled += OnTimerChanged;
        _taskManager.TaskCompleted += OnTaskCompleted;
        _outcomes.Changed += OnOutcomesChanged;

        // First sweep a little after startup (the guide and library are loaded by then);
        // an empty history is seeded from the library first.
        _debounce.Change(StartupDelay, Timeout.InfiniteTimeSpan);
        _periodic.Change(StartupDelay + Periodic, Periodic);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _liveTvManager.TimerCreated -= OnTimerChanged;
        _liveTvManager.SeriesTimerCreated -= OnTimerChanged;
        _liveTvManager.TimerCancelled -= OnTimerChanged;
        _taskManager.TaskCompleted -= OnTaskCompleted;
        _outcomes.Changed -= OnOutcomesChanged;
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        _periodic.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _debounce.Dispose();
        _periodic.Dispose();
        _runLock.Dispose();
    }

    /// <summary>Schedules a sweep; bursts of requests collapse into one.</summary>
    public void Request(string reason) => Request(reason, Debounce);

    /// <summary>
    /// Schedules a sweep after <paramref name="delay"/>, for requests where someone is
    /// looking at the guide (a menu's "mark as watched") and the usual debounce would feel slow.
    /// </summary>
    public void Request(string reason, TimeSpan delay)
    {
        _pendingReason = reason;
        _debounce.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerChanged(object? sender, EventArgs e) => Request("schedule changed");

    // JellyDVR Schedule skipped / moved / restored something: someone may be looking at the guide.
    private void OnOutcomesChanged(object? sender, EventArgs e) => Request("conflict decisions changed", TimeSpan.FromSeconds(3));

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Task.ScheduledTask.Key == RefreshGuideTaskKey)
        {
            Request("guide refreshed");
        }
    }

    private async Task RunScheduledAsync()
    {
        try
        {
            var firstRun = _firstRun;
            _firstRun = false;
            if (firstRun && _store.IsEmpty())
            {
                _logger.LogInformation("Episode history is empty; importing from the library first");
                await _importer.ImportAsync(null, CancellationToken.None).ConfigureAwait(false);
            }

            await SweepAsync(_pendingReason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GuideRefreshingException)
        {
            // TaskCompleted for the refresh will request another sweep.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Episode history sweep failed");
        }
    }

    private bool IsGuideRefreshing() =>
        _taskManager.ScheduledTasks.Any(t => t.ScheduledTask.Key == RefreshGuideTaskKey && t.State != TaskState.Idle);

    public async Task<SweepResult> SweepAsync(string reason, CancellationToken cancellationToken)
    {
        if (IsGuideRefreshing())
        {
            throw new GuideRefreshingException();
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await SweepCoreAsync(reason, sw, cancellationToken).ConfigureAwait(false);
            LastSweep = result;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GuideRefreshingException)
        {
            LastSweep = new SweepResult(DateTime.UtcNow, reason, sw.ElapsedMilliseconds, 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), ex.Message);
            throw;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<SweepResult> SweepCoreAsync(string reason, Stopwatch sw, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        RememberSymbols(cfg);

        // Note new recordings' file names (and follow moved ones) before the index is built,
        // so a recording moved to an archive folder still counts as in the library.
        _recordings.Resolve();
        var marker = new GuideMarker(cfg);
        var index = BuildIndex(cfg, marker);
        var now = DateTime.UtcNow;

        var timers = (await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false)).Items.ToList();

        // 1. Duplicate skipping.
        var cancelled = new List<string>();
        var cancelledIds = new HashSet<string>(StringComparer.Ordinal);
        if (cfg.EnableDuplicateSkipping)
        {
            // Episodes waiting to be recorded again in full: their copy in the library (or the
            // watched mark) is the incomplete one, so a series timer on a re-airing is wanted.
            var rerecords = _programmes.All()
                .Where(p => p.State is Programmes.Waiting or Programmes.Scheduled)
                .ToList();
            var favourites = _favourites.SkipInLibraryPrograms();
            foreach (var timer in timers)
            {
                // A favourite's airing is remembered by programme, so scheduling it again by hand sticks.
                var series = !string.IsNullOrEmpty(timer.SeriesTimerId);
                var favourite = !series && Guid.TryParse(timer.ProgramId, out var programId) && favourites.Contains(programId);
                var cancelKey = favourite ? FavouriteCancelKey(timer.ProgramId) : timer.Id;
                if (timer.Status != RecordingStatus.New || !(series || favourite)
                    || timer.StartDate <= now.AddMinutes(1) || _store.HasCancelled(cancelKey))
                {
                    continue;
                }

                if (GetProgram(timer.ProgramId) is not { } program)
                {
                    continue;
                }

                var identity = EpisodeIdentity.FromProgram(program, marker);
                if (rerecords.Any(p => string.Equals(p.NextProgramId, timer.ProgramId, StringComparison.OrdinalIgnoreCase)
                    || (p.Identity is { } waiting && waiting.SameEpisode(identity))))
                {
                    continue;
                }

                // A favourite's own setting is about the library only, not watched episodes.
                var state = index.Evaluate(identity);
                var skipReason = state.InLibrary ? "episode already in library"
                    : series && cfg.SkipWatchedEpisodes && state.Watched ? "episode already watched"
                    : null;
                if (skipReason is null)
                {
                    continue;
                }

                var title = HistoryEvents.Describe(program.Name, marker.Strip(program.EpisodeTitle));
                await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
                if (series)
                {
                    // A cancelled one-off is deleted outright; only series episodes stay armed.
                    await DisarmAsync(timer, cancellationToken).ConfigureAwait(false);
                }

                _store.AddCancelled(cancelKey, title, skipReason);
                cancelledIds.Add(timer.Id);
                cancelled.Add(title + " @ " + timer.StartDate.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
                _logger.LogInformation(
                    series ? "Cancelled series recording of {Title} at {Start:u}: {Reason}" : "Cancelled favourite's recording of {Title} at {Start:u}: {Reason}",
                    title,
                    timer.StartDate,
                    skipReason);
            }
        }

        // Series episodes cancelled elsewhere (the web/TV clients, or Jellyfin's own series
        // refresh) have the same problem as the ones cancelled above.
        foreach (var timer in timers.Where(t => t.Status == RecordingStatus.Cancelled
            && !string.IsNullOrEmpty(t.SeriesTimerId) && t.StartDate > now && !_disarmed.Contains(t.Id)))
        {
            await DisarmAsync(timer, cancellationToken).ConfigureAwait(false);
        }

        var activeTimers = timers
            .Where(t => (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress) && !cancelledIds.Contains(t.Id))
            .ToList();
        var scheduledPrograms = activeTimers
            .Select(t => Guid.TryParse(t.ProgramId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        // 2. Guide marks.
        var programs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            MinEndDate = now
        }).OfType<LiveTvProgram>().ToList();

        int watched = 0, inLibrary = 0, recordedBefore = 0;
        var outcomes = _outcomes.Load();
        var changed = new List<LiveTvProgram>();
        var markedButScheduled = new HashSet<Guid>();
        foreach (var program in programs)
        {
            var current = program.EpisodeTitle;
            var original = marker.Strip(current);
            var state = index.Evaluate(EpisodeIdentity.FromProgram(program, marker));
            watched += state.Watched ? 1 : 0;
            inLibrary += state.InLibrary ? 1 : 0;
            recordedBefore += state.RecordedBefore ? 1 : 0;

            var scheduled = scheduledPrograms.Contains(program.Id);
            if (scheduled && marker.IsMarked(current))
            {
                markedButScheduled.Add(program.Id);
            }

            var desired = DesiredTitle(cfg, marker, original, state, scheduled, ScheduleMarkFor(cfg, outcomes, program.Id));
            if (!string.Equals(desired, current, StringComparison.Ordinal))
            {
                program.EpisodeTitle = desired;
                changed.Add(program);
            }
        }

        foreach (var group in changed.GroupBy(p => p.ParentId))
        {
            var parent = _libraryManager.GetItemById(group.Key) ?? group.First().GetParent();
            await _libraryManager.UpdateItemsAsync(group.ToList<BaseItem>(), parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        // Entries whose recording was scheduled or cancelled since the last sweep go in the change
        // feed too, so open guides redraw their recording dot (e.g. the airing a recording moved to).
        var scheduleChanged = _lastScheduled is null ? new HashSet<Guid>() : new HashSet<Guid>(scheduledPrograms);
        if (_lastScheduled is not null)
        {
            scheduleChanged.SymmetricExceptWith(_lastScheduled);
        }

        _lastScheduled = scheduledPrograms;
        RecordChanges(changed.Select(p => p.Id).Concat(scheduleChanged).Distinct());

        // 3. Timers that copied a marked title: make them re-copy the clean one.
        int fixedTimers = 0;
        var seriesToRefresh = new HashSet<string>(StringComparer.Ordinal);
        foreach (var timer in activeTimers.Where(t => t.Status == RecordingStatus.New
            && Guid.TryParse(t.ProgramId, out var pid) && markedButScheduled.Contains(pid)))
        {
            if (!string.IsNullOrEmpty(timer.SeriesTimerId))
            {
                seriesToRefresh.Add(timer.SeriesTimerId);
                continue;
            }

            // A one-off timer has no refresh path; cancelling deletes it, and re-creating
            // copies the programme's (now unmarked) metadata again.
            await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
            await _liveTvManager.CreateTimer(
                new TimerInfoDto
                {
                    ProgramId = timer.ProgramId,
                    ChannelId = timer.ChannelId,
                    ServiceName = timer.ServiceName,
                    Name = timer.Name,
                    StartDate = timer.StartDate,
                    EndDate = timer.EndDate,
                    PrePaddingSeconds = timer.PrePaddingSeconds,
                    PostPaddingSeconds = timer.PostPaddingSeconds,
                    IsPrePaddingRequired = timer.IsPrePaddingRequired,
                    IsPostPaddingRequired = timer.IsPostPaddingRequired,
                    KeepUntil = timer.KeepUntil
                },
                cancellationToken).ConfigureAwait(false);
            fixedTimers++;
        }

        foreach (var seriesTimerId in seriesToRefresh)
        {
            // Saving the series timer unchanged makes Jellyfin re-copy each child timer's
            // metadata from its programme (UpdateTimersForSeriesTimer).
            var seriesTimer = await _liveTvManager.GetSeriesTimer(seriesTimerId, cancellationToken).ConfigureAwait(false);
            if (seriesTimer is not null)
            {
                await _liveTvManager.UpdateSeriesTimer(seriesTimer, cancellationToken).ConfigureAwait(false);
                fixedTimers++;
            }
        }

        sw.Stop();
        var result = new SweepResult(
            DateTime.UtcNow, reason, sw.ElapsedMilliseconds, programs.Count, watched, inLibrary, recordedBefore,
            changed.Count, cancelled.Count, fixedTimers, cancelled, null);
        _logger.LogInformation(
            "Episode history sweep ({Reason}): {Programs} guide entries, {Watched} watched / {InLibrary} in library / {Before} recorded before; " +
            "{Updated} entries updated, {Cancelled} recordings cancelled, {Fixed} timers refreshed ({Ms} ms)",
            reason, programs.Count, watched, inLibrary, recordedBefore, changed.Count, cancelled.Count, fixedTimers, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>The skipped-recordings key of a favourite's airing (its timer id changes if it's scheduled again).</summary>
    internal static string FavouriteCancelKey(string programId) => "favourite:" + programId.ToLowerInvariant();

    /// <summary>
    /// Makes a cancelled series episode really not record. Jellyfin 12 cancels a series
    /// episode by flagging it Cancelled without stopping its in-memory start timer
    /// (TimerManager.AddOrUpdate(timer, resetTimer: false)), and the fired handler never
    /// checks the status, so the episode records anyway unless the server restarts first.
    /// Saving the timer (UpdateTimer only copies its padding) goes through
    /// TimerManager.Update, which stops the start timer and, seeing Cancelled, doesn't
    /// restart it. Each timer id only needs this once per server run.
    /// </summary>
    private async Task DisarmAsync(TimerInfoDto timer, CancellationToken cancellationToken)
    {
        try
        {
            await _liveTvManager.UpdateTimer(timer, cancellationToken).ConfigureAwait(false);
            _disarmed.Add(timer.Id);
            _logger.LogInformation("Stopped the start timer of cancelled series recording {Name} at {Start:u}", timer.Name, timer.StartDate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't stop the start timer of cancelled series recording {Name} at {Start:u}", timer.Name, timer.StartDate);
        }
    }

    /// <summary>
    /// The episode title a guide entry should carry. Entries with an active timer stay
    /// unmarked, since the timer copies the title into the recording's name.
    /// </summary>
    private static string? DesiredTitle(PluginConfiguration cfg, GuideMarker marker, string? original, HistoryState state, bool scheduled, ScheduleMark schedule) =>
        cfg.EnableGuideMarks && !scheduled ? marker.Compose(original, state, schedule) : original;

    private static ScheduleMark ScheduleMarkFor(PluginConfiguration cfg, IReadOnlyDictionary<Guid, string> outcomes, Guid programId) =>
        !cfg.EnableScheduleMarks || !outcomes.TryGetValue(programId, out var kind) ? ScheduleMark.None
        : kind == ScheduleOutcomes.KindSkipped ? ScheduleMark.Skipped
        : ScheduleMark.OtherAiring;

    /// <summary>
    /// Re-marks one guide entry right away (after a "mark as watched" from a client menu),
    /// by the same rules as a sweep, and returns its new episode title so the client can
    /// show it immediately. The episode's other airings follow with the next sweep.
    /// </summary>
    public async Task<string?> ApplyNowAsync(Guid programId, CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_libraryManager.GetItemById(programId) is not LiveTvProgram program)
            {
                return null;
            }

            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var marker = new GuideMarker(cfg);
            var state = BuildIndex(cfg, marker).Evaluate(EpisodeIdentity.FromProgram(program, marker));

            var timers = (await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false)).Items;
            var scheduled = timers.Any(t => (t.Status == RecordingStatus.New || t.Status == RecordingStatus.InProgress)
                && Guid.TryParse(t.ProgramId, out var id) && id == programId);

            var current = program.EpisodeTitle;
            var desired = DesiredTitle(cfg, marker, marker.Strip(current), state, scheduled, ScheduleMarkFor(cfg, _outcomes.Load(), programId));
            if (!string.Equals(desired, current, StringComparison.Ordinal))
            {
                program.EpisodeTitle = desired;
                await program.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                RecordChanges(new[] { programId });
            }

            return desired;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private HistoryIndex BuildIndex(PluginConfiguration cfg, GuideMarker marker)
    {
        var index = new HistoryIndex(_store.LoadAll(), id => _libraryManager.GetItemById(id) is not null);
        if (cfg.CountAllLibraries)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            });
            // A stub (a recording that failed after a few seconds) doesn't count as having the
            // episode: it would mark the guide and cancel the next airing's recording. Only a
            // known short runtime excludes an item; checking file sizes for items without one
            // would read every file on network shares.
            var minTicks = TimeSpan.FromMinutes(cfg.MinRecordingMinutes).Ticks;
            index.AddLibraryItems(items
                .Where(i => i.RunTimeTicks is not > 0 || i.RunTimeTicks >= minTicks)
                .Where(i => !LiveBuffers.IsUnkeptBuffer(i)) // a live TV buffer isn't "having" the episode
                .Select(i => (i.Id, Identity: EpisodeIdentity.FromLibraryItem(i, marker, null)))
                .Where(x => x.Identity is not null)
                .Select(x => (x.Id, x.Identity!)));
        }

        return index;
    }

    /// <summary>
    /// Adds the current symbols to <see cref="PluginConfiguration.UsedSymbols"/> before they
    /// go into the guide, so a later symbol change can still recognise and remove them.
    /// </summary>
    private void RememberSymbols(PluginConfiguration cfg)
    {
        var used = cfg.UsedSymbols ?? Array.Empty<string>();
        var missing = new[] { cfg.WatchedSymbol, cfg.InLibrarySymbol, cfg.RecordedBeforeSymbol, cfg.SkippedSymbol, cfg.OtherAiringSymbol }
            .Where(sym => !string.IsNullOrEmpty(sym) && !used.Contains(sym, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0 || Plugin.Instance is not { } plugin)
        {
            return;
        }

        cfg.UsedSymbols = used.Concat(missing).ToArray();
        plugin.SaveConfiguration();
        _logger.LogInformation("Remembering guide mark symbols {Symbols}", string.Join(" ", missing));
    }

    private LiveTvProgram? GetProgram(string? id) =>
        Guid.TryParse(id, out var guid) ? _libraryManager.GetItemById(guid) as LiveTvProgram : null;

    /// <summary>What the history says about one guide entry (for the API / debugging).</summary>
    /// <returns>The entry's identity, its marks, and the existing library items that make it
    /// "in library" (for "Play from library").</returns>
    public (EpisodeIdentity Identity, HistoryState State, IReadOnlyList<Guid> LibraryItems)? Lookup(Guid programId)
    {
        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program)
        {
            return null;
        }

        var marker = new GuideMarker(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        var identity = EpisodeIdentity.FromProgram(program, marker);
        var index = BuildIndex(Plugin.Instance?.Configuration ?? new PluginConfiguration(), marker);
        return (identity, index.Evaluate(identity), index.LibraryItems(identity));
    }
}

/// <summary>
/// A sweep was refused because "Refresh Guide" is running: saving programmes then could
/// overwrite the refresh's own updates. A sweep follows automatically when it finishes.
/// </summary>
public sealed class GuideRefreshingException : InvalidOperationException
{
    public GuideRefreshingException()
        : base("The guide is being refreshed; the history is applied automatically when that finishes.")
    {
    }
}
