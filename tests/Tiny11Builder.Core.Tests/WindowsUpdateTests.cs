using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateTests
{
    private static BuildRecipe Recipe(BuildFixture fixture)
    {
        var cab = Path.Combine(fixture.Root, "prerequisite.cab");
        var msu = Path.Combine(fixture.Root, "latest update.msu");
        File.WriteAllBytes(cab, [1, 2, 3, 4]);
        File.WriteAllBytes(msu, [5, 6, 7, 8]);
        return fixture.Recipe with { UpdatePackages = [cab, msu] };
    }

    [Fact]
    public async Task Updates_are_staged_verified_and_applied_in_chosen_order_before_removals()
    {
        using var fixture = new BuildFixture(26300);
        var recipe = Recipe(fixture);
        var bytes = recipe.UpdatePackages.Select(File.ReadAllBytes).ToArray();
        var staged = new List<string>();
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Add-Package")) return;
            var path = args.Single(arg => arg.StartsWith("/PackagePath:"))["/PackagePath:".Length..];
            Assert.True(PathSafety.IsInside(path, recipe.WorkspacePath));
            Assert.Equal(bytes[staged.Count], File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
            Assert.DoesNotContain("/IgnoreCheck", args);
            Assert.DoesNotContain("/Online", args);
            Assert.Contains("/NoRestart", args);
            staged.Add(path);
        };
        var result = await fixture.Engine.BuildAsync(recipe, null, CancellationToken.None);
        Assert.Equal(recipe.UpdatePackages.Select(Path.GetFileName), staged.Select(Path.GetFileName));
        Assert.All(recipe.UpdatePackages.Select((path, i) => (path, i)), item => Assert.Equal(bytes[item.i], File.ReadAllBytes(item.path)));
        var add = fixture.Events.FindLastIndex(e => e.Contains("/Add-Package"));
        Assert.True(add < fixture.Events.FindIndex(e => e.Contains("/Remove-ProvisionedAppxPackage")));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        var updates = report.RootElement.GetProperty("Updates").EnumerateArray().ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes[0])), updates[0].GetProperty("Sha256").GetString());
        Assert.NotEmpty(report.RootElement.GetProperty("UpdatePackageStates").EnumerateArray());
        Assert.False(Directory.Exists(Path.Combine(result.WorkDirectory, "updates")));
    }

    [Fact]
    public async Task Updates_invalidate_the_optional_lazy_mount_feature_inventory()
    {
        using var fixture = new BuildFixture();
        var updated = false;
        var runner = new CommandResultRunner(fixture.Runner, args =>
        {
            if (args.Contains("/Add-Package")) updated = true;
            return updated && args.Contains("/Get-Features") ?
                new ProcessResult(0, "Feature Name : Recall\nState : Enabled\n") : null;
        });
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost(),
            new BuildEngineOptions(AllowOptimizedMount: true));
        var result = await engine.BuildAsync(Recipe(fixture) with
        {
            TurboMode = true, SelectedOptions = ["recall-feature"]
        }, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Disable-Feature") && args.Contains("/FeatureName:Recall"));
    }

    [Fact]
    public async Task Package_servicing_failure_stops_build_and_discards_owned_mount()
    {
        using var fixture = new BuildFixture();
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Add-Package") ?
            new ProcessResult(unchecked((int)0x800F081E), "The package is not applicable to this image.") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var error = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(Recipe(fixture), null, CancellationToken.None));
        Assert.Contains("not applicable", error.Message);
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Discard"));
        Assert.DoesNotContain("TrimCore", fixture.Events);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public async Task Pending_updates_are_retained_but_cannot_be_pruned()
    {
        using var fixture = new BuildFixture();
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Get-Packages") ?
            new ProcessResult(0, "Package Identity : TestUpdate~31bf~amd64~~1.0\nState : Install Pending\n") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var recipe = Recipe(fixture) with { Flavor = BuildFlavor.Core, CoreAcknowledged = true };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, CancellationToken.None));
        Assert.Contains("pending", failure.Message);
        Assert.DoesNotContain("TrimCore", fixture.Events);
        Assert.DoesNotContain("RemoveOriginalFiles", fixture.Events);
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("/Commit"));
    }

    [Fact]
    public async Task Serviceable_pending_updates_keep_unsafe_payloads_and_honestly_skip_cleanup()
    {
        using var fixture = new BuildFixture();
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Get-Packages") ?
            new ProcessResult(0, "Package Identity : TestUpdate~31bf~amd64~~1.0\nState : Install Pending\n") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var recipe = Recipe(fixture) with { SelectedOptions = ["cleanup", "edge-files"] };
        var progress = new List<BuildProgress>();
        var result = await engine.BuildAsync(recipe, new ImmediateProgress<BuildProgress>(progress.Add), CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Contains("/Cleanup-Image"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Windows update servicing has pending"));
        Assert.Contains(progress, item => item.OperationId == "retained:edge-files" && item.State == BuildOperationState.Skipped);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal("Install Pending", Assert.Single(report.RootElement.GetProperty("UpdatePackageStates").EnumerateArray())
            .GetProperty("Detail").GetString());
    }

    [Fact]
    public async Task Package_sources_must_not_follow_symlinks_or_be_changed_by_staging()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture);
        var link = Path.Combine(fixture.Root, "shortcut.cab");
        File.CreateSymbolicLink(link, recipe.UpdatePackages[0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.BuildAsync(
            recipe with { UpdatePackages = [link] }, null, CancellationToken.None));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(recipe.UpdatePackages[0]));
    }

    [Fact]
    public async Task Cancelling_at_an_update_boundary_preserves_original_packages_and_discards_the_mount()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture);
        using var cancellation = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Add-Package")) cancellation.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(recipe, null, cancellation.Token));
        Assert.True(failure.IsCancellation);
        Assert.Single(fixture.Runner.Calls, args => args.Contains("/Add-Package"));
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Discard"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(recipe.UpdatePackages[0]));
        Assert.False(File.Exists(recipe.OutputPath));
    }

    [Fact]
    public async Task Resume_does_not_reapply_updates_or_require_the_original_package_files()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture);
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, recipe);
        var saved = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(2, saved.Report.Updates.Count);
        foreach (var file in recipe.UpdatePackages) File.Delete(file);
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), args => args.Contains("/Add-Package"));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal(2, report.RootElement.GetProperty("Updates").GetArrayLength());
    }

    [Fact]
    public async Task Changed_update_order_is_frozen_on_resume()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture);
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, recipe);
        var before = fixture.Runner.Calls.Count;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure),
            recipe with { UpdatePackages = [.. Enumerable.Reverse(recipe.UpdatePackages)] }, null, CancellationToken.None));
        Assert.Contains("UpdatePackages", error.Message);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), JobScenarios.Mutating);
    }

    [Fact]
    public async Task Invalid_sources_are_refused_before_any_mount_or_export()
    {
        using var fixture = new BuildFixture();
        var cab = Path.Combine(fixture.Root, "missing.cab");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.Engine.BuildAsync(fixture.Recipe with { UpdatePackages = [cab] }, null, CancellationToken.None));
        Assert.Empty(fixture.Runner.Calls);
        File.WriteAllBytes(cab, []);
        Assert.Throws<InvalidDataException>(() => WindowsUpdates.ValidateSources(fixture.Recipe with { UpdatePackages = [cab] }));
        Assert.Throws<InvalidDataException>(() => (fixture.Recipe with { UpdatePackages = [cab, cab.ToUpperInvariant()] }).ValidateOptions());
        Assert.Throws<InvalidDataException>(() => (fixture.Recipe with { UpdatePackages = [Path.ChangeExtension(cab, ".cap")] }).ValidateOptions());
        Assert.Throws<InvalidDataException>(() => (fixture.Recipe with { UpdatePackages = ["https://example.com/update.msu"] }).ValidateOptions());
    }

    [Fact]
    public void Old_recipes_have_no_selected_updates_and_round_trip_them_when_added()
    {
        Assert.Empty(JsonSerializer.Deserialize<BuildRecipe>("{}", JsonDefaults.Options)!.UpdatePackages);
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture);
        var path = Path.Combine(fixture.Root, "updates-recipe.json");
        recipe.Write(path);
        Assert.Equal(recipe.UpdatePackages, BuildRecipe.Read(path).UpdatePackages);
    }

    [Fact]
    public void Server_media_is_identified_as_unsupported_without_suggesting_signature_bypass()
    {
        var server = new WindowsImage(1, "Windows Server 2025 Standard", "", "arm64", new(10, 0, 26100, 1),
            "ServerStandard", "Server", 1);
        var error = Assert.Throws<InvalidDataException>(server.Validate);
        Assert.Contains("Windows Server images are not supported", error.Message);
        Assert.DoesNotContain("signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
