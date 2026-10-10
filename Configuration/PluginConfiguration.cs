using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EpisodeHistory.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultWatchedSymbol = "✓";
    public const string DefaultInLibrarySymbol = "●";
    public const string DefaultRecordedBeforeSymbol = "○";
    public const string DefaultSkippedSymbol = "🟧";
    public const string DefaultOtherAiringSymbol = "↪";

    // Trimmed on set: the config page tends to leave stray whitespace on pasted values.
    private string _watchedSymbol = DefaultWatchedSymbol;
    private string _inLibrarySymbol = DefaultInLibrarySymbol;
    private string _recordedBeforeSymbol = DefaultRecordedBeforeSymbol;
    private string _skippedSymbol = DefaultSkippedSymbol;
    private string _otherAiringSymbol = DefaultOtherAiringSymbol;

    /// <summary>
    /// Prefix guide entries' episode titles with watched / in-library / recorded-before
    /// symbols. Turning this off strips any marks already applied on the next sweep.
    /// </summary>
    public bool EnableGuideMarks { get; set; } = true;

    /// <summary>
    /// Cancel a series recording's upcoming episode while a recorded copy of that episode
    /// is currently in the library. Manual one-off recordings are never touched.
    /// </summary>
    public bool EnableDuplicateSkipping { get; set; } = true;

    /// <summary>
    /// With duplicate skipping: also cancel a series recording's upcoming episode that someone
    /// on this server has already watched (whether or not a copy is still in the library).
    /// "Forget this episode" / "Forget this show" make it record again.
    /// </summary>
    public bool SkipWatchedEpisodes { get; set; } = true;

    /// <summary>
    /// Mark guide entries JellyDVR Schedule took off the schedule: skipped for a tuner conflict,
    /// or recorded at another airing instead (moved, or a duplicate dropped).
    /// </summary>
    public bool EnableScheduleMarks { get; set; } = true;

    /// <summary>
    /// A finished recording only counts as "recorded" when it ran for at least this share
    /// of the programme's scheduled length, so a recording cut short (stopped by hand, tuner
    /// lost, server restarted) doesn't block the next airing.
    /// </summary>
    public int MinRecordedPercent { get; set; } = 80;

    /// <summary>
    /// Also treat episodes and movies from every library (not only DVR recordings) as "in
    /// library": they get the in-library mark and series recordings skip them.
    /// </summary>
    public bool CountAllLibraries { get; set; }

    /// <summary>
    /// A file in the recordings folders that the plugin didn't see being recorded (library
    /// import, or copied in by hand) only counts as recorded if it is at least this long, so
    /// a stub from a recording stopped early doesn't block the next airing.
    /// </summary>
    public int MinRecordingMinutes { get; set; } = 5;

    /// <summary>
    /// Every mark symbol the plugin has put in the guide. Marks are recognised (and removed)
    /// by these as well as the current and default symbols, so changing a symbol never
    /// leaves the old one stacked in front of titles. Maintained by the plugin.
    /// </summary>
    public string[] UsedSymbols { get; set; } = System.Array.Empty<string>();

    /*
     * The web client's home-screen recordings row (SageTV's Recordings view): what the DVR
     * recorded, newest first, including recordings since moved to an archive folder.
     */

    /// <summary>Show the recordings row on the web client's home screen.</summary>
    public bool RecordingsRowEnabled { get; set; } = true;

    /// <summary>The row's heading.</summary>
    public string RecordingsRowTitle { get; set; } = "Recorded Shows";

    /// <summary>
    /// Where the row goes: "top", "after-continue-watching", "after-next-up",
    /// "before-latest" (ahead of the "Recently Added in …" rows) or "bottom".
    /// </summary>
    public string RecordingsRowPosition { get; set; } = "before-latest";

    /// <summary>One card per show (with a count) instead of one per recording.</summary>
    public bool RecordingsRowGroupByShow { get; set; } = true;

    /// <summary>Include movie recordings (they already have their own library rows).</summary>
    public bool RecordingsRowIncludeMovies { get; set; }

    /// <summary>Most cards in the row.</summary>
    public int RecordingsRowLimit { get; set; } = 24;

    /// <summary>
    /// Which recordings the row shows until a viewer picks otherwise: "all", "current"
    /// (still in the recordings folders) or "archived" (moved to an archive folder).
    /// </summary>
    public string RecordingsRowDefaultFilter { get; set; } = "all";

    /// <summary>
    /// Live TV > Recordings: "replace" shows the recordings grid instead of Jellyfin's
    /// "Recently Added Recordings" (which mixes in everything from the libraries holding the
    /// recordings folders), "add" shows it above that row, "off" leaves the tab alone.
    /// </summary>
    public string RecordingsTabMode { get; set; } = "replace";

    /// <summary>Most cards in the Live TV > Recordings grid.</summary>
    public int RecordingsTabLimit { get; set; } = 200;

    /// <summary>
    /// Show a "Recordings" row on a show's page (above Next Up) listing that show's DVR
    /// recordings, newest first, archived ones included.
    /// </summary>
    public bool ShowPageRecordingsEnabled { get; set; } = true;

    /// <summary>
    /// Also list files in the recordings folders that don't count as recorded (cut short, failed
    /// stubs), marked "Partial", so they're easy to find and delete. They stay out of the history.
    /// </summary>
    public bool ShowPartialRecordings { get; set; } = true;

    public string WatchedSymbol
    {
        get => _watchedSymbol;
        set => _watchedSymbol = Clean(value, DefaultWatchedSymbol);
    }

    public string InLibrarySymbol
    {
        get => _inLibrarySymbol;
        set => _inLibrarySymbol = Clean(value, DefaultInLibrarySymbol);
    }

    public string RecordedBeforeSymbol
    {
        get => _recordedBeforeSymbol;
        set => _recordedBeforeSymbol = Clean(value, DefaultRecordedBeforeSymbol);
    }

    public string SkippedSymbol
    {
        get => _skippedSymbol;
        set => _skippedSymbol = Clean(value, DefaultSkippedSymbol);
    }

    public string OtherAiringSymbol
    {
        get => _otherAiringSymbol;
        set => _otherAiringSymbol = Clean(value, DefaultOtherAiringSymbol);
    }

    private static string Clean(string? value, string fallback)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? fallback : v;
    }
}
