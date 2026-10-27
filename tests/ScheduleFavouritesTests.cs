using System;
using System.IO;
using Jellyfin.Plugin.EpisodeHistory.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EpisodeHistory.Tests;

/// <summary>Reading which airings JellyDVR Schedule's favourites scheduled, from its files.</summary>
public sealed class ScheduleFavouritesTests : IDisposable
{
    private static readonly Guid Pilot = Guid.Parse("13c8e686-bdef-87cb-1e73-27c255712c23");
    private static readonly Guid Ortolan = Guid.Parse("2b4722d9-4919-c423-5149-9f3c14a156b1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eh-tests-favourites-" + Guid.NewGuid().ToString("N"));
    private readonly ScheduleFavourites _favourites;

    public ScheduleFavouritesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "jellydvr-schedule"));
        Directory.CreateDirectory(Path.Combine(_root, "configurations"));
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_root, "data"));
        paths.Setup(p => p.PluginConfigurationsPath).Returns(Path.Combine(_root, "configurations"));
        _favourites = new ScheduleFavourites(paths.Object, NullLogger<ScheduleFavourites>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void NoSchedulePlugin_Nothing()
    {
        Assert.Empty(_favourites.SkipInLibraryPrograms());
    }

    [Fact]
    public void SkippingRule_ItsAiringsOnly()
    {
        WriteRules(Rule("jiro", "true"), Rule("news", "false"));
        WriteCreated(("jiro", Pilot), ("news", Ortolan));

        var programs = _favourites.SkipInLibraryPrograms();

        Assert.Contains(Pilot, programs);
        Assert.DoesNotContain(Ortolan, programs);
    }

    [Fact]
    public void SettingNotWritten_SkipsLikeSchedulesDefault()
    {
        WriteRules("<FavouriteRule><Id>jiro</Id><Title>Get Jiro!</Title></FavouriteRule>");
        WriteCreated(("jiro", Pilot));

        Assert.Contains(Pilot, _favourites.SkipInLibraryPrograms());
    }

    [Fact]
    public void RemovedRule_NotCounted()
    {
        WriteRules(Rule("other", "true"));
        WriteCreated(("jiro", Pilot));

        Assert.Empty(_favourites.SkipInLibraryPrograms());
    }

    [Fact]
    public void FileChanged_ReadAgain()
    {
        WriteRules(Rule("jiro", "true"));
        WriteCreated(("jiro", Pilot));
        Assert.Single(_favourites.SkipInLibraryPrograms());

        WriteCreated(("jiro", Pilot), ("jiro", Ortolan));
        File.SetLastWriteTimeUtc(Path.Combine(_root, "data", "jellydvr-schedule", "favourites.json"), DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(2, _favourites.SkipInLibraryPrograms().Count);
    }

    [Fact]
    public void BrokenFile_KeepsLastGoodRead()
    {
        WriteRules(Rule("jiro", "true"));
        WriteCreated(("jiro", Pilot));
        Assert.Single(_favourites.SkipInLibraryPrograms());

        var file = Path.Combine(_root, "data", "jellydvr-schedule", "favourites.json");
        File.WriteAllText(file, "[{\"ProgramId\": ");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

        Assert.Single(_favourites.SkipInLibraryPrograms());
    }

    [Fact]
    public void CancelKey_SameForAnyCase()
    {
        Assert.Equal(
            GuideSweeper.FavouriteCancelKey(Pilot.ToString("N").ToUpperInvariant()),
            GuideSweeper.FavouriteCancelKey(Pilot.ToString("N")));
    }

    private static string Rule(string id, string skipInLibrary) =>
        $"<FavouriteRule><Id>{id}</Id><Title>{id}</Title><SkipInLibrary>{skipInLibrary}</SkipInLibrary><Enabled>true</Enabled></FavouriteRule>";

    private void WriteRules(params string[] rules) =>
        File.WriteAllText(
            Path.Combine(_root, "configurations", "Jellyfin.Plugin.DvrSchedule.xml"),
            "<?xml version=\"1.0\"?><PluginConfiguration><Favourites>" + string.Concat(rules) + "</Favourites></PluginConfiguration>");

    private void WriteCreated(params (string Rule, Guid Program)[] created)
    {
        var items = Array.ConvertAll(created, c =>
            $"{{\"ProgramId\": \"{c.Program}\", \"RuleId\": \"{c.Rule}\", \"Title\": \"x\", \"End\": \"2026-11-01T02:00:00Z\", \"CreatedUtc\": \"2026-10-25T22:30:47Z\"}}");
        File.WriteAllText(Path.Combine(_root, "data", "jellydvr-schedule", "favourites.json"), "[" + string.Join(",", items) + "]");
    }
}
