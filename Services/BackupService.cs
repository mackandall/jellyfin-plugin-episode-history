using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Backup and restore of the whole history as one JSON document, for a server rebuild or a
/// move to another machine (the database itself already survives plugin upgrades and
/// reinstalls, living in Jellyfin's data folder).
///
/// JSON rather than a copy of history.db so the file is readable and independent of the
/// schema. A backup only means anything on another server if it can be re-linked there:
/// user ids differ between servers, so watched rows carry the user name and are mapped by
/// name; library item ids can differ too, so every row carries the item's file path and is
/// re-linked by path (a recording at the same path is "in library" again after a restore).
/// </summary>
public sealed class BackupService
{
    public const int CurrentFormatVersion = 1;

    private readonly HistoryStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<BackupService> _logger;

    public BackupService(HistoryStore store, IUserManager userManager, ILibraryManager libraryManager, ILogger<BackupService> logger)
    {
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public sealed record BackupRow(
        string Kind,
        Guid? UserId,
        string? UserName,
        Guid ItemId,
        string? ItemPath,
        EpisodeIdentity Identity,
        string Source,
        DateTime EventUtc);

    public sealed record BackupFile(
        int FormatVersion,
        DateTime ExportedUtc,
        string PluginVersion,
        List<BackupRow> History,
        List<HistoryStore.CancelledTimer> CancelledTimers);

    public sealed record RestoreResult(
        int RowsInFile,
        int RowsAdded,
        int RowsAlreadyPresent,
        int ItemsRelinkedByPath,
        int WatchedSkippedUnknownUser,
        IReadOnlyList<string> UnknownUsers,
        int CancelledTimersAdded,
        bool Replaced);

    public BackupFile Export()
    {
        var userNames = _userManager.GetUsers().ToDictionary(u => u.Id, u => u.Username);
        var rows = _store.LoadAll().Select(r => new BackupRow(
            r.Kind,
            r.UserId,
            r.UserId is { } uid && userNames.TryGetValue(uid, out var name) ? name : null,
            r.ItemId,
            _libraryManager.GetItemById(r.ItemId)?.Path,
            r.Identity,
            r.Source,
            r.EventUtc)).ToList();

        var file = new BackupFile(
            CurrentFormatVersion,
            DateTime.UtcNow,
            Plugin.Instance?.Version.ToString() ?? "unknown",
            rows,
            _store.RecentCancelled(int.MaxValue));
        _logger.LogInformation("Episode history backup: {Rows} rows, {Timers} skipped recordings", rows.Count, file.CancelledTimers.Count);
        return file;
    }

    /// <exception cref="ArgumentException">The file isn't a backup this version understands.</exception>
    public RestoreResult Restore(BackupFile file, bool replace)
    {
        // Validate everything before touching the database, so a bad file never wipes it.
        if (file is null || file.History is null)
        {
            throw new ArgumentException("Not an Episode History backup file.");
        }

        if (file.FormatVersion < 1 || file.FormatVersion > CurrentFormatVersion)
        {
            throw new ArgumentException($"Backup format version {file.FormatVersion} isn't supported by this plugin version.");
        }

        if (file.History.Any(r => r is null || r.Identity is null
            || (r.Kind != HistoryStore.KindRecorded && r.Kind != HistoryStore.KindWatched)))
        {
            throw new ArgumentException("The backup file contains malformed history rows.");
        }

        var users = _userManager.GetUsers().ToList();
        var byId = users.ToDictionary(u => u.Id, u => u.Id);
        var byName = users
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var toInsert = new List<(string, Guid?, Guid, EpisodeIdentity, string, DateTime)>();
        var unknownUsers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedUser = 0, relinked = 0;
        foreach (var row in file.History)
        {
            Guid? userId = null;
            if (row.Kind == HistoryStore.KindWatched)
            {
                // Same server: the id still exists. Another server: match by user name.
                if (row.UserId is { } id && byId.ContainsKey(id))
                {
                    userId = id;
                }
                else if (row.UserName is { } name && byName.TryGetValue(name, out var mapped))
                {
                    userId = mapped;
                }
                else
                {
                    skippedUser++;
                    unknownUsers.Add(row.UserName ?? row.UserId?.ToString() ?? "?");
                    continue;
                }
            }

            var itemId = row.ItemId;
            if (_libraryManager.GetItemById(itemId) is null && !string.IsNullOrEmpty(row.ItemPath)
                && RecordingFiles.ItemAt(_libraryManager, row.ItemPath) is { } found)
            {
                itemId = found.Id;
                relinked++;
            }

            toInsert.Add((row.Kind, userId, itemId, row.Identity, row.Source, row.EventUtc));
        }

        if (replace)
        {
            _store.DeleteAll();
        }

        var added = _store.UpsertMany(toInsert, overwrite: false);
        var timersAdded = _store.ImportCancelled(file.CancelledTimers ?? new List<HistoryStore.CancelledTimer>());

        var result = new RestoreResult(
            file.History.Count, added, toInsert.Count - added, relinked, skippedUser, unknownUsers.ToList(), timersAdded, replace);
        _logger.LogInformation(
            "Episode history restore ({Mode}): {Rows} rows in file, {Added} added, {Present} already present, {Relinked} re-linked by path, " +
            "{Skipped} watched rows skipped for unknown users {Users}, {Timers} skipped recordings added",
            replace ? "replace" : "merge", result.RowsInFile, added, result.RowsAlreadyPresent, relinked, skippedUser,
            string.Join(", ", unknownUsers), timersAdded);
        return result;
    }
}
