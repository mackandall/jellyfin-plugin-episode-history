using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.EpisodeHistory.Configuration;
using Jellyfin.Plugin.EpisodeHistory.History;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Mends recordings that came out in pieces or incomplete, from what <see cref="Programmes"/>
/// knows of each airing:
/// <list type="bullet">
/// <item>Pieces of an interrupted recording are joined into one file once the airing is over.</item>
/// <item>A recording that holds less of the programme than the "complete" setting is recorded
/// again at the next airing found in the guide; when that one is complete, the incomplete files
/// are deleted (or kept, by setting).</item>
/// </list>
/// Run from the recordings poll, after it has dealt with what just ended.
/// </summary>
public sealed class RecordingRepair
{
    // A stopped piece may be followed by another (the tuner tuned again, the server restarted):
    // the airing is over once its end has passed, or nothing more came for this long.
    private static readonly TimeSpan AfterProgramEnd = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AfterLastPiece = TimeSpan.FromMinutes(10);

    // Comskip and the cuts work on a recording right after it ends; leave them to finish.
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SearchEvery = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan CleanEvery = TimeSpan.FromHours(1);
    private static readonly Regex PieceSuffix = new(@" - (\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Programmes _programmes;
    private readonly Airings _airings;
    private readonly StallNotes _stalls;
    private readonly HistoryStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IRecordingsManager _recordingsManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RecordingRepair> _logger;
    private int _joining;
    private DateTime _lastSearch = DateTime.MinValue;
    private DateTime _lastClean = DateTime.MinValue;
    private readonly SemaphoreSlim _searching = new(1, 1);

    public RecordingRepair(
        Programmes programmes,
        Airings airings,
        StallNotes stalls,
        HistoryStore store,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IRecordingsManager recordingsManager,
        IServiceProvider serviceProvider,
        ILogger<RecordingRepair> logger)
    {
        _programmes = programmes;
        _airings = airings;
        _stalls = stalls;
        _store = store;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _recordingsManager = recordingsManager;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private ILiveTvManager LiveTv => _serviceProvider.GetRequiredService<ILiveTvManager>();

    /// <summary>An identity good enough to pick out another airing of the same thing by itself.</summary>
    public static bool Reliable(EpisodeIdentity? identity) =>
        identity is not null && (identity.ValidProgramId is not null || identity.NameKeys().Any());

    /// <summary>Whether the airing is over: no piece can follow any more.</summary>
    private static bool Over(Programmes.Programme p, DateTime now) =>
        p.LastEndedUtc is { } ended && (now >= p.ProgramEndUtc + AfterProgramEnd || now - ended >= AfterLastPiece);

    /// <summary>
    /// One round: joins what is ready, decides what needs recording again, and (every few
    /// minutes) looks in the guide for airings. <paramref name="busy"/>: files the poll is
    /// still dealing with.
    /// </summary>
    public async Task ProcessAsync(Func<string, bool> busy, CancellationToken ct)
    {
        var cfg = Config;
        var now = DateTime.UtcNow;

        if (now - _lastClean > CleanEvery)
        {
            _lastClean = now;
            PieceJoiner.DeleteOldPieces(RecordingFiles.Folders(_recordingsManager), cfg.JoinKeepPiecesDays, _logger);
            DeleteReplaced(cfg);
        }

        // Only airings with something left to do: undecided ones, and joined ones not yet in the history.
        foreach (var p in _programmes.All()
            .Where(p => p.State == Programmes.None || (p.JoinedPath is not null && p.Counted && !p.JoinCounted))
            .Where(p => Over(p, now) && !p.Pieces.Any(x => busy(x.Path))))
        {
            try
            {
                if (cfg.JoinSplitRecordings && p.State == Programmes.None && p.JoinedUtc is null && p.JoinTries < 3 && p.Files().Count >= 2 && !UnkeptBuffer(p))
                {
                    StartJoin(p, cfg);
                    continue; // decided once it is one file
                }

                CountJoined(p);
                Decide(p, cfg, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't look after the recording of {Title}", p.Title);
            }
        }

        // Asked-for re-records are looked after even with the automatic one turned off.
        if (now - _lastSearch > SearchEvery
            && _programmes.All().Any(p => p.State is Programmes.Waiting or Programmes.Scheduled && (cfg.AutoRerecord || p.Manual)))
        {
            _lastSearch = now;
            await SearchAsync(cfg, ct).ConfigureAwait(false);
        }
    }

    private static bool UnkeptBuffer(Programmes.Programme p) => p.Pieces.Any(x => LiveBuffers.IsUnkeptBuffer(x.Path));

    // ---- joining -----------------------------------------------------------------------------

    private void StartJoin(Programmes.Programme p, PluginConfiguration cfg)
    {
        var pieces = p.Files();
        var now = DateTime.UtcNow;
        if (pieces.Any(f => now - File.GetLastWriteTimeUtc(f) < Settle || IsOpenElsewhere(f))
            || Interlocked.Exchange(ref _joining, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await JoinFilesAsync(pieces, cfg.JoinKeepPiecesDays > 0, CancellationToken.None).ConfigureAwait(false);
                _programmes.Change(all =>
                {
                    if (!all.TryGetValue(p.Key, out var x))
                    {
                        return;
                    }

                    x.JoinTries++;
                    x.JoinNote = result.Message;
                    if (result.Ok)
                    {
                        x.JoinedPath = pieces[0];
                        x.JoinedUtc = DateTime.UtcNow;
                    }
                });
                if (result.Ok)
                {
                    AfterJoin(p, pieces);
                }
                else
                {
                    _logger.LogWarning("The pieces of {Title} weren't joined: {Message}", p.Title, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Joining the pieces of {Title} failed", p.Title);
            }
            finally
            {
                Volatile.Write(ref _joining, 0);
            }
        });
    }

    /// <summary>Joins files (in playing order) into the first of them and has the library look at the folder again.</summary>
    public async Task<PieceJoiner.Result> JoinFilesAsync(IReadOnlyList<string> pieces, bool keepPieces, CancellationToken ct)
    {
        var encoder = _serviceProvider.GetService<IMediaEncoder>();
        if (encoder?.EncoderPath is not { Length: > 0 } ffmpeg || encoder.ProbePath is not { Length: > 0 } ffprobe)
        {
            return new PieceJoiner.Result(false, "Jellyfin's ffmpeg isn't known yet.");
        }

        var result = await PieceJoiner.JoinAsync(ffmpeg, ffprobe, pieces, pieces[0], keepPieces, _logger, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            foreach (var gone in pieces.Skip(1))
            {
                RecordingTidy.DeleteSideFiles(gone, _logger);
            }

            _libraryMonitor.ReportFileSystemChanged(Path.GetDirectoryName(pieces[0])!);
        }

        return result;
    }

    /// <summary>The pieces of an item's recording still in the recordings folders, in playing order ("Name", "Name - 1"…).</summary>
    public static List<string> OrderPieces(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.Ordinal)
            .OrderBy(p => PieceSuffix.Match(Path.GetFileNameWithoutExtension(p)) is { Success: true } m
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0)
            .ToList();

    // History rows and stall notes of the pieces that are gone move to the file they became.
    private void AfterJoin(Programmes.Programme p, List<string> pieces)
    {
        var names = pieces.Skip(1).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var rows = _store.LoadRecorded().Where(r => r.FileName is not null && names.Contains(r.FileName)).Select(r => r.Id).ToList();
        if (rows.Count > 0)
        {
            _store.DeleteRows(rows);
        }

        foreach (var piece in pieces.Skip(1))
        {
            _stalls.Remove(piece);
        }

        var missing = p.MissingSeconds;
        var firstGap = p.Pieces.Where(x => x.FirstStallUtc is not null).Select(x => x.FirstStallUtc!.Value)
            .Concat(p.Pieces.OrderBy(x => x.FirstSeenUtc).SkipLast(1).Select(x => x.EndedUtc ?? x.FirstSeenUtc))
            .DefaultIfEmpty(DateTime.UtcNow).Min();
        if (Config.StallWarnSeconds > 0 && missing >= Config.StallWarnSeconds)
        {
            _stalls.Set(pieces[0], new StallNotes.Note(
                firstGap,
                (int)Math.Max(1, Math.Round(missing / 60)),
                (int)Math.Round(p.CapturedSeconds / 60),
                (int)Math.Round(p.ScheduledSeconds / 60),
                DateTime.UtcNow));
        }
        else
        {
            _stalls.Remove(pieces[0]);
        }

        _logger.LogInformation(
            "{Title}: {Count} pieces joined into {File}; {Captured:F0} of {Scheduled:F0} minutes ({Missing:F0} min missing)",
            p.Title, pieces.Count, Path.GetFileName(pieces[0]), p.CapturedSeconds / 60, p.ScheduledSeconds / 60, missing / 60);
    }

    // The joined file takes the airing's place in the history once the library has it.
    private void CountJoined(Programmes.Programme p)
    {
        if (p.JoinedPath is not { } joined || p.JoinCounted || !p.Counted || !File.Exists(joined)
            || RecordingFiles.ItemAt(_libraryManager, joined) is not { } item)
        {
            return;
        }

        if (!_store.HasRecorded(item.Id) && p.Identity is { } identity)
        {
            _store.Upsert(HistoryStore.KindRecorded, null, item.Id, identity, "recording", DateTime.UtcNow);
        }

        _programmes.Change(all =>
        {
            if (all.TryGetValue(p.Key, out var x))
            {
                x.JoinCounted = true;
            }
        });
    }

    // ---- recording again ---------------------------------------------------------------------

    private void Decide(Programmes.Programme p, PluginConfiguration cfg, DateTime now)
    {
        if (p.State != Programmes.None || p.Pieces.Count == 0)
        {
            return;
        }

        var complete = p.Percent >= Math.Clamp(cfg.RerecordBelowPercent, 50, 100);
        var last = p.Pieces.OrderBy(x => x.FirstSeenUtc).Last();
        var chains = _programmes.All()
            .Where(q => q.Key != p.Key && q.State is Programmes.Waiting or Programmes.Scheduled && Same(q, p))
            .ToList();
        string state;
        if (complete)
        {
            state = Programmes.Complete;
            foreach (var q in chains)
            {
                Replace(q, p, cfg);
            }
        }
        else if (chains.Count > 0)
        {
            // An incomplete try at replacing one: that one goes on looking, while it has tries left.
            state = Programmes.Attempt;
            var max = Math.Clamp(cfg.RerecordMaxAttempts, 1, 10);
            _programmes.Change(all =>
            {
                foreach (var q in chains.Select(c => all.GetValueOrDefault(c.Key)).OfType<Programmes.Programme>())
                {
                    q.State = q.Attempts >= max ? Programmes.GaveUp : Programmes.Waiting;
                    q.NextProgramId = null;
                    q.NextStartUtc = null;
                    q.NextChannel = null;
                }
            });
            _logger.LogInformation("{Title} was recorded again but came out incomplete too ({Percent:F0}%)", p.Title, p.Percent);
        }
        else if (!cfg.AutoRerecord || p.IsBuffer || !Reliable(p.Identity))
        {
            state = Programmes.Complete;
        }
        else if (last.StoppedByUser
            || (last.Cancelled && last.StalledSeconds < 30 && last.EndedUtc < p.ProgramEndUtc - TimeSpan.FromMinutes(2)))
        {
            // Cancelled early with data still arriving: someone (or the schedule) stopped it.
            state = Programmes.Stopped;
        }
        else
        {
            state = Programmes.Waiting;
            _lastSearch = DateTime.MinValue; // look in the guide at once
            _logger.LogInformation(
                "{Title} is incomplete ({Captured:F0} of {Scheduled:F0} minutes); it will be recorded again at the next airing found",
                p.Title, p.CapturedSeconds / 60, p.ScheduledSeconds / 60);
        }

        _programmes.Change(all =>
        {
            if (all.TryGetValue(p.Key, out var x) && x.State == Programmes.None)
            {
                x.State = state;
                x.FlaggedUtc = state == Programmes.Waiting ? now : null;
            }
        });
    }

    private static bool Same(Programmes.Programme a, Programmes.Programme b) =>
        a.Identity is { } x && b.Identity is { } y && Airings.Same(x, y);

    // A full copy is in: the old one (and incomplete tries at replacing it) are renamed out of the
    // library and deleted after the keep days, deleted at once, or kept, by setting.
    private void Replace(Programmes.Programme old, Programmes.Programme full, PluginConfiguration cfg)
    {
        var mode = (cfg.RerecordReplace ?? string.Empty).ToLowerInvariant() switch
        {
            "keep" => "keep",
            "delete" => "delete",
            _ => "rename"
        };
        var folders = RecordingFiles.Folders(_recordingsManager);
        var group = _programmes.All().Where(q => q.Key == old.Key || (q.State == Programmes.Attempt && Same(q, old))).ToList();
        var keep = full.Files().ToHashSet(StringComparer.Ordinal);
        var replaced = new Dictionary<string, List<Programmes.ReplacedFile>>(StringComparer.Ordinal);
        var done = 0;
        if (mode != "keep")
        {
            foreach (var q in group)
            {
                // An asked-for re-record may be of a recording since moved to an archive; the
                // automatic ones only ever touch the recordings folders.
                foreach (var file in FilesOf(q).Where(f => !keep.Contains(f) && (q.Manual || RecordingFiles.IsUnder(f, folders))))
                {
                    if (_recordingsManager.GetActiveRecordingInfo(file) is not null)
                    {
                        continue;
                    }

                    try
                    {
                        if (mode == "delete")
                        {
                            if (RecordingFiles.ItemAt(_libraryManager, file) is { } item)
                            {
                                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
                            }
                            else
                            {
                                File.Delete(file);
                                RecordingTidy.DeleteSideFiles(file, _logger);
                            }
                        }
                        else
                        {
                            var entry = new Programmes.ReplacedFile { Path = file, ReplacedUtc = DateTime.UtcNow };
                            File.Move(file, entry.RenamedPath, true);
                            _libraryMonitor.ReportFileSystemChanged(Path.GetDirectoryName(file)!);
                            if (!replaced.TryGetValue(q.Key, out var list))
                            {
                                replaced[q.Key] = list = new List<Programmes.ReplacedFile>();
                            }

                            list.Add(entry);
                        }

                        _stalls.Remove(file);
                        done++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        _logger.LogWarning(ex, "Couldn't {What} the replaced recording {File}", mode, file);
                    }
                }
            }
        }

        _programmes.Change(all =>
        {
            foreach (var q in group.Select(g => all.GetValueOrDefault(g.Key)).OfType<Programmes.Programme>())
            {
                q.State = Programmes.Replaced;
                if (replaced.TryGetValue(q.Key, out var list))
                {
                    q.ReplacedFiles.AddRange(list);
                }
            }
        });
        _logger.LogInformation(
            "{Title} was recorded again in full ({Percent:F0}%); {What}",
            full.Title,
            full.Percent,
            mode switch
            {
                "delete" => done + " old file(s) deleted",
                "rename" => done + " old file(s) renamed, to be deleted in " + Math.Clamp(cfg.RerecordKeepDays, 0, 365) + " days",
                _ => "the old files were kept"
            });
    }

    // Where an airing's recording is now: the library item it was asked for on (it may have moved
    // to an archive), else its files in the recordings folders.
    private List<string> FilesOf(Programmes.Programme q) =>
        q.ItemId is { } id && _libraryManager.GetItemById(id) is Video v && !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path)
            ? new List<string> { v.Path }
            : q.Files();

    // Replaced recordings past the keep days go for good, with their side files (unless a new
    // recording took the same name in the same folder).
    private void DeleteReplaced(PluginConfiguration cfg)
    {
        var days = Math.Clamp(cfg.RerecordKeepDays, 0, 365);
        var now = DateTime.UtcNow;
        var gone = new List<(string Key, string Path)>();
        foreach (var p in _programmes.All())
        {
            foreach (var f in p.ReplacedFiles.Where(f => !f.Deleted && now - f.ReplacedUtc >= TimeSpan.FromDays(days)))
            {
                try
                {
                    if (File.Exists(f.RenamedPath))
                    {
                        File.Delete(f.RenamedPath);
                    }

                    if (!File.Exists(f.Path))
                    {
                        RecordingTidy.DeleteSideFiles(f.Path, _logger);
                    }

                    gone.Add((p.Key, f.Path));
                    _logger.LogInformation("Deleted {File}, replaced by a new recording {Days} days ago", f.RenamedPath, days);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Couldn't delete the replaced recording {File}", f.RenamedPath);
                }
            }
        }

        if (gone.Count > 0)
        {
            _programmes.Change(all =>
            {
                foreach (var (key, path) in gone)
                {
                    if (all.TryGetValue(key, out var p) && p.ReplacedFiles.FirstOrDefault(f => f.Path == path) is { } f)
                    {
                        f.Deleted = true;
                    }
                }
            });
        }
    }

    private async Task SearchAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        await _searching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SearchLockedAsync(cfg, ct).ConfigureAwait(false);
        }
        finally
        {
            _searching.Release();
        }
    }

    private async Task SearchLockedAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sameChannel = string.Equals(cfg.RerecordChannelScope, "same", StringComparison.OrdinalIgnoreCase);
        var max = Math.Clamp(cfg.RerecordMaxAttempts, 1, 10);
        var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
        bool Active(string? programId) => programId is not null && timers.Any(t =>
            string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase)
            && t.Status is not (RecordingStatus.Cancelled or RecordingStatus.Completed or RecordingStatus.Error));

        foreach (var p in _programmes.All().Where(p => p.State is Programmes.Waiting or Programmes.Scheduled && (cfg.AutoRerecord || p.Manual)))
        {
            if (p.State == Programmes.Scheduled)
            {
                if (Active(p.NextProgramId) || (p.NextStartUtc is { } start && now > start && now - start < TimeSpan.FromHours(12)))
                {
                    continue; // set, on now, or just over and being decided
                }

                // Its recording was taken off the schedule before it ran: someone doesn't want it.
                var cancelled = p.NextStartUtc is { } s && now < s;
                Set(p, x =>
                {
                    x.State = cancelled ? Programmes.Kept : Programmes.Waiting;
                    x.NextProgramId = null;
                });
                if (cancelled)
                {
                    _logger.LogInformation("{Title}: the recording set to replace it was cancelled, so it is kept as it is", p.Title);
                    continue;
                }
            }

            if (p.Attempts >= max || now - (p.FlaggedUtc ?? p.CreatedUtc) > TimeSpan.FromDays(Math.Clamp(cfg.RerecordGiveUpDays, 1, 365)))
            {
                Set(p, x => x.State = Programmes.GaveUp);
                _logger.LogInformation("{Title}: gave up on recording it again ({Attempts} tries)", p.Title, p.Attempts);
                continue;
            }

            if (p.Identity is not { } identity)
            {
                continue;
            }

            // An airing with a recording set already will do; else the best of the rest (a free
            // tuner, commercial-free, soonest). "The same channel" applies to the automatic ones.
            var found = await _airings.FindAsync(identity, ct).ConfigureAwait(false);
            bool OnChannel(Airings.Airing a) => !sameChannel || p.Manual
                || string.Equals((_libraryManager.GetItemById(a.ChannelId) as LiveTvChannel)?.ExternalId, p.ChannelId, StringComparison.OrdinalIgnoreCase);
            var next = found.FirstOrDefault(a => a.StartUtc > now && a.Recording is not null && OnChannel(a))
                ?? Airings.Best(found.Where(a => a.Recording is null), now, OnChannel);
            if (next is null)
            {
                continue;
            }

            var programId = next.ProgramId.ToString("N");
            var ours = !Active(programId);
            try
            {
                if (ours)
                {
                    await ScheduleAsync(programId, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't schedule {Title} again", p.Title);
                continue;
            }

            var channel = next.ChannelName;
            Set(p, x =>
            {
                x.State = Programmes.Scheduled;
                x.Attempts++;
                x.NextProgramId = programId;
                x.NextStartUtc = next.StartUtc;
                x.NextChannel = channel;
                x.NextIsOurs = ours;
            });
            _logger.LogInformation(
                "{Title} will be recorded again on {Start:u} ({Channel}){How}",
                p.Title, next.StartUtc, channel, ours ? string.Empty : ": a recording was already set for it");
        }
    }

    // ---- recordings that produced nothing (see ScheduleLedger) --------------------------------

    /// <summary>
    /// Airings the ledger found produced no recording at all: each goes on the list to be
    /// recorded at the next airing (or, when it was itself the airing set to replace an
    /// incomplete recording, that one goes on looking), and the admins are told.
    /// </summary>
    public async Task NoteFailedAsync(IReadOnlyList<ScheduleLedger.Entry> failed, CancellationToken ct)
    {
        if (failed.Count == 0)
        {
            return;
        }

        var cfg = Config;
        var max = Math.Clamp(cfg.RerecordMaxAttempts, 1, 10);
        var now = DateTime.UtcNow;
        var lines = new List<string>();
        foreach (var e in failed)
        {
            string next;
            var chains = _programmes.All()
                .Where(q => q.State is Programmes.Waiting or Programmes.Scheduled && q.Identity is { } x && e.Identity is { } y && x.SameEpisode(y))
                .ToList();
            if (chains.Count > 0)
            {
                // The recording set to replace an incomplete one failed: that one goes on looking.
                var gaveUp = false;
                _programmes.Change(all =>
                {
                    foreach (var q in chains.Select(c => all.GetValueOrDefault(c.Key)).OfType<Programmes.Programme>())
                    {
                        q.State = q.Attempts >= max ? Programmes.GaveUp : Programmes.Waiting;
                        gaveUp |= q.State == Programmes.GaveUp;
                        q.NextProgramId = null;
                        q.NextStartUtc = null;
                        q.NextChannel = null;
                    }
                });
                next = gaveUp ? "It was a second try at an incomplete recording; no more tries are left." : "Another airing will be looked for.";
            }
            else if (!cfg.AutoRerecord)
            {
                next = "Recording again is turned off.";
            }
            else if (!Reliable(e.Identity))
            {
                next = "The guide can't tell this episode from the others, so it isn't recorded again by itself.";
            }
            else
            {
                _programmes.Change(all => all.TryAdd(e.Key, new Programmes.Programme
                {
                    Key = e.Key,
                    Title = e.Title,
                    Identity = e.Identity,
                    ChannelId = e.ChannelId,
                    ProgramStartUtc = e.StartUtc,
                    ProgramEndUtc = e.EndUtc,
                    CreatedUtc = now,
                    State = Programmes.Waiting,
                    FlaggedUtc = now
                }));
                next = "It will be recorded at the next airing found in the guide.";
            }

            if (next.StartsWith("It will", StringComparison.Ordinal) || next.StartsWith("Another", StringComparison.Ordinal))
            {
                _lastSearch = DateTime.MinValue; // look in the guide at once
            }

            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1}{2:ddd h:mm tt}) didn't record. {3} {4}",
                e.Title,
                e.ChannelName is null ? string.Empty : (e.ChannelNumber is null ? string.Empty : e.ChannelNumber + " ") + e.ChannelName + ", ",
                e.StartUtc.ToLocalTime(),
                e.Reason,
                next));
        }

        var text = string.Join("\n", lines);
        _logger.LogWarning("Recordings that didn't record: {Text}", text);
        await LogActivityAsync(failed.Count == 1 ? "Recording failed: " + failed[0].Title : failed.Count + " recordings failed", text).ConfigureAwait(false);
        if (cfg.FailedRecordingOnScreen)
        {
            await TellAdminsAsync(text, ct).ConfigureAwait(false);
        }
    }

    private async Task LogActivityAsync(string name, string overview)
    {
        try
        {
            await _serviceProvider.GetRequiredService<IActivityManager>().CreateAsync(new ActivityLog(name, "EpisodeHistoryRecordingFailed", Guid.Empty)
            {
                Overview = overview,
                ShortOverview = "Episode History",
                LogSeverity = LogLevel.Warning
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not add the activity log entry");
        }
    }

    private async Task TellAdminsAsync(string text, CancellationToken ct)
    {
        var sessions = _serviceProvider.GetRequiredService<ISessionManager>();
        var users = _serviceProvider.GetRequiredService<IUserManager>();
        var cutoff = DateTime.UtcNow.AddMinutes(-15);
        foreach (var s in sessions.Sessions
            .Where(s => s.IsActive && s.LastActivityDate > cutoff && !s.UserId.Equals(Guid.Empty))
            .Where(s => users.GetUserById(s.UserId)?.HasPermission(PermissionKind.IsAdministrator) == true)
            .ToList())
        {
            try
            {
                await sessions.SendMessageCommand(
                    null!,
                    s.Id,
                    new MessageCommand { Header = "Recording failed", Text = text, TimeoutMs = 60000 },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not show the alert on {Device}", s.DeviceName);
            }
        }
    }

    /// <summary>"Don't record it again" for an airing on the failed list (by its key).</summary>
    public Task KeepAiringAsync(string key, CancellationToken ct) =>
        _programmes.All().FirstOrDefault(p => p.Key == key) is { } p ? KeepAsync(p, ct) : Task.CompletedTask;

    /// <summary>Schedules a one-off recording of a guide entry (library id), with the usual padding.</summary>
    public async Task ScheduleAsync(string programId, CancellationToken ct)
    {
        var defaults = await LiveTv.GetNewTimerDefaults(programId, ct).ConfigureAwait(false);
        await LiveTv.CreateTimer(
            new TimerInfoDto
            {
                ProgramId = programId,
                ChannelId = defaults.ChannelId,
                ServiceName = defaults.ServiceName,
                Name = defaults.Name,
                StartDate = defaults.StartDate,
                EndDate = defaults.EndDate,
                PrePaddingSeconds = defaults.PrePaddingSeconds,
                PostPaddingSeconds = defaults.PostPaddingSeconds,
                IsPrePaddingRequired = defaults.IsPrePaddingRequired,
                IsPostPaddingRequired = defaults.IsPostPaddingRequired,
                KeepUntil = defaults.KeepUntil
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Keep it as it is": the airing a file belongs to is no longer recorded again; a recording
    /// this plugin set for that is taken off the schedule.
    /// </summary>
    public Task KeepAsync(string? path, CancellationToken ct) =>
        _programmes.ForFile(path) is { } p ? KeepAsync(p, ct) : Task.CompletedTask;

    private async Task KeepAsync(Programmes.Programme p, CancellationToken ct)
    {
        if (p.State == Programmes.Scheduled && p.NextIsOurs && p.NextProgramId is { } programId)
        {
            var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
            foreach (var t in timers.Where(t => string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase) && t.Status == RecordingStatus.New))
            {
                await LiveTv.CancelTimer(t.Id).ConfigureAwait(false);
            }
        }

        Set(p, x =>
        {
            x.State = Programmes.Kept;
            x.NextProgramId = null;
            x.NextStartUtc = null;
        });
    }

    /// <summary>Notes that an airing was set to record in a file's place by hand ("Record again").</summary>
    public void NoteScheduled(string? path, string programId, DateTime startUtc, string? channel, bool ours)
    {
        if (_programmes.ForFile(path) is { } p)
        {
            Set(p, x =>
            {
                x.State = Programmes.Scheduled;
                x.Attempts++;
                x.FlaggedUtc ??= DateTime.UtcNow;
                x.NextProgramId = programId;
                x.NextStartUtc = startUtc;
                x.NextChannel = channel;
                x.NextIsOurs = ours;
            });
        }
    }

    /// <summary>Where a recording stands with being recorded again, by its file (null: nothing asked or flagged).</summary>
    public Programmes.Programme? For(string? path) => _programmes.ForFile(path);

    /// <summary>
    /// "Re-record…": a recording someone found a flaw in is recorded again, at
    /// <paramref name="programId"/> (an airing they picked) or the best airing found in the guide
    /// (searched now, then every 20 minutes for the give-up days). When the new recording is in
    /// full, this one is replaced as the setting says. Returns the airing set, if any.
    /// </summary>
    public async Task<Programmes.Programme?> RequestAsync(Video item, EpisodeIdentity identity, Guid? programId, string? reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var key = _programmes.ForFile(item.Path)?.Key ?? "manual|" + item.Id.ToString("N");
        var seconds = item.RunTimeTicks is { } ticks && ticks > 0 ? TimeSpan.FromTicks(ticks).TotalSeconds : 3600;
        var title = item is MediaBrowser.Controller.Entities.TV.Episode ep && !string.IsNullOrEmpty(ep.SeriesName)
            ? ep.SeriesName + " · " + item.Name
            : item.Name;
        _programmes.Change(all =>
        {
            if (!all.TryGetValue(key, out var p))
            {
                // A recording from before airings were tracked (or since moved): it stands for itself.
                p = new Programmes.Programme
                {
                    Key = key,
                    Title = title ?? string.Empty,
                    Identity = identity,
                    CreatedUtc = now,
                    ProgramStartUtc = now - TimeSpan.FromSeconds(seconds),
                    ProgramEndUtc = now,
                    Pieces = new List<Programmes.Piece>
                    {
                        new() { Path = item.Path, FirstSeenUtc = now, EndedUtc = now, CapturedSeconds = seconds }
                    }
                };
                all[key] = p;
            }

            p.Identity ??= identity;
            p.Manual = true;
            p.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            p.ItemId = item.Id;
            p.State = Programmes.Waiting;
            p.FlaggedUtc = now;
            p.Attempts = 0;
            p.NextProgramId = null;
            p.NextStartUtc = null;
            p.NextChannel = null;
        });

        if (programId is { } picked)
        {
            if (_libraryManager.GetItemById(picked) is not LiveTvProgram program || program.StartDate <= now)
            {
                throw new ArgumentException("That airing isn't in the guide any more, or has started.");
            }

            var id = picked.ToString("N");
            var timers = (await LiveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false)).Items;
            var already = timers.Any(t => string.Equals(t.ProgramId, id, StringComparison.OrdinalIgnoreCase)
                && t.Status is not (RecordingStatus.Cancelled or RecordingStatus.Completed or RecordingStatus.Error));
            if (!already)
            {
                await ScheduleAsync(id, ct).ConfigureAwait(false);
            }

            var start = program.StartDate;
            var channel = _libraryManager.GetItemById(program.ChannelId)?.Name;
            _programmes.Change(all =>
            {
                if (all.TryGetValue(key, out var x))
                {
                    x.State = Programmes.Scheduled;
                    x.Attempts = 1;
                    x.NextProgramId = id;
                    x.NextStartUtc = start;
                    x.NextChannel = channel;
                    x.NextIsOurs = !already;
                }
            });
            _logger.LogInformation("{Title} will be recorded again on {Start:u} ({Channel}), as asked: {Reason}", title, start, channel, reason);
        }
        else
        {
            _logger.LogInformation("{Title} is to be recorded again at the best airing found, as asked: {Reason}", title, reason);
            await SearchAsync(Config, ct).ConfigureAwait(false);
            _lastSearch = DateTime.UtcNow;
        }

        return _programmes.All().FirstOrDefault(p => p.Key == key);
    }

    private void Set(Programmes.Programme p, Action<Programmes.Programme> change) =>
        _programmes.Change(all =>
        {
            if (all.TryGetValue(p.Key, out var x))
            {
                change(x);
            }
        });

    /// <summary>What the recordings lists say about a file: a short chip text and the story behind it.</summary>
    public (string? Short, string? Text) Warning(string? path)
    {
        var note = _stalls.For(path);
        var p = _programmes.ForFile(path);
        var asked = p is { Manual: true } ? "Re-record asked for" + (p.Reason is { } r ? " (" + r + ")" : string.Empty) + "." : null;
        var story = note?.Text ?? asked ?? (p is null || p.Percent >= 100 ? null : string.Format(
            CultureInfo.InvariantCulture,
            "Only {0:F0} of {1:F0} min of the programme were recorded.",
            p.CapturedSeconds / 60,
            p.ScheduledSeconds / 60));
        switch (p?.State)
        {
            case Programmes.Waiting:
                return ("Waiting for a re-airing", story + " The next airing found in the guide will be recorded" + After());
            case Programmes.Scheduled when p.NextStartUtc is { } start:
                var when = start.ToLocalTime();
                return (
                    "Re-records " + when.ToString("MMM d", CultureInfo.InvariantCulture),
                    story + string.Format(CultureInfo.InvariantCulture, " It will be recorded again on {0:ddd, MMM d} at {0:h:mm tt}{1}", when, p.NextChannel is null ? string.Empty : " (" + p.NextChannel + ")") + After());
            case Programmes.GaveUp:
                return (note?.Short ?? "Incomplete", story + " No full copy could be recorded since.");
            default:
                return (note?.Short, note?.Text);
        }

        static string After() =>
            string.Equals(Config.RerecordReplace, "keep", StringComparison.OrdinalIgnoreCase)
                ? "; this recording is kept."
                : ", and this recording deleted once that one is in.";
    }

    // Open in another program (Comskip scanning it, ffmpeg cutting or playing it), found through /proc/*/fd.
    private static bool IsOpenElsewhere(string path)
    {
        try
        {
            var self = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            foreach (var proc in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(proc);
                if (!char.IsDigit(name[0]) || name == self)
                {
                    continue;
                }

                try
                {
                    foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(proc, "fd")))
                    {
                        if (string.Equals(new FileInfo(fd).LinkTarget, path, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another user's process, or one that just exited.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }
}
