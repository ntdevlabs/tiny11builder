using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class MountPolicyTests
{
    private static string FakeDismTool(BuildFixture fixture)
    {
        var tool = Path.Combine(fixture.Root, "tools", "dism.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, "fixture");
        return tool;
    }

    private static string CachePath(BuildFixture fixture) => Path.Combine(fixture.Recipe.WorkspacePath, "mount-optimization.json");

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Tool\":\"\",\"Version\":\"\",\"ExitCode\":0}")]
    public async Task Default_Turbo_never_probes_optimize_and_never_reads_or_rewrites_the_advisory_cache(string cacheText)
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        Directory.CreateDirectory(fixture.Recipe.WorkspacePath);
        File.WriteAllText(CachePath(fixture), cacheText);
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with { TurboMode = true, DismPath = tool }, null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Optimize"));
        Assert.Equal(1, fixture.Runner.Calls.Count(call => call.Contains("/Mount-Image")));
        Assert.Equal(cacheText, File.ReadAllText(CachePath(fixture)));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.False(report.RootElement.GetProperty("UsedOptimizedMount").GetBoolean());
        Assert.False(report.RootElement.GetProperty("Execution").GetProperty("OptimizedMount").GetBoolean());
        Assert.Contains("lazy mounting is opt-in and not enabled", File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log")));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("{ not json", "JsonException")]
    [InlineData("{\"Tool\":\"x\",\"Version\":\"y\",\"ExitCode\":5}", "InvalidDataException")]
    public async Task An_opted_in_build_with_an_invalid_advisory_cache_logs_explicitly_and_disables_lazy_mounting_without_a_probe(
        string cacheText, string errorType)
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        Directory.CreateDirectory(fixture.Recipe.WorkspacePath);
        File.WriteAllText(CachePath(fixture), cacheText);
        var messages = new List<string>();
        var result = await fixture.OptInOptimizedEngine.BuildAsync(fixture.Recipe with { TurboMode = true, DismPath = tool },
            new ImmediateProgress<BuildProgress>(item => messages.Add(item.Message)), CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Optimize"));
        Assert.Contains(messages, text => text.Contains("advisory cache could not be used", StringComparison.Ordinal) &&
            text.Contains(errorType, StringComparison.Ordinal) && text.Contains("disabled for this run", StringComparison.Ordinal));
        var log = File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log"));
        Assert.Contains("advisory cache could not be used", log);
        Assert.Equal(cacheText, File.ReadAllText(CachePath(fixture)));      // diagnosing never overwrites the user's file
        Assert.True(File.Exists(result.ReportPath));
    }

    [Fact]
    public async Task An_unreadable_advisory_cache_is_a_logged_fallback_not_a_failure()
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        Directory.CreateDirectory(fixture.Recipe.WorkspacePath);
        File.WriteAllText(CachePath(fixture), "{}");
        using var exclusive = new FileStream(CachePath(fixture), FileMode.Open, FileAccess.Read, FileShare.None);
        var messages = new List<string>();
        Assert.True(MountOptimizationCache.IsUnavailable(fixture.Recipe.WorkspacePath, tool, messages.Add));
        Assert.Contains(messages, text => text.Contains("IOException", StringComparison.Ordinal) && text.Contains("disabled for this run"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Only_expected_cache_failures_are_treated_as_advisory()
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        // A relative workspace is invalid input, not an unreadable cache: it must not be swallowed.
        Assert.Throws<InvalidDataException>(() => MountOptimizationCache.IsUnavailable("relative\\workspace", tool, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MountOptimizationCache.TryRecordUnavailable(fixture.Root, tool, 5, _ => { }));
    }

    [Fact]
    public async Task An_opted_in_build_with_a_matching_negative_cache_skips_the_probe_and_a_missing_cache_still_falls_back()
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        fixture.Runner.FailOptimizedMount = true;
        var first = await fixture.OptInOptimizedEngine.BuildAsync(fixture.Recipe with { TurboMode = true, DismPath = tool }, null, CancellationToken.None);
        Assert.Equal(1, fixture.Runner.Calls.Count(call => call.Contains("/Optimize")));    // the one explicit probe
        Assert.True(File.Exists(CachePath(fixture)));
        Assert.Empty(first.Warnings);
        var probes = fixture.Runner.Calls.Count(call => call.Contains("/Optimize"));
        File.Delete(fixture.Recipe.OutputPath);
        await fixture.OptInOptimizedEngine.BuildAsync(fixture.Recipe with { TurboMode = true, DismPath = tool }, null, CancellationToken.None);
        Assert.Equal(probes, fixture.Runner.Calls.Count(call => call.Contains("/Optimize"))); // cache hit: no second probe
    }

    [Fact]
    public async Task A_cache_that_cannot_be_written_is_reported_as_a_warning_and_the_normal_mount_fallback_still_succeeds()
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        fixture.Runner.FailOptimizedMount = true;
        Directory.CreateDirectory(CachePath(fixture));          // a directory squats on the advisory file's name
        var result = await fixture.OptInOptimizedEngine.BuildAsync(fixture.Recipe with { TurboMode = true, DismPath = tool }, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.Contains(result.Warnings, warning => warning.Contains("advisory cache could not be updated", StringComparison.Ordinal));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Mount-Image") && !call.Contains("/Optimize"));
    }

    [Fact]
    public void Recording_the_advisory_cache_is_best_effort_and_says_why_it_failed()
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        var workspaceThatIsAFile = Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(workspaceThatIsAFile, "x");
        var messages = new List<string>();
        Assert.False(MountOptimizationCache.TryRecordUnavailable(workspaceThatIsAFile, tool, 4350, messages.Add));
        Assert.Contains(messages, text => text.Contains("could not be updated", StringComparison.Ordinal));
        Assert.True(MountOptimizationCache.TryRecordUnavailable(fixture.Root, tool, 87, messages.Add));
        Assert.True(MountOptimizationCache.IsUnavailable(fixture.Root, tool, _ => { }));
    }

    [Fact]
    public async Task Inventory_never_uses_lazy_mounting_and_never_reads_the_advisory_cache_even_when_the_host_opts_in()
    {
        using var fixture = new BuildFixture();
        var tool = FakeDismTool(fixture);
        Directory.CreateDirectory(fixture.Recipe.WorkspacePath);
        File.WriteAllText(CachePath(fixture), "{ corrupt");
        var items = await fixture.OptInOptimizedEngine.InventoryAsync(fixture.Recipe with { TurboMode = true, DismPath = tool }, null, CancellationToken.None);
        Assert.NotEmpty(items);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Optimize"));
        Assert.Equal("{ corrupt", File.ReadAllText(CachePath(fixture)));
    }

    [Fact]
    public void Older_Turbo_recipes_load_unchanged_and_carry_no_lazy_mount_flag()
    {
        using var fixture = new BuildFixture();
        var file = Path.Combine(fixture.Root, "old-turbo.json");
        File.WriteAllText(file, """{"SchemaVersion":1,"TurboMode":true,"RemoveRedundantData":true,"SelectedOptions":[]}""");
        var recipe = BuildRecipe.Read(file);
        Assert.True(recipe.TurboMode);
        recipe.Write(file);
        var json = File.ReadAllText(file);
        Assert.Contains("\"TurboMode\": true", json);
        Assert.DoesNotContain("Optimiz", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(BuildExecutionPlan.Create(recipe, 8).OptimizedMount);
    }
}

[SupportedOSPlatform("windows")]
public sealed class TrackerCompletionTests
{
    [Fact]
    public void A_stage_that_ended_before_a_cancellation_is_never_marked_cancelled()
    {
        var events = new List<BuildProgress>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), _ => { });
        tracker.BeginStage("Commit", "Commit");
        tracker.EndStage();
        tracker.Fail(new OperationCanceledException(), cancelled: true);
        Assert.Equal(BuildOperationState.Completed, events.Last(item => item.OperationId == "stage:Commit").State);
        Assert.DoesNotContain(events, item => item.State == BuildOperationState.Cancelled);
    }

    [Fact]
    public void Ending_a_stage_twice_or_without_one_is_harmless()
    {
        var tracker = new BuildOperationTracker(null, _ => { });
        tracker.EndStage();
        tracker.BeginStage("Mount", "Mount");
        tracker.EndStage();
        tracker.EndStage();
        Assert.Equal(BuildOperationState.Completed, Assert.Single(tracker.Timings).State);
    }

    [Fact]
    public void An_observer_that_throws_cannot_leave_a_failed_item_or_stage_looking_active()
    {
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(item =>
        {
            if (item.State == BuildOperationState.Failed) throw new InvalidOperationException("observer");
        }), _ => { });
        tracker.BeginStage("Remove apps", "Remove apps");
        var aggregate = Assert.Throws<AggregateException>(() => tracker.Fail(new IOException("boom"), cancelled: false));
        Assert.Contains(aggregate.InnerExceptions, error => error.Message == "observer");
        Assert.Equal(BuildOperationState.Failed, tracker.Snapshot.Single(item => item.Id == "stage:Remove apps").State);
    }

    [Fact]
    public async Task A_failing_observer_is_attached_to_the_item_failure_it_was_reporting_instead_of_replacing_it()
    {
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(item =>
        {
            if (item.State == BuildOperationState.Failed) throw new InvalidOperationException("observer");
        }), _ => { });
        tracker.BeginStage("Remove apps", "Remove apps");
        var original = new IOException("the real failure");
        var thrown = await Assert.ThrowsAsync<IOException>(() => tracker.RunAsync("app:A", "Remove apps", "Remove A", () => throw original));
        Assert.Same(original, thrown);
        Assert.IsType<InvalidOperationException>(thrown.Data[BuildOperationTracker.ObserverFailureKey + "app:A"]);
        Assert.Equal(BuildOperationState.Failed, tracker.Snapshot.Single(item => item.Id == "app:A").State);
    }

    [Fact]
    public void Restored_history_keeps_states_details_and_durations_without_raising_progress()
    {
        var events = new List<BuildProgress>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), _ => { });
        tracker.Restore([new("stage:Commit", "Commit", "Commit", BuildOperationState.Completed, "kept", 1234.5),
            new("stage:Compress", "Compress", "Compress", BuildOperationState.Pending, null, null)]);
        Assert.Empty(events);
        Assert.Equal(1234.5, tracker.Snapshot.Single(item => item.Id == "stage:Commit").DurationMilliseconds);
        Assert.Equal("kept", tracker.Snapshot.Single(item => item.Id == "stage:Commit").Detail);
        tracker.BeginStage("Compress", "Compress");   // an already planned stage is reused, never planned twice
        tracker.EndStage();
        Assert.Equal(2, tracker.Timings.Count);
        Assert.All(tracker.Timings, timing => Assert.Equal(BuildOperationState.Completed, timing.State));
    }

    [Fact]
    public void A_snapshot_can_describe_still_running_operations_as_already_completed()
    {
        var tracker = new BuildOperationTracker(null, _ => { });
        tracker.BeginStage("Commit", "Commit");
        var snapshot = tracker.SnapshotCompleting("stage:Commit");
        var commit = snapshot.Single(item => item.Id == "stage:Commit");
        Assert.Equal(BuildOperationState.Completed, commit.State);
        Assert.NotNull(commit.DurationMilliseconds);
        Assert.Equal(BuildOperationState.Running, tracker.Snapshot.Single(item => item.Id == "stage:Commit").State); // the live state is unchanged
    }

    [Fact]
    public void The_resume_check_stage_is_part_of_the_commit_phase_and_progress_range()
    {
        Assert.Equal(BuildProgressText.Phase("Commit"), BuildProgressText.Phase(BuildProgressText.ResumeCheckStage));
        Assert.Equal(BuildProgressText.Range("Commit"), BuildProgressText.Range(BuildProgressText.ResumeCheckStage));
    }
}
