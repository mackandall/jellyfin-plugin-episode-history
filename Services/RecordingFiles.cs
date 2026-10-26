using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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

    /// <summary>Every library folder and DVR recording folder, each with a trailing separator.</summary>
    public static List<string> LibraryFolders(ILibraryManager libraryManager, IRecordingsManager recordingsManager) =>
        libraryManager.GetVirtualFolders().SelectMany(f => f.Locations)
            .Concat(recordingsManager.GetRecordingFolders().SelectMany(f => f.Locations))
            .Select(p => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Live TV's general recording folders (not the movie or series ones), each with a trailing
    /// separator: where programmes without season and episode land.
    /// </summary>
    public static List<string> OtherFolders(IRecordingsManager recordingsManager) =>
        recordingsManager.GetRecordingFolders()
            .Where(f => f.CollectionType is null)
            .SelectMany(f => f.Locations)
            .Select(p => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar)
            .ToList();

    /// <summary>
    /// The library item for a recording file. FindByPath leaves out alternate versions, and
    /// that is what Jellyfin makes of a recording resumed after a restart or retry ("Name - 1.ts"
    /// next to "Name.ts"), so those are looked for too.
    /// </summary>
    public static BaseItem? ItemAt(ILibraryManager libraryManager, string path) =>
        libraryManager.FindByPath(path, false)
        ?? libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = path,
            IsFolder = false,
            IncludeOwnedItems = true,
            Limit = 1,
            DtoOptions = new DtoOptions(true)
        }).FirstOrDefault();

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
