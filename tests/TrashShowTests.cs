using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.EpisodeHistory.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpisodeHistory.Tests;

/// <summary>
/// Deleting a whole show or season: every episode under it goes to the trash as one entry, the
/// show or season leaves the library, and its folder goes only when nothing but artwork is left.
/// </summary>
public sealed class TrashShowTests : IDisposable
{
    private readonly string _dir;
    private readonly string _library;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<ILibraryMonitor> _libraryMonitor = new();
    private readonly Dictionary<Guid, BaseItem> _inLibrary = new();

    public TrashShowTests()
    {
        TestPlugin.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "eh-tests-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_dir, "TV");
        Directory.CreateDirectory(_library);
        _libraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { new() { Locations = new[] { _library } } });
        _libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _inLibrary.GetValueOrDefault(id));
        _libraryManager.Setup(m => m.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<bool>()))
            .Callback((BaseItem item, DeleteOptions _, bool _) => _inLibrary.Remove(item.Id));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private Trash NewTrash()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_dir, "data"));
        var recordings = new Mock<IRecordingsManager>();
        recordings.Setup(r => r.GetRecordingFolders()).Returns(Array.Empty<VirtualFolderInfo>());
        return new Trash(paths.Object, _libraryManager.Object, recordings.Object, _libraryMonitor.Object, NullLogger<Trash>.Instance);
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        _inLibrary[item.Id] = item;
        return item;
    }

    private Episode Episode(string show, int season, int number, bool onDisk = true)
    {
        var path = Path.Combine(_library, show, $"Season {season}", $"{show} S{season:00}E{number:00}.ts");
        if (onDisk)
        {
            Touch(Path.GetRelativePath(_library, path));
            Touch(Path.GetRelativePath(_library, Path.ChangeExtension(path, ".nfo")));
        }

        return Add(new Episode { Name = $"Episode {number}", SeriesName = show, ParentIndexNumber = season, IndexNumber = number, Path = path });
    }

    /// <summary>A folder item (show or season) at <paramref name="path"/> whose children are <paramref name="children"/>.</summary>
    private T FolderOf<T>(string name, string path, params BaseItem[] children)
        where T : Folder
    {
        var folder = new Mock<T> { CallBase = true };
        folder.Setup(f => f.Children).Returns(children);
        var item = folder.Object;
        item.Name = name;
        item.Path = path;
        return Add(item);
    }

    /// <summary>"Show" with two seasons of two episodes, artwork beside the show and in season 1.</summary>
    private (Series Show, Season Season1, Season Season2, Episode[] Episodes) TwoSeasons()
    {
        var e = new[] { Episode("Show", 1, 1), Episode("Show", 1, 2), Episode("Show", 2, 1), Episode("Show", 2, 2) };
        Touch("Show/tvshow.nfo");
        Touch("Show/poster.jpg");
        Touch("Show/Season 1/season.nfo");
        var s1 = FolderOf<Season>("Season 1", Path.Combine(_library, "Show", "Season 1"), e[0], e[1]);
        var s2 = FolderOf<Season>("Season 2", Path.Combine(_library, "Show", "Season 2"), e[2], e[3]);
        var show = FolderOf<Series>("Show", Path.Combine(_library, "Show"), s1, s2);
        return (show, s1, s2, e);
    }

    [Fact]
    public void WholeShow_EveryEpisodeGoesAsOneEntry()
    {
        var (show, _, _, episodes) = TwoSeasons();

        var entry = NewTrash().Delete(new BaseItem[] { show }, "tester");

        Assert.NotNull(entry);
        Assert.Equal(4, entry!.Videos);
        Assert.Equal("Show", entry.Title);
        Assert.Equal("4 episodes", entry.Detail);
        Assert.All(episodes, e => Assert.False(File.Exists(e.Path), e.Path));
        Assert.All(episodes, e => Assert.True(File.Exists(Path.Combine(_library, Trash.FolderName, entry.Id, Path.GetFileName(e.Path))), e.Path));
        Assert.Single(NewTrash().All());
    }

    [Fact]
    public void WholeShow_LeavesTheLibraryKeepingFiles()
    {
        var (show, _, _, episodes) = TwoSeasons();

        NewTrash().Delete(new BaseItem[] { show }, null);

        // Nothing Jellyfin deletes here deletes files: the trash has them.
        _libraryManager.Verify(m => m.DeleteItem(It.IsAny<BaseItem>(), It.Is<DeleteOptions>(o => o.DeleteFileLocation), It.IsAny<bool>()), Times.Never);
        _libraryManager.Verify(m => m.DeleteItem(show, It.IsAny<DeleteOptions>(), true), Times.Once);
        Assert.All(episodes, e => _libraryManager.Verify(m => m.DeleteItem(e, It.IsAny<DeleteOptions>(), true), Times.Once));
    }

    [Fact]
    public void WholeShow_FolderWithOnlyArtworkLeft_Goes()
    {
        var (show, _, _, _) = TwoSeasons();

        NewTrash().Delete(new BaseItem[] { show }, null);

        Assert.False(Directory.Exists(Path.Combine(_library, "Show")));
        Assert.True(Directory.Exists(_library));
    }

    [Fact]
    public void WholeShow_ArtworkGoesToTheTrashToo()
    {
        var (show, _, _, _) = TwoSeasons();

        var entry = NewTrash().Delete(new BaseItem[] { show }, null)!;

        var moved = Path.Combine(_library, Trash.FolderName, entry.Id, "Show");
        Assert.True(File.Exists(Path.Combine(moved, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(moved, "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(moved, "Season 1", "season.nfo")));
    }

    [Fact]
    public void RestoringAShow_BringsBackItsArtwork()
    {
        var (show, _, _, episodes) = TwoSeasons();
        var entry = NewTrash().Delete(new BaseItem[] { show }, null)!;

        Assert.Null(NewTrash().Restore(entry.Id));

        Assert.True(File.Exists(Path.Combine(_library, "Show", "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(_library, "Show", "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(_library, "Show", "Season 1", "season.nfo")));
        Assert.All(episodes, e => Assert.True(File.Exists(e.Path), e.Path));
        Assert.False(Directory.Exists(Path.Combine(_library, Trash.FolderName)));
    }

    [Fact]
    public void RestoringAShow_AfterANewRecording_MergesAndKeepsTheNewFiles()
    {
        var (show, _, _, episodes) = TwoSeasons();
        var entry = NewTrash().Delete(new BaseItem[] { show }, null)!;
        var recorded = Touch("Show/Season 3/Show S03E01.ts");
        File.WriteAllText(Path.Combine(_library, "Show", "poster.jpg"), "new poster");

        Assert.Null(NewTrash().Restore(entry.Id));

        Assert.True(File.Exists(recorded));
        Assert.Equal("new poster", File.ReadAllText(Path.Combine(_library, "Show", "poster.jpg")));
        Assert.True(File.Exists(Path.Combine(_library, "Show", "tvshow.nfo")));
        Assert.All(episodes, e => Assert.True(File.Exists(e.Path), e.Path));
        Assert.Empty(NewTrash().All());
        Assert.False(Directory.Exists(Path.Combine(_library, Trash.FolderName)));
    }

    [Fact]
    public void RestoringASeason_BringsBackItsArtwork()
    {
        var (_, season1, _, _) = TwoSeasons();
        var entry = NewTrash().Delete(new BaseItem[] { season1 }, null)!;

        Assert.Null(NewTrash().Restore(entry.Id));

        Assert.True(File.Exists(Path.Combine(_library, "Show", "Season 1", "season.nfo")));
    }

    [Fact]
    public void WholeShow_FolderWithSomethingElseInIt_Stays()
    {
        var (show, _, _, _) = TwoSeasons();
        var other = Touch("Show/Specials/Behind the scenes.mkv");

        NewTrash().Delete(new BaseItem[] { show }, null);

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void OneSeason_OnlyItsEpisodesGo()
    {
        var (_, season1, _, episodes) = TwoSeasons();

        var entry = NewTrash().Delete(new BaseItem[] { season1 }, null);

        Assert.Equal(2, entry!.Videos);
        Assert.False(File.Exists(episodes[0].Path));
        Assert.False(File.Exists(episodes[1].Path));
        Assert.True(File.Exists(episodes[2].Path));
        Assert.True(File.Exists(episodes[3].Path));
        Assert.True(File.Exists(Path.Combine(_library, "Show", "tvshow.nfo")));
        Assert.False(Directory.Exists(Path.Combine(_library, "Show", "Season 1")));
        _libraryManager.Verify(m => m.DeleteItem(season1, It.IsAny<DeleteOptions>(), true), Times.Once);
    }

    [Fact]
    public void ShowAndOneOfItsEpisodes_EachEpisodeGoesOnce()
    {
        var (show, _, _, episodes) = TwoSeasons();

        var entry = NewTrash().Delete(new BaseItem[] { show, episodes[0] }, null);

        Assert.Equal(4, entry!.Videos);
        Assert.Equal(4, entry.Files.Count(f => f.From.EndsWith(".ts", StringComparison.Ordinal)));
    }

    [Fact]
    public void SomeEpisodeFilesAlreadyGone_JellyfinDeletesThoseItems()
    {
        var present = Episode("Show", 1, 1);
        var gone = Episode("Show", 1, 2, onDisk: false);
        var show = FolderOf<Series>("Show", Path.Combine(_library, "Show"), present, gone);

        var entry = NewTrash().Delete(new BaseItem[] { show }, null);

        Assert.Equal(1, entry!.Videos);
        _libraryManager.Verify(m => m.DeleteItem(gone, It.Is<DeleteOptions>(o => o.DeleteFileLocation), true), Times.Once);
    }

    [Fact]
    public void ShowWithNoEpisodeFiles_JellyfinDeletesTheShow()
    {
        var show = FolderOf<Series>("Show", Path.Combine(_library, "Show"), Episode("Show", 1, 1, onDisk: false));
        Touch("Show/tvshow.nfo");

        var entry = NewTrash().Delete(new BaseItem[] { show }, null);

        Assert.Null(entry);
        Assert.Empty(NewTrash().All());
        _libraryManager.Verify(m => m.DeleteItem(show, It.Is<DeleteOptions>(o => o.DeleteFileLocation), true), Times.Once);
    }

    [Fact]
    public void RestoringAShow_PutsEveryEpisodeBackInItsSeasonFolder()
    {
        var (show, _, _, episodes) = TwoSeasons();
        var entry = NewTrash().Delete(new BaseItem[] { show }, null)!;

        Assert.Null(NewTrash().Restore(entry.Id));

        Assert.All(episodes, e => Assert.True(File.Exists(e.Path), e.Path));
        Assert.All(episodes, e => Assert.True(File.Exists(Path.ChangeExtension(e.Path, ".nfo")), e.Path));
        Assert.False(Directory.Exists(Path.Combine(_library, Trash.FolderName)));
        Assert.All(episodes, e => _libraryMonitor.Verify(m => m.ReportFileSystemChanged(e.Path!), Times.Once));
    }
}
