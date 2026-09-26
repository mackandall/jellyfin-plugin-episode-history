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

    private sealed record Entry(string Kind, bool HasProgramId, bool ItemExists);

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
            var entry = new Entry(row.Kind, pid is not null, exists);
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

    public int ProgramIdCount => _byProgramId.Count;

    public int NameKeyCount => _byNameKey.Count;

    public HistoryState Evaluate(EpisodeIdentity identity)
    {
        bool watched = false, inLibrary = false, recordedBefore = false;

        void Apply(Entry e)
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

        var pid = identity.ValidProgramId;
        if (pid is not null && _byProgramId.TryGetValue(pid, out var byId))
        {
            foreach (var e in byId)
            {
                Apply(e);
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

                Apply(e);
            }
        }

        return new HistoryState(watched, inLibrary, recordedBefore && !inLibrary);
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
