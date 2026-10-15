using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Joins the pieces of a recording that was interrupted (a server restart, a tuner tuned again
/// after a stall: Jellyfin continues in "Name - 1.ts", "Name - 2.ts"…) into one file, without
/// re-encoding. The joined file is checked against the pieces' lengths before anything is
/// replaced; the pieces are then deleted or kept hidden beside it for a few days.
/// </summary>
public static class PieceJoiner
{
    /// <summary>What a kept piece is renamed to: ".Name - 1.piece.ts" (hidden, so the library ignores it).</summary>
    public const string PieceMarker = ".piece";

    public sealed record Result(bool Ok, string Message, double Seconds = 0);

    /// <summary>
    /// Joins <paramref name="pieces"/> (in playing order) into <paramref name="target"/>, which
    /// may be one of them. <paramref name="keepPieces"/>: rename the pieces to hidden files
    /// instead of deleting them.
    /// </summary>
    public static async Task<Result> JoinAsync(
        string ffmpeg,
        string ffprobe,
        IReadOnlyList<string> pieces,
        string target,
        bool keepPieces,
        ILogger logger,
        CancellationToken ct)
    {
        if (pieces.Count < 2)
        {
            return new Result(false, "There is only one piece.");
        }

        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        var list = Path.Combine(dir, "." + stem + ".joining.txt");
        var output = Path.Combine(dir, "." + stem + ".joining" + ext);
        try
        {
            double expected = 0;
            foreach (var piece in pieces)
            {
                var seconds = await DurationAsync(ffprobe, piece, ct).ConfigureAwait(false);
                if (seconds is not > 0)
                {
                    return new Result(false, "Couldn't read the length of " + Path.GetFileName(piece) + ".");
                }

                expected += seconds.Value;
            }

            // The concat demuxer's own quoting: a quote inside a quoted name is '\''.
            await File.WriteAllLinesAsync(
                list,
                pieces.Select(p => "file '" + p.Replace("'", "'\\''", StringComparison.Ordinal) + "'"),
                new UTF8Encoding(false),
                ct).ConfigureAwait(false);

            var (code, error) = await RunAsync(
                ffmpeg,
                new[] { "-hide_banner", "-loglevel", "error", "-y", "-fflags", "+genpts", "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-ignore_unknown", "-c", "copy", output },
                ct).ConfigureAwait(false);
            if (code != 0 || !File.Exists(output))
            {
                return new Result(false, "ffmpeg couldn't join the pieces (" + Tail(error) + ").");
            }

            var joined = await DurationAsync(ffprobe, output, ct).ConfigureAwait(false);
            var tolerance = Math.Max(5, expected * 0.02);
            if (joined is not > 0 || Math.Abs(joined.Value - expected) > tolerance)
            {
                return new Result(false, string.Format(
                    CultureInfo.InvariantCulture,
                    "The joined file is {0:F0} s long but the pieces add up to {1:F0} s, so the pieces were left as they are.",
                    joined ?? 0,
                    expected));
            }

            foreach (var piece in pieces)
            {
                if (keepPieces)
                {
                    var hidden = Path.Combine(dir, "." + Path.GetFileNameWithoutExtension(piece) + PieceMarker + Path.GetExtension(piece));
                    File.Move(piece, hidden, true);
                    File.SetLastWriteTimeUtc(hidden, DateTime.UtcNow); // its keep time counts from now
                }
                else
                {
                    File.Delete(piece);
                }
            }

            File.Move(output, target, true);
            logger.LogInformation("Joined {Count} pieces into {Target} ({Seconds:F0} s)", pieces.Count, target, joined.Value);
            return new Result(true, "Joined " + pieces.Count + " pieces.", joined.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Couldn't join the pieces of {Target}", target);
            return new Result(false, "Joining failed: " + ex.Message);
        }
        finally
        {
            TryDelete(list);
            TryDelete(output);
        }
    }

    /// <summary>Deletes kept pieces (".Name.piece.ext") older than <paramref name="days"/> under the folders; how many went.</summary>
    public static int DeleteOldPieces(IEnumerable<string> folders, int days, ILogger logger)
    {
        var deleted = 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(0, days));
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, ".*" + PieceMarker + ".*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileNameWithoutExtension(file).EndsWith(PieceMarker, StringComparison.Ordinal)
                        && File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                        logger.LogInformation("Deleted the kept piece {File}", file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Couldn't clear kept pieces under {Folder}", folder);
            }
        }

        return deleted;
    }

    /// <summary>A media file's length in seconds, or null.</summary>
    public static async Task<double?> DurationAsync(string ffprobe, string file, CancellationToken ct)
    {
        var (code, _, output) = await RunWithOutputAsync(
            ffprobe,
            new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file },
            ct).ConfigureAwait(false);
        return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }

    private static async Task<(int Code, string Error)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var (code, error, _) = await RunWithOutputAsync(exe, args, ct).ConfigureAwait(false);
        return (code, error);
    }

    private static async Task<(int Code, string Error, string Output)> RunWithOutputAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start " + exe);
        try
        {
            var error = process.StandardError.ReadToEndAsync(ct);
            var output = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await error.ConfigureAwait(false), await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private static string Tail(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no message";
        return line.Length > 200 ? line[..200] : line;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
