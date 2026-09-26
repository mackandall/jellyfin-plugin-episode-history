using System;
using System.Collections.Concurrent;
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
/// <item>A user marks something played / unplayed, or finishes playing it.</item>
/// <item>A recorded item is deleted (it turns from "in library" into "recorded before").</item>
/// </list>
/// </summary>
public sealed class HistoryEvents : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait after a recording stops for Jellyfin to finish adding it to the library.</summary>
    private static readonly TimeSpan LibraryWait = TimeSpan.FromMinutes(15);

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
        ILogger<HistoryEvents> logger)
    {
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
        /// <summary>When the DVR was first seen to have stopped writing it.</summary>
        public DateTime? EndedUtc { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved += OnItemRemoved;
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _poll = new Timer(_ => _ = PollAsync(), null, PollInterval, PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
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

        foreach (var dto in result.Items)
        {
            if (string.IsNullOrEmpty(dto.Path) || _pending.ContainsKey(dto.Path)
                || _recordingsManager.GetActiveRecordingInfo(dto.Path)?.Timer is not { } timer)
            {
                continue;
            }

            var marker = new GuideMarker(Config);
            var identity = EpisodeIdentity.FromTimer(timer, marker, null);
            if (identity.ValidProgramId is null && FindProgram(timer)?.ShowId is { } showId)
            {
                identity = identity with { ProgramId = showId };
            }

            var pending = new PendingRecording(
                dto.Id,
                identity,
                Describe(timer.Name, marker.Strip(timer.EpisodeTitle)),
                timer.StartDate,
                timer.EndDate,
                DateTime.UtcNow);
            if (_pending.TryAdd(dto.Path, pending))
            {
                _logger.LogInformation(
                    "Tracking recording {Title} (program id {ProgramId}) at {Path}",
                    pending.Title, pending.Identity.ValidProgramId ?? "none", dto.Path);
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
                    continue;
                }

                pending.EndedUtc ??= DateTime.UtcNow;
                try
                {
                    if (TryComplete(path, pending))
                    {
                        _pending.TryRemove(path, out _);
                    }
                }
                catch (Exception ex)
                {
                    _pending.TryRemove(path, out _);
                    _logger.LogWarning(ex, "Failed to record history for finished recording {Path}", path);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    /// <returns>False while still waiting for Jellyfin to (re)add the finished recording to the library.</returns>
    private bool TryComplete(string path, PendingRecording pending)
    {
        var item = _libraryManager.GetItemById(pending.ItemId) ?? _libraryManager.FindByPath(path, false);
        if (item is null || !File.Exists(path))
        {
            if (DateTime.UtcNow - pending.EndedUtc!.Value < LibraryWait && File.Exists(path))
            {
                return false;
            }

            _logger.LogInformation("Recording {Title} ended but its file/item is gone; not recorded in history", pending.Title);
            return true;
        }

        // Measured from when the poll first saw it (within 30 s of the DVR starting) to when
        // it stopped, against the programme's scheduled length without padding.
        var scheduled = pending.ProgramEnd - pending.ProgramStart;
        var ran = pending.EndedUtc!.Value - pending.FirstSeenUtc;
        var percent = scheduled > TimeSpan.Zero ? ran / scheduled * 100 : 100;
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

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is Video && _store.HasRecorded(e.Item.Id))
            {
                _sweeper.Request("recording deleted");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle removal of {Name}", e.Item?.Name);
        }
    }

    internal static string Describe(string? name, string? episodeTitle) =>
        string.IsNullOrEmpty(episodeTitle) ? name ?? "?" : name + " - " + episodeTitle;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
