using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Reads what JellyDVR Schedule did to airings it took off the schedule (skipped for a tuner
/// conflict, moved to another airing, dropped as a duplicate) from its shared
/// <c>&lt;data&gt;/jellydvr-schedule/outcomes.json</c>, so the guide can mark them. No code
/// dependency: without that plugin the file just isn't there. Raises <see cref="Changed"/> when
/// the file changes, so the guide follows within seconds.
/// </summary>
public sealed class ScheduleOutcomes : IDisposable
{
    public const string KindSkipped = "skipped";

    private readonly string _dir;
    private readonly string _file;
    private readonly ILogger<ScheduleOutcomes> _logger;
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;
    private DateTime _loadedStamp = DateTime.MinValue;
    private Dictionary<Guid, string> _kinds = new();

    public ScheduleOutcomes(IApplicationPaths paths, ILogger<ScheduleOutcomes> logger)
    {
        _logger = logger;
        _dir = Path.Combine(paths.DataPath, "jellydvr-schedule");
        _file = Path.Combine(_dir, "outcomes.json");
    }

    public event EventHandler? Changed;

    /// <summary>Starts watching the file (its folder is created by JellyDVR Schedule; retried on later calls).</summary>
    public void Watch()
    {
        if (_watcher is not null || !Directory.Exists(_dir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_dir, "outcomes.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Can't watch {File}", _file);
            _watcher = null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Outcome kind ("skipped", "moved", "duplicate") per guide programme id.</summary>
    public IReadOnlyDictionary<Guid, string> Load()
    {
        Watch();
        lock (_lock)
        {
            try
            {
                var stamp = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : DateTime.MinValue;
                if (stamp == _loadedStamp)
                {
                    return _kinds;
                }

                var kinds = new Dictionary<Guid, string>();
                if (stamp != DateTime.MinValue)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_file));
                    var now = DateTime.UtcNow;
                    foreach (var o in doc.RootElement.EnumerateArray())
                    {
                        if (o.TryGetProperty("ProgramId", out var id) && id.TryGetGuid(out var programId)
                            && o.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.String
                            && !(o.TryGetProperty("End", out var end) && end.TryGetDateTime(out var e) && e.ToUniversalTime() < now))
                        {
                            kinds[programId] = kind.GetString()!;
                        }
                    }
                }

                _kinds = kinds;
                _loadedStamp = stamp;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Can't read {File}", _file);
            }

            return _kinds;
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
