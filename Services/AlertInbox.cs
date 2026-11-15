using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Reports problems to JellyDVR Schedule's alerts (phone, email, the TV's Problems list) without a
/// code dependency: each report is a JSON file dropped in <c>&lt;data&gt;/jellydvr-alerts/inbox/</c>,
/// which Schedule reads and removes within seconds. Without Schedule installed the folder just
/// isn't read, so old reports are cleared here after a day.
/// </summary>
public sealed class AlertInbox
{
    public const string Failed = "failed";
    public const string Short = "short";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan KeepUnread = TimeSpan.FromDays(1);

    private readonly string _dir;
    private readonly ILogger<AlertInbox> _logger;

    public AlertInbox(IApplicationPaths paths, ILogger<AlertInbox> logger)
    {
        _dir = Path.Combine(paths.DataPath, "jellydvr-alerts", "inbox");
        _logger = logger;
    }

    /// <summary>One report (the fields JellyDVR Schedule's AlertReport reads).</summary>
    public sealed class Report
    {
        public string Kind { get; set; } = Failed;

        /// <summary>The same key is the same problem: a later report updates or clears it.</summary>
        public string Key { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string? Next { get; set; }

        public string? Where { get; set; }

        public DateTime? AiredUtc { get; set; }

        public Guid? ItemId { get; set; }

        public Guid? ProgramId { get; set; }

        public string Source { get; set; } = "Episode History";

        public bool Fixed { get; set; }

        public bool UpdateOnly { get; set; }
    }

    /// <summary>The key for a recording problem, by airing (see <see cref="Programmes.KeyFor"/>).</summary>
    public static string KeyFor(string airingKey) => "rec:" + airingKey;

    public void Send(Report r)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var old in Directory.GetFiles(_dir, "*.json"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > KeepUnread)
                {
                    File.Delete(old);
                }
            }

            // Written beside, then renamed: the reader never sees half a file.
            var name = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            var tmp = Path.Combine(_dir, name + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, JsonOptions));
            File.Move(tmp, Path.Combine(_dir, name + ".json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not hand the alert to JellyDVR Schedule: {Title}", r.Title);
        }
    }
}
