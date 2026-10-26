using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Clears up what a deleted recording, episode or movie leaves behind: the files beside it (.nfo,
/// -thumb.jpg, .edl…) and, once no video is left in them, its season and show folders.
/// </summary>
internal static class RecordingTidy
{
    // As the trash's: SageTV and Emby archives add .ts.properties, .ini and .vprj files.
    private static readonly string[] SideExtensions = { ".nfo", ".edl", ".txt", ".log", ".xml", ".jpg", ".png", ".srt", ".properties", ".vprj", ".ini" };

    private static bool IsSide(string file) =>
        SideExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(file), "Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a deleted item's leftovers are tidied: any video in the recordings folders, and an
    /// episode or movie in another library (an archive). Other videos (home videos, clips) may
    /// share a folder with photos that must stay, so those are left as they are.
    /// </summary>
    public static bool Covers(BaseItem? item, IReadOnlyList<string> recordingFolders, IReadOnlyList<string> libraryFolders) =>
        item is Video { Path: { Length: > 0 } path }
        && (RecordingFiles.IsUnder(path, recordingFolders) || (item is Episode or Movie && RecordingFiles.IsUnder(path, libraryFolders)));

    /// <summary>
    /// What Jellyfin, Comskip and other DVRs leave next to a recording: its .nfo, -thumb.jpg, .edl,
    /// .ts.properties and so on, and the picture Emby keeps for it in a "metadata" folder.
    /// </summary>
    public static void DeleteSideFiles(string? videoPath, ILogger logger)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }

        var name = Path.GetFileName(videoPath)!;
        var stem = Path.GetFileNameWithoutExtension(videoPath)!;
        var metadata = Path.Combine(dir, "metadata");
        var files = Directory.EnumerateFiles(dir)
            .Concat(Directory.Exists(metadata) ? Directory.EnumerateFiles(metadata) : Enumerable.Empty<string>())
            .ToList();
        foreach (var f in files)
        {
            var rest = Path.GetFileNameWithoutExtension(f);
            if (IsSide(f) && (rest == stem || rest == stem + "-thumb" || Path.GetFileName(f).StartsWith(name + ".", StringComparison.Ordinal)))
            {
                try
                {
                    File.Delete(f);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Couldn't delete {File}", f);
                }
            }
        }
    }

    /// <summary>The show's folder in the recordings folders: the recording's folder, or its parent for a "Season N" folder.</summary>
    public static string? ShowFolder(string? videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is not null && Path.GetFileName(dir).StartsWith("Season", StringComparison.OrdinalIgnoreCase))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }

    /// <summary>
    /// Removes a folder once no video is left anywhere in it (only tvshow.nfo, artwork and
    /// empty season folders); true when it went. Never one of <paramref name="roots"/> itself
    /// (each with a trailing separator), nor a folder outside them.
    /// </summary>
    /// <summary>
    /// True when <paramref name="dir"/> is a folder inside a library folder (not one itself) with
    /// nothing in it, at any depth, but artwork and metadata.
    /// </summary>
    public static bool OnlyArtworkLeft(string dir, IReadOnlyList<string> roots)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
        return Directory.Exists(dir) && !roots.Contains(trimmed) && roots.Any(r => trimmed.StartsWith(r, StringComparison.Ordinal))
            && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => !IsSide(f));
    }

    public static bool RemoveIfNoVideos(string dir, IReadOnlyList<string> roots, ILogger logger)
    {
        try
        {
            if (OnlyArtworkLeft(dir, roots))
            {
                Directory.Delete(dir, true);
                logger.LogInformation("Removed the emptied folder {Folder}", dir);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't remove the folder {Folder}", dir);
        }

        return false;
    }

    /// <summary>
    /// After a single recording left the library: if its file is gone, so are the files beside
    /// it, then its season folder and its show folder when nothing else is in them. True when
    /// the show folder went. A file that is still there (or was replaced by another of the
    /// same name, as a cut does) keeps everything.
    /// </summary>
    public static bool AfterDelete(string videoPath, IReadOnlyList<string> roots, ILogger logger)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir) || File.Exists(videoPath))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (!Directory.EnumerateFiles(dir).Any(f => !IsSide(f) && Path.GetFileNameWithoutExtension(f) == stem))
        {
            DeleteSideFiles(videoPath, logger);
        }

        var show = ShowFolder(videoPath)!;
        if (!string.Equals(dir, show, StringComparison.Ordinal))
        {
            RemoveIfNoVideos(dir, roots, logger);
        }

        return RemoveIfNoVideos(show, roots, logger);
    }
}
