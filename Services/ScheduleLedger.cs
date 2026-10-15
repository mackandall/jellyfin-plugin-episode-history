using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// What was due to record, and what came of it. Everything else in the repair starts from a
/// file, but a recording that fails outright leaves none: Jellyfin retries a failed start every
/// minute until the programme ends (at most 10 times) and then deletes the timer, and a timer
/// whose programme ended while the server was off is deleted at startup. Neither raises the
/// "timer cancelled" event that a cancel (by someone, JellyDVR Schedule, or the guide sweep) does.
/// So each timer still waiting as its start comes near is noted here, and once it is gone or its
/// programme is over it is decided:
/// <list type="bullet">
/// <item>recorded: it ran (the repair looks after a short one), or Jellyfin marked it Completed;</item>
/// <item>cancelled: the cancel event, Jellyfin's "Cancelled" (a series episode skipped), or gone
/// before it was due;</item>
/// <item>failed: gone after its start with nothing recorded, or still waiting when the programme ended.</item>
/// </list>
/// Timers made after their programme started (live TV buffers, "record" on what is on now) aren't
/// noted. In &lt;data&gt;/episode-history/ledger.json, so a failure while the server was off counts.
/// </summary>
public sealed class ScheduleLedger
{
    public const string Recorded = "recorded";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    // Note a timer this long before it is due (and keep it noted across a restart in between).
    private static readonly TimeSpan NoteAhead = TimeSpan.FromHours(26);

    // Gone from the timers for this long (two polls) before it counts: a cancel deletes the timer
    // a moment before its event is raised.
    private static readonly TimeSpan GoneFor = TimeSpan.FromSeconds(50);

    // Still waiting this long after the programme (and its padding) ended.
    private static readonly TimeSpan OverBy = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan KeepDecided = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions TimerJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _file;
    private readonly string _timersFile;
    private readonly ILibraryManager _libraryManager;
    private readonly RecordingChannels _channels;
    private readonly ILogger<ScheduleLedger> _logger;
    private readonly object _lock = new();
    private readonly DateTime _startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private Dictionary<string, Entry>? _all;

    public ScheduleLedger(IApplicationPaths applicationPaths, ILibraryManager libraryManager, RecordingChannels channels, ILogger<ScheduleLedger> logger)
    {
        var directory = Path.Combine(applicationPaths.DataPath, "episode-history");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "ledger.json");
        _timersFile = Path.Combine(applicationPaths.DataPath, "livetv", "timers.json");
        _libraryManager = libraryManager;
        _channels = channels;
        _logger = logger;
    }

    /// <summary>One timer that was due to record.</summary>
    public sealed class Entry
    {
        /// <summary>The DVR's id of the timer.</summary>
        public string TimerId { get; set; } = string.Empty;

        /// <summary>The airing (see <see cref="Programmes.KeyFor"/>).</summary>
        public string Key { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public EpisodeIdentity? Identity { get; set; }

        /// <summary>The tuner's channel id ("hdhr_232").</summary>
        public string? ChannelId { get; set; }

        public string? ChannelName { get; set; }

        public string? ChannelNumber { get; set; }

        /// <summary>The programme's start and end (without padding).</summary>
        public DateTime StartUtc { get; set; }

        public DateTime EndUtc { get; set; }

        /// <summary>When the timer was due to fire (start less pre-padding).</summary>
        public DateTime DueUtc { get; set; }

        /// <summary>When the recording was due to stop (end plus post-padding).</summary>
        public DateTime StopUtc { get; set; }

        public DateTime NotedUtc { get; set; }

        /// <summary>Jellyfin's tries at starting it again after a failed start.</summary>
        public int Retries { get; set; }

        /// <summary>Seen recording.</summary>
        public bool Ran { get; set; }

        /// <summary>The "timer cancelled" event was seen for it.</summary>
        public bool CancelEvent { get; set; }

        /// <summary>First seen missing from the timers.</summary>
        public DateTime? GoneUtc { get; set; }

        /// <summary><see cref="Recorded"/>, <see cref="Cancelled"/>, <see cref="Failed"/>, or empty while undecided.</summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>Why it failed, for people.</summary>
        public string? Reason { get; set; }

        public DateTime? DecidedUtc { get; set; }
    }

    /// <summary>The "timer cancelled" event (DVR id).</summary>
    public void NoteCancelled(string timerId) =>
        Change(all =>
        {
            if (all.TryGetValue(timerId, out var e) && e.Outcome.Length == 0)
            {
                e.CancelEvent = true;
            }
        });

    /// <summary>Failures of the last 30 days, newest first.</summary>
    public List<Entry> Failures()
    {
        lock (_lock)
        {
            return Load().Values.Where(e => e.Outcome == Failed).OrderByDescending(e => e.StartUtc).ToList();
        }
    }

    /// <summary>
    /// Notes timers coming due and decides the ones that are over. <paramref name="recorded"/>:
    /// whether a recording of an airing (by key) was seen. Returns the ones that just failed.
    /// </summary>
    public List<Entry> Process(Func<string, bool> recorded, GuideMarker marker)
    {
        var timers = ReadTimers();
        if (timers is null)
        {
            return new List<Entry>();
        }

        var now = DateTime.UtcNow;
        var byId = timers.Where(t => !string.IsNullOrEmpty(t.Id)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var failed = new List<Entry>();
        var fresh = new List<TimerInfo>();
        lock (_lock)
        {
            var known = Load();
            fresh.AddRange(byId.Values.Where(t => !known.ContainsKey(t.Id)
                && t.Status == RecordingStatus.New
                && t.RetryCount == 0
                && Due(t) - now is var ahead && ahead < NoteAhead && ahead > -TimeSpan.FromSeconds(60)
                && t.EndDate > now));
        }

        // Outside the lock: the guide lookup for the identity takes a moment.
        var noted = fresh.Select(t => New(t, marker, now)).ToList();

        Change(all =>
        {
            foreach (var e in noted)
            {
                all.TryAdd(e.TimerId, e);
            }

            foreach (var e in all.Values.Where(e => e.Outcome.Length == 0))
            {
                if (byId.TryGetValue(e.TimerId, out var t))
                {
                    e.GoneUtc = null;
                    e.Retries = Math.Max(e.Retries, t.RetryCount);
                    if (t.Status == RecordingStatus.InProgress)
                    {
                        e.Ran = true;
                    }
                    else if (t.Status == RecordingStatus.New && !e.Ran && e.Retries == 0 && now < e.DueUtc)
                    {
                        // Not due yet: follow guide changes (a retry moves the start, so not after one).
                        e.Key = Programmes.KeyFor(t.ChannelId, t.ProgramId, t.EndDate);
                        e.StartUtc = t.StartDate;
                        e.EndUtc = t.EndDate;
                        e.DueUtc = Due(t);
                        e.StopUtc = Stop(t);
                    }

                    if (t.Status == RecordingStatus.Cancelled)
                    {
                        Decide(e, Cancelled, null, now);
                    }
                    else if (t.Status == RecordingStatus.Completed)
                    {
                        Decide(e, Recorded, null, now);
                    }
                    else if (t.Status != RecordingStatus.InProgress && now > e.StopUtc + OverBy)
                    {
                        if (e.Ran || recorded(e.Key))
                        {
                            Decide(e, Recorded, null, now);
                        }
                        else
                        {
                            Decide(e, Failed, e.Retries > 0
                                ? $"Jellyfin couldn't start it ({e.Retries} tries)."
                                : "It never started: Jellyfin was waiting for a tuner that didn't answer.", now);
                            failed.Add(e);
                        }
                    }

                    continue;
                }

                e.GoneUtc ??= now;
                if (now - e.GoneUtc < GoneFor)
                {
                    continue;
                }

                if (e.CancelEvent)
                {
                    Decide(e, Cancelled, null, now);
                }
                else if (e.Ran || recorded(e.Key))
                {
                    Decide(e, Recorded, null, now);
                }
                else if (e.GoneUtc < e.DueUtc)
                {
                    Decide(e, Cancelled, null, now); // taken off the schedule before it was due
                }
                else
                {
                    Decide(e, Failed, e.Retries > 0
                        ? $"Jellyfin couldn't start it ({e.Retries + 1} tries) and gave up."
                        : _startedUtc > e.DueUtc ? "The server was off when it was due."
                        : "Jellyfin couldn't start it and gave up.", now);
                    failed.Add(e);
                }
            }

            foreach (var old in all.Values.Where(e => (e.DecidedUtc is { } d && now - d > KeepDecided)
                    || (e.Outcome.Length == 0 && now - e.StopUtc > TimeSpan.FromDays(2)))
                .Select(e => e.TimerId).ToList())
            {
                all.Remove(old);
            }
        });

        foreach (var e in failed)
        {
            _logger.LogWarning("{Title} ({Start:u}) didn't record: {Reason}", e.Title, e.StartUtc, e.Reason);
        }

        return failed;
    }

    private static void Decide(Entry e, string outcome, string? reason, DateTime now)
    {
        e.Outcome = outcome;
        e.Reason = reason;
        e.DecidedUtc = now;
    }

    private static DateTime Due(TimerInfo t) => t.StartDate.AddSeconds(-Math.Max(0, t.PrePaddingSeconds));

    private static DateTime Stop(TimerInfo t) => t.EndDate.AddSeconds(Math.Max(0, t.PostPaddingSeconds));

    private Entry New(TimerInfo t, GuideMarker marker, DateTime now)
    {
        var identity = EpisodeIdentity.FromTimer(t, marker, null);
        try
        {
            if (identity.ValidProgramId is null && FindProgram(t)?.ShowId is { } showId)
            {
                identity = identity with { ProgramId = showId };
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Couldn't look up the guide entry of {Title}", t.Name);
        }

        var channel = _channels.ByExternalId(t.ChannelId);
        return new Entry
        {
            TimerId = t.Id,
            Key = Programmes.KeyFor(t.ChannelId, t.ProgramId, t.EndDate),
            Title = HistoryEvents.Describe(t.Name, marker.Strip(t.EpisodeTitle)),
            Identity = identity,
            ChannelId = t.ChannelId,
            ChannelName = channel?.Name,
            ChannelNumber = channel?.Number,
            StartUtc = t.StartDate,
            EndUtc = t.EndDate,
            DueUtc = Due(t),
            StopUtc = Stop(t),
            NotedUtc = now
        };
    }

    // The guide entry a timer was made from (one-off timers carry no ShowId); see HistoryEvents.
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

    // Jellyfin's timers with their DVR ids, status and retry count (the API gives other ids and
    // no retries). Null when the file can't be read just now (Jellyfin writing it).
    private List<TimerInfo>? ReadTimers()
    {
        try
        {
            if (!File.Exists(_timersFile))
            {
                return new List<TimerInfo>();
            }

            return JsonSerializer.Deserialize<List<TimerInfo>>(File.ReadAllText(_timersFile), TimerJson) ?? new List<TimerInfo>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Can't read Jellyfin's timers.json");
            return null;
        }
    }

    private void Change(Action<Dictionary<string, Entry>> change)
    {
        lock (_lock)
        {
            var all = Load();
            var before = JsonSerializer.Serialize(all);
            change(all);
            if (JsonSerializer.Serialize(all) != before)
            {
                Save(all);
            }
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_all is not null)
        {
            return _all;
        }

        try
        {
            if (File.Exists(_file))
            {
                _all = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read {File}", _file);
        }

        return _all ??= new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, Entry> all)
    {
        try
        {
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't write {File}", _file);
        }
    }
}
