using System.Runtime.Versioning;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class PortableRecipeTests
{
    private static BuildRecipe Example => BuildRecipe.Read(Path.Combine(AppContext.BaseDirectory, "Recipes", "cd700-custom.json"));

    [Theory]
    [InlineData(22621, "x64", 3)]
    [InlineData(22631, "arm64", 1)]
    [InlineData(26300, "x64", 7)]
    [InlineData(26340, "arm64", 2)]
    [InlineData(29691, "x64", 4)]
    public void Extreme_recipe_binds_to_any_selected_supported_ISO_without_overwriting_local_paths(int build, string architecture, int index)
    {
        using var fixture = new BuildFixture(build, architecture);
        var current = fixture.Recipe with { ImageIndex = index };
        var profile = Example;
        Assert.Empty(profile.SourcePath);
        Assert.Empty(profile.WorkspacePath);
        Assert.Empty(profile.OutputPath);
        Assert.False(profile.ReuseSourceInstallImage);
        Assert.True(string.IsNullOrEmpty(profile.CustomSetupMediaPath));
        Assert.Equal(BuildFlavor.Core, profile.Flavor);
        Assert.True(profile.ShrinkBootImage);
        Assert.NotEmpty(profile.SelectedOptions);
        var bound = RecipeSelection.BindPaths(profile, current);
        bound.ValidateOptions();
        Assert.Equal(current.SourcePath, bound.SourcePath);
        Assert.Equal(current.WorkspacePath, bound.WorkspacePath);
        Assert.Equal(current.OutputPath, bound.OutputPath);
        Assert.Equal(index, bound.ImageIndex);
        CompatibilityTests.Image(build, architecture).Validate();
    }

    [Fact]
    public void Import_checks_every_selected_group_and_clears_unselected_groups()
    {
        var choices = CleanupCatalog.Options.Select(option => new OptionChoice(option) { Selected = true }).ToArray();
        var profile = Example;
        RecipeSelection.ApplyGroups(choices, profile.SelectedOptions);
        Assert.Equal(profile.SelectedOptions.Order(), choices.Where(choice => choice.Selected).Select(choice => choice.Option.Id).Order());
        Assert.All(choices.Where(choice => !profile.SelectedOptions.Contains(choice.Option.Id)), choice => Assert.False(choice.Selected));
        Assert.True(choices.Single(choice => choice.Option.Id == "remove-wow64").Selected);
        Assert.True(choices.Single(choice => choice.Option.Id == "remove-desktop-netfx").Selected);
    }

    [Fact]
    public void Exported_portable_recipe_has_no_preservation_or_external_installer_flags()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Example, JsonDefaults.Options);
        Assert.DoesNotContain("ReuseSourceInstallImage", json);
        Assert.DoesNotContain("CustomSetupMediaPath", json);
        Assert.Contains("\"ShrinkBootImage\": true", json);
    }

    [Fact]
    public void Source_binding_does_not_reset_removal_selections_or_require_a_prepared_source()
    {
        var profile = Example;
        var choices = CleanupCatalog.Options.Select(option => new OptionChoice(option)).ToArray();
        RecipeSelection.ApplyGroups(choices, profile.SelectedOptions);
        var rebound = RecipeSelection.BindPaths(profile, new() { SourcePath = @"C:\media\other.iso",
            WorkspacePath = @"C:\work", OutputPath = @"C:\out.iso", ImageIndex = 6 });
        Assert.Equal(profile.SelectedOptions, rebound.SelectedOptions);
        Assert.Equal(profile.SelectedOptions.Length, choices.Count(choice => choice.Selected));
        Assert.Equal(700000000, rebound.IsoSizeLimitBytes);
    }

    [Fact]
    public void Explicit_job_recipes_still_restore_their_own_paths_and_edition()
    {
        var original = Example with { SourcePath = @"C:\source.iso", WorkspacePath = @"C:\job", OutputPath = @"C:\job.iso", ImageIndex = 2 };
        var bound = RecipeSelection.BindPaths(original, new() { SourcePath = @"C:\different.iso", ImageIndex = 9 });
        Assert.Equal(original.SourcePath, bound.SourcePath);
        Assert.Equal(original.WorkspacePath, bound.WorkspacePath);
        Assert.Equal(original.OutputPath, bound.OutputPath);
        Assert.Equal(2, bound.ImageIndex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Obsolete_preservation_or_external_installer_recipes_fail_with_migration_guidance(bool preservation)
    {
        var recipe = new BuildRecipe { Mode = BuildMode.Custom, ExpertAcknowledged = true,
            ReuseSourceInstallImage = preservation, CustomSetupMediaPath = preservation ? "" : @"C:\old.iso" };
        var error = Assert.Throws<InvalidDataException>(recipe.ValidateOptions);
        Assert.Contains("portable extreme recipe", error.Message);
        Assert.Contains("Shrink boot.wim", error.Message);
    }

    [Fact]
    public void Extreme_groups_are_not_added_to_either_existing_preset()
    {
        Assert.Equal(38, CleanupCatalog.ForMode(BuildMode.Tiny11).Length);
        Assert.Equal(36, CleanupCatalog.ForMode(BuildMode.Tiny11Core).Length);
        Assert.All(CleanupCatalog.Options.Where(option => option.CustomOnly), option =>
        {
            Assert.DoesNotContain(option.Id, CleanupCatalog.ForMode(BuildMode.Tiny11));
            Assert.DoesNotContain(option.Id, CleanupCatalog.ForMode(BuildMode.Tiny11Core));
        });
    }

    [Theory]
    [InlineData(22621, "x64")]
    [InlineData(26300, "arm64")]
    [InlineData(29691, "x64")]
    public async Task Portable_extreme_choices_run_through_the_normal_image_pipeline(int build, string architecture)
    {
        using var fixture = new BuildFixture(build, architecture);
        var recipe = RecipeSelection.BindPaths(Example, fixture.Recipe) with
        {
            DismPath = fixture.Recipe.DismPath, OscdimgPath = fixture.Recipe.OscdimgPath, IsoSizeLimitBytes = null
        };
        var result = await fixture.Engine.BuildAsync(recipe, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.Equal(recipe.SelectedOptions, fixture.Registry.LastRecipe!.SelectedOptions);
        Assert.Contains("TrimCore", fixture.Events);
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Bootable"));
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Export-Image") && args.Contains("/Compress:recovery"));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Any(arg => arg.Contains("cd700-26300.9457", StringComparison.OrdinalIgnoreCase)));
    }
}
