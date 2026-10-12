using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

/// <summary>
/// Regression tests for faults that were reproduced against the original engine. They use only API that existed before
/// the fixes, so the same tests fail on the original source and pass on the fixed one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OriginalFaultTests
{
    private static int Discards(BuildFixture fixture) => fixture.Runner.Calls.Count(call => call.Contains("/Discard"));

    private static Dictionary<string, string?> StageStates(string jobDirectory)
    {
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDirectory, "timings.json")));
        return timings.RootElement.GetProperty("Stages").EnumerateArray()
            .ToDictionary(stage => stage.GetProperty("Stage").GetString()!, stage => stage.GetProperty("State").GetString());
    }

    private static string FakeDismTool(BuildFixture fixture)
    {
        var tool = Path.Combine(fixture.Root, "tools", "dism.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, "fixture");
        return tool;
    }

    [Fact]
    public async Task Cancellation_observed_at_the_next_stage_does_not_relabel_the_finished_Commit_stage()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Unmount-Image") && args.Contains("/Commit")) cancel.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(fixture.Recipe with { SelectedOptions = ["hardware-bypass"] }, null, cancel.Token));
        var states = StageStates(failure.WorkDirectory);
        Assert.Equal("Completed", states["Commit"]);
        Assert.NotEqual("Cancelled", states["Setup image"]);
        Assert.Equal("cancelled", JsonSerializer.Deserialize<JobState>(
            File.ReadAllText(Path.Combine(failure.WorkDirectory, "job.json")), JsonDefaults.Options)!.Status);
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "working.wim")));
    }

    [Fact]
    public async Task A_throwing_progress_observer_cannot_prevent_the_owned_mount_from_being_discarded()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        var observer = new ImmediateProgress<BuildProgress>(item =>
        {
            if (item.State is BuildOperationState.Cancelled or BuildOperationState.Failed)
                throw new InvalidOperationException("progress observer faulted");
        });
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, observer, cancel.Token));
        Assert.Equal(1, Discards(fixture));
        var job = JsonSerializer.Deserialize<JobState>(File.ReadAllText(Path.Combine(failure.WorkDirectory, "job.json")), JsonDefaults.Options)!;
        Assert.NotEqual("running", job.Status);
    }

    [Fact]
    public async Task Bookkeeping_that_fails_after_publication_does_not_turn_the_published_iso_into_a_failed_build()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Count > 0 && args[^1].EndsWith("output.iso", StringComparison.Ordinal) && args.Contains("-m"))
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(args[^1])!, "operations.json"));
        };
        var result = await fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
        Assert.True(File.Exists(fixture.Recipe.OutputPath));
        Assert.True(File.Exists(result.ReportPath));
        Assert.Equal("completed", JsonSerializer.Deserialize<JobState>(
            File.ReadAllText(Path.Combine(result.WorkDirectory, "job.json")), JsonDefaults.Options)!.Status);
    }

    [Fact]
    public async Task A_publication_race_keeps_the_verified_iso_and_writes_no_success_report()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Count > 0 && args[^1].EndsWith("output.iso", StringComparison.Ordinal) && args.Contains("-m"))
                File.WriteAllText(fixture.Recipe.OutputPath, "racing writer");
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        Assert.Equal("racing writer", File.ReadAllText(fixture.Recipe.OutputPath));
        Assert.False(File.Exists(Path.Combine(failure.WorkDirectory, "report.json")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.partial"));
        var preserved = Path.Combine(failure.WorkDirectory, "output.iso");
        Assert.True(File.Exists(preserved));
        Assert.Contains(preserved, failure.Message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Tool\":\"\",\"Version\":\"\",\"ExitCode\":0}")]
    public async Task A_corrupt_advisory_cache_never_blocks_a_Turbo_build_and_is_never_rewritten(string cacheText)
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        Directory.CreateDirectory(fixture.Recipe.WorkspacePath);
        var cache = Path.Combine(fixture.Recipe.WorkspacePath, "mount-optimization.json");
        File.WriteAllText(cache, cacheText);
        await fixture.Engine.BuildAsync(fixture.Recipe with { TurboMode = true, DismPath = tool }, null, CancellationToken.None);
        Assert.Equal(cacheText, File.ReadAllText(cache));
    }

    [Fact]
    public async Task Turbo_never_probes_lazy_mounting_unless_the_host_opts_in()
    {
        using var fixture = new BuildFixture();
        await fixture.Engine.BuildAsync(fixture.Recipe with { TurboMode = true }, null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Optimize"));
        Assert.Equal(1, fixture.Runner.Calls.Count(call => call.Contains("/Mount-Image")));
    }

    [Fact]
    public async Task Recovery_does_not_silently_delete_a_committed_image_that_can_still_be_resumed()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Unmount-Image") && args.Contains("/Commit")) cancel.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, cancel.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Engine.RecoverAsync(
            Path.Combine(failure.WorkDirectory, "job.json"), fixture.Recipe.DismPath, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "working.wim")));
        Assert.True(Directory.Exists(Path.Combine(failure.WorkDirectory, "media")));
    }
}
