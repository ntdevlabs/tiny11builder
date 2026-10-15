using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

/// <summary>
/// DISM refuses to clean the component store while the image has pending operations (public report: "Clean superseded
/// component files" failed with exit -2146498554, 0x800F0806, right after .NET 3.5 was enabled).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PendingOperationsTests
{
    // Exactly what the report shows: the signed exit code and DISM's own words.
    private const int PendingExitCode = -2146498554;
    private const string ReportedOutput = "Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.8972\r\n\r\n" +
        "Image Version: 10.0.26300.9457\r\n\r\n\r\nError: 0x800f0806\r\n\r\n" +
        "The operation could not be completed due to pending operations.\r\n\r\n" +
        @"The DISM log file can be found at C:\tiny11\tiny11-c147b000c50344c1b65e8656fc5c38b9\dism.log" + "\r\n";

    private static bool IsCleanup(IReadOnlyList<string> args) => args.Contains("/StartComponentCleanup");

    private static bool EnablesNetFx3(IReadOnlyList<string> args) =>
        args.Contains("/Enable-Feature") && args.Contains("/FeatureName:NetFx3");

    private static ProcessResult Pending(string? output = null) => new(PendingExitCode, output ?? ReportedOutput);

    /// <summary>Like real DISM: once <paramref name="cause"/> ran, every later cleanup reports pending operations.</summary>
    private static Func<IReadOnlyList<string>, ProcessResult?> PendingAfter(Func<IReadOnlyList<string>, bool> cause)
    {
        var pending = false;
        return args =>
        {
            pending |= cause(args);
            return pending && IsCleanup(args) ? Pending() : null;
        };
    }

    private static BuildEngine Engine(BuildFixture fixture, Func<IReadOnlyList<string>, ProcessResult?> replace) =>
        new(new CommandResultRunner(fixture.Runner, replace), new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());

    private static BuildEngine EngineWith(BuildFixture fixture, IImageOperations operations, Func<IReadOnlyList<string>, ProcessResult?> replace) =>
        new(new CommandResultRunner(fixture.Runner, replace), new FakeMediaFactory(), fixture.Registry, operations, new FakeHost());

    private static void AddNetFx3Source(BuildFixture fixture)
    {
        var sxs = Path.Combine(fixture.Source, "sources", "sxs");
        Directory.CreateDirectory(sxs);
        File.WriteAllText(Path.Combine(sxs, "netfx3.cab"), "fixture");
    }

    // The reported build: Tiny11 with every original-style removal, component cleanup and .NET 3.5.
    private static BuildRecipe Tiny11Recipe(BuildFixture fixture) => fixture.Recipe with
    {
        Mode = BuildMode.Tiny11, SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11), EnableNetFx3 = true, RemoveRedundantData = true
    };

    private static BuildRecipe CoreRecipe(BuildFixture fixture) => fixture.Recipe with
    {
        Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
        SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core), EnableNetFx3 = true
    };

    private static string[] StageOrder(string directory)
    {
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "timings.json")));
        return timings.RootElement.GetProperty("Stages").EnumerateArray().Select(stage => stage.GetProperty("Stage").GetString()!).ToArray();
    }

    private static JsonElement Operation(JsonDocument operations, string id) =>
        operations.RootElement.EnumerateArray().Single(entry => entry.GetProperty("Id").GetString() == id);

    private static BuildOperationState[] StateTrail(IEnumerable<BuildProgress> events, string id) =>
        events.Where(item => item.OperationId == id && item.State is not null).Select(item => item.State!.Value).ToArray();

    // ---- the reported failure and its fix ----------------------------------------------------------------------------

    [Fact]
    public void The_pending_exit_code_is_the_signed_value_in_the_report()
    {
        Assert.Equal(PendingExitCode, unchecked((int)0x800F0806));
        Assert.Equal(PendingExitCode, BuildEngine.PendingOperationsExitCode);
        Assert.StartsWith("DISM failed (exit -2146498554). Deployment Image Servicing and Management tool",
            new DismCommandException(PendingExitCode, ReportedOutput).Message);
    }

    [Theory]
    [InlineData(22621, "x64", false)]
    [InlineData(26300, "x64", false)]
    [InlineData(26300, "arm64", true)]
    [InlineData(29689, "x64", true)]
    public async Task The_reported_recipe_cleans_the_component_store_before_NetFx3_is_enabled(int build, string architecture, bool turbo)
    {
        using var fixture = new BuildFixture(build, architecture);
        AddNetFx3Source(fixture);
        // Real DISM leaves .NET 3.5 pending until first boot and refuses cleanup meanwhile; only the new order avoids it.
        var engine = Engine(fixture, PendingAfter(EnablesNetFx3));
        var result = await engine.BuildAsync(Tiny11Recipe(fixture) with { TurboMode = turbo }, null, CancellationToken.None);

        Assert.Empty(result.Warnings);
        var features = fixture.Events.FindIndex(e => e.Contains("/Disable-Feature"));
        var cleanup = fixture.Events.FindIndex(e => e.Contains("/StartComponentCleanup"));
        var netFx3 = fixture.Events.FindIndex(e => e.Contains("/FeatureName:NetFx3"));
        var commit = fixture.Events.FindIndex(e => e.Contains("/Unmount-Image") && e.Contains("/Commit"));
        Assert.True(features >= 0 && features < cleanup && cleanup < netFx3 && netFx3 < commit);
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/ResetBase") || e.Contains("RevertPendingActions"));
        Assert.All(JobScenarios.StageStates(result.WorkDirectory).Values, state => Assert.Equal("Completed", state));
        var order = StageOrder(result.WorkDirectory);
        Assert.True(Array.IndexOf(order, "Optional features") < Array.IndexOf(order, "Component cleanup") &&
            Array.IndexOf(order, "Component cleanup") < Array.IndexOf(order, ".NET 3.5") &&
            Array.IndexOf(order, ".NET 3.5") < Array.IndexOf(order, "Windows settings"));
    }

    [Theory]
    [InlineData(26300, "x64")]
    [InlineData(29689, "arm64")]
    public async Task Core_resets_the_component_base_before_NetFx3_and_prunes_only_afterwards(int build, string architecture)
    {
        using var fixture = new BuildFixture(build, architecture);
        AddNetFx3Source(fixture);
        var result = await Engine(fixture, PendingAfter(EnablesNetFx3)).BuildAsync(CoreRecipe(fixture), null, CancellationToken.None);

        Assert.Empty(result.Warnings);
        var apps = fixture.Events.FindIndex(e => e.Contains("/Remove-ProvisionedAppxPackage"));
        var features = fixture.Events.FindIndex(e => e.Contains("/Disable-Feature"));
        var reset = fixture.Events.FindIndex(e => e.Contains("/StartComponentCleanup") && e.Contains("/ResetBase"));
        var netFx3 = fixture.Events.FindIndex(e => e.Contains("/FeatureName:NetFx3"));
        var prune = fixture.Events.IndexOf("TrimCore");
        Assert.True(apps >= 0 && features >= 0 && apps < reset && features < reset && reset < netFx3 && netFx3 < prune);
        Assert.DoesNotContain(fixture.Events.Skip(prune + 1), e => e.Contains("/Cleanup-Image") || e.Contains("/Enable-Feature"));
        Assert.All(JobScenarios.StageStates(result.WorkDirectory).Values, state => Assert.Equal("Completed", state));
    }

    [Fact]
    public void Overall_progress_stays_monotonic_through_the_reordered_stages()
    {
        var cleanup = BuildProgressText.Range("Component cleanup");
        var netFx3 = BuildProgressText.Range(".NET 3.5");
        var settings = BuildProgressText.Range("Windows settings");
        Assert.Equal(BuildProgressText.Range("Optional features").End, cleanup.Start);
        Assert.Equal(cleanup.End, netFx3.Start);
        Assert.Equal(netFx3.End, settings.Start);
        Assert.Equal(settings.End, BuildProgressText.Range("Advanced payloads").Start);
    }

    // ---- an image that still has pending operations ----------------------------------------------------------------------

    [Fact]
    public async Task Tiny11_skips_the_cleanup_with_a_warning_and_commits_the_pending_actions_for_first_boot()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var events = new List<BuildProgress>();
        // Removing optional features can leave operations pending too.
        var engine = Engine(fixture, PendingAfter(args => args.Contains("/Disable-Feature")));
        var result = await engine.BuildAsync(Tiny11Recipe(fixture), new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);

        Assert.True(File.Exists(result.IsoPath));
        var cleanup = fixture.Events.FindIndex(e => e.Contains("/StartComponentCleanup"));
        var netFx3 = fixture.Events.FindIndex(e => e.Contains("/FeatureName:NetFx3"));
        Assert.True(cleanup >= 0 && netFx3 > cleanup);
        Assert.Contains(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Commit"));
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Discard") || e.Contains("RevertPendingActions"));
        Assert.Single(fixture.Events, e => e.Contains("/Cleanup-Image"));

        Assert.Equal(2, result.Warnings.Count);
        var warning = Assert.Single(result.Warnings, item => item.StartsWith("Component cleanup was skipped", StringComparison.Ordinal));
        Assert.Contains("0x800F0806", warning);
        Assert.Contains("first boot", warning);
        Assert.DoesNotContain("…", ErrorText.Summary(warning, 200));   // the result page shows 200 characters of each warning
        Assert.Contains(events, e => e.Stage == "Component cleanup" && e.Detail == "WARNING: " + warning);
        Assert.Contains("WARNING: " + warning, File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log")));

        // the stage reports what happened and is never relabelled as completed
        Assert.Equal([BuildOperationState.Pending, BuildOperationState.Running, BuildOperationState.Skipped],
            StateTrail(events, "stage:Component cleanup"));
        var stages = JobScenarios.StageStates(result.WorkDirectory);
        Assert.Equal("Skipped", stages["Component cleanup"]);
        Assert.All(stages.Where(pair => pair.Key != "Component cleanup"), pair => Assert.Equal("Completed", pair.Value));
        using var operations = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.WorkDirectory, "operations.json")));
        var skipped = Operation(operations, "stage:Component cleanup");
        Assert.Equal("Skipped", skipped.GetProperty("State").GetString());
        Assert.Contains("pending operations", skipped.GetProperty("Detail").GetString());
        Assert.NotEqual(JsonValueKind.Null, skipped.GetProperty("DurationMilliseconds").ValueKind);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(result.Warnings, report.RootElement.GetProperty("Warnings").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Original_file_removals_pass_through_unchanged_when_no_cleanup_was_deferred()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var operations = new RecordingOperations(fixture.Operations);
        var events = new List<BuildProgress>();
        var recipe = Tiny11Recipe(fixture);
        var result = await EngineWith(fixture, operations, PendingAfter(EnablesNetFx3))
            .BuildAsync(recipe, new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Contains("edge-files", recipe.SelectedOptions);
        Assert.Equal(recipe.SelectedOptions, Assert.Single(operations.OriginalFileOptions));
        Assert.DoesNotContain(events, e => e.OperationId == "retained:edge-files");
        Assert.DoesNotContain(fixture.Events.Skip(fixture.Events.FindIndex(e => e.Contains("/FeatureName:NetFx3")) + 1),
            e => e.Contains("/Get-Packages") || e.Contains("/Get-Features"));
    }

    [Fact]
    public async Task A_deferred_cleanup_keeps_every_Edge_file_and_says_so_while_other_original_removals_stay()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var operations = new RecordingOperations(fixture.Operations);
        var events = new List<BuildProgress>();
        var recipe = Tiny11Recipe(fixture);
        var result = await EngineWith(fixture, operations, PendingAfter(args => args.Contains("/Disable-Feature")))
            .BuildAsync(recipe, new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);

        var passed = Assert.Single(operations.OriginalFileOptions);
        Assert.Equal(recipe.SelectedOptions.Where(option => option != "edge-files"), passed);
        Assert.Contains("onedrive-files", passed);

        const string reason = "Retained because pending servicing may reference these component files";
        Assert.Equal([BuildOperationState.Pending, BuildOperationState.Skipped], StateTrail(events, "retained:edge-files"));
        Assert.Equal(reason, events.Last(e => e.OperationId == "retained:edge-files").Detail);
        var log = File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log"));
        Assert.Contains("Remove Edge and WebView2: " + reason, log);
        using var operationsJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.WorkDirectory, "operations.json")));
        var kept = Operation(operationsJson, "retained:edge-files");
        Assert.Equal("Skipped", kept.GetProperty("State").GetString());
        Assert.Equal("Advanced payloads", kept.GetProperty("Stage").GetString());
        Assert.Equal(reason, kept.GetProperty("Detail").GetString());

        Assert.Equal(2, result.Warnings.Count);
        var warning = Assert.Single(result.Warnings, item => item.StartsWith("Edge and WebView2 removal was selected but not applied", StringComparison.Ordinal));
        Assert.Contains("0x800F0806", warning);
        Assert.Contains("all Edge files were kept", warning);
        Assert.DoesNotContain("…", ErrorText.Summary(warning, 200));
        Assert.Contains("WARNING: " + warning, log);

        Assert.Contains("edge-files", BuildRecipe.Read(Path.Combine(result.WorkDirectory, JobCheckpoint.RecipeFileName)).SelectedOptions);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Contains("edge-files", report.RootElement.GetProperty("SelectedOptions").EnumerateArray().Select(item => item.GetString()));
        Assert.True(File.Exists(result.IsoPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: 0x800f0806")]
    [InlineData("Fehler: 0x800f0806. Der Vorgang kann wegen ausstehender Vorgaenge nicht abgeschlossen werden.")]
    [InlineData("DISM could not clean the image.")]
    public async Task Pending_operations_are_recognised_by_the_exit_code_not_by_the_wording(string output)
    {
        using var fixture = new BuildFixture();
        var engine = Engine(fixture, args => IsCleanup(args) ? Pending(output) : null);
        var result = await engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);

        Assert.Contains("0x800F0806", Assert.Single(result.Warnings));
        Assert.Equal("Skipped", JobScenarios.StageStates(result.WorkDirectory)["Component cleanup"]);
        Assert.True(File.Exists(result.IsoPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(-2146498529)]   // 0x800F081F: source files not found
    [InlineData(-2146498270)]   // 0x800F0922: a component failed to install
    [InlineData(-2146498553)]   // one above the pending code
    [InlineData(-2146498555)]   // one below it
    public async Task Any_other_DISM_failure_stays_fatal_even_with_the_pending_wording(int exitCode)
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var engine = Engine(fixture, args => IsCleanup(args) ? new ProcessResult(exitCode, ReportedOutput) : null);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(Tiny11Recipe(fixture), null, CancellationToken.None));

        Assert.Equal(exitCode, Assert.IsType<DismCommandException>(failure.InnerException).ExitCode);
        Assert.Equal("Failed", JobScenarios.StageStates(failure.WorkDirectory)["Component cleanup"]);
        Assert.Equal("failed", JobScenarios.ReadState(failure.WorkDirectory).Status);
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Enable-Feature"));
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Commit"));
        Assert.Contains(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Discard"));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.DoesNotContain("Component cleanup was skipped", File.ReadAllText(Path.Combine(failure.WorkDirectory, "build.log")));
    }

    [Fact]
    public async Task Core_stops_before_removing_any_component_and_explains_the_pending_operations()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var sourceBefore = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\install.wim"));
        var engine = Engine(fixture, PendingAfter(args => args.Contains("/Disable-Feature")));
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(CoreRecipe(fixture), null, CancellationToken.None));

        Assert.False(failure.IsCancellation);
        var cause = Assert.IsType<PendingOperationsException>(failure.InnerException);
        Assert.Equal(PendingExitCode, Assert.IsType<DismCommandException>(cause.InnerException).ExitCode);
        Assert.Contains("0x800F0806", Assert.Single(cause.Evidence));
        Assert.Equal(PendingOperations.Refusal(CoreRecipe(fixture)), cause.Message);
        Assert.StartsWith("Core stopped before trimming any component", cause.Message);
        Assert.Contains("first boot", cause.Message);
        Assert.Contains("source was not changed", failure.Message);
        // everything irreversible, and every later feature change, is still ahead of the refusal
        Assert.DoesNotContain(fixture.Events, e => e is "TrimCore" or "RemoveOriginalFiles" or "CleanRedundantData");
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Enable-Feature"));
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Commit"));
        Assert.Contains(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Discard"));
        Assert.Equal(sourceBefore, File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\install.wim")));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.Equal("Failed", stages["Component cleanup"]);
        Assert.Equal("Pending", stages[".NET 3.5"]);
        Assert.Equal("Pending", stages["Core pruning"]);
        // the notice the app shows keeps the cause and the way out within its length limit
        var notice = OperationOutcomes.Classify(OperationKind.Build, failure).Message;
        Assert.Equal(cause.Message, notice);
        Assert.Contains("first boot", notice);
        Assert.Contains("serviceable Tiny11", notice);
        Assert.Contains("never reverted", notice);
    }

    [Fact]
    public async Task Removing_component_payloads_stops_before_anything_is_removed_when_operations_are_pending()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var action = new ExpertAction(ImageItemKind.ComponentPayload, "amd64_critical-component", ExpertActionMode.Remove);
        var recipe = fixture.Recipe with { ExpertAcknowledged = true, ExpertActions = [action], EnableNetFx3 = true };
        var engine = Engine(fixture, args => IsCleanup(args) ? Pending() : null);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, CancellationToken.None));

        var cause = Assert.IsType<PendingOperationsException>(failure.InnerException);
        Assert.Equal(PendingOperations.Refusal(recipe), cause.Message);
        Assert.StartsWith("Component payloads were not removed", cause.Message);
        Assert.Contains("first boot", cause.Message);
        Assert.Contains("0x800F0806", Assert.Single(cause.Evidence));
        Assert.Empty(fixture.Operations.LastActions);
        Assert.DoesNotContain(fixture.Events, e => e is "RemoveOriginalFiles" or "CleanRedundantData");
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Enable-Feature") || e.Contains("/Commit"));
        Assert.Contains(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Discard"));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public async Task Without_the_cleanup_option_the_build_is_unchanged_whatever_DISM_would_say()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var engine = Engine(fixture, args => IsCleanup(args) ? Pending() : null);
        var result = await engine.BuildAsync(fixture.Recipe with { SelectedOptions = ["copilot-app"], EnableNetFx3 = true }, null,
            CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Cleanup-Image"));
        Assert.Contains(fixture.Events, e => e.Contains("/FeatureName:NetFx3"));
        Assert.DoesNotContain("Component cleanup", StageOrder(result.WorkDirectory));
        Assert.All(JobScenarios.StageStates(result.WorkDirectory).Values, state => Assert.Equal("Completed", state));

        // Component-payload removals without the option never ask DISM either.
        using var custom = new BuildFixture();
        var action = new ExpertAction(ImageItemKind.ComponentPayload, "amd64_critical-component", ExpertActionMode.Remove);
        var customResult = await Engine(custom, args => IsCleanup(args) ? Pending() : null).BuildAsync(
            custom.Recipe with { SelectedOptions = ["copilot-app"], ExpertAcknowledged = true, ExpertActions = [action] },
            null, CancellationToken.None);
        Assert.Empty(customResult.Warnings);
        Assert.Equal(new[] { action }, custom.Operations.LastActions);
        Assert.DoesNotContain(custom.Events, e => e.Contains("/Cleanup-Image"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_the_cleanup_is_never_turned_into_a_skip_or_a_refusal(bool core)
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (IsCleanup(args)) cancel.Cancel(); };
        var events = new List<BuildProgress>();
        var engine = Engine(fixture, args => IsCleanup(args) ? Pending() : null);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(core ? CoreRecipe(fixture) : Tiny11Recipe(fixture),
            new ImmediateProgress<BuildProgress>(events.Add), cancel.Token));

        Assert.True(failure.IsCancellation);
        Assert.IsType<OperationCanceledException>(failure.InnerException);
        Assert.Equal("Cancelled", JobScenarios.StageStates(failure.WorkDirectory)["Component cleanup"]);
        Assert.DoesNotContain(events, e => e.OperationId == "stage:Component cleanup" && e.State == BuildOperationState.Skipped);
        Assert.DoesNotContain("Component cleanup was skipped", File.ReadAllText(Path.Combine(failure.WorkDirectory, "build.log")));
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Enable-Feature") || e is "TrimCore" or "RemoveOriginalFiles");
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Commit"));
        Assert.Contains(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Discard"));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    // ---- warnings survive a resume ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resumed_build_still_reports_the_skipped_cleanup()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Contains("/Unmount-Image") && args.Contains("/Commit")) cancel.Cancel();
        };
        var engine = Engine(fixture, PendingAfter(args => args.Contains("/Disable-Feature")));
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(Tiny11Recipe(fixture), null, cancel.Token));
        fixture.Runner.OnCommand = null;

        var checkpoint = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(2, checkpoint.Report.Warnings.Count);
        Assert.Contains(checkpoint.Report.Warnings, item => item.StartsWith("Component cleanup was skipped", StringComparison.Ordinal) && item.Contains("0x800F0806"));
        Assert.Contains(checkpoint.Report.Warnings, item => item.StartsWith("Edge and WebView2 removal was selected but not applied", StringComparison.Ordinal));
        Assert.Equal(BuildOperationState.Skipped, Assert.Single(checkpoint.Operations, item => item.Id == "stage:Component cleanup").State);
        Assert.Equal(BuildOperationState.Skipped, Assert.Single(checkpoint.Operations, item => item.Id == "retained:edge-files").State);

        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory),
            null, CancellationToken.None);
        Assert.Equal(checkpoint.Report.Warnings, result.Warnings);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(result.Warnings, report.RootElement.GetProperty("Warnings").EnumerateArray().Select(item => item.GetString()));
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.Equal("Skipped", stages["Component cleanup"]);
        Assert.All(stages.Where(pair => pair.Key != "Component cleanup"), pair => Assert.Equal("Completed", pair.Value));
    }

    [Fact]
    public async Task A_checkpoint_written_before_warnings_were_saved_still_resumes_without_any()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var file = Path.Combine(failure.WorkDirectory, JobCheckpoint.FileName);
        var saved = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.True(saved["Report"]!.AsObject().Remove("Warnings"));
        File.WriteAllText(file, saved.ToJsonString(JsonDefaults.Options));

        Assert.Empty(CheckpointStore.Read(failure.WorkDirectory).Report.Warnings);
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory),
            null, CancellationToken.None);
        Assert.Empty(result.Warnings);
    }

    // ---- the tracker ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_skipped_stage_keeps_its_outcome_and_time_when_it_is_closed()
    {
        var events = new List<BuildProgress>();
        var log = new List<string>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), log.Add);
        tracker.BeginStage("Component cleanup", "Clean superseded component files");
        tracker.SkipStage("Pending operations.");
        tracker.EndStage();

        Assert.Equal([BuildOperationState.Pending, BuildOperationState.Running, BuildOperationState.Skipped],
            StateTrail(events, "stage:Component cleanup"));
        Assert.Equal("Pending operations.", events.Last(item => item.OperationId == "stage:Component cleanup").Detail);
        var timing = Assert.Single(tracker.Timings);
        Assert.Equal(BuildOperationState.Skipped, timing.State);
        Assert.NotNull(timing.Milliseconds);
        Assert.Contains("Clean superseded component files: Pending operations.", log);
        Assert.DoesNotContain(log, line => line.StartsWith("Completed stage", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stage_skipped_through_the_general_skip_is_not_overwritten_by_closing_it()
    {
        var events = new List<BuildProgress>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), _ => { });
        tracker.BeginStage("Component cleanup", "Clean");
        tracker.Skip("stage:Component cleanup", "Component cleanup", "Clean", "Not needed.");
        tracker.BeginStage("Windows settings", "Settings");
        tracker.EndStage();

        Assert.Equal(BuildOperationState.Skipped, StateTrail(events, "stage:Component cleanup")[^1]);
        Assert.Equal(BuildOperationState.Completed, StateTrail(events, "stage:Windows settings")[^1]);
    }

    [Fact]
    public void A_stage_that_was_not_skipped_still_completes_and_a_skip_needs_an_open_stage()
    {
        var events = new List<BuildProgress>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), _ => { });
        Assert.Throws<InvalidOperationException>(() => tracker.SkipStage("No stage is open."));
        tracker.BeginStage("Component cleanup", "Clean");
        tracker.EndStage();
        Assert.Equal([BuildOperationState.Pending, BuildOperationState.Running, BuildOperationState.Completed],
            StateTrail(events, "stage:Component cleanup"));
        Assert.Throws<InvalidOperationException>(() => tracker.SkipStage("Already closed."));
    }
}

internal sealed class RecordingOperations(IImageOperations inner) : IImageOperations
{
    public List<string[]> OriginalFileOptions { get; } = [];

    public IReadOnlyList<ImageInventoryItem> ReadNativeInventory(string mount) => inner.ReadNativeInventory(mount);

    public void ApplyNativeActions(string mount, IReadOnlyList<ExpertAction> actions, Action<string> log, bool turboMode = false) =>
        inner.ApplyNativeActions(mount, actions, log, turboMode);

    public void RemoveOriginalFiles(string mount, string architecture, IReadOnlyList<string> options, Action<string> log, bool turboMode = false)
    {
        OriginalFileOptions.Add(options.ToArray());
        inner.RemoveOriginalFiles(mount, architecture, options, log, turboMode);
    }

    public void TrimCore(string mount, string architecture, Action<string> log, CancellationToken token, bool turboMode = false) =>
        inner.TrimCore(mount, architecture, log, token, turboMode);

    public IReadOnlyList<ImageCleanupResult> CleanRedundantData(string mount, Action<string> log, CancellationToken token) =>
        inner.CleanRedundantData(mount, log, token);
}

/// <summary>Runs every command through the fixture's FakeRunner, so its calls and events stay exact, and lets a test replace one result.</summary>
internal sealed class CommandResultRunner(IProcessRunner inner, Func<IReadOnlyList<string>, ProcessResult?> replace) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output,
        CancellationToken cancellationToken, bool finishOnCancellation = true)
    {
        var actual = await inner.RunAsync(executable, arguments, output, cancellationToken, finishOnCancellation).ConfigureAwait(false);
        if (replace(arguments) is not { } replacement) return actual;
        // The real runner streams each line DISM prints, so the job log holds the tool's own words.
        foreach (var line in replacement.Output.Split('\n')) output?.Invoke(line.TrimEnd('\r'));
        return replacement;
    }
}
