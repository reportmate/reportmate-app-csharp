using System.IO;
using ReportMate.App.Services;
using Xunit;

namespace ReportMate.App.Tests;

/// <summary>
/// How a snapshot is assembled from the runner's cache: newest run wins per module,
/// and event.json stands in for any module the runner did not write as its own file.
/// </summary>
/// <remarks>
/// The rules here are not obvious from any single run's contents, which is why the
/// Hardware tab could report its module missing on a machine whose hardware was in
/// the cache the whole time.
/// </remarks>
public sealed class DeviceSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rm-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Run(string stamp)
    {
        var dir = Path.Combine(_root, stamp);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string name, string json) =>
        File.WriteAllText(Path.Combine(dir, name + ".json"), json);

    private DeviceSnapshot Load() => new DeviceSnapshotStore(_root).Load();

    /// <summary>The case that was broken: no hardware.json in any run, but event.json has it.</summary>
    [Fact]
    public void ModuleComesFromEventJsonWhenItHasNoFileOfItsOwn()
    {
        var run = Run("2026-09-10-223523");
        Write(run, "network", """{"moduleId":"network"}""");
        Write(run, "event", """
            {"metadata":{"serialNumber":"ABC123"},"events":[],
             "hardware":{"moduleId":"hardware","manufacturer":"Dell"}}
            """);

        var s = Load();
        Assert.NotNull(s.Hardware);
        Assert.Equal("Dell", s.Hardware!.Manufacturer);
        // The tabs gate on both, so populating one without the other still renders
        // "module missing".
        Assert.True(s.HasModule("hardware"));
    }

    [Fact]
    public void LooseFileWinsOverTheUnifiedPayload()
    {
        var run = Run("2026-09-10-223523");
        Write(run, "hardware", """{"moduleId":"hardware","manufacturer":"FromFile"}""");
        Write(run, "event", """
            {"metadata":{},"events":[],"hardware":{"moduleId":"hardware","manufacturer":"FromEvent"}}
            """);

        Assert.Equal("FromFile", Load().Hardware!.Manufacturer);
    }

    [Fact]
    public void NewestRunWinsPerModule()
    {
        Write(Run("2026-09-09-010000"), "hardware", """{"moduleId":"hardware","manufacturer":"Old"}""");
        Write(Run("2026-09-10-223523"), "hardware", """{"moduleId":"hardware","manufacturer":"New"}""");

        Assert.Equal("New", Load().Hardware!.Manufacturer);
    }

    /// <summary>
    /// A partial run must not blank the modules it does not contain: the runner
    /// writes different modules on different schedules, so the newest run is
    /// routinely two or three files.
    /// </summary>
    [Fact]
    public void PartialNewestRunFallsBackToEarlierRuns()
    {
        Write(Run("2026-09-09-010000"), "hardware", """{"moduleId":"hardware","manufacturer":"Dell"}""");
        Write(Run("2026-09-10-223523"), "network", """{"moduleId":"network"}""");

        var s = Load();
        Assert.NotNull(s.Hardware);
        Assert.NotNull(s.Network);
    }

    /// <summary>
    /// The twelve-run window bounds event history, not module search. A module on a
    /// daily schedule sits further back than twelve hourly runs, and before this it
    /// would have been invisible whenever it lived only in event.json.
    /// </summary>
    [Fact]
    public void UnifiedPayloadIsStillSearchedBeyondTheEventWindow()
    {
        for (var i = 0; i < 20; i++)
            Write(Run($"2026-09-10-{i:00}0000"), "network", """{"moduleId":"network"}""");
        Write(Run("2026-09-09-010000"), "event", """
            {"metadata":{},"events":[],"hardware":{"moduleId":"hardware","manufacturer":"Dell"}}
            """);

        var s = Load();
        Assert.NotNull(s.Hardware);
        Assert.True(s.HasModule("hardware"));
    }

    [Fact]
    public void EmptyCacheIsAnEmptySnapshot()
    {
        Directory.CreateDirectory(_root);
        Assert.True(Load().IsEmpty);
    }

    /// <summary>A directory that is not a run stamp is not a run.</summary>
    [Fact]
    public void UnstampedDirectoriesAreIgnored()
    {
        var dir = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(dir);
        Write(dir, "hardware", """{"moduleId":"hardware","manufacturer":"Dell"}""");

        Assert.True(Load().IsEmpty);
    }

    /// <summary>
    /// Unreadable is not the same as uncollected, and the tab says so. Truncated
    /// JSON is the realistic case: the reader can open a file the runner is still
    /// writing.
    /// </summary>
    [Fact]
    public void UnreadableModuleIsReportedRatherThanTreatedAsAbsent()
    {
        Write(Run("2026-09-10-223523"), "hardware", """{"moduleId":"hard""");

        var s = Load();
        Assert.Null(s.Hardware);
        Assert.NotNull(s.ModuleError("hardware"));
    }
}
