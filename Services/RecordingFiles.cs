using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>Which library files are DVR recordings, and which are too short to count.</summary>
internal static class RecordingFiles
{
    // Used when Jellyfin hasn't probed the runtime yet: a 29-second stub is ~23 MB and five
    // minutes of broadcast video is several hundred, so this separates stubs from recordings.
    private const long MinBytesWithoutRuntime = 100L * 1024 * 1024;

    /// <summary>The DVR recording folders, each with a trailing separator for prefix tests.</summary>
    public static List<string> Folders(IRecordingsManager recordingsManager) =>
        recordingsManager.GetRecordingFolders()
            .SelectMany(f => f.Locations)
            .Select(p => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar)
            .ToList();

    public static bool IsUnder(string? path, IReadOnlyList<string> folders) =>
        !string.IsNullOrEmpty(path) && folders.Any(f => path.StartsWith(f, StringComparison.Ordinal));

    /// <summary>
    /// Whether a recording file seen only after the fact (import, copied in by hand) is long
    /// enough to count as recorded. Recordings the plugin watched being made use the stricter
    /// share-of-programme rule instead.
    /// </summary>
    public static bool IsLongEnough(BaseItem item, int minMinutes)
    {
        if (item.RunTimeTicks is > 0 and var ticks)
        {
            return TimeSpan.FromTicks(ticks) >= TimeSpan.FromMinutes(minMinutes);
        }

        try
        {
            return !string.IsNullOrEmpty(item.Path) && new FileInfo(item.Path).Length >= MinBytesWithoutRuntime;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
