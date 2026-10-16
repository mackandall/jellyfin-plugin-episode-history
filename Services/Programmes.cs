using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// What the DVR recorded of each airing: its pieces (one file, or several when the recording
/// was interrupted and Jellyfin went on in "Name - 1.ts"), how much of the programme they hold,
/// whether they were joined, and where a recording that came out incomplete stands with being
/// recorded again. In &lt;data&gt;/episode-history/programmes.json, so it survives the restarts
/// that split recordings in the first place.
/// </summary>
public sealed class Programmes
{
    /// <summary>Nothing more to do (complete, or nothing decided yet).</summary>
    public const string None = "";

    /// <summary>Incomplete; the next airing found in the guide will be recorded.</summary>
    public const string Waiting = "waiting";

    /// <summary>Incomplete; an airing is set to record.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>A full copy was recorded since.</summary>
    public const string Replaced = "replaced";

    /// <summary>No full copy came of it (attempts used up, or no airing in time).</summary>
    public const string GaveUp = "gaveup";

    /// <summary>Someone chose to keep it as it is.</summary>
    public const string Kept = "kept";

    /// <summary>Stopped on purpose before the end: not recorded again by itself.</summary>
    public const string Stopped = "stopped";

    /// <summary>An incomplete try at replacing another incomplete recording.</summary>
    public const string Attempt = "attempt";

    /// <summary>Recorded in full.</summary>
    public const string Complete = "complete";

    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);

    private readonly string _file;
    private readonly ILogger<Programmes> _logger;
    private readonly object _lock = new();
    private Dictionary<string, Programme>? _all;

    // File name (without extension) -> the newest airing it belongs to; rebuilt after a change.
    private Dictionary<string, Programme>? _byFile;

    public Programmes(IApplicationPaths applicationPaths, ILogger<Programmes> logger)
    {
        var directory = Path.Combine(applicationPaths.DataPath, "episode-history");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "programmes.json");
        _logger = logger;
    }

    /// <summary>One file the DVR wrote for an airing.</summary>
    public sealed class Piece
    {
        public string Path { get; set; } = string.Empty;

        public DateTime FirstSeenUtc { get; set; }

        public DateTime? EndedUtc { get; set; }

        /// <summary>Seconds of the programme in the file (time it ran, less time without data).</summary>
        public double CapturedSeconds { get; set; }

        public double StalledSeconds { get; set; }

        public DateTime? FirstStallUtc { get; set; }

        /// <summary>Closed by a server restart, not seen ending.</summary>
        public bool ClosedAtStartup { get; set; }

        /// <summary>Someone pressed Stop recording.</summary>
        public bool StoppedByUser { get; set; }

        /// <summary>Its timer was cancelled (it didn't end by itself or fail).</summary>
        public bool Cancelled { get; set; }
    }

    /// <summary>One airing the DVR recorded, in one or more pieces.</summary>
    public sealed class Programme
    {
        public string Key { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public EpisodeIdentity? Identity { get; set; }

        /// <summary>The tuner's channel id (as timers have it).</summary>
        public string? ChannelId { get; set; }

        public DateTime ProgramStartUtc { get; set; }

        public DateTime ProgramEndUtc { get; set; }

        public DateTime CreatedUtc { get; set; }

        public List<Piece> Pieces { get; set; } = new();

        /// <summary>Started as a live TV buffer.</summary>
        public bool IsBuffer { get; set; }

        /// <summary>In the history as recorded.</summary>
        public bool Counted { get; set; }

        /// <summary>The one file the pieces were joined into.</summary>
        public string? JoinedPath { get; set; }

        public DateTime? JoinedUtc { get; set; }

        public int JoinTries { get; set; }

        public string? JoinNote { get; set; }

        /// <summary>The joined file's history row was written.</summary>
        public bool JoinCounted { get; set; }

        public string State { get; set; } = None;

        public DateTime? FlaggedUtc { get; set; }

        /// <summary>Airings scheduled so far to replace it.</summary>
        public int Attempts { get; set; }

        /// <summary>The guide entry (library id) set to record in its place.</summary>
        public string? NextProgramId { get; set; }

        public DateTime? NextStartUtc { get; set; }

        public string? NextChannel { get; set; }

        /// <summary>That recording was scheduled by this plugin (not one that was already set).</summary>
        public bool NextIsOurs { get; set; }

        /// <summary>Someone asked for it to be recorded again ("Re-record…"), not the completeness check.</summary>
        public bool Manual { get; set; }

        /// <summary>What was wrong with it, as given with the request.</summary>
        public string? Reason { get; set; }

        /// <summary>The library item it was asked for on (its file may have moved to an archive since).</summary>
        public Guid? ItemId { get; set; }

        /// <summary>Its files once a new recording replaced them: renamed out of the library, deleted after the keep days.</summary>
        public List<ReplacedFile> ReplacedFiles { get; set; } = new();

        public double ScheduledSeconds => Math.Max(0, (ProgramEndUtc - ProgramStartUtc).TotalSeconds);

        public double CapturedSeconds => Pieces.Sum(p => p.CapturedSeconds);

        public double Percent => ScheduledSeconds > 0 ? CapturedSeconds / ScheduledSeconds * 100 : 100;

        public DateTime? LastEndedUtc => Pieces.Count > 0 && Pieces.All(p => p.EndedUtc is not null) ? Pieces.Max(p => p.EndedUtc) : null;

        /// <summary>Minutes of the programme the pieces don't hold, between the first one starting and the last ending.</summary>
        public double MissingSeconds =>
            LastEndedUtc is { } end && Pieces.Count > 0 ? Math.Max(0, (end - Pieces.Min(p => p.FirstSeenUtc)).TotalSeconds - CapturedSeconds) : 0;

        /// <summary>The files it is in now: the joined one, or the pieces still there.</summary>
        public List<string> Files() =>
            JoinedPath is { } joined
                ? (File.Exists(joined) ? new List<string> { joined } : new List<string>())
                : Pieces.OrderBy(p => p.FirstSeenUtc).Select(p => p.Path).Where(File.Exists).ToList();
    }

    /// <summary>A replaced recording's file, kept under <see cref="RenamedPath"/> until it is deleted.</summary>
    public sealed class ReplacedFile
    {
        public string Path { get; set; } = string.Empty;

        public DateTime ReplacedUtc { get; set; }

        /// <summary>Gone for good (deleted after the keep days).</summary>
        public bool Deleted { get; set; }

        public string RenamedPath => Path + ".replaced";
    }

    /// <summary>The airing a timer records: its guide entry on its channel (its end time when it has none).</summary>
    public static string KeyFor(string? channelId, string? programId, DateTime endUtc) =>
        (channelId ?? "?") + "|" + (string.IsNullOrEmpty(programId) ? "end" + endUtc.Ticks : programId);

    /// <summary>Runs <paramref name="change"/> on the list and saves it.</summary>
    public void Change(Action<Dictionary<string, Programme>> change)
    {
        lock (_lock)
        {
            var all = Load();
            change(all);
            // Not one still holding a replaced file to delete, or a re-record still under way.
            foreach (var old in all.Where(kv => DateTime.UtcNow - kv.Value.CreatedUtc > KeepFor
                    && !kv.Value.ReplacedFiles.Any(f => !f.Deleted)
                    && kv.Value.State is not (Waiting or Scheduled))
                .Select(kv => kv.Key).ToList())
            {
                all.Remove(old);
            }

            _byFile = null;
            Save(all);
        }
    }

    public List<Programme> All()
    {
        lock (_lock)
        {
            return Load().Values.ToList();
        }
    }

    /// <summary>The airing a file belongs to, by file name (which survives a move to an archive folder).</summary>
    public Programme? ForFile(string? path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? string.Empty);
        if (name.Length == 0)
        {
            return null;
        }

        lock (_lock)
        {
            if (_byFile is null)
            {
                _byFile = new Dictionary<string, Programme>(StringComparer.Ordinal);
                foreach (var p in Load().Values.OrderBy(p => p.CreatedUtc))
                {
                    foreach (var file in p.Pieces.Select(x => x.Path).Append(p.JoinedPath).OfType<string>())
                    {
                        _byFile[Path.GetFileNameWithoutExtension(file)] = p;
                    }
                }
            }

            return _byFile.GetValueOrDefault(name);
        }
    }

    private Dictionary<string, Programme> Load()
    {
        if (_all is not null)
        {
            return _all;
        }

        try
        {
            if (File.Exists(_file))
            {
                _all = JsonSerializer.Deserialize<Dictionary<string, Programme>>(File.ReadAllText(_file));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read {File}", _file);
        }

        return _all ??= new Dictionary<string, Programme>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, Programme> all)
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
