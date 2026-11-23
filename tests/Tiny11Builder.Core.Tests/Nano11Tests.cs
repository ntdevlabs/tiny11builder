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
    public void Nano11_file_removals_apply_to_a_mount_and_name_what_they_skip()
    {
        using var mount = new TempMount();
        mount.File(@"Windows\Help\help.html");
        mount.File(@"Windows\Web\wallpaper.jpg");
        mount.File(@"Windows\Fonts\seguiemj.ttf");
        mount.File(@"Windows\Fonts\arial.ttf");
        mount.File(@"Windows\Fonts\malgun.ttf");
        mount.File(@"Windows\System32\DriverStore\FileRepository\prnms001.inf_x64_neutral_deadbeef\prnms001.inf");
        mount.File(@"Windows\System32\DriverStore\FileRepository\nvhda.inf_x64_deadbeef\nvhda.inf");
        mount.File(@"Windows\assembly\NativeImages_v4.0.30319_64\cache.dat");
        mount.File(@"Windows\System32\Tasks\Microsoft\Windows\Chkdsk\Proxy");
        var log = new List<string>();
        Nano11Files.Apply(mount.Root, log.Add);

        Assert.False(File.Exists(mount.Path(@"Windows\Help\help.html")));
        Assert.False(File.Exists(mount.Path(@"Windows\Web\wallpaper.jpg")));
        Assert.True(File.Exists(mount.Path(@"Windows\Fonts\seguiemj.ttf")));    // kept by the plan's prefix list
        Assert.True(File.Exists(mount.Path(@"Windows\Fonts\arial.ttf")));
        Assert.False(File.Exists(mount.Path(@"Windows\Fonts\malgun.ttf")));    // non-Latin family the plan names
        Assert.False(Directory.Exists(mount.Path(@"Windows\System32\DriverStore\FileRepository\prnms001.inf_x64_neutral_deadbeef")));
        Assert.True(Directory.Exists(mount.Path(@"Windows\System32\DriverStore\FileRepository\nvhda.inf_x64_deadbeef")));
        Assert.False(Directory.Exists(mount.Path(@"Windows\assembly\NativeImages_v4.0.30319_64")));
        Assert.False(File.Exists(mount.Path(@"Windows\System32\Tasks\Microsoft\Windows\Chkdsk\Proxy")));
        // The recovery image becomes the empty placeholder the script leaves behind.
        Assert.True(File.Exists(mount.Path(@"Windows\System32\Recovery\winre.wim")));
        Assert.Empty(File.ReadAllBytes(mount.Path(@"Windows\System32\Recovery\winre.wim")));
        Assert.Contains(log, line => line.Contains(@"Nano11: not present: Windows\System32\OneDriveSetup.exe"));
    }

    [Fact]
    public void Nano11_recipe_settings_are_the_smallest_ones_available()
    {
        var recipe = new BuildRecipe
        {
            Mode = BuildMode.Nano11, Flavor = BuildFlavor.Nano, CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(BuildMode.Nano11)
        };
        // The smallest output: recovery ESD, no redundant data, no staged .NET 3.5 payload, rebuilt boot.wim.
        var smallest = recipe.WithSmallestOutput();
        Assert.Equal(Compression.Recovery, smallest.Compression);
        Assert.True(smallest.RemoveRedundantData);
        Assert.True(smallest.OmitNetFx3SetupPayload);
        Assert.True(smallest.ShrinkBootImage);
        smallest.ValidateOptions();   // a shrunk boot.wim is valid for Nano11, not only for Custom
        Assert.Throws<InvalidDataException>(() => (recipe with { Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core,
            ShrinkBootImage = true }).ValidateOptions());
    }

    [Fact]
    public async Task Nano11_builds_the_smallest_output_even_when_the_recipe_asks_for_larger_settings()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture) with
        {
            Compression = Compression.Fast, RemoveRedundantData = false, OmitNetFx3SetupPayload = false, ShrinkBootImage = false
        };
        await fixture.Engine.BuildAsync(recipe, null, CancellationToken.None);
        // Fast output, kept redundant data and the .NET 3.5 setup payload are never what Nano11 produces.
        var compressed = fixture.Runner.Calls.Single(c => c.Contains("/Export-Image") && c.Any(a => a.EndsWith("install.esd")));
        Assert.Contains("/Compress:recovery", compressed);
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Compress:max"));   // the rebuilt boot.wim
        Assert.DoesNotContain(fixture.Events, e => e == "CleanRedundantData");
    }

    [Fact]
    public async Task Fixed_nano11_recipes_refuse_changes_to_the_nano11_removal_set()
    {
        using var fixture = new BuildFixture();
        var recipe = Recipe(fixture) with { SelectedOptions = [.. CleanupCatalog.ForMode(BuildMode.Nano11), "camera"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.BuildAsync(recipe, null, CancellationToken.None));
    }

internal sealed class TempMount : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "nano11-mount-" + Guid.NewGuid().ToString("N"));
    public TempMount() => Directory.CreateDirectory(Root);
    public string Path(string relative) => System.IO.Path.Combine(Root, relative);
    public void File(string relative)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "fixture");
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
}
