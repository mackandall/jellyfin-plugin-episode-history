using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.EpisodeHistory.Services;

/// <summary>
/// Dashboard → Scheduled Tasks entry to re-seed the history from the library (it also runs
/// by itself when the history is empty). No default trigger: live events keep the history
/// current once seeded.
/// </summary>
public sealed class ImportHistoryTask : IScheduledTask
{
    private readonly LibraryImporter _importer;
    private readonly GuideSweeper _sweeper;

    public ImportHistoryTask(LibraryImporter importer, GuideSweeper sweeper)
    {
        _importer = importer;
        _sweeper = sweeper;
    }

    public string Name => "Import episode history from library";

    public string Key => "EpisodeHistoryImport";

    public string Description =>
        "Adds every DVR recording in the library as \"recorded\" and every played episode/movie as \"watched\" to the episode history, then re-marks the guide.";

    public string Category => "Live TV";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _importer.ImportAsync(progress, cancellationToken).ConfigureAwait(false);
        _sweeper.Request("library import");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
