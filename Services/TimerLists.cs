using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Jellyfin's timers, read without the cost of the ones not wanted. Jellyfin keeps every cancelled
/// series episode as a timer (hundreds, so the series doesn't schedule them again), and a plain
/// GetTimers looks up the programme and channel of each one; its status filters run before those
/// look-ups.
/// </summary>
internal static class TimerLists
{
    /// <summary>
    /// The recordings to come and the ones under way, soonest first, read straight from the DVR
    /// services. LiveTvManager.GetTimers builds a full programme DTO (all fields, series images)
    /// for every timer, about 40 ms each; only the timer's own fields and channel name are used here.
    /// </summary>
    public static Task<List<TimerInfoDto>> LiveAsync(ILiveTvManager liveTv, ILibraryManager library, CancellationToken ct) =>
        ReadAsync(liveTv, library, s => s is RecordingStatus.New or RecordingStatus.InProgress, ct);

    /// <summary>The recordings under way.</summary>
    public static Task<List<TimerInfoDto>> RunningAsync(ILiveTvManager liveTv, ILibraryManager library, CancellationToken ct) =>
        ReadAsync(liveTv, library, s => s == RecordingStatus.InProgress, ct);

    private static async Task<List<TimerInfoDto>> ReadAsync(ILiveTvManager liveTv, ILibraryManager library, Func<RecordingStatus, bool> wanted, CancellationToken ct)
    {
        var list = new List<TimerInfoDto>();
        var channels = new Dictionary<Guid, string?>();
        foreach (var service in liveTv.Services)
        {
            IEnumerable<TimerInfo> raw;
            try
            {
                raw = await service.GetTimersAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // as GetTimers: a failing service is left out
            }

            foreach (var t in raw.Where(t => wanted(t.Status)))
            {
                list.Add(Light(library, service.Name, t, channels));
            }
        }

        return list.OrderBy(t => t.StartDate).ToList();
    }

    // The fields of LiveTvDtoService.GetTimerInfoDto, without ProgramInfo.
    private static TimerInfoDto Light(ILibraryManager library, string serviceName, TimerInfo info, Dictionary<Guid, string?> channels)
    {
        var channelId = library.GetNewItemId((serviceName + info.ChannelId + "4").ToLowerInvariant(), typeof(LiveTvChannel));
        if (!channels.TryGetValue(channelId, out var channelName))
        {
            channelName = channels[channelId] = string.IsNullOrEmpty(info.ChannelId) ? null : library.GetItemById(channelId)?.Name;
        }

        return new TimerInfoDto
        {
            Id = InternalTimerId("Emby", info.Id),
            ExternalId = info.Id,
            Name = info.Name,
            Overview = info.Overview,
            StartDate = info.StartDate,
            EndDate = info.EndDate,
            Status = info.Status,
            ChannelId = channelId,
            ExternalChannelId = info.ChannelId,
            ChannelName = channelName,
            SeriesTimerId = string.IsNullOrEmpty(info.SeriesTimerId) ? null : InternalTimerId("Emby", info.SeriesTimerId),
            ExternalSeriesTimerId = info.SeriesTimerId,
            ProgramId = string.IsNullOrEmpty(info.ProgramId) ? null
                : library.GetNewItemId(("Emby" + info.ProgramId + "4").ToLowerInvariant(), typeof(LiveTvProgram)).ToString("N"),
            ExternalProgramId = info.ProgramId,
            PrePaddingSeconds = info.PrePaddingSeconds,
            PostPaddingSeconds = info.PostPaddingSeconds,
            IsPrePaddingRequired = info.IsPrePaddingRequired,
            IsPostPaddingRequired = info.IsPostPaddingRequired,
            KeepUntil = info.KeepUntil,
            Priority = info.Priority,
            ServiceName = serviceName,
            RunTimeTicks = (info.EndDate - info.StartDate).Ticks,
        };
    }

    /// <summary>
    /// The API ids of cancelled series episodes still to come, read from the services themselves
    /// (no look-ups). [ServiceName + external id], as Jellyfin's LiveTvDtoService.GetInternalTimerId.
    /// </summary>
    public static async Task<List<string>> CancelledSeriesEpisodeIdsAsync(ILiveTvManager liveTv, DateTime afterUtc, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var service in liveTv.Services)
        {
            var raw = await service.GetTimersAsync(ct).ConfigureAwait(false);
            ids.AddRange(raw
                .Where(t => t.Status == RecordingStatus.Cancelled && !string.IsNullOrEmpty(t.SeriesTimerId) && t.StartDate > afterUtc)
                .Select(t => InternalTimerId(service.Name, t.Id)));
        }

        return ids;
    }

    /// <summary>The API id Jellyfin gives a service's timer.</summary>
    public static string InternalTimerId(string serviceName, string externalId) =>
        new Guid(MD5.HashData(Encoding.Unicode.GetBytes((serviceName + externalId + "4").ToLowerInvariant()))).ToString("N");
}
