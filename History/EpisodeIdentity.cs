using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.EpisodeHistory.History;

/// <summary>
/// What identifies "the same episode" across a guide entry, a timer and a library item.
///
/// Matching mirrors SageTV: the Gracenote/TMS programme id (<c>EP000191100039</c>) is
/// authoritative when both sides have one. Without it, fall back to normalised series
/// name + season/episode, then series name + episode title. Movies match on normalised
/// title + year.
///
/// Library items rarely carry the TMS id (recording NFOs don't store it), so rows captured
/// when a recording finishes take it from the timer, and rows imported from the library
/// rely on the name keys.
/// </summary>
public sealed record EpisodeIdentity(
    string? ProgramId,
    string? SeriesName,
    string? SeriesAlt,
    int? Season,
    int? Episode,
    string? EpisodeTitle,
    bool IsMovie,
    int? Year)
{
    // EP = episode, MV = movie, SP = sports event. SH (show-level) ids and EP ids ending in
    // 0000 are "generic" — they name a series, not an episode — so they identify nothing.
    private static readonly Regex TmsId = new(@"^(EP|MV|SP)\d{8,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingYear = new(@"\s*\(\d{4}\)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The id if it is a specific TMS episode/movie id, else null.</summary>
    public string? ValidProgramId =>
        ProgramId is { } id && TmsId.IsMatch(id) && !(id.StartsWith("EP", StringComparison.Ordinal) && id.EndsWith("0000", StringComparison.Ordinal))
            ? id
            : null;

    /// <summary>Name-based match keys (everything except the programme id).</summary>
    public IEnumerable<string> NameKeys()
    {
        var names = new List<string>(2);
        AddName(names, SeriesName);
        AddName(names, SeriesAlt);

        foreach (var name in names)
        {
            if (IsMovie)
            {
                yield return "m:" + name + "|" + (Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                continue;
            }

            if (Season is > 0 && Episode is > 0)
            {
                yield return string.Format(CultureInfo.InvariantCulture, "e:{0}|{1}|{2}", name, Season, Episode);
            }

            var title = Normalize(EpisodeTitle);
            if (title.Length >= 3)
            {
                yield return "t:" + name + "|" + title;
            }
        }
    }

    private static void AddName(List<string> names, string? raw)
    {
        var n = Normalize(raw);
        if (n.Length > 0 && !names.Contains(n))
        {
            names.Add(n);
        }
    }

    /// <summary>
    /// Case/accent/punctuation-insensitive form: "The Twilight Zone (1959)" and
    /// "twilight zone" compare equal, as do "Law &amp; Order" and "Law and Order".
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var s = TrailingYear.Replace(value, string.Empty).Replace("&", " and ", StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                sb.Append(' ');
            }

            // Everything else (combining accents, symbols) is dropped.
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = words.Length > 1 && words[0] == "the" ? 1 : 0;
        return string.Concat(words.Skip(start));
    }

    public static EpisodeIdentity FromProgram(LiveTvProgram program, GuideMarker marker)
    {
        return new EpisodeIdentity(
            program.ShowId,
            program.Name,
            null,
            program.ParentIndexNumber,
            program.IndexNumber,
            marker.Strip(program.EpisodeTitle),
            program.IsMovie,
            program.ProductionYear);
    }

    public static EpisodeIdentity FromTimer(TimerInfo timer, GuideMarker marker, string? librarySeriesName)
    {
        var isMovie = timer.IsMovie && !timer.IsProgramSeries;
        return new EpisodeIdentity(
            timer.ShowId,
            timer.Name,
            librarySeriesName,
            isMovie ? null : timer.SeasonNumber,
            isMovie ? null : timer.EpisodeNumber,
            isMovie ? null : marker.Strip(timer.EpisodeTitle),
            isMovie,
            timer.ProductionYear);
    }

    /// <summary>Identity of a library episode or movie; null for anything else.</summary>
    public static EpisodeIdentity? FromLibraryItem(BaseItem item, GuideMarker marker, string? programId)
    {
        switch (item)
        {
            case Episode ep:
                // The series folder name is the guide title the DVR created it under
                // ("Last Week Tonight With John Oliver (2014)"), which survives the
                // metadata provider renaming the series itself.
                return new EpisodeIdentity(
                    programId,
                    ep.SeriesName,
                    FolderName(ep.Series?.Path),
                    ep.ParentIndexNumber,
                    ep.IndexNumber,
                    marker.Strip(ep.Name),
                    false,
                    null);
            case Movie movie:
                return new EpisodeIdentity(
                    programId,
                    movie.Name,
                    string.IsNullOrWhiteSpace(movie.OriginalTitle) ? FolderName(Path.GetDirectoryName(movie.Path)) : movie.OriginalTitle,
                    null,
                    null,
                    null,
                    true,
                    movie.ProductionYear);
            default:
                return null;
        }
    }

    private static string? FolderName(string? path)
    {
        return string.IsNullOrEmpty(path) ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
    }
}
