using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// The airings of one movie or episode still to come in the guide (or on now): where and when,
/// whether the channel is commercial-free, whether a recording is already set for it, and how
/// many recordings already want a tuner then. For "Other airings" in the clients, and for
/// picking the airing a re-record uses.
/// </summary>
public sealed class Airings
{
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigurationManager _configuration;
    private readonly IServiceProvider _serviceProvider;

    public Airings(ILibraryManager libraryManager, IConfigurationManager configuration, IServiceProvider serviceProvider)
    {
        _libraryManager = libraryManager;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
    }

    /// <summary>One airing. <c>Busy</c>: other recordings set during it; <c>Tuners</c>: 0 when unlimited or unknown.</summary>
    public sealed record Airing(
        Guid ProgramId,
        string Name,
        string? EpisodeTitle,
        int? Season,
        int? Episode,
        int? Year,
        DateTime StartUtc,
        DateTime EndUtc,
        Guid ChannelId,
        string? ChannelName,
        string? ChannelNumber,
        bool HasLogo,
        bool CommercialFree,
        string? Recording,
        int Busy,
        int Tuners,
        List<string> Clashes)
    {
        /// <summary>Every tuner is spoken for then.</summary>
        public bool Clash => Tuners > 0 && Busy >= Tuners;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    /// <summary>
    /// Whether two identities are the same thing. As <see cref="EpisodeIdentity.SameEpisode"/>,
    /// but a movie matches on its title when one side has no year or the years are a year apart
    /// (guides and metadata disagree on release years, and some guides have none).
    /// </summary>
    public static bool Same(EpisodeIdentity a, EpisodeIdentity b)
    {
        if (a.SameEpisode(b))
        {
            return true;
        }

        if (!(a.IsMovie || b.IsMovie) || (a.ValidProgramId is { } x && b.ValidProgramId is { } y && x != y))
        {
            return false;
        }

        // A movie against a guide entry with episode details isn't the same thing.
        if ((!a.IsMovie && (a.Episode is > 0 || EpisodeIdentity.Normalize(a.EpisodeTitle).Length >= 3))
            || (!b.IsMovie && (b.Episode is > 0 || EpisodeIdentity.Normalize(b.EpisodeTitle).Length >= 3)))
        {
            return false;
        }

        var names = Names(a);
        return Names(b).Any(names.Contains) && (a.Year is null || b.Year is null || Math.Abs(a.Year.Value - b.Year.Value) <= 1);
    }

    private static HashSet<string> Names(EpisodeIdentity i) =>
        new[] { i.SeriesName, i.SeriesAlt }.Select(EpisodeIdentity.Normalize).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a channel is commercial-free, by the channel names in the settings (HBO, Starz…).</summary>
    public static bool IsCommercialFree(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        var name = " " + EpisodeIdentity.Normalize(channelName.Replace("+", " plus", StringComparison.Ordinal)) + " ";
        foreach (var raw in (Config.CommercialFreeChannels ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var word = EpisodeIdentity.Normalize(raw.Replace("+", " plus", StringComparison.Ordinal));
            if (word.Length > 0 && name.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A tuner source: the channel id prefix its channels have, and its tuners (0: unlimited).</summary>
    private sealed record Source(string Prefix, int Tuners);

    // HDHomeRun-type sources (BroadcastHub) name channels "hdhr_…"; an M3U source names them
    // "m3u_" + the MD5 of its address (as a Guid) + the channel's own hash.
    private List<Source> Sources()
    {
        try
        {
            var hosts = _configuration.GetConfiguration<LiveTvOptions>("livetv").TunerHosts ?? Array.Empty<TunerHostInfo>();
            var list = new List<Source>();
            var hdhr = hosts.Where(h => string.Equals(h.Type, "hdhomerun", StringComparison.OrdinalIgnoreCase)).ToList();
            if (hdhr.Count > 0)
            {
                list.Add(new Source("hdhr_", hdhr.Any(h => h.TunerCount <= 0) ? 0 : hdhr.Sum(h => h.TunerCount)));
            }

            foreach (var h in hosts.Where(h => string.Equals(h.Type, "m3u", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(h.Url)))
            {
                var guid = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.Unicode.GetBytes(h.Url)));
                list.Add(new Source("m3u_" + guid.ToString("N"), Math.Max(0, h.TunerCount)));
            }

            return list;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException)
        {
            return new List<Source>();
        }
    }

    private static Source? SourceOf(IReadOnlyList<Source> sources, string? externalId) =>
        externalId is null ? null : sources.FirstOrDefault(s => externalId.StartsWith(s.Prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The airings of <paramref name="identity"/> not over yet, soonest first.</summary>
    public async Task<List<Airing>> FindAsync(EpisodeIdentity identity, CancellationToken ct)
    {
        var marker = new GuideMarker(Config);
        var now = DateTime.UtcNow;
        var names = Names(identity);
        var programs = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
                MinEndDate = now
            })
            .OfType<LiveTvProgram>()
            .Where(p => (identity.ValidProgramId is { } id && string.Equals(p.ShowId, id, StringComparison.Ordinal))
                || names.Contains(EpisodeIdentity.Normalize(marker.Strip(p.Name) ?? p.Name)))
            .Where(p => Same(identity, EpisodeIdentity.FromProgram(p, marker)))
            .OrderBy(p => p.StartDate)
            .ToList();
        if (programs.Count == 0)
        {
            return new List<Airing>();
        }

        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items
            .Where(t => t.Status is RecordingStatus.New or RecordingStatus.InProgress)
            .ToList();
        var sources = Sources();
        var external = new Dictionary<Guid, string?>();
        string? ExternalOf(Guid channelId)
        {
            if (!external.TryGetValue(channelId, out var id))
            {
                external[channelId] = id = (_libraryManager.GetItemById(channelId) as LiveTvChannel)?.ExternalId;
            }

            return id;
        }

        var result = new List<Airing>();
        foreach (var p in programs)
        {
            var start = p.StartDate;
            if (p.EndDate is not { } end)
            {
                continue;
            }

            var programId = p.Id.ToString("N");
            var own = timers.FirstOrDefault(t => string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase));
            var channel = _libraryManager.GetItemById(p.ChannelId) as LiveTvChannel;
            // Only recordings on the same source's tuners count against it.
            var source = SourceOf(sources, channel?.ExternalId);
            var tuners = source?.Tuners ?? 0;
            var others = timers
                .Where(t => t != own
                    && t.StartDate.AddSeconds(-t.PrePaddingSeconds) < end
                    && t.EndDate.AddSeconds(t.PostPaddingSeconds) > start
                    && (source is null || SourceOf(sources, ExternalOf(t.ChannelId)) == source))
                .ToList();
            result.Add(new Airing(
                p.Id,
                marker.Strip(p.Name) ?? p.Name,
                marker.Strip(p.EpisodeTitle),
                p.ParentIndexNumber,
                p.IndexNumber,
                p.ProductionYear,
                start,
                end,
                p.ChannelId,
                channel?.Name,
                channel?.Number,
                channel?.HasImage(MediaBrowser.Model.Entities.ImageType.Primary) == true,
                IsCommercialFree(channel?.Name),
                own is null ? null : string.IsNullOrEmpty(own.SeriesTimerId) ? "recording" : "series",
                others.Count,
                tuners,
                others.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(3).ToList()));
        }

        return result;
    }

    /// <summary>
    /// The airing a re-record should use: one still to start with no recording set, a free
    /// tuner before a clash, commercial-free before not, then the soonest.
    /// </summary>
    public static Airing? Best(IEnumerable<Airing> airings, DateTime now, Func<Airing, bool>? allowed = null) =>
        airings
            .Where(a => a.StartUtc > now && (allowed is null || allowed(a)))
            .OrderBy(a => a.Clash ? 1 : 0)
            .ThenBy(a => a.CommercialFree ? 0 : 1)
            .ThenBy(a => a.StartUtc)
            .FirstOrDefault();
}
