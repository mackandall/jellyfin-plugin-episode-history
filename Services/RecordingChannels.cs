using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// The channel each recording came from, for the recordings list's channel logo and number.
/// Jellyfin forgets it: the library item has no channel, and the DVR's timers.json keeps a
/// finished timer's file and channel only for a while. So it is noted here from the timers (and
/// the airings the repair keeps), by file name without extension, which survives a move to an
/// archive folder, a cut and a join; in &lt;data&gt;/episode-history/channels.json. Recordings
/// made before this was first run, or not by the DVR, have none.
/// </summary>
public sealed class RecordingChannels
{
    private static readonly TimeSpan ChannelsFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HarvestEvery = TimeSpan.FromSeconds(15);

    private readonly string _file;
    private readonly string _timersFile;
    private readonly ILibraryManager _libraryManager;
    private readonly Programmes _programmes;
    private readonly ILogger<RecordingChannels> _logger;
    private readonly object _lock = new();
    private Dictionary<string, string>? _byFile;
    private DateTime _timersWritten;
    private DateTime _harvestedAt;
    private Dictionary<string, Channel>? _channels;
    private DateTime _channelsAt;

    public RecordingChannels(IApplicationPaths applicationPaths, ILibraryManager libraryManager, Programmes programmes, ILogger<RecordingChannels> logger)
    {
        var directory = Path.Combine(applicationPaths.DataPath, "episode-history");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "channels.json");
        _timersFile = Path.Combine(applicationPaths.DataPath, "livetv", "timers.json");
        _libraryManager = libraryManager;
        _programmes = programmes;
        _logger = logger;
    }

    /// <summary>A Live TV channel: its item (for the logo, when it has one), name and number.</summary>
    public sealed record Channel(Guid Id, string Name, string? Number, bool HasLogo);

    private static string Key(string? path) => Path.GetFileNameWithoutExtension(path ?? string.Empty);

    /// <summary>The channel a recording file came from, or null when it isn't known.</summary>
    public Channel? For(string? path)
    {
        var key = Key(path);
        if (key.Length == 0)
        {
            return null;
        }

        lock (_lock)
        {
            Harvest();
            return Load().TryGetValue(key, out var externalId) && Channels().TryGetValue(externalId, out var channel) ? channel : null;
        }
    }

    // New timers and airings since the last look; cheap when nothing changed.
    private void Harvest()
    {
        if (DateTime.UtcNow - _harvestedAt < HarvestEvery)
        {
            return;
        }

        _harvestedAt = DateTime.UtcNow;
        var known = Load();
        var added = 0;
        try
        {
            var written = File.Exists(_timersFile) ? File.GetLastWriteTimeUtc(_timersFile) : DateTime.MinValue;
            if (written != _timersWritten)
            {
                _timersWritten = written;
                using var doc = JsonDocument.Parse(File.ReadAllText(_timersFile));
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (t.TryGetProperty("RecordingPath", out var p) && p.ValueKind == JsonValueKind.String
                        && t.TryGetProperty("ChannelId", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        added += Note(known, p.GetString(), c.GetString());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Couldn't read the DVR's timers for recording channels");
        }

        foreach (var a in _programmes.All())
        {
            foreach (var piece in a.Pieces)
            {
                added += Note(known, piece.Path, a.ChannelId);
            }

            added += Note(known, a.JoinedPath, a.ChannelId);
        }

        if (added > 0)
        {
            Save(known);
        }
    }

    private static int Note(Dictionary<string, string> known, string? path, string? channelId)
    {
        var key = Key(path);
        if (key.Length == 0 || string.IsNullOrEmpty(channelId) || known.ContainsKey(key))
        {
            return 0;
        }

        known[key] = channelId;
        return 1;
    }

    // Live TV's channels by the tuner's id ("hdhr_232"), which is what timers keep.
    private Dictionary<string, Channel> Channels()
    {
        if (_channels is not null && DateTime.UtcNow - _channelsAt < ChannelsFor)
        {
            return _channels;
        }

        var result = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.TvChannel } }).OfType<LiveTvChannel>())
        {
            if (!string.IsNullOrEmpty(c.ExternalId))
            {
                result.TryAdd(c.ExternalId, new Channel(c.Id, c.Name, c.Number, c.HasImage(ImageType.Primary)));
            }
        }

        _channelsAt = DateTime.UtcNow;
        return _channels = result;
    }

    private Dictionary<string, string> Load()
    {
        if (_byFile is not null)
        {
            return _byFile;
        }

        try
        {
            if (File.Exists(_file))
            {
                _byFile = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read {File}", _file);
        }

        return _byFile ??= new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> byFile)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(byFile, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't write {File}", _file);
        }
    }
}
