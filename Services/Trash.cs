using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// A trash bin for deleted recordings and videos, as Channels DVR has: a delete moves the files
/// into a hidden ".jdvr-trash" folder at the top of their library folder (the same drive, so it's
/// a rename) and takes the item out of the library. They can be put back until they're purged:
/// after <see cref="PluginConfiguration.TrashKeepDays"/>, or sooner, oldest first, when their drive
/// has less than <see cref="PluginConfiguration.TrashMinFreeGb"/> free. Jellyfin skips hidden
/// folders, so nothing in the trash comes back with a library scan.
/// The list is in &lt;data&gt;/episode-history/trash.json.
/// </summary>
public sealed class Trash : IHostedService, IDisposable
{
    public const string FolderName = ".jdvr-trash";

    private static readonly string[] VideoExtensions = { ".ts", ".mkv", ".mp4", ".m4v", ".mpg", ".mpeg", ".avi", ".wmv", ".mov", ".m2ts", ".webm", ".flv", ".iso" };
    private static readonly string[] SideExtensions = { ".nfo", ".edl", ".txt", ".log", ".xml", ".jpg", ".png", ".srt", ".properties", ".vprj", ".ini" };

    private readonly ILibraryManager _libraryManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<Trash> _logger;
    private readonly string _file;
    private readonly object _lock = new();
    private List<Entry>? _all;
    private Timer? _timer;
    private int _purging;

    public Trash(IApplicationPaths applicationPaths, ILibraryManager libraryManager, IRecordingsManager recordingsManager, ILibraryMonitor libraryMonitor, ILogger<Trash> logger)
    {
        _libraryManager = libraryManager;
        _recordingsManager = recordingsManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
        var directory = Path.Combine(applicationPaths.DataPath, "episode-history");
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "trash.json");
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public static bool Enabled => Config.TrashEnabled;

    /// <summary>One file or folder in the trash, and where it came from.</summary>
    public sealed class Moved
    {
        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;

        public bool IsFolder { get; set; }

        /// <summary>
        /// A show's or season's folder holding only artwork and .nfo files: put back into the folder
        /// if it's there again (a new recording), never over what is in it.
        /// </summary>
        public bool Artwork { get; set; }
    }

    /// <summary>One delete: a recording, or several deleted together (a whole show, a partial's pieces).</summary>
    public sealed class Entry
    {
        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? Show { get; set; }

        /// <summary>"S6E8 · Frozen Evidence", a movie's year, or "27 episodes".</summary>
        public string? Detail { get; set; }

        /// <summary>Episode, Movie or Video; Mixed for several kinds.</summary>
        public string Kind { get; set; } = "Video";

        public int Videos { get; set; }

        public long Bytes { get; set; }

        public DateTime DeletedUtc { get; set; }

        public string? DeletedBy { get; set; }

        /// <summary>The share or folder it was in (dvr_storage, NetworkUSB_storage…), for the list.</summary>
        public string? Place { get; set; }

        /// <summary>The trash folders holding it (one per drive it came from).</summary>
        public List<string> Folders { get; set; } = new();

        public List<Moved> Files { get; set; } = new();
    }

    public List<Entry> All()
    {
        lock (_lock)
        {
            return Load().OrderByDescending(e => e.DeletedUtc).ToList();
        }
    }

    public static DateTime ExpiresUtc(Entry e) => e.DeletedUtc.AddDays(Math.Max(0, Config.TrashKeepDays));

    /// <summary>
    /// Deletes <paramref name="items"/> as one trash entry: each video's file and the files beside
    /// it (a movie alone in its folder: the whole folder, artwork included) go to the trash, then
    /// the item leaves the library. A show or season stands for all its videos. With the trash
    /// off, or for what isn't a video file, it's Jellyfin's own delete. Null when nothing went to
    /// the trash. Partial failures stop before the library is touched for the item that failed.
    /// </summary>
    public Entry? Delete(IReadOnlyList<BaseItem> items, string? deletedBy)
    {
        if (!Enabled)
        {
            foreach (var item in items)
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
            }

            return null;
        }

        var videos = new List<Video>();
        var containers = new List<Folder>();
        var others = new List<BaseItem>();
        foreach (var item in items)
        {
            switch (item)
            {
                case Video v:
                    videos.Add(v);
                    break;
                case Series or Season:
                    containers.Add((Folder)item);
                    videos.AddRange(((Folder)item).GetRecursiveChildren(i => i is Video).OfType<Video>());
                    break;
                default:
                    others.Add(item);
                    break;
            }
        }

        // Only files go to the trash: anything else (and a video whose file is gone already) is
        // deleted as Jellyfin does.
        videos = videos.DistinctBy(v => v.Id).ToList();
        others.AddRange(videos.Where(v => v.IsVirtualItem || string.IsNullOrEmpty(v.Path) || !File.Exists(v.Path)));
        videos = videos.Except(others.OfType<Video>()).ToList();
        foreach (var item in others)
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
        }

        if (videos.Count == 0)
        {
            foreach (var c in containers.Where(c => _libraryManager.GetItemById(c.Id) is not null))
            {
                _libraryManager.DeleteItem(c, new DeleteOptions { DeleteFileLocation = true }, true);
            }

            return null;
        }

        var entry = new Entry
        {
            Id = Guid.NewGuid().ToString("N"),
            DeletedUtc = DateTime.UtcNow,
            DeletedBy = deletedBy,
            Videos = videos.Count
        };
        Describe(entry, videos);
        var roots = LibraryRoots();
        var moved = 0;

        // Alternates first: deleting a primary would promote one of them to take its place.
        foreach (var v in videos.OrderBy(v => v.PrimaryVersionId.HasValue ? 0 : 1))
        {
            var path = v.Path!;
            var folder = Path.Combine(TrashFolderFor(path, roots), entry.Id);
            Directory.CreateDirectory(folder);
            if (!entry.Folders.Contains(folder))
            {
                entry.Folders.Add(folder);
            }

            var bytes = 0L;
            try
            {
                foreach (var (from, isFolder) in WhatGoes(v, path))
                {
                    var to = Path.Combine(folder, Path.GetFileName(from));
                    bytes += isFolder ? FolderSize(from) : new FileInfo(from).Length;
                    if (isFolder)
                    {
                        Directory.Move(from, to);
                    }
                    else
                    {
                        File.Move(from, to);
                    }

                    entry.Files.Add(new Moved { From = from, To = to, IsFolder = isFolder });
                }
            }
            catch (Exception ex)
            {
                // What did move stays listed (and can be put back); this one keeps its library item.
                _logger.LogWarning(ex, "Couldn't move {Path} to the trash", path);
                Save(entry);
                throw;
            }

            entry.Bytes += bytes;
            moved++;
            try
            {
                _libraryManager.DeleteItem(v, new DeleteOptions { DeleteFileLocation = false }, true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Name} had already left the library", v.Name);
            }
        }

        // A show or season deleted as a whole leaves the library with its videos; its folder, if
        // nothing but artwork is left in it, goes to the trash too, so a restore brings back the
        // show's poster and .nfo files with the episodes.
        foreach (var c in containers)
        {
            try
            {
                var dir = c.Path;
                if (_libraryManager.GetItemById(c.Id) is { } current)
                {
                    _libraryManager.DeleteItem(current, new DeleteOptions { DeleteFileLocation = false }, true);
                }

                if (!string.IsNullOrEmpty(dir) && RecordingTidy.OnlyArtworkLeft(dir, roots))
                {
                    var folder = Path.Combine(TrashFolderFor(dir, roots), entry.Id);
                    var to = Path.Combine(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)));
                    if (!Directory.Exists(to) && !File.Exists(to))
                    {
                        Directory.CreateDirectory(folder);
                        if (!entry.Folders.Contains(folder))
                        {
                            entry.Folders.Add(folder);
                        }

                        var bytes = FolderSize(dir);
                        Directory.Move(dir, to);
                        entry.Files.Add(new Moved { From = dir, To = to, IsFolder = true, Artwork = true });
                        entry.Bytes += bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't remove {Name} from the library", c.Name);
            }
        }

        Save(entry);
        _logger.LogInformation("Moved {Title} ({Videos} video(s), {MB} MB) to the trash, deleted by {User}", entry.Title, moved, entry.Bytes / 1048576, deletedBy ?? "the server");
        _ = Task.Run(() => PurgeAsync("after a delete"));
        return entry;
    }

    /// <summary>Puts an entry's files back where they were and has Jellyfin add them again. Null when all went back; else why not.</summary>
    public string? Restore(string id)
    {
        Entry? entry;
        lock (_lock)
        {
            entry = Load().FirstOrDefault(e => e.Id == id);
        }

        if (entry is null)
        {
            return "It isn't in the trash any more.";
        }

        var clash = entry.Files.FirstOrDefault(f => File.Exists(f.From) || (Directory.Exists(f.From) && !f.Artwork));
        if (clash is not null)
        {
            return $"Something is already at {clash.From}.";
        }

        var back = entry.Files.Where(f => f.IsFolder || IsVideo(f.From)).Select(f => f.From).ToList();

        // Folders first, outermost first: a show's folder goes back before the episodes go into it.
        foreach (var f in entry.Files.OrderByDescending(f => f.IsFolder).ThenBy(f => f.From.Length).ToList())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f.From)!);
                if (f.Artwork && Directory.Exists(f.From))
                {
                    MergeBack(f.To, f.From);
                }
                else if (f.IsFolder)
                {
                    Directory.Move(f.To, f.From);
                }
                else if (File.Exists(f.To))
                {
                    File.Move(f.To, f.From);
                }

                entry.Files.Remove(f);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't put {Path} back", f.From);
                Save(entry);
                return $"Couldn't put {Path.GetFileName(f.From)} back: {ex.Message}";
            }
        }

        foreach (var folder in entry.Folders)
        {
            TryDelete(folder);
        }

        lock (_lock)
        {
            var all = Load();
            all.RemoveAll(e => e.Id == id);
            Write(all);
        }

        // Jellyfin finds them again (same paths, so the same items, watched state and all).
        foreach (var path in back)
        {
            _libraryMonitor.ReportFileSystemChanged(path);
        }

        _logger.LogInformation("Put {Title} back from the trash", entry.Title);
        return null;
    }

    /// <summary>Deletes an entry for good now. False when it wasn't there.</summary>
    public bool Purge(string id, string why)
    {
        Entry? entry;
        lock (_lock)
        {
            var all = Load();
            entry = all.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            all.Remove(entry);
            Write(all);
        }

        foreach (var folder in entry.Folders)
        {
            TryDelete(folder);
            if (Config.TrashClearNasRecycle)
            {
                // A NAS recycle bin (Synology's #recycle) catches what is deleted over the share
                // and keeps it on the same disk: the copy of this trash folder goes from there too.
                if (RecycledCopy(folder) is { } copy)
                {
                    TryDelete(copy);
                }
            }
        }

        _logger.LogInformation("Deleted {Title} from the trash for good ({Why}, {MB} MB)", entry.Title, why, entry.Bytes / 1048576);
        return true;
    }

    public int PurgeAll()
    {
        var n = 0;
        foreach (var e in All())
        {
            n += Purge(e.Id, "trash emptied") ? 1 : 0;
        }

        return n;
    }

    /// <summary>
    /// Purges what is past its time, then, on each drive with less free space than the limit,
    /// the oldest entries there until there's enough (or that drive's trash is empty).
    /// </summary>
    public async Task PurgeAsync(string why)
    {
        if (Interlocked.Exchange(ref _purging, 1) == 1)
        {
            return;
        }

        try
        {
            await Task.Yield();
            var now = DateTime.UtcNow;
            foreach (var e in All().Where(e => ExpiresUtc(e) <= now))
            {
                Purge(e.Id, $"kept {Config.TrashKeepDays} day(s)");
            }

            var minFree = Math.Max(0, Config.TrashMinFreeGb) * 1024L * 1024 * 1024;
            if (minFree == 0)
            {
                return;
            }

            foreach (var drive in All().SelectMany(e => e.Folders).Select(MountOf).Distinct().ToList())
            {
                var before = FreeSpace(drive);
                foreach (var e in All().Where(e => e.Folders.Any(f => MountOf(f) == drive)).OrderBy(e => e.DeletedUtc))
                {
                    var free = FreeSpace(drive);
                    if (free is null || free >= minFree)
                    {
                        break;
                    }

                    Purge(e.Id, $"{drive} had {free / (1024 * 1024 * 1024)} GB free, under {Config.TrashMinFreeGb} GB ({why})");
                }

                if (FreeSpace(drive) is { } after && after < minFree && before is { } b && after - b < 1024L * 1024 * 1024)
                {
                    _logger.LogWarning("Emptying the trash on {Drive} freed almost nothing: something else there (a NAS recycle bin?) still holds the files", drive);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trash purge failed");
        }
        finally
        {
            Interlocked.Exchange(ref _purging, 0);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = PurgeAsync("check"), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    // ---- What goes, and where ------------------------------------------------------------------

    /// <summary>
    /// A movie alone in its own folder: the folder (artwork and all). Anything else: the video and
    /// the files named after it (.nfo, -thumb.jpg, .edl, .ts.properties…) and its hidden originals.
    /// </summary>
    private IEnumerable<(string Path, bool IsFolder)> WhatGoes(Video v, string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (v is Movie && !v.IsInMixedFolder && !LibraryRoots().Contains(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar)
            && !Directory.EnumerateFiles(dir).Any(f => f != path && IsVideo(f) && !IsOriginalOf(f, path))
            && !Directory.EnumerateDirectories(dir).Any())
        {
            return new[] { (dir, true) };
        }

        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var list = new List<(string, bool)> { (path, false) };
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var fn = Path.GetFileName(f);
            if (IsOriginalOf(f, path))
            {
                list.Add((f, false));
                continue;
            }

            if (f == path || IsVideo(f))
            {
                continue;
            }

            var rest = Path.GetFileNameWithoutExtension(f);
            if (fn.StartsWith(name + ".", StringComparison.Ordinal)
                || (IsSide(f) && (rest == stem || rest == stem + "-thumb")))
            {
                list.Add((f, false));
            }
        }

        return list;
    }

    /// <summary>
    /// The copy kept hidden beside a video for Undo: JellyDVR Space's Convert to HEVC
    /// (".Heat.h264-original.ts") or the Comskip plugin's cut to the programme start (".Heat.original.ts"),
    /// and the .edl the Comskip plugin's commercial cut keeps with it (".Heat.original.edl").
    /// </summary>
    private static bool IsOriginalOf(string f, string video)
    {
        var fn = Path.GetFileName(f);
        var stem = Path.GetFileNameWithoutExtension(video);
        var ext = Path.GetExtension(video);
        return OriginalSuffixes.Any(s => string.Equals(fn, "." + stem + s + ext, StringComparison.Ordinal)
            || string.Equals(fn, "." + stem + s + ".edl", StringComparison.Ordinal));
    }

    private static readonly string[] OriginalSuffixes = { ".h264-original", ".original" };

    private static bool IsVideo(string f) => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    private static bool IsSide(string f) => SideExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    /// <summary>The library and recording folders, each with a trailing separator.</summary>
    private List<string> LibraryRoots() =>
        _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations)
            .Concat(_recordingsManager.GetRecordingFolders().SelectMany(f => f.Locations))
            .Select(p => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The trash folder for a file: in the library folder it's under (the deepest one), when that
    /// is on the same drive; else beside the file. Either way on the file's drive, so moving is a rename.
    /// </summary>
    private static string TrashFolderFor(string path, List<string> roots)
    {
        var root = roots.Where(r => path.StartsWith(r, StringComparison.Ordinal)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is not null && MountOf(root) == MountOf(path))
        {
            return Path.Combine(root, FolderName);
        }

        return Path.Combine(Path.GetDirectoryName(path)!, FolderName);
    }

    /// <summary>The mount point a path is on (from /proc/self/mountinfo); "/" when unknown.</summary>
    public static string MountOf(string path)
    {
        try
        {
            var best = "/";
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 5)
                {
                    continue;
                }

                var mount = parts[4].Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
                var prefix = mount == "/" ? "/" : mount + "/";
                if ((path == mount || path.StartsWith(prefix, StringComparison.Ordinal)) && mount.Length > best.Length)
                {
                    best = mount;
                }
            }

            return best;
        }
        catch (IOException)
        {
            return "/";
        }
    }

    private static long? FreeSpace(string drive)
    {
        try
        {
            return new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Where a NAS recycle bin keeps a deleted trash folder: &lt;share&gt;/#recycle/&lt;path in the share&gt;.</summary>
    private static string? RecycledCopy(string folder)
    {
        var mount = MountOf(folder);
        if (mount == "/")
        {
            return null;
        }

        var recycle = Path.Combine(mount, "#recycle");
        return Directory.Exists(recycle) ? Path.Combine(recycle, Path.GetRelativePath(mount, folder)) : null;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            // The trash folder itself, once empty.
            var parent = Path.GetDirectoryName(path);
            if (parent is not null && Path.GetFileName(parent) == FolderName && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't delete {Path}", path);
        }
    }

    /// <summary>Moves what is in <paramref name="from"/> into <paramref name="to"/>, skipping files already there.</summary>
    private static void MergeBack(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
            }
        }
    }

    private static long FolderSize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static void Describe(Entry entry, List<Video> videos)
    {
        var episodes = videos.OfType<Episode>().ToList();
        var shows = episodes.Select(e => e.SeriesName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        entry.Kind = videos.All(v => v is Episode) ? "Episode" : videos.All(v => v is Movie) ? "Movie" : videos.Count == 1 ? "Video" : "Mixed";
        entry.Place = videos.Select(v => (v.Path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Length > 2 && p[0] is "mnt" or "media" or "srv" ? p[1] : p.Length > 1 ? p[0] : null)
            .OfType<string>().Distinct().FirstOrDefault();
        if (videos.Count == 1)
        {
            var v = videos[0];
            if (v is Episode e)
            {
                entry.Show = e.SeriesName;
                entry.Title = e.SeriesName ?? e.Name;
                var se = e.ParentIndexNumber is { } s && e.IndexNumber is { } n ? $"S{s}E{n}" : null;
                entry.Detail = string.Join(" · ", new[] { se, e.Name }.Where(x => !string.IsNullOrEmpty(x)));
            }
            else
            {
                entry.Title = v.Name;
                entry.Detail = v.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else if (shows.Count == 1 && episodes.Count == videos.Count)
        {
            entry.Show = shows[0];
            entry.Title = shows[0]!;
            entry.Detail = $"{videos.Count} episodes";
        }
        else
        {
            entry.Title = string.Join(", ", videos.Select(v => v is Episode e ? e.SeriesName : v.Name).Distinct().Take(3));
            entry.Detail = $"{videos.Count} videos";
        }
    }

    // ---- The list ------------------------------------------------------------------------------

    private void Save(Entry entry)
    {
        lock (_lock)
        {
            var all = Load();
            all.RemoveAll(e => e.Id == entry.Id);
            if (entry.Files.Count > 0)
            {
                all.Add(entry);
            }

            Write(all);
        }
    }

    private List<Entry> Load()
    {
        if (_all is null)
        {
            try
            {
                _all = File.Exists(_file) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't read {File}", _file);
            }

            _all ??= new List<Entry>();
        }

        return _all;
    }

    private void Write(List<Entry> all)
    {
        _all = all;
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, true);
    }
}
