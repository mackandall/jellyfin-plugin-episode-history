using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpisodeHistory.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Web;

/// <summary>
/// Jellyfin's own deletes (the web client's Delete, several at once, other apps) go to the trash
/// too: DELETE /Items/{id}, DELETE /Items?ids=…, DELETE /LiveTv/Recordings/{id}. Only when every
/// item may be deleted by the caller, as Jellyfin checks; otherwise Jellyfin answers as it would.
/// Runs after sign-in and after JellyDVR Space's device rights (a resource filter), so a refused
/// TV is refused first.
/// </summary>
public sealed class TrashFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly Trash _trash;
    private readonly ILogger<TrashFilter> _logger;

    public TrashFilter(ILibraryManager libraryManager, IUserManager userManager, Trash trash, ILogger<TrashFilter> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _trash = trash;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!Trash.Enabled || !HttpMethods.IsDelete(request.Method) || Ids(request) is not { Count: > 0 } ids)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // An API key signs in as no user (an empty id): Jellyfin deletes it as asked, no trash.
        var user = Guid.TryParse(context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value, out var userId) && userId != Guid.Empty ? _userManager.GetUserById(userId) : null;
        var items = ids.Select(id => _libraryManager.GetItemById(id)).ToList();
        if (user is null || items.Any(i => i is null || !i.CanDelete(user)) || !items.Any(i => i is Video or MediaBrowser.Controller.Entities.TV.Series or MediaBrowser.Controller.Entities.TV.Season))
        {
            await next().ConfigureAwait(false);
            return;
        }

        try
        {
            _trash.Delete(items!, user.Username);
            context.Result = new NoContentResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't move {Count} item(s) to the trash", items.Count);
            context.Result = new ObjectResult(new { Message = "Couldn't move it to the trash: " + ex.Message }) { StatusCode = StatusCodes.Status500InternalServerError };
        }
    }

    /// <summary>The items a delete request names; null when it isn't one of Jellyfin's item deletes.</summary>
    private static List<Guid>? Ids(HttpRequest request)
    {
        var seg = (request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool Is(int i, string name) => seg.Length > i && string.Equals(seg[i], name, StringComparison.OrdinalIgnoreCase);
        IEnumerable<string>? raw = null;
        if (Is(0, "Items") && seg.Length == 2)
        {
            raw = new[] { seg[1] };
        }
        else if (Is(0, "Items") && seg.Length == 1)
        {
            raw = request.Query["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
        else if (Is(0, "LiveTv") && Is(1, "Recordings") && seg.Length == 3)
        {
            raw = new[] { seg[2] };
        }

        return raw?.Select(r => Guid.TryParse(r, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
    }
}
