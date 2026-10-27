using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Which upcoming airings a JellyDVR Schedule favourite scheduled, for favourites set to skip
/// episodes in the library. Those are one-off timers, so the sweep would otherwise leave them
/// alone, and the favourite's own check only reads the guide marks, which a guide refresh wipes
/// until the next sweep (2026-10-25: Get Jiro! episodes already in the library scheduled again).
/// Read from Schedule's <c>&lt;data&gt;/jellydvr-schedule/favourites.json</c> (program ids it
/// created, with their rule) and its settings file (each rule's SkipInLibrary). No code
/// dependency: without that plugin the files just aren't there.
/// </summary>
public sealed class ScheduleFavourites
{
    private readonly string _createdFile;
    private readonly string _settingsFile;
    private readonly ILogger<ScheduleFavourites> _logger;
    private readonly object _lock = new();
    private (DateTime Created, DateTime Settings) _loadedStamps = (DateTime.MinValue, DateTime.MinValue);
    private HashSet<Guid> _programs = new();

    public ScheduleFavourites(IApplicationPaths paths, ILogger<ScheduleFavourites> logger)
    {
        _logger = logger;
        _createdFile = Path.Combine(paths.DataPath, "jellydvr-schedule", "favourites.json");
        _settingsFile = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.DvrSchedule.xml");
    }

    /// <summary>Guide programme ids a favourite with "skip episodes in the library" scheduled.</summary>
    public IReadOnlySet<Guid> SkipInLibraryPrograms()
    {
        lock (_lock)
        {
            try
            {
                var stamps = (Stamp(_createdFile), Stamp(_settingsFile));
                if (stamps == _loadedStamps)
                {
                    return _programs;
                }

                var programs = new HashSet<Guid>();
                if (stamps.Item1 != DateTime.MinValue && stamps.Item2 != DateTime.MinValue)
                {
                    var rules = SkippingRules();
                    using var doc = JsonDocument.Parse(File.ReadAllText(_createdFile));
                    foreach (var c in doc.RootElement.EnumerateArray())
                    {
                        if (c.TryGetProperty("ProgramId", out var id) && id.TryGetGuid(out var programId)
                            && c.TryGetProperty("RuleId", out var rule) && rule.ValueKind == JsonValueKind.String
                            && rules.Contains(rule.GetString()!))
                        {
                            programs.Add(programId);
                        }
                    }
                }

                _programs = programs;
                _loadedStamps = stamps;
            }
            catch (Exception ex) when (ex is IOException or JsonException or XmlException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Can't read JellyDVR Schedule's favourites");
            }

            return _programs;
        }
    }

    // Rule ids with SkipInLibrary on (Schedule's default when the setting isn't written).
    private HashSet<string> SkippingRules() =>
        XDocument.Load(_settingsFile).Root?.Element("Favourites")?.Elements("FavouriteRule")
            .Where(r => !string.Equals(r.Element("SkipInLibrary")?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Element("Id")?.Value.Trim())
            .OfType<string>()
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    private static DateTime Stamp(string file) => File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
}
