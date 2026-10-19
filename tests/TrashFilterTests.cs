using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EpisodeHistory.Services;
using Jellyfin.Plugin.EpisodeHistory.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpisodeHistory.Tests;

/// <summary>
/// Deletes from the Jellyfin web page and other apps: which requests the trash takes over, and
/// which it leaves for Jellyfin to answer (wrong user, not allowed, not a video, not a delete).
/// </summary>
public sealed class TrashFilterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _library;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly User _user = new("tester", "Default", "Default");
    private readonly Trash _trash;
    private bool _passedOn;

    public TrashFilterTests()
    {
        TestPlugin.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "eh-tests-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_dir, "Movies");
        Directory.CreateDirectory(_library);
        _libraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { new() { Locations = new[] { _library } } });
        _libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _userManager.Setup(m => m.GetUserById(_user.Id)).Returns(_user);

        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_dir, "data"));
        var recordings = new Mock<IRecordingsManager>();
        recordings.Setup(r => r.GetRecordingFolders()).Returns(Array.Empty<VirtualFolderInfo>());
        _trash = new Trash(paths.Object, _libraryManager.Object, recordings.Object, new Mock<ILibraryMonitor>().Object, NullLogger<Trash>.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>A movie on disk, alone in its folder, that <paramref name="canDelete"/> says the user may delete.</summary>
    private BaseItem Movie(string name, bool canDelete = true)
    {
        var path = Path.Combine(_library, name, name + ".ts");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        var movie = new Mock<Movie> { CallBase = true };
        movie.Setup(m => m.CanDelete(It.IsAny<User>())).Returns(canDelete);
        var item = movie.Object;
        item.Id = Guid.NewGuid();
        item.Name = name;
        item.Path = path;
        _items[item.Id] = item;
        return item;
    }

    private bool OnDisk(BaseItem item) => File.Exists(item.Path);

    /// <summary>Runs the filter on a request; the result it set, or null when it let Jellyfin answer.</summary>
    private async Task<IActionResult?> Send(string method, string path, string query = "", Guid? userId = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        if (userId != Guid.Empty)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", (userId ?? _user.Id).ToString()) }, "test"));
        }

        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
        _passedOn = false;
        var filter = new TrashFilter(_libraryManager.Object, _userManager.Object, _trash, NullLogger<TrashFilter>.Instance);
        await filter.OnActionExecutionAsync(context, () =>
        {
            _passedOn = true;
            return Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), new object()));
        });
        return context.Result;
    }

    // ---- Taken over ------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteOneItem_GoesToTheTrash()
    {
        var movie = Movie("Heat (1995)");

        var result = await Send("DELETE", $"/Items/{movie.Id}");

        Assert.IsType<NoContentResult>(result);
        Assert.False(_passedOn);
        Assert.False(OnDisk(movie));
        var entry = Assert.Single(_trash.All());
        Assert.Equal("tester", entry.DeletedBy);
    }

    [Fact]
    public async Task DeleteSeveralItems_OneTrashEntry()
    {
        var a = Movie("A (2001)");
        var b = Movie("B (2002)");

        var result = await Send("DELETE", "/Items", $"?ids={a.Id:N},{b.Id}");

        Assert.IsType<NoContentResult>(result);
        Assert.False(OnDisk(a));
        Assert.False(OnDisk(b));
        Assert.Equal(2, Assert.Single(_trash.All()).Videos);
    }

    [Fact]
    public async Task DeleteRecording_GoesToTheTrash()
    {
        var movie = Movie("Heat (1995)");

        var result = await Send("DELETE", $"/LiveTv/Recordings/{movie.Id}");

        Assert.IsType<NoContentResult>(result);
        Assert.False(OnDisk(movie));
    }

    [Fact]
    public async Task PathCase_DoesNotMatter()
    {
        var movie = Movie("Heat (1995)");

        await Send("DELETE", $"/items/{movie.Id}");

        Assert.False(OnDisk(movie));
    }

    // ---- Left to Jellyfin ------------------------------------------------------------------------

    [Fact]
    public async Task NotAllowedToDelete_JellyfinAnswers()
    {
        var movie = Movie("Heat (1995)", canDelete: false);

        var result = await Send("DELETE", $"/Items/{movie.Id}");

        Assert.Null(result);
        Assert.True(_passedOn);
        Assert.True(OnDisk(movie));
        Assert.Empty(_trash.All());
    }

    [Fact]
    public async Task OneOfSeveralNotAllowed_NoneGoToTheTrash()
    {
        var allowed = Movie("A (2001)");
        var refused = Movie("B (2002)", canDelete: false);

        await Send("DELETE", "/Items", $"?ids={allowed.Id},{refused.Id}");

        Assert.True(_passedOn);
        Assert.True(OnDisk(allowed));
        Assert.True(OnDisk(refused));
    }

    [Fact]
    public async Task UnknownItem_JellyfinAnswers()
    {
        var movie = Movie("Heat (1995)");

        await Send("DELETE", "/Items", $"?ids={movie.Id},{Guid.NewGuid()}");

        Assert.True(_passedOn);
        Assert.True(OnDisk(movie));
    }

    [Fact]
    public async Task NotSignedIn_JellyfinAnswers()
    {
        var movie = Movie("Heat (1995)");

        await Send("DELETE", $"/Items/{movie.Id}", userId: Guid.Empty);

        Assert.True(_passedOn);
        Assert.True(OnDisk(movie));
    }

    [Fact]
    public async Task UnknownUser_JellyfinAnswers()
    {
        var movie = Movie("Heat (1995)");

        await Send("DELETE", $"/Items/{movie.Id}", userId: Guid.NewGuid());

        Assert.True(_passedOn);
        Assert.True(OnDisk(movie));
    }

    [Fact]
    public async Task TrashOff_JellyfinAnswers()
    {
        TestPlugin.Config.TrashEnabled = false;
        var movie = Movie("Heat (1995)");

        await Send("DELETE", $"/Items/{movie.Id}");

        Assert.True(_passedOn);
        Assert.True(OnDisk(movie));
    }

    [Fact]
    public async Task NotAVideo_JellyfinAnswers()
    {
        var song = new Mock<Audio> { CallBase = true };
        song.Setup(m => m.CanDelete(It.IsAny<User>())).Returns(true);
        song.Object.Id = Guid.NewGuid();
        _items[song.Object.Id] = song.Object;

        await Send("DELETE", $"/Items/{song.Object.Id}");

        Assert.True(_passedOn);
        Assert.Empty(_trash.All());
    }

    [Theory]
    [InlineData("GET", "/Items/{0}")]
    [InlineData("POST", "/Items/{0}")]
    [InlineData("DELETE", "/Items/{0}/Images/Primary")]
    [InlineData("DELETE", "/Users/{0}")]
    [InlineData("DELETE", "/Videos/{0}/AlternateSources")]
    public async Task OtherRequests_PassStraightThrough(string method, string path)
    {
        var movie = Movie("Heat (1995)");

        var result = await Send(method, string.Format(System.Globalization.CultureInfo.InvariantCulture, path, movie.Id));

        Assert.Null(result);
        Assert.True(_passedOn);
        Assert.True(OnDisk(movie));
    }

    [Fact]
    public async Task DeleteWithNoIds_PassesThrough()
    {
        await Send("DELETE", "/Items", "?ids=");

        Assert.True(_passedOn);
    }
}
