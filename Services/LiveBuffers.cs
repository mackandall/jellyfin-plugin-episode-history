using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Recordings the JellyDVR Live plugin made as live TV buffers and nobody has kept. They are
/// not "recorded" or "in library" as far as history goes: counting one would mark the guide
/// and cancel the real recording of that episode. Read from the buffer list JellyDVR Live keeps
/// at &lt;data&gt;/jellydvr-live/buffers.json (no code dependency), plus its item tag.
/// </summary>
internal static class LiveBuffers
{
    private const string Tag = "Live buffer";
    private static readonly object Lock = new();
    private static DateTime _loadedStamp = DateTime.MinValue;
    private static HashSet<string> _paths = new(StringComparer.Ordinal);
    private static HashSet<Guid> _items = new();

    public static bool IsUnkeptBuffer(BaseItem? item) =>
        item is not null
        && (item.Tags.Contains(Tag, StringComparer.OrdinalIgnoreCase) || IsUnkeptBuffer(item.Path) || Load().Items.Contains(item.Id));

    public static bool IsUnkeptBuffer(string? path) => !string.IsNullOrEmpty(path) && Load().Paths.Contains(path);

    private static (HashSet<string> Paths, HashSet<Guid> Items) Load()
    {
        lock (Lock)
        {
            try
            {
                var dataPath = Plugin.Instance?.DataPath;
                var file = dataPath is null ? null : Path.Combine(dataPath, "jellydvr-live", "buffers.json");
                if (file is null || !File.Exists(file))
                {
                    _paths.Clear();
                    _items.Clear();
                    return (_paths, _items);
                }

                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamp != _loadedStamp)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var unkept = doc.RootElement.EnumerateArray()
                        .Where(b => Bool(b, "Owned", true) && !Bool(b, "Kept", false))
                        .ToList();
                    _paths = unkept.Select(b => Str(b, "Path")).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    _items = unkept.Select(b => Guid.TryParse(Str(b, "ItemId"), out var g) ? g : (Guid?)null).OfType<Guid>().ToHashSet();
                    _loadedStamp = stamp;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Mid-write or unreadable: keep what was last read.
            }

            return (_paths, _items);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
}
