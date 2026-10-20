using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpisodeHistory.Tests;

/// <summary>
/// The trash on real files in a temporary folder, with Jellyfin's library mocked: what a delete
/// moves (and, above all, what it leaves alone), putting back, and deleting for good.
/// </summary>
public sealed class TrashTests : IDisposable
{
    private static PluginConfiguration Config => TestPlugin.Config;

    private readonly string _dir;
    private readonly string _library;
    private readonly string _data;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<ILibraryMonitor> _libraryMonitor = new();

    public TrashTests()
    {
        TestPlugin.Reset();

        _dir = Path.Combine(Path.GetTempPath(), "eh-tests-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_dir, "Movies");
        _data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(_library);
        _libraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { new() { Locations = new[] { _library } } });
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private Trash NewTrash()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_data);
        var recordings = new Mock<IRecordingsManager>();
        recordings.Setup(r => r.GetRecordingFolders()).Returns(Array.Empty<VirtualFolderInfo>());
        return new Trash(paths.Object, _libraryManager.Object, recordings.Object, _libraryMonitor.Object, NullLogger<Trash>.Instance);
    }

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static Movie MovieAt(string path) => new() { Id = Guid.NewGuid(), Name = Path.GetFileNameWithoutExtension(path), Path = path };

    private static Episode EpisodeAt(string path) => new() { Id = Guid.NewGuid(), Name = "Episode", SeriesName = "Show", Path = path };

    private string TrashRoot => Path.Combine(_library, Trash.FolderName);

    private void VerifyLeftLibraryKeepingFiles(BaseItem item) =>
        _libraryManager.Verify(m => m.DeleteItem(item, It.Is<DeleteOptions>(o => !o.DeleteFileLocation), true), Times.Once);

    // ---- What a delete moves ---------------------------------------------------------------------

    [Fact]
    public void MovieAloneInItsFolder_TheWholeFolderGoes()
    {
        var movie = Touch("Heat (1995)/Heat (1995).ts");
        Touch("Heat (1995)/poster.jpg");
        Touch("Heat (1995)/movie.nfo");
        var item = MovieAt(movie);

        var entry = NewTrash().Delete(new[] { item }, "tester");

        Assert.NotNull(entry);
        Assert.False(Directory.Exists(Path.Combine(_library, "Heat (1995)")));
        var moved = Path.Combine(TrashRoot, entry!.Id, "Heat (1995)");
        Assert.True(File.Exists(Path.Combine(moved, "Heat (1995).ts")));
        Assert.True(File.Exists(Path.Combine(moved, "poster.jpg")));
        Assert.Single(entry.Files);
        Assert.True(entry.Files[0].IsFolder);
        Assert.Equal("tester", entry.DeletedBy);
        VerifyLeftLibraryKeepingFiles(item);
    }

    [Fact]
    public void MovieLooseInTheLibraryFolder_OnlyItsOwnFilesGo()
    {
        // The library folder itself must never be taken for "the movie's folder".
        var movie = Touch("Big Brother (2018).mpg");
        Touch("Big Brother (2018).edl");
        Touch("Big Brother (2018).nfo");
        Touch("Big Brother (2018)-poster.jpg");
        var other = Touch("Other Movie (2001).ts");
        var otherNfo = Touch("Other Movie (2001).nfo");

        var entry = NewTrash().Delete(new[] { MovieAt(movie) }, null);

        Assert.NotNull(entry);
        Assert.True(Directory.Exists(_library));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(otherNfo));
        Assert.False(File.Exists(movie));
        Assert.False(File.Exists(Path.Combine(_library, "Big Brother (2018).edl")));
        Assert.False(File.Exists(Path.Combine(_library, "Big Brother (2018).nfo")));
        Assert.All(entry!.Files, f => Assert.False(f.IsFolder));
    }

    [Fact]
    public void ConvertedMovieAloneInItsFolder_TheFolderGoesWithItsHiddenOriginals()
    {
        // Land of the Lost: the converter's hidden original was taken for a second movie, so only the
        // new copy went and the 19.8 GB original stayed behind.
        var movie = Touch("Heat (1995)/Heat (1995).ts");
        Touch("Heat (1995)/.Heat (1995).h264-original.ts");
        Touch("Heat (1995)/.Heat (1995).original.ts");

        var entry = NewTrash().Delete(new[] { MovieAt(movie) }, null);

        Assert.False(Directory.Exists(Path.Combine(_library, "Heat (1995)")));
        Assert.True(Assert.Single(entry!.Files).IsFolder);
        var moved = Path.Combine(TrashRoot, entry.Id, "Heat (1995)");
        Assert.True(File.Exists(Path.Combine(moved, ".Heat (1995).h264-original.ts")));
        Assert.True(File.Exists(Path.Combine(moved, ".Heat (1995).original.ts")));
    }

    [Fact]
    public void ConvertedMovieBesideAnother_ItsOriginalGoesAndTheOthersStays()
    {
        var movie = Touch("Heat (1995).ts");
        var original = Touch(".Heat (1995).h264-original.ts");
        var other = Touch("Ronin (1998).ts");
        var otherOriginal = Touch(".Ronin (1998).h264-original.ts");
        var lookalike = Touch(".Heat (1995).h264-original.mkv");

        var trash = NewTrash();
        var entry = trash.Delete(new[] { MovieAt(movie) }, null);

        Assert.False(File.Exists(movie));
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(otherOriginal));
        Assert.True(File.Exists(lookalike));

        Assert.Null(trash.Restore(entry!.Id));
        Assert.True(File.Exists(movie));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void OnlyVideoInTheLibraryFolder_TheLibraryFolderStays()
    {
        var movie = Touch("Lonely (2010).ts");
        var art = Touch("folder.jpg");

        var entry = NewTrash().Delete(new[] { MovieAt(movie) }, null);

        Assert.True(Directory.Exists(_library));
        Assert.True(File.Exists(art));
        Assert.All(entry!.Files, f => Assert.False(f.IsFolder));
    }

    [Fact]
    public void MovieSharingItsFolderWithAnotherVideo_TheOtherStays()
    {
        var movie = Touch("Two/Movie A.ts");
        var other = Touch("Two/Movie B.ts");
        Touch("Two/Movie A.nfo");

        NewTrash().Delete(new[] { MovieAt(movie) }, null);

        Assert.True(Directory.Exists(Path.Combine(_library, "Two")));
        Assert.True(File.Exists(other));
        Assert.False(File.Exists(movie));
        Assert.False(File.Exists(Path.Combine(_library, "Two", "Movie A.nfo")));
    }

    [Fact]
    public void MovieFolderWithASubfolder_TheFolderStays()
    {
        var movie = Touch("Alien (1979)/Alien (1979).ts");
        var extra = Touch("Alien (1979)/extras/Making of.mkv");

        NewTrash().Delete(new[] { MovieAt(movie) }, null);

        Assert.True(File.Exists(extra));
        Assert.False(File.Exists(movie));
    }

    [Fact]
    public void Episode_OnlyItsFilesGo_NotOnesWhoseNameStartsTheSame()
    {
        var e1 = Touch("Show/Season 1/Show S01E1.ts");
        Touch("Show/Season 1/Show S01E1.ts.properties");
        Touch("Show/Season 1/Show S01E1.nfo");
        Touch("Show/Season 1/Show S01E1-thumb.jpg");
        Touch("Show/Season 1/Show S01E1.edl");
        var keep = new[]
        {
            Touch("Show/Season 1/Show S01E10.ts"),
            Touch("Show/Season 1/Show S01E10.ts.properties"),
            Touch("Show/Season 1/Show S01E10.nfo"),
            Touch("Show/Season 1/Show S01E10-thumb.jpg"),
            Touch("Show/Season 1/Show S01E1 (2).nfo"),
            Touch("Show/Season 1/season.nfo"),
            Touch("Show/tvshow.nfo"),
        };

        var entry = NewTrash().Delete(new[] { EpisodeAt(e1) }, null);

        Assert.All(keep, f => Assert.True(File.Exists(f), f));
        Assert.Equal(5, entry!.Files.Count);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(e1)!, "Show S01E1.*"));
        Assert.Equal("Show", entry.Title);
        Assert.Equal("Episode", entry.Kind);
    }

    [Fact]
    public void TrashOff_JellyfinDeletesItAndNothingIsMoved()
    {
        Config.TrashEnabled = false;
        var movie = Touch("Heat (1995)/Heat (1995).ts");
        var item = MovieAt(movie);

        var entry = NewTrash().Delete(new[] { item }, null);

        Assert.Null(entry);
        Assert.True(File.Exists(movie));
        Assert.False(Directory.Exists(TrashRoot));
        _libraryManager.Verify(m => m.DeleteItem(item, It.Is<DeleteOptions>(o => o.DeleteFileLocation), true), Times.Once);
    }

    [Fact]
    public void FileAlreadyGone_JellyfinDeletesTheItem()
    {
        var item = MovieAt(Path.Combine(_library, "Gone (2000)", "Gone (2000).ts"));

        var entry = NewTrash().Delete(new[] { item }, null);

        Assert.Null(entry);
        Assert.Empty(NewTrash().All());
        _libraryManager.Verify(m => m.DeleteItem(item, It.Is<DeleteOptions>(o => o.DeleteFileLocation), true), Times.Once);
    }

    [Fact]
    public void DeletingSeveral_IsOneEntry()
    {
        var a = Touch("Show/Show S01E01.ts", "aaaa");
        var b = Touch("Show/Show S01E02.ts", "bb");

        var entry = NewTrash().Delete(new BaseItem[] { EpisodeAt(a), EpisodeAt(b) }, null);

        Assert.Equal(2, entry!.Videos);
        Assert.Equal(6, entry.Bytes);
        Assert.Equal("2 episodes", entry.Detail);
        Assert.Single(NewTrash().All());
    }

    // ---- Putting back ----------------------------------------------------------------------------

    [Fact]
    public void Restore_PutsEverythingBackAndTellsJellyfin()
    {
        var movie = Touch("Heat (1995)/Heat (1995).ts", "the movie");
        Touch("Heat (1995)/poster.jpg");
        var entry = NewTrash().Delete(new[] { MovieAt(movie) }, null)!;

        // A new instance: the list is read back from disk, as after a server restart.
        var error = NewTrash().Restore(entry.Id);

        Assert.Null(error);
        Assert.Equal("the movie", File.ReadAllText(movie));
        Assert.True(File.Exists(Path.Combine(_library, "Heat (1995)", "poster.jpg")));
        Assert.False(Directory.Exists(TrashRoot));
        Assert.Empty(NewTrash().All());
        _libraryMonitor.Verify(m => m.ReportFileSystemChanged(Path.Combine(_library, "Heat (1995)")), Times.Once);
    }

    [Fact]
    public void Restore_WhenSomethingIsAlreadyThere_MovesNothing()
    {
        var episode = Touch("Show/Show S01E01.ts", "deleted one");
        Touch("Show/Show S01E01.nfo");
        var trash = NewTrash();
        var entry = trash.Delete(new[] { EpisodeAt(episode) }, null)!;
        // The clash is on the second file: a restore that didn't look first would put the video
        // back and stop halfway.
        var nfo = Path.Combine(_library, "Show", "Show S01E01.nfo");
        File.WriteAllText(nfo, "new");

        var error = trash.Restore(entry.Id);

        Assert.NotNull(error);
        Assert.False(File.Exists(episode));
        Assert.Equal("new", File.ReadAllText(nfo));
        Assert.All(entry.Files, f => Assert.True(File.Exists(f.To), f.To));
        Assert.Single(trash.All());
    }

    [Fact]
    public void Restore_UnknownId_SaysSo()
    {
        Assert.NotNull(NewTrash().Restore("nope"));
    }

    // ---- Deleting for good -----------------------------------------------------------------------

    [Fact]
    public void Purge_DeletesTheFilesAndTheEmptyTrashFolder()
    {
        var movie = Touch("Heat (1995)/Heat (1995).ts");
        var trash = NewTrash();
        var entry = trash.Delete(new[] { MovieAt(movie) }, null)!;

        Assert.True(trash.Purge(entry.Id, "test"));

        Assert.False(Directory.Exists(TrashRoot));
        Assert.Empty(trash.All());
        Assert.False(trash.Purge(entry.Id, "again"));
    }

    [Fact]
    public void Purge_LeavesOtherEntriesAlone()
    {
        var trash = NewTrash();
        var first = trash.Delete(new[] { MovieAt(Touch("A (2001)/A (2001).ts")) }, null)!;
        var second = trash.Delete(new[] { MovieAt(Touch("B (2002)/B (2002).ts")) }, null)!;

        trash.Purge(first.Id, "test");

        Assert.Equal(second.Id, Assert.Single(trash.All()).Id);
        Assert.True(File.Exists(Path.Combine(TrashRoot, second.Id, "B (2002)", "B (2002).ts")));
    }

    [Fact]
    public async Task PurgeAsync_DeletesOnlyWhatIsPastItsDays()
    {
        var old = Seed(DateTime.UtcNow.AddDays(-3));
        var recent = Seed(DateTime.UtcNow.AddDays(-1));

        var trash = NewTrash();
        await trash.PurgeAsync("test");

        Assert.Equal(recent, Assert.Single(trash.All()).Id);
        Assert.False(Directory.Exists(Path.Combine(TrashRoot, old)));
        Assert.True(Directory.Exists(Path.Combine(TrashRoot, recent)));
    }

    [Fact]
    public async Task PurgeAsync_LowOnSpace_EmptiesThatDrivesTrash()
    {
        Seed(DateTime.UtcNow.AddHours(-2));
        Seed(DateTime.UtcNow.AddHours(-1));

        // No drive has a million GB free.
        Config.TrashMinFreeGb = 1_000_000;
        var trash = NewTrash();
        await trash.PurgeAsync("test");

        Assert.Empty(trash.All());
    }

    [Fact]
    public async Task PurgeAsync_EnoughSpace_KeepsEverything()
    {
        Seed(DateTime.UtcNow.AddHours(-2));
        Config.TrashMinFreeGb = 0;

        var trash = NewTrash();
        await trash.PurgeAsync("test");

        Assert.Single(trash.All());
    }

    /// <summary>A trash entry deleted at <paramref name="deletedUtc"/>, written as the plugin keeps it.</summary>
    private string Seed(DateTime deletedUtc)
    {
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(TrashRoot, id);
        Directory.CreateDirectory(folder);
        var to = Path.Combine(folder, id + ".ts");
        File.WriteAllText(to, "x");
        var file = Path.Combine(_data, "episode-history", "trash.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var all = File.Exists(file) ? JsonSerializer.Deserialize<List<Trash.Entry>>(File.ReadAllText(file))! : new List<Trash.Entry>();
        all.Add(new Trash.Entry
        {
            Id = id,
            Title = id,
            DeletedUtc = deletedUtc,
            Folders = { folder },
            Files = { new Trash.Moved { From = Path.Combine(_library, id + ".ts"), To = to } },
        });
        File.WriteAllText(file, JsonSerializer.Serialize(all));
        return id;
    }
}
