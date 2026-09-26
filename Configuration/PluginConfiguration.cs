using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EpisodeHistory.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultWatchedSymbol = "✓";
    public const string DefaultInLibrarySymbol = "●";
    public const string DefaultRecordedBeforeSymbol = "○";

    // Trimmed on set: the config page tends to leave stray whitespace on pasted values.
    private string _watchedSymbol = DefaultWatchedSymbol;
    private string _inLibrarySymbol = DefaultInLibrarySymbol;
    private string _recordedBeforeSymbol = DefaultRecordedBeforeSymbol;

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
    /// A finished recording only counts as "recorded" when it ran for at least this share
    /// of the programme's scheduled length, so a recording cut short (stopped by hand, tuner
    /// lost, server restarted) doesn't block the next airing.
    /// </summary>
    public int MinRecordedPercent { get; set; } = 80;

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

    private static string Clean(string? value, string fallback)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? fallback : v;
    }
}
