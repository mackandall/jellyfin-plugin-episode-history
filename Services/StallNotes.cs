using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Recordings whose tuner went quiet while they were being made: part of the programme is missing
/// from the file. Kept so the recordings lists can warn, until someone decides to keep the
/// recording as it is. By file name (without extension), which survives a move to an archive
/// folder and a cut; in &lt;data&gt;/episode-history/stalls.json.
/// </summary>
public sealed class StallNotes
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    private readonly string _file;
    private readonly ILogger<StallNotes> _logger;
    private readonly object _lock = new();
    private Dictionary<string, Note>? _notes;

    public StallNotes(IApplicationPaths applicationPaths, ILogger<StallNotes> logger)
    {
        var directory = Path.Combine(applicationPaths.DataPath, "episode-history");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "stalls.json");
        _logger = logger;
    }

    /// <summary>When the data first stopped, and how much of the programme is missing and present.</summary>
    public sealed record Note(DateTime FirstStallUtc, int LostMinutes, int RecordedMinutes, int ScheduledMinutes, DateTime NotedUtc)
    {
        /// <summary>For a card's chip.</summary>
        public string Short => "Stalled · " + LostMinutes.ToString(CultureInfo.InvariantCulture) + " min lost";

        /// <summary>The whole story, in server time.</summary>
        public string Text => string.Format(
            CultureInfo.InvariantCulture,
            "The tuner stopped sending at {0:h:mm tt} on {0:MMM d}: about {1} min of the programme were not recorded ({2} of {3} min are in the file).",
            FirstStallUtc.ToLocalTime(),
            LostMinutes,
            RecordedMinutes,
            ScheduledMinutes);
    }

    private static string Key(string? path) => Path.GetFileNameWithoutExtension(path ?? string.Empty);

    public Note? For(string? path)
    {
        var key = Key(path);
        lock (_lock)
        {
            return key.Length > 0 && Load().TryGetValue(key, out var note) ? note : null;
        }
    }

    public void Set(string path, Note note)
    {
        lock (_lock)
        {
            var notes = Load();
            foreach (var old in notes.Where(kv => DateTime.UtcNow - kv.Value.NotedUtc > KeepFor).Select(kv => kv.Key).ToList())
            {
                notes.Remove(old);
            }

            notes[Key(path)] = note;
            Save(notes);
        }
    }

    public bool Remove(string? path)
    {
        lock (_lock)
        {
            var notes = Load();
            if (!notes.Remove(Key(path)))
            {
                return false;
            }

            Save(notes);
            return true;
        }
    }

    private Dictionary<string, Note> Load()
    {
        if (_notes is not null)
        {
            return _notes;
        }

        try
        {
            if (File.Exists(_file))
            {
                _notes = JsonSerializer.Deserialize<Dictionary<string, Note>>(File.ReadAllText(_file));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read {File}", _file);
        }

        return _notes ??= new Dictionary<string, Note>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, Note> notes)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't write {File}", _file);
        }
    }
}
