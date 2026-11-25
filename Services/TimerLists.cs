using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>The recordings to come and the ones under way, soonest first.</summary>
    public static async Task<List<TimerInfoDto>> LiveAsync(ILiveTvManager liveTv, CancellationToken ct)
    {
        var scheduled = (await liveTv.GetTimers(new TimerQuery { IsScheduled = true }, ct).ConfigureAwait(false)).Items;
        var running = (await liveTv.GetTimers(new TimerQuery { IsActive = true }, ct).ConfigureAwait(false)).Items;
        return scheduled.Concat(running).OrderBy(t => t.StartDate).ToList();
    }

    /// <summary>The recordings under way.</summary>
    public static async Task<IReadOnlyList<TimerInfoDto>> RunningAsync(ILiveTvManager liveTv, CancellationToken ct) =>
        (await liveTv.GetTimers(new TimerQuery { IsActive = true }, ct).ConfigureAwait(false)).Items;

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
