using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class CapabilityBatchTests
{
    private static readonly string[] Three =
        ["App.StepsRecorder~~~~0.0.1.0", "MathRecognizer~~~~0.0.1.0", "Language.OCR~~~en-US~0.0.1.0"];

    private static BuildFixture Fixture(params string[] extra)
    {
        var fixture = new BuildFixture();
        fixture.Runner.InstalledCapabilities.AddRange(extra);
        return fixture;
    }

    private static BuildRecipe CapabilityRecipe(BuildFixture fixture, bool turbo) => fixture.Recipe with
    {
        TurboMode = turbo, SelectedOptions = ["language-extras", "legacy-capabilities"]
    };

    // ---- DismClient: the exact semantics of the verified batch ------------------------------------------------------------

    [Fact]
    public async Task Batch_uses_one_command_with_repeated_CapabilityName_and_one_post_removal_inventory()
    {
        using var fixture = Fixture("App.StepsRecorder~~~~0.0.1.0", "MathRecognizer~~~~0.0.1.0");
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        var result = await dism.RemoveCapabilitiesAsync(@"C:\mock\mount", Three, CancellationToken.None);
        Assert.Equal(2, fixture.Runner.Calls.Count);
        Assert.Equal(3, fixture.Runner.Calls[0].Count(arg => arg.StartsWith("/CapabilityName:", StringComparison.Ordinal)));
        Assert.Contains("/Remove-Capability", fixture.Runner.Calls[0]);
        Assert.Contains("/NoRestart", fixture.Runner.Calls[0]);
        Assert.Contains("/Get-Capabilities", fixture.Runner.Calls[1]);
        Assert.All(result.Items, item => Assert.Equal(RemovalProof.Absent, item.Proof));
    }

    [Fact]
    public async Task Names_are_deduplicated_ordinal_ignore_case_before_the_command_is_built()
    {
        using var fixture = Fixture();
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        await dism.RemoveCapabilitiesAsync(@"C:\mock\mount",
            ["Language.OCR~~~en-US~0.0.1.0", "LANGUAGE.ocr~~~EN-us~0.0.1.0", " "], CancellationToken.None);
        Assert.Single(fixture.Runner.Calls[0], arg => arg.StartsWith("/CapabilityName:", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => dism.RemoveCapabilitiesAsync(@"C:\mock\mount", [" "], CancellationToken.None));
    }

    [Theory]
    [InlineData("Not Present", RemovalProof.Absent)]
    [InlineData("not  present", RemovalProof.Absent)]
    [InlineData("Installed", RemovalProof.Present)]
    [InlineData("Staged", RemovalProof.Present)]
    [InlineData("Partially Installed", RemovalProof.Present)]
    [InlineData("Uninstall Pending", RemovalProof.Pending)]
    [InlineData("Install Pending", RemovalProof.Pending)]
    [InlineData("Removed", RemovalProof.Unknown)]
    [InlineData("", RemovalProof.Unknown)]
    [InlineData(null, RemovalProof.Unknown)]
    public void Only_Not_Present_is_proof_that_a_capability_is_gone(string? state, RemovalProof expected) =>
        Assert.Equal(expected, DismClient.ClassifyCapabilityState(state));

    [Theory]
    [InlineData("Installed", "still present")]
    [InlineData("Uninstall Pending", "removal pending")]
    [InlineData("Surprise", "unrecognised state")]
    public async Task A_batch_whose_absence_is_not_proven_fails_with_a_distinct_diagnostic(string state, string diagnostic)
    {
        using var fixture = Fixture();
        fixture.Runner.CapabilityStateAfterRemoval = state;
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            dism.RemoveCapabilitiesAsync(@"C:\mock\mount", ["Language.OCR~~~en-US~0.0.1.0"], CancellationToken.None));
        Assert.Contains(diagnostic, failure.Message);
        Assert.Contains("Language.OCR~~~en-US~0.0.1.0", failure.Message);
    }

    [Fact]
    public async Task An_identity_missing_from_the_post_removal_inventory_is_not_proof()
    {
        using var fixture = Fixture();
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            dism.RemoveCapabilitiesAsync(@"C:\mock\mount", ["Never.Listed~~~~0.0.1.0"], CancellationToken.None));
        Assert.Contains("missing from the post-removal inventory", failure.Message);
    }

    [Fact]
    public async Task A_removal_that_DISM_reports_as_success_but_did_not_happen_is_caught_by_the_inventory()
    {
        using var fixture = Fixture();
        fixture.Runner.IgnoreCapabilityRemoval = true;     // exit 0, nothing removed
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            dism.RemoveCapabilitiesAsync(@"C:\mock\mount", ["Language.OCR~~~en-US~0.0.1.0"], CancellationToken.None));
        Assert.Contains("still present", failure.Message);
    }

    // ---- BuildEngine: Turbo batches once, Standard keeps per-item behaviour ------------------------------------------------

    [Fact]
    public async Task Turbo_removes_every_selected_capability_in_one_batch_with_an_honest_shared_duration()
    {
        using var fixture = Fixture("App.StepsRecorder~~~~0.0.1.0", "MathRecognizer~~~~0.0.1.0");
        var events = new List<BuildProgress>();
        var result = await fixture.Engine.BuildAsync(CapabilityRecipe(fixture, turbo: true),
            new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);
        Assert.Single(fixture.Runner.Calls, call => call.Contains("/Remove-Capability"));
        Assert.All(Three, id => Assert.Contains(events, item => item.OperationId == "capability:" + id &&
            item.State == BuildOperationState.Completed && item.Detail!.Contains("shared by the whole batch") &&
            item.Detail.Contains("Verified absent")));
        using var operations = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.WorkDirectory, "operations.json")));
        Assert.Equal(3, operations.RootElement.EnumerateArray().Count(item =>
            item.GetProperty("Id").GetString()!.StartsWith("capability:", StringComparison.Ordinal)));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(3, report.RootElement.GetProperty("Capabilities").GetArrayLength());
        Assert.Contains("optimized mount=False", File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log")));
    }

    [Fact]
    public async Task Turbo_proves_absence_even_for_a_single_capability()
    {
        using var fixture = Fixture();
        await fixture.Engine.BuildAsync(CapabilityRecipe(fixture, turbo: true), null, CancellationToken.None);
        var removal = fixture.Runner.Calls.FindIndex(call => call.Contains("/Remove-Capability"));
        Assert.True(removal > 0);
        Assert.Contains("/Get-Capabilities", fixture.Runner.Calls[removal + 1]);
    }

    [Fact]
    public async Task Standard_mode_keeps_one_remove_capability_command_per_identity()
    {
        using var fixture = Fixture("App.StepsRecorder~~~~0.0.1.0", "MathRecognizer~~~~0.0.1.0");
        await fixture.Engine.BuildAsync(CapabilityRecipe(fixture, turbo: false), null, CancellationToken.None);
        var removals = fixture.Runner.Calls.Where(call => call.Contains("/Remove-Capability")).ToArray();
        Assert.Equal(3, removals.Length);
        Assert.All(removals, call => Assert.Single(call, arg => arg.StartsWith("/CapabilityName:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_failed_Turbo_capability_batch_fails_every_member_and_the_build_and_discards_the_mount()
    {
        using var fixture = Fixture("App.StepsRecorder~~~~0.0.1.0");
        fixture.Runner.CapabilityStateAfterRemoval = "Uninstall Pending";
        var events = new List<BuildProgress>();
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(
            CapabilityRecipe(fixture, turbo: true), new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None));
        Assert.Contains("removal pending", failure.Message);
        Assert.All(new[] { "App.StepsRecorder~~~~0.0.1.0", "Language.OCR~~~en-US~0.0.1.0" }, id =>
            Assert.Equal(BuildOperationState.Failed, events.Last(item => item.OperationId == "capability:" + id).State));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Unmount-Image") && call.Contains("/Discard"));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));    // never committed: nothing to resume
    }
}

[SupportedOSPlatform("windows")]
public sealed class FeatureStateTests
{
    private static BuildRecipe FeatureRecipe(BuildFixture fixture, bool turbo) => fixture.Recipe with
    {
        TurboMode = turbo, SelectedOptions = ["workfolders", "xps"]
    };

    [Theory]
    [InlineData("Disabled with Payload Removed", FeatureRemovalState.PayloadRemoved)]
    [InlineData("disabled  WITH payload removed", FeatureRemovalState.PayloadRemoved)]
    [InlineData("Disabled", FeatureRemovalState.DisabledPayloadRetained)]
    [InlineData("Disable Pending", FeatureRemovalState.DisablePending)]
    [InlineData("Enabled", FeatureRemovalState.Enabled)]
    [InlineData("Enable Pending", FeatureRemovalState.Enabled)]
    [InlineData("", FeatureRemovalState.Unknown)]
    [InlineData(null, FeatureRemovalState.Unknown)]
    [InlineData("Surprise", FeatureRemovalState.Unknown)]
    public void Feature_states_are_classified_without_calling_plain_Disabled_payload_removal(string? state, FeatureRemovalState expected) =>
        Assert.Equal(expected, DismClient.ClassifyFeatureState(state));

    [Theory]
    [InlineData("Disabled with Payload Removed", "payload removed", "PayloadRemoved")]
    [InlineData("Disabled", "left its payload in the image", "DisabledPayloadRetained")]
    [InlineData("Disable Pending", "disable pending", "DisablePending")]
    public async Task Turbo_reports_the_real_state_of_every_feature_and_accepts_plain_Disabled(string state, string wording, string outcome)
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FeatureStateAfterRemoval = state;
        var events = new List<BuildProgress>();
        var result = await fixture.Engine.BuildAsync(FeatureRecipe(fixture, turbo: true),
            new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        foreach (var id in new[] { "feature:WorkFolders-Client", "feature:Printing-XPSServices-Features" })
        {
            var completed = events.Last(item => item.OperationId == id);
            Assert.Equal(BuildOperationState.Completed, completed.State);
            Assert.Contains(wording, completed.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("shared by the whole batch", completed.Detail);
            if (outcome != "PayloadRemoved") Assert.DoesNotContain("payload removed", completed.Detail, StringComparison.OrdinalIgnoreCase);
        }
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.All(report.RootElement.GetProperty("OptionalFeatures").EnumerateArray(), item =>
            Assert.Equal(outcome, item.GetProperty("Outcome").GetString()));
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Enable Pending")]
    [InlineData("Surprise")]
    public async Task A_feature_that_is_still_enabled_or_unrecognised_fails_the_batch_and_every_member(string state)
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FeatureStateAfterRemoval = state;
        var events = new List<BuildProgress>();
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(
            FeatureRecipe(fixture, turbo: true), new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None));
        Assert.Contains("did not disable every requested feature", failure.Message);
        Assert.Contains(state, failure.Message);
        Assert.All(new[] { "feature:WorkFolders-Client", "feature:Printing-XPSServices-Features" }, id =>
            Assert.Equal(BuildOperationState.Failed, events.Last(item => item.OperationId == id).State));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Unmount-Image") && call.Contains("/Discard"));
    }

    [Fact]
    public async Task The_batch_check_stays_compatible_for_callers_that_only_want_the_command_output()
    {
        using var fixture = new BuildFixture();
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        var output = await dism.DisableFeaturesAsync(@"C:\mock\mount", ["WorkFolders-Client", "Printing-XPSServices-Features"], CancellationToken.None);
        Assert.Contains("completed successfully", output);
        await Assert.ThrowsAsync<ArgumentException>(() => dism.DisableFeaturesAsync(@"C:\mock\mount", [], CancellationToken.None));
    }

    [Fact]
    public async Task A_feature_missing_from_the_post_removal_inventory_fails_the_batch()
    {
        using var fixture = new BuildFixture();
        var dism = new DismClient(@"C:\mock\dism.exe", fixture.Runner);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => dism.DisableFeaturesWithStatesAsync(
            @"C:\mock\mount", ["WorkFolders-Client", "Never-Listed"], CancellationToken.None));
        Assert.Contains("Never-Listed => missing from the post-removal inventory", failure.Message);
    }
}
