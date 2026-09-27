using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.EpisodeHistory.History;

/// <summary>
/// An in-memory snapshot of the history, built once per sweep, that answers "what do we
/// know about this guide entry?" for ~30k guide entries without touching the database.
///
/// Matching rule: when both the guide entry and a history row carry a specific TMS id, the
/// ids alone decide (so a renumbered or retitled airing still matches, and two different
/// episodes that happen to share a title don't). A row without an id is matched by its
/// name keys; so is a guide entry without an id, against every row.
/// </summary>
public sealed class HistoryIndex
{
    private readonly Dictionary<string, List<Entry>> _byProgramId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _byNameKey = new(StringComparer.Ordinal);

    private const string LibraryKind = "library";

    /// <param name="ItemId">The library item this entry stands for, when it still exists (an
    /// existing recording, or a "count all libraries" item); null for watched rows and
    /// deleted recordings.</param>
    private sealed record Entry(string Kind, bool HasProgramId, bool ItemExists, Guid? ItemId = null);

    /// <param name="rows">Every history row.</param>
    /// <param name="itemExists">Whether a library item id still exists.</param>
    public HistoryIndex(IEnumerable<HistoryStore.Row> rows, Func<Guid, bool> itemExists)
    {
        var existsCache = new Dictionary<Guid, bool>();
        foreach (var row in rows)
        {
            // Only recorded rows need the (costlier) existence check: a watched row means
            // "watched" whether or not the file is still around.
            bool exists = false;
            if (row.Kind == HistoryStore.KindRecorded && !existsCache.TryGetValue(row.ItemId, out exists))
            {
                exists = itemExists(row.ItemId);
                existsCache[row.ItemId] = exists;
            }

            var pid = row.Identity.ValidProgramId;
            var entry = new Entry(row.Kind, pid is not null, exists, exists ? row.ItemId : null);
            if (pid is not null)
            {
                Add(_byProgramId, pid, entry);
            }

            foreach (var key in row.Identity.NameKeys())
            {
                Add(_byNameKey, key, entry);
            }
        }
    }

    /// <summary>
    /// Adds library items that aren't DVR recordings (the "count all libraries" setting).
    /// They count as in library and have no programme id, so they match by name keys.
    /// </summary>
    public void AddLibraryItems(IEnumerable<(Guid ItemId, EpisodeIdentity Identity)> items)
    {
        foreach (var (itemId, identity) in items)
        {
            var entry = new Entry(LibraryKind, false, true, itemId);
            foreach (var key in identity.NameKeys())
            {
                Add(_byNameKey, key, entry);
            }
        }
    }

    public int ProgramIdCount => _byProgramId.Count;

    public int NameKeyCount => _byNameKey.Count;

    public HistoryState Evaluate(EpisodeIdentity identity)
    {
        bool watched = false, inLibrary = false, recordedBefore = false;
        foreach (var e in Matches(identity))
        {
            if (e.Kind == HistoryStore.KindWatched)
            {
                watched = true;
            }
            else if (e.ItemExists)
            {
                inLibrary = true;
            }
            else
            {
                recordedBefore = true;
            }
        }

        return new HistoryState(watched, inLibrary, recordedBefore && !inLibrary);
    }

    /// <summary>
    /// The existing library items behind an "in library" mark (recordings still present, or
    /// items from any library with "count all libraries"), by the same matching as
    /// <see cref="Evaluate"/>: what "Play from library" in the guide can open.
    /// </summary>
    public IReadOnlyList<Guid> LibraryItems(EpisodeIdentity identity)
    {
        var ids = new List<Guid>();
        foreach (var e in Matches(identity))
        {
            if (e.ItemExists && e.ItemId is { } id && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private IEnumerable<Entry> Matches(EpisodeIdentity identity)
    {
        var pid = identity.ValidProgramId;
        if (pid is not null && _byProgramId.TryGetValue(pid, out var byId))
        {
            foreach (var e in byId)
            {
                yield return e;
            }
        }

        foreach (var key in identity.NameKeys())
        {
            if (!_byNameKey.TryGetValue(key, out var byName))
            {
                continue;
            }

            foreach (var e in byName)
            {
                // Both sides have an id: the id comparison above already decided.
                if (pid is not null && e.HasProgramId)
                {
                    continue;
                }

                yield return e;
            }
        }
    }

    private static void Add(Dictionary<string, List<Entry>> map, string key, Entry entry)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<Entry>(1);
            map[key] = list;
        }

        list.Add(entry);
    }
}
