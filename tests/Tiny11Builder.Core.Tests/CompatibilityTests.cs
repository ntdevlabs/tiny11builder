using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class CompatibilityTests
{
    [Theory]
    [InlineData(22621, "x64")]
    [InlineData(22621, "arm64")]
    [InlineData(22631, "x64")]
    [InlineData(26100, "arm64")]
    [InlineData(26200, "x64")]
    [InlineData(26300, "arm64")]
    [InlineData(28000, "x64")]
    [InlineData(29689, "arm64")]
    [InlineData(99999, "x64")]
    public void Accepts_22H2_through_newer_and_Canary_without_an_upper_limit(int build, string architecture) =>
        Image(build, architecture).Validate();

    [Theory]
    [InlineData(22000, "x64", "Client")]
    [InlineData(22621, "x86", "Client")]
    [InlineData(29689, "arm64", "Server")]
    public void Rejects_non_supported_image_families(int build, string architecture, string installation) =>
        Assert.Throws<InvalidDataException>(() => (Image(build, architecture) with { Installation = installation }).Validate());

    [Fact]
    public void Parses_actual_build_revision_and_empty_description()
    {
        var parsed = DismClient.ParseImage(3, """
            Deployment Image Servicing and Management tool
            Version: 10.0.29689.1000

            Name : Windows 11 Pro
            Description :
            Architecture : ARM64
            Version : 10.0.29689
            ServicePack Build : 1234
            Edition : Professional
            Installation : Client
            Size : 16,234,567,890 bytes
            """);
        Assert.Equal("arm64", parsed.Architecture);
        Assert.Equal("", parsed.Description);
        Assert.Equal(new Version(10, 0, 29689, 1234), parsed.Version);
        Assert.Equal(16234567890, parsed.Size);
    }

    [Fact]
    public void Image_build_is_not_replaced_by_the_host_DISM_banner()
    {
        var parsed = DismClient.ParseImage(1, """
            Deployment Image Servicing and Management tool
            Version: 10.0.29689.1000

            Index : 1
            Name : Windows 11 Enterprise
            Description : Windows 11 Enterprise
            Architecture : arm64
            Version : 10.0.22621
            ServicePack Build : 2428
            Edition : Enterprise
            Installation : Client
            Size : 22.283.108.197 bytes
            """);
        Assert.Equal(new Version(10, 0, 22621, 2428), parsed.Version);
        Assert.Throws<InvalidDataException>(() => DismClient.ParseImage(1, """
            Version: 10.0.29689.1000
            Name : Windows 11
            Architecture : x64
            """));
    }

    [Fact]
    public void Metadata_does_not_default_missing_or_invalid_values()
    {
        Assert.Throws<InvalidDataException>(() => DismClient.ParseImage(1, "Name : Windows"));
        Assert.Throws<InvalidDataException>(() => DismClient.ParseFeatures("Operation completed successfully."));
    }

    [Fact]
    public void App_matching_is_exact_and_does_not_remove_shared_frameworks()
    {
        var option = CleanupCatalog.Options.Single(o => o.Id == "copilot-app");
        Assert.True(CleanupCatalog.MatchesApp(option, new("Microsoft.Copilot", "Microsoft.Copilot_1.0_arm64__8weky")));
        Assert.False(CleanupCatalog.MatchesApp(option, new("Microsoft.Copilot.Framework", "Microsoft.Copilot.Framework_1.0_arm64__8weky")));
        Assert.False(CleanupCatalog.MatchesApp(option, new("Microsoft.Copilot", "NotMicrosoft.Copilot_1.0_arm64__8weky")));
    }

    [Theory]
    [InlineData("x64", "amd64")]
    [InlineData("arm64", "arm64")]
    public void Unattend_is_architecture_correct_single_index_without_accounts_or_eula_acceptance(string architecture, string expected)
    {
        var text = UnattendBuilder.Create(Image(29689, architecture), new() { PreferLocalAccount = true });
        var xml = XDocument.Parse(text);
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        Assert.All(xml.Descendants(ns + "component"), c => Assert.Equal(expected, c.Attribute("processorArchitecture")!.Value));
        Assert.Equal("1", xml.Descendants(ns + "MetaData").Single().Element(ns + "Value")!.Value);
        Assert.Empty(xml.Descendants(ns + "UserAccounts"));
        Assert.Empty(xml.Descendants(ns + "AutoLogon"));
        Assert.Empty(xml.Descendants(ns + "AcceptEula"));
        Assert.Equal("false", xml.Descendants(ns + "DynamicUpdate").Single().Element(ns + "Enable")!.Value);
    }

    [Fact]
    public void Build_modes_have_exact_original_core_and_empty_custom_removal_sets()
    {
        Assert.Equal(3, Enum.GetValues<BuildMode>().Length);
        Assert.Equal(CleanupCatalog.ForPreset(CleanupPreset.Original), CleanupCatalog.ForMode(BuildMode.Tiny11));
        var core = CleanupCatalog.Options.Where(o => o.Minimal || o.Id is
            "language-extras" or "edge-files" or "onedrive-files" or
            "hardware-bypass" or "reserved-storage" or "device-encryption").Select(o => o.Id).ToArray();
        Assert.Equal(core, CleanupCatalog.ForMode(BuildMode.Tiny11Core));
        Assert.Empty(CleanupCatalog.ForMode(BuildMode.Custom));
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupCatalog.ForMode((BuildMode)99));
    }

    [Theory]
    [InlineData(BuildMode.Tiny11, BuildFlavor.Regular)]
    [InlineData(BuildMode.Tiny11Core, BuildFlavor.Core)]
    public void Fixed_modes_require_their_exact_removals_and_flavor(BuildMode mode, BuildFlavor flavor)
    {
        var recipe = new BuildRecipe
        {
            Mode = mode, Flavor = flavor, CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(mode)
        };
        recipe.ValidateOptions();
        Assert.Throws<InvalidDataException>(() => (recipe with { SelectedOptions = [] }).ValidateOptions());
        Assert.Throws<InvalidDataException>(() => (recipe with
        { Flavor = flavor == BuildFlavor.Core ? BuildFlavor.Regular : BuildFlavor.Core }).ValidateOptions());
        Assert.Throws<InvalidDataException>(() => (recipe with
        {
            ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.Service, "Driver", ExpertActionMode.Disable)]
        }).ValidateOptions());
    }

    [Fact]
    public void Custom_mode_keeps_arbitrary_choices_and_optional_core_removals()
    {
        new BuildRecipe { Mode = BuildMode.Custom }.ValidateOptions();
        new BuildRecipe
        {
            Mode = BuildMode.Custom, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = ["camera"], ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.Service, "Driver", ExpertActionMode.Disable)]
        }.ValidateOptions();
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { Mode = (BuildMode)99 }.ValidateOptions());
    }

    [Fact]
    public void Legacy_recipes_infer_modes_without_changing_custom_core_choices()
    {
        Assert.Equal(BuildMode.Tiny11, CleanupCatalog.ModeForRecipe(new()
        { SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11) }));
        Assert.Equal(BuildMode.Tiny11Core, CleanupCatalog.ModeForRecipe(new()
        { Flavor = BuildFlavor.Core, SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core) }));
        var modifiedCore = new BuildRecipe { Flavor = BuildFlavor.Core, SelectedOptions = ["camera"] };
        Assert.Equal(BuildMode.Custom, CleanupCatalog.ModeForRecipe(modifiedCore));
        Assert.Equal(BuildFlavor.Core, modifiedCore.Flavor);
        Assert.Equal(["camera"], modifiedCore.SelectedOptions);
        Assert.Equal(BuildMode.Custom, CleanupCatalog.ModeForRecipe(new()
        { Mode = BuildMode.Custom, SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11) }));
    }

    [Fact]
    public void Custom_mode_recipe_round_trip_preserves_mode_options_and_core()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with
        {
            Mode = BuildMode.Custom, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = ["camera"], ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.Service, "Driver", ExpertActionMode.Disable)]
        };
        var path = Path.Combine(fixture.Root, "recipe.json");
        recipe.Write(path);
        var restored = BuildRecipe.Read(path);
        Assert.Equal(recipe.Mode, restored.Mode);
        Assert.Equal(recipe.Flavor, restored.Flavor);
        Assert.Equal(recipe.SelectedOptions, restored.SelectedOptions);
        Assert.Equal(recipe.ExpertActions, restored.ExpertActions);
    }

    [Fact]
    public void Core_and_expert_actions_require_explicit_acknowledgement()
    {
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { Flavor = BuildFlavor.Core }.ValidateOptions());
        Assert.Throws<InvalidDataException>(() => new BuildRecipe
        { ExpertActions = [new(ImageItemKind.Service, "BootCriticalDriver", ExpertActionMode.Remove)] }.ValidateOptions());
        new BuildRecipe { Flavor = BuildFlavor.Core, CoreAcknowledged = true, ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.Service, "BootCriticalDriver", ExpertActionMode.Remove)] }.ValidateOptions();
    }

    [Fact]
    public void Recipes_reject_unknown_options_invalid_modes_duplicates_and_path_injection()
    {
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { SelectedOptions = ["unknown"] }.ValidateOptions());
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { SelectedOptions = ["ads", "ads"] }.ValidateOptions());
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.ComponentPayload, @"..\System32", ExpertActionMode.Remove)] }.ValidateOptions());
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.Package, "Package", ExpertActionMode.Disable)] }.ValidateOptions());
    }

    [Fact]
    public void Expert_actions_must_exist_in_the_selected_image_even_when_protected()
    {
        var item = new ImageInventoryItem(ImageItemKind.Service, "ProtectedBootDriver", "Boot", true);
        ImageInventory.ValidateActions([new(item.Kind, item.Identity, ExpertActionMode.Remove)], [item]);
        Assert.Throws<InvalidDataException>(() => ImageInventory.ValidateActions(
            [new(ImageItemKind.Service, "GuessedDriver", ExpertActionMode.Remove)], [item]));
    }

    [Theory]
    [InlineData("amd64_microsoft.windows.common-controls_6595_10.0.99999_none_x", true)]
    [InlineData("arm64_microsoft.windows.gdiplus_6595_10.0.29689_none_x", true)]
    [InlineData("amd64_microsoft-windows-servicingstack_31bf_10.0.99999_none_x", true)]
    [InlineData("amd64_microsoft-windows-s..stack-inetsrv-extra_31bf_newbuild", true)]
    [InlineData("arm64_microsoft.windows.c..-controls.resources_6595_newbuild", true)]
    [InlineData("Manifests", true)]
    [InlineData("arm64_optional-component_31bf_10.0.29689_none_x", false)]
    public void Core_retention_is_not_tied_to_a_specific_build(string name, bool kept) =>
        Assert.Equal(kept, ImageOperations.PreserveCoreDirectory(name));

    [Fact]
    public void Privacy_keeps_required_diagnostics_and_does_not_disable_security_services()
    {
        var values = PolicyCatalog.ForOption("privacy");
        Assert.Contains(values, p => p.Name == "AllowTelemetry" && Equals(p.Value, 1));
        Assert.DoesNotContain(values, p => p.Hive == PolicyHive.System || p.Key.Contains("Defender"));
    }

    [Fact]
    public void Widgets_use_the_supported_policy_without_the_protected_taskbar_preference()
    {
        var values = PolicyCatalog.ForOption("widgets");
        Assert.Contains(values, value => value.Hive == PolicyHive.Software && value.Key == @"Policies\Microsoft\Dsh" &&
            value.Name == "AllowNewsAndInterests" && Equals(value.Value, 0));
        Assert.DoesNotContain(values, value => value.Name == "TaskbarDa");
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\image.iso")]
    [InlineData(@"C:\test\file.iso:payload")]
    public void Rejects_unsafe_workspace_or_path_formats(string path)
    {
        if (path.StartsWith(@"\\") || path.Contains(":payload"))
            Assert.Throws<InvalidDataException>(() => PathSafety.Full(path));
        else
        {
            using var fixture = new BuildFixture();
            Assert.Throws<InvalidDataException>(() => PathSafety.Validate(fixture.Recipe with { WorkspacePath = path }));
        }
    }

    [Fact]
    public void Existing_output_and_input_overlap_are_refused_without_touching_data()
    {
        using var fixture = new BuildFixture();
        File.WriteAllText(fixture.Recipe.OutputPath, "keep");
        Assert.Throws<InvalidDataException>(() => PathSafety.Validate(fixture.Recipe));
        Assert.Equal("keep", File.ReadAllText(fixture.Recipe.OutputPath));
        Assert.Throws<InvalidDataException>(() => PathSafety.Validate(fixture.Recipe with
        { OutputPath = Path.Combine(fixture.Source, "output.iso"), WorkspacePath = fixture.Source }));
    }

    [Fact]
    public async Task Iso_validation_checks_the_required_shape()
    {
        await IsoStructure.ValidateAsync(new MemoryStream(BuildFixture.IsoBytes()), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => IsoStructure.ValidateAsync(new MemoryStream(new byte[65536]), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => IsoStructure.ValidateAsync(new MemoryStream(new byte[34817]), CancellationToken.None));
    }

    public static WindowsImage Image(int build = 29689, string architecture = "x64") =>
        new(3, "Windows 11 Pro", "", architecture, new Version(10, 0, build, 123), "Professional", "Client", 16L * 1024 * 1024 * 1024);
}
