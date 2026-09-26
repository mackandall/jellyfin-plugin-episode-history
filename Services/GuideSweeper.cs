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
/// timer is only ever cancelled once, so re-enabling it by hand sticks.</item>
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
    private readonly ILogger<GuideSweeper> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
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
        ILogger<GuideSweeper> logger)
    {
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _liveTvManager.TimerCreated += OnTimerChanged;
        _liveTvManager.SeriesTimerCreated += OnTimerChanged;
        _liveTvManager.TimerCancelled += OnTimerChanged;
        _taskManager.TaskCompleted += OnTaskCompleted;

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
    public void Request(string reason)
    {
        _pendingReason = reason;
        _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerChanged(object? sender, EventArgs e) => Request("schedule changed");

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
        var marker = new GuideMarker(cfg);
        var index = new HistoryIndex(_store.LoadAll(), id => _libraryManager.GetItemById(id) is not null);
        var now = DateTime.UtcNow;

        var timers = (await _liveTvManager.GetTimers(new TimerQuery(), cancellationToken).ConfigureAwait(false)).Items.ToList();

        // 1. Duplicate skipping.
        var cancelled = new List<string>();
        var cancelledIds = new HashSet<string>(StringComparer.Ordinal);
        if (cfg.EnableDuplicateSkipping)
        {
            foreach (var timer in timers)
            {
                if (timer.Status != RecordingStatus.New || string.IsNullOrEmpty(timer.SeriesTimerId)
                    || timer.StartDate <= now.AddMinutes(1) || _store.HasCancelled(timer.Id))
                {
                    continue;
                }

                if (GetProgram(timer.ProgramId) is not { } program
                    || !index.Evaluate(EpisodeIdentity.FromProgram(program, marker)).InLibrary)
                {
                    continue;
                }

                var title = HistoryEvents.Describe(program.Name, marker.Strip(program.EpisodeTitle));
                await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
                _store.AddCancelled(timer.Id, title, "episode already in library");
                cancelledIds.Add(timer.Id);
                cancelled.Add(title + " @ " + timer.StartDate.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
                _logger.LogInformation("Cancelled series recording of {Title} at {Start:u}: episode already in library", title, timer.StartDate);
            }
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

            var desired = cfg.EnableGuideMarks && !scheduled ? marker.Compose(original, state) : original;
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

    private LiveTvProgram? GetProgram(string? id) =>
        Guid.TryParse(id, out var guid) ? _libraryManager.GetItemById(guid) as LiveTvProgram : null;

    /// <summary>What the history says about one guide entry (for the API / debugging).</summary>
    public (EpisodeIdentity Identity, HistoryState State)? Lookup(Guid programId)
    {
        if (_libraryManager.GetItemById(programId) is not LiveTvProgram program)
        {
            return null;
        }

        var marker = new GuideMarker(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        var identity = EpisodeIdentity.FromProgram(program, marker);
        var index = new HistoryIndex(_store.LoadAll(), id => _libraryManager.GetItemById(id) is not null);
        return (identity, index.Evaluate(identity));
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
