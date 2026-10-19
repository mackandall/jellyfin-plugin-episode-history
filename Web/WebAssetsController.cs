using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EpisodeHistory.Web;

/// <summary>
/// Serves the jellyfin-web scripts (guidemenu.js, recordings.js) embedded in this assembly.
/// jellyfin-web's index.html is patched to load it from here (see install-web.sh; the
/// plugin can't write that root-owned file itself), so a plugin redeploy is enough to
/// update the script.
///
/// Anonymous: index.html loads it on every page, including the login screen before
/// anyone is signed in. The script does nothing until a guide program dialog opens.
/// </summary>
[ApiController]
[Route("EpisodeHistory/web")]
[AllowAnonymous]
public sealed class WebAssetsController : ControllerBase
{
    [HttpGet("guidemenu.js")]
    [Produces("application/javascript")]
    public Task<ContentResult> GetGuideMenuJs() => Embedded("guidemenu.js");

    /// <summary>The home screen's recordings row; guidemenu.js loads it.</summary>
    [HttpGet("recordings.js")]
    [Produces("application/javascript")]
    public Task<ContentResult> GetRecordingsJs() => Embedded("recordings.js");

    /// <summary>The trash on Live TV > Recordings, and trash wording in delete confirmations; guidemenu.js loads it.</summary>
    [HttpGet("trash.js")]
    [Produces("application/javascript")]
    public Task<ContentResult> GetTrashJs() => Embedded("trash.js");

    private async Task<ContentResult> Embedded(string fileName)
    {
        var assembly = typeof(WebAssetsController).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Web.{fileName}";
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return Content(await reader.ReadToEndAsync().ConfigureAwait(false), "application/javascript");
    }
}
