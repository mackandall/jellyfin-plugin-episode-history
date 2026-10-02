using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.EpisodeHistory.Configuration;

namespace Jellyfin.Plugin.EpisodeHistory.History;

/// <summary>
/// Composes and strips the marks put in front of a guide entry's episode title, e.g.
/// "✓● What's in the Box?". The episode title is the field every client (jellyfin-web,
/// Wholphin, Moonfin, Android TV) shows in the guide cell. Entries with no episode title
/// (most movies) get the symbols plus a word, e.g. "✓ Watched".
///
/// Stripping recognises the configured symbols, the defaults and every symbol used before
/// (<see cref="PluginConfiguration.UsedSymbols"/>), so changing a symbol in the settings
/// doesn't strand old marks in the guide.
/// </summary>
public sealed class GuideMarker
{
    private const string WatchedLabel = "Watched";
    private const string InLibraryLabel = "In library";
    private const string RecordedBeforeLabel = "Recorded before";
    private const string SkippedLabel = "Not recording";
    private const string OtherAiringLabel = "Recording another airing";

    private static readonly string[] Labels = { WatchedLabel, InLibraryLabel, RecordedBeforeLabel, SkippedLabel, OtherAiringLabel };

    private readonly string _watched;
    private readonly string _inLibrary;
    private readonly string _recordedBefore;
    private readonly string _skipped;
    private readonly string _otherAiring;
    private readonly Regex _prefix;

    public GuideMarker(PluginConfiguration config)
    {
        _watched = config.WatchedSymbol;
        _inLibrary = config.InLibrarySymbol;
        _recordedBefore = config.RecordedBeforeSymbol;
        _skipped = config.SkippedSymbol;
        _otherAiring = config.OtherAiringSymbol;

        var symbols = new[]
            {
                _watched, _inLibrary, _recordedBefore, _skipped, _otherAiring,
                PluginConfiguration.DefaultWatchedSymbol, PluginConfiguration.DefaultInLibrarySymbol, PluginConfiguration.DefaultRecordedBeforeSymbol,
                PluginConfiguration.DefaultSkippedSymbol, PluginConfiguration.DefaultOtherAiringSymbol
            }
            .Concat(config.UsedSymbols ?? Array.Empty<string>())
            .Where(sym => !string.IsNullOrEmpty(sym))
            .Distinct()
            .OrderByDescending(s => s.Length)
            .Select(Regex.Escape);
        _prefix = new Regex("^(?:" + string.Join('|', symbols) + ")+\\s*", RegexOptions.CultureInvariant);
    }

    /// <summary>The episode title with any marks removed; null if nothing real is left.</summary>
    public string? Strip(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var match = _prefix.Match(title);
        if (!match.Success)
        {
            return title;
        }

        var rest = title[match.Length..];
        return rest.Length == 0 || Labels.Contains(rest, StringComparer.Ordinal) ? null : rest;
    }

    public bool IsMarked(string? title) => !string.IsNullOrEmpty(title) && _prefix.IsMatch(title);

    /// <summary>
    /// The title to show for <paramref name="state"/>, given the unmarked title. A JellyDVR
    /// Schedule mark (<paramref name="schedule"/>) comes first: it's the one to act on.
    /// </summary>
    public string? Compose(string? original, HistoryState state, ScheduleMark schedule = ScheduleMark.None)
    {
        if (!state.Any && schedule == ScheduleMark.None)
        {
            return original;
        }

        var sb = new StringBuilder();
        if (schedule == ScheduleMark.Skipped)
        {
            sb.Append(_skipped);
        }
        else if (schedule == ScheduleMark.OtherAiring)
        {
            sb.Append(_otherAiring);
        }

        if (state.Watched)
        {
            sb.Append(_watched);
        }

        if (state.InLibrary)
        {
            sb.Append(_inLibrary);
        }
        else if (state.RecordedBefore)
        {
            sb.Append(_recordedBefore);
        }

        sb.Append(' ');
        if (string.IsNullOrEmpty(original))
        {
            sb.Append(
                state.Watched ? WatchedLabel
                : state.InLibrary ? InLibraryLabel
                : state.RecordedBefore ? RecordedBeforeLabel
                : schedule == ScheduleMark.Skipped ? SkippedLabel
                : OtherAiringLabel);
        }
        else
        {
            sb.Append(original);
        }

        return sb.ToString();
    }
}

/// <summary>What JellyDVR Schedule did to a guide entry.</summary>
public enum ScheduleMark
{
    None,

    /// <summary>Not recording: lost a tuner conflict.</summary>
    Skipped,

    /// <summary>Recorded at another airing (moved there, or this copy dropped as a duplicate).</summary>
    OtherAiring
}

/// <summary>What the history knows about one episode.</summary>
public readonly record struct HistoryState(bool Watched, bool InLibrary, bool RecordedBefore)
{
    public bool Any => Watched || InLibrary || RecordedBefore;
}
