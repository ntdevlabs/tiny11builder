using System.Runtime.Versioning;
using System.Xml.Linq;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class CustomRecipeEntryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Custom_entry_preserves_preset_selections_while_global_import_keeps_the_original_mode(bool useCustom)
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core,
            CoreAcknowledged = true, SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core).ToArray() };
        var path = Path.Combine(fixture.Root, "recipe.json");
        recipe.Write(path);
        var before = File.ReadAllBytes(path);
        var selected = RecipeSelection.Read(path, useCustom);
        Assert.Equal(useCustom ? BuildMode.Custom : BuildMode.Tiny11Core, selected.Mode);
        Assert.Equal(BuildFlavor.Core, selected.Flavor);
        Assert.Equal(recipe.SelectedOptions, selected.SelectedOptions);
        Assert.Equal(recipe.SourcePath, selected.SourcePath);
        Assert.Equal(recipe.ImageIndex, selected.ImageIndex);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Bundled_CD700_recipe_can_be_loaded_from_Custom_without_losing_its_specialized_settings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Recipes", "cd700-custom.json");
        var recipe = RecipeSelection.Read(path, useCustom: true);
        Assert.Equal(BuildMode.Custom, recipe.Mode);
        Assert.False(recipe.ReuseSourceInstallImage);
        Assert.True(recipe.ShrinkBootImage);
        Assert.Equal(700000000, recipe.IsoSizeLimitBytes);
        Assert.True(recipe.CompactInstallation);
        Assert.NotEmpty(recipe.SelectedOptions);
        Assert.Empty(recipe.ExpertActions);
        recipe.ValidateOptions();
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"SchemaVersion\":99}")]
    [InlineData("{\"SelectedOptions\":[\"unknown-group\"]}")]
    public void Invalid_recipes_fail_before_any_candidate_can_be_applied(string text)
    {
        using var fixture = new BuildFixture();
        var path = Path.Combine(fixture.Root, "invalid.json");
        File.WriteAllText(path, text);
        Assert.ThrowsAny<Exception>(() => RecipeSelection.Read(path, useCustom: true));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void Both_Custom_cards_and_the_inline_recipe_button_have_wired_entry_points_and_a_cancel_default()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace local = "using:Tiny11Builder.App";
        XElement Named(string name) => Assert.Single(document.Descendants(), element => element.Attribute(x + "Name")?.Value == name);
        var welcome = Named("CustomModeCard");
        var components = Named("CustomSelection");
        Assert.Equal("Custom", welcome.Attribute("Tag")?.Value);
        Assert.Equal("Custom", components.Attribute("Tag")?.Value);
        Assert.Equal("OnModeCardClick", welcome.Attribute("Click")?.Value);
        Assert.Equal("OnRemovalModeClick", components.Attribute("Click")?.Value);
        var dialog = Named("CustomEntryDialog");
        Assert.Equal(ns + "ContentDialog", dialog.Name);
        Assert.Equal("Close", dialog.Attribute("DefaultButton")?.Value);
        Assert.Equal("Choose manually", dialog.Attribute("PrimaryButtonText")?.Value);
        Assert.Equal("Load recipe…", dialog.Attribute("SecondaryButtonText")?.Value);
        Assert.Contains("SecondaryButtonText=CustomEntry.Load", dialog.Attribute(local + "Translation.Properties")!.Value);
        var inline = Named("LoadCustomRecipeButton");
        Assert.Equal("OnLoadCustomRecipe", inline.Attribute("Click")?.Value);
        Assert.Contains(Named("CustomChoices"), inline.Ancestors());
    }
}
