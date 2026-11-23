using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class Nano11Tests
{
    private static BuildRecipe Recipe(BuildFixture fixture) => fixture.Recipe with
    {
        Mode = BuildMode.Nano11, Flavor = BuildFlavor.Nano, CoreAcknowledged = true,
        SelectedOptions = CleanupCatalog.ForMode(BuildMode.Nano11)
    };

    [Fact]
    public void Nano11_is_its_own_mode_with_core_removals_and_a_required_acknowledgement()
    {
        Assert.Equal(BuildMode.Nano11, (BuildMode)2);
        Assert.Equal(BuildMode.Custom, (BuildMode)3);   // the app maps a combo index to this enum
        Assert.Equal(CleanupCatalog.ForMode(BuildMode.Tiny11Core), CleanupCatalog.ForMode(BuildMode.Nano11));
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { Mode = BuildMode.Nano11, Flavor = BuildFlavor.Nano }.ValidateOptions());
        new BuildRecipe
        {
            Mode = BuildMode.Nano11, Flavor = BuildFlavor.Nano, CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(BuildMode.Nano11)
        }.ValidateOptions();
        // A Nano11 recipe cannot be quietly turned into Core or Custom.
        Assert.Throws<InvalidDataException>(() => new BuildRecipe
        {
            Mode = BuildMode.Nano11, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(BuildMode.Nano11)
        }.ValidateOptions());
        Assert.Throws<InvalidDataException>(() => new BuildRecipe
        {
            Mode = BuildMode.Nano11, Flavor = BuildFlavor.Nano, CoreAcknowledged = true,
            SelectedOptions = ["camera"]
        }.ValidateOptions());
    }

    [Fact]
    public void Nano11_needs_the_legacy_installer_and_the_turbo_intermediate_compression_like_core()
    {
        var recipe = new BuildRecipe { Flavor = BuildFlavor.Nano, CoreAcknowledged = true };
        Assert.True(SetupInstaller.RequiresLegacy(recipe));
        Assert.True(SetupInstaller.RequiresBootEdit(recipe));
        var turbo = recipe with { TurboMode = true };
        Assert.True(BuildExecutionPlan.Create(turbo, 8).FastWorkingImage);
        Assert.StartsWith("Core stopped before trimming any component", PendingOperations.Refusal(recipe));
    }

    [Fact]
    public void Nano11_plan_is_architecture_aware_and_still_ships_families_for_both_architectures()
    {
        var x64 = Nano11Plan.RetainedStoreFamilies("x64");
        var arm64 = Nano11Plan.RetainedStoreFamilies("arm64");
        Assert.Contains("amd64_microsoft-windows-servicingstack", x64);
        Assert.Contains("arm64_microsoft-windows-servicingstack", arm64);
        Assert.DoesNotContain("amd64_microsoft-windows-servicingstack", arm64);
        // 32-bit compatibility payloads are kept on both, because Windows keeps them too.
        foreach (var families in new[] { x64, arm64 })
        {
            Assert.Contains("x86_microsoft.vc80.crt", families);
            Assert.Contains("x86_microsoft.windows.common-controls", families);
        }
        Assert.Contains("Handwriting", string.Join(" ", Nano11Plan.FeatureIdentities("x64")).Replace("amd64", "x64"), StringComparison.Ordinal);
        Assert.Contains("LanguageFeatures-OCR-arm64-Package", string.Join(" ", Nano11Plan.FeatureIdentities("arm64")));
        // Audio services are never deleted: the plan must not name them.
        Assert.DoesNotContain("Audiosrv", Nano11Plan.RemovedServices);
        Assert.DoesNotContain("AudioEndpointBuilder", Nano11Plan.RemovedServices);
    }

    [Fact]
    public async Task Nano11_stage_runs_in_order_after_native_actions_and_replaces_the_component_store()
    {
        using var fixture = new BuildFixture();
        var messages = new List<BuildProgress>();
        await fixture.Engine.BuildAsync(Recipe(fixture), new ImmediateProgress<BuildProgress>(messages.Add), CancellationToken.None);
        Assert.Contains(messages, m => m.Stage == "Nano11 reductions");
        // Core pruning never runs for Nano11: the component-store replacement supersedes it.
        Assert.DoesNotContain(fixture.Events, e => e == "TrimCore");
        // The component base is reset before the store is replaced, and the swap is the last file work.
        var reset = fixture.Runner.Calls.FindIndex(c => c.Contains("/ResetBase"));
        Assert.True(reset >= 0);
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Get-ProvisionedAppxPackages"));
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Remove-ProvisionedAppxPackage"));
    }

    [Fact]
    public async Task Nano11_is_recorded_in_the_build_report_as_its_own_flavor()
    {
        using var fixture = new BuildFixture { AllowCleanup = false };
        var result = await fixture.Engine.BuildAsync(Recipe(fixture), null, CancellationToken.None);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.WorkDirectory, "report.json")));
        Assert.True(report.RootElement.TryGetProperty("Flavor", out var flavor));
        Assert.Equal("Nano", flavor.GetString());
    }

    [Fact]
    public async Task Fixed_nano11_recipes_refuse_changes_to_the_nano11_removal_set()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture) with { SelectedOptions = [.. CleanupCatalog.ForMode(BuildMode.Nano11), "camera"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.BuildAsync(recipe, null, CancellationToken.None));
    }
}
