using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.EpisodeHistory.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.EpisodeHistory.Tests;

/// <summary>
/// What a delete leaves behind, in the recordings folders and in an archive library: the files
/// beside the video, then the season folder and the show folder once no video is left in them.
/// </summary>
public sealed class RecordingTidyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _recordings;
    private readonly string _archive;
    private readonly List<string> _roots;

    public RecordingTidyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eh-tidy-" + Guid.NewGuid().ToString("N"));
        _recordings = Path.Combine(_dir, "Recordings");
        _archive = Path.Combine(_dir, "ArchivedTV");
        Directory.CreateDirectory(_recordings);
        Directory.CreateDirectory(_archive);
        _roots = new List<string> { _recordings + Path.DirectorySeparatorChar, _archive + Path.DirectorySeparatorChar };
    }

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>An episode file with what Jellyfin and Comskip put beside it; the video itself already deleted when <paramref name="deleted"/>.</summary>
    private string Episode(string show, string season, string name, bool deleted = true)
    {
        var dir = Path.Combine(_archive, show, season);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(_archive, show, "tvshow.nfo"), "x");
        var path = Path.Combine(dir, name + ".ts");
        foreach (var side in new[] { ".nfo", ".edl", "-thumb.jpg" })
        {
            File.WriteAllText(Path.Combine(dir, name + side), "x");
        }

        if (!deleted)
        {
            File.WriteAllText(path, "x");
        }

        return path;
    }

    [Fact]
    public void LastEpisodeOfTheLastSeason_ShowFolderGoes()
    {
        var path = Episode("South Park", "Season 1", "South Park S01E07 Pink Eye");

        Assert.True(RecordingTidy.AfterDelete(path, _roots, NullLogger.Instance));

        Assert.False(Directory.Exists(Path.Combine(_archive, "South Park")));
        Assert.True(Directory.Exists(_archive));
    }

    [Fact]
    public void LastEpisodeOfASeason_OtherSeasonLeft_OnlyTheSeasonFolderGoes()
    {
        var path = Episode("South Park", "Season 10", "South Park S10E11 Hell on Earth 2006");
        Episode("South Park", "Season 1", "South Park S01E07 Pink Eye", deleted: false);

        Assert.False(RecordingTidy.AfterDelete(path, _roots, NullLogger.Instance));

        Assert.False(Directory.Exists(Path.Combine(_archive, "South Park", "Season 10")));
        Assert.True(File.Exists(Path.Combine(_archive, "South Park", "Season 1", "South Park S01E07 Pink Eye.ts")));
        Assert.True(File.Exists(Path.Combine(_archive, "South Park", "tvshow.nfo")));
    }

    [Fact]
    public void OtherEpisodeInTheSeason_OnlyTheFilesBesideItGo()
    {
        var path = Episode("South Park", "Season 12", "South Park S12E12 About Last Night");
        var other = Episode("South Park", "Season 12", "South Park S12E13 Major Boobage", deleted: false);

        RecordingTidy.AfterDelete(path, _roots, NullLogger.Instance);

        var dir = Path.GetDirectoryName(path)!;
        Assert.Empty(Directory.GetFiles(dir, "South Park S12E12 About Last Night*"));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Path.ChangeExtension(other, ".edl")));
    }

    [Fact]
    public void ShowWithoutSeasonFolders_ShowFolderGoes()
    {
        var dir = Path.Combine(_archive, "Nova");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Nova S50E01.nfo"), "x");
        File.WriteAllText(Path.Combine(dir, "poster.jpg"), "x");

        Assert.True(RecordingTidy.AfterDelete(Path.Combine(dir, "Nova S50E01.ts"), _roots, NullLogger.Instance));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void FileInTheLibraryFolderItself_LibraryFolderStays()
    {
        File.WriteAllText(Path.Combine(_archive, "Heat (1995).nfo"), "x");

        RecordingTidy.AfterDelete(Path.Combine(_archive, "Heat (1995).ts"), _roots, NullLogger.Instance);

        Assert.True(Directory.Exists(_archive));
        Assert.False(File.Exists(Path.Combine(_archive, "Heat (1995).nfo")));
    }

    [Fact]
    public void SageTvAndEmbyFiles_GoToo_SeasonFolderGoes()
    {
        var dir = Path.Combine(_archive, "black-ish", "Season 03");
        Directory.CreateDirectory(Path.Combine(dir, "metadata"));
        Directory.CreateDirectory(Path.Combine(_archive, "black-ish", "Season 04"));
        File.WriteAllText(Path.Combine(_archive, "black-ish", "Season 04", "black-ish.S04E01.ts"), "x");
        foreach (var f in new[] { "black-ish.S03E23.Liberal.Arts.nfo", "black-ish.S03E23.Liberal.Arts.xml", "black-ish.S03E23.Liberal.Arts.ts.properties", "folder.jpg", "season.nfo", "Thumbs.db", Path.Combine("metadata", "black-ish.S03E23.Liberal.Arts.jpg") })
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
        }

        RecordingTidy.AfterDelete(Path.Combine(dir, "black-ish.S03E23.Liberal.Arts.ts"), _roots, NullLogger.Instance);

        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(Path.Combine(_archive, "black-ish", "Season 04")));
    }

    [Fact]
    public void OtherEpisodesEmbyPicture_Stays()
    {
        var dir = Path.Combine(_archive, "Nature", "Season 05");
        Directory.CreateDirectory(Path.Combine(dir, "metadata"));
        File.WriteAllText(Path.Combine(dir, "Nature S05E08.ts"), "x");
        File.WriteAllText(Path.Combine(dir, "metadata", "Nature S05E08.jpg"), "x");
        File.WriteAllText(Path.Combine(dir, "metadata", "Nature S05E07.jpg"), "x");
        File.WriteAllText(Path.Combine(dir, "Nature S05E07.ts.properties"), "x");

        RecordingTidy.AfterDelete(Path.Combine(dir, "Nature S05E07.ts"), _roots, NullLogger.Instance);

        Assert.False(File.Exists(Path.Combine(dir, "metadata", "Nature S05E07.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "Nature S05E07.ts.properties")));
        Assert.True(File.Exists(Path.Combine(dir, "metadata", "Nature S05E08.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, "Nature S05E08.ts")));
    }

    [Fact]
    public void VideoStillThere_NothingGoes()
    {
        var path = Episode("South Park", "Season 1", "South Park S01E07 Pink Eye", deleted: false);

        Assert.False(RecordingTidy.AfterDelete(path, _roots, NullLogger.Instance));

        Assert.True(File.Exists(Path.ChangeExtension(path, ".edl")));
    }

    // ---- Which deletes are tidied --------------------------------------------------------------

    private List<string> RecordingsOnly => new() { _recordings + Path.DirectorySeparatorChar };

    [Fact]
    public void EpisodeInAnArchive_Covered() =>
        Assert.True(RecordingTidy.Covers(new Episode { Path = Path.Combine(_archive, "Show", "Show S01E01.ts") }, RecordingsOnly, _roots));

    [Fact]
    public void MovieInAnArchive_Covered() =>
        Assert.True(RecordingTidy.Covers(new Movie { Path = Path.Combine(_archive, "Heat (1995)", "Heat (1995).ts") }, RecordingsOnly, _roots));

    [Fact]
    public void OtherVideoInARecordingsFolder_Covered() =>
        Assert.True(RecordingTidy.Covers(new Video { Path = Path.Combine(_recordings, "Game", "Game.ts") }, RecordingsOnly, _roots));

    [Fact]
    public void HomeVideoInALibrary_NotCovered() =>
        Assert.False(RecordingTidy.Covers(new Video { Path = Path.Combine(_archive, "Holidays", "Beach.mp4") }, RecordingsOnly, _roots));

    [Fact]
    public void EpisodeOutsideEveryLibrary_NotCovered() =>
        Assert.False(RecordingTidy.Covers(new Episode { Path = Path.Combine(_dir, "Elsewhere", "Show S01E01.ts") }, RecordingsOnly, _roots));

    [Fact]
    public void Folder_NotCovered() =>
        Assert.False(RecordingTidy.Covers(new Series { Path = Path.Combine(_archive, "Show") }, RecordingsOnly, _roots));
}
