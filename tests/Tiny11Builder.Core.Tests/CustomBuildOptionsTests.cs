using System.Runtime.Versioning;
using System.Xml.Linq;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class CustomBuildOptionsTests
{
    [Theory]
    [InlineData(700000000, true)]
    [InlineData(700000001, false)]
    public void Whole_ISO_budget_remains_exact_and_inclusive(long bytes, bool accepted)
    {
        if (accepted) BuildEngine.RequireIsoBudget(bytes, 700000000);
        else Assert.Throws<InvalidDataException>(() => BuildEngine.RequireIsoBudget(bytes, 700000000));
    }

    [Fact]
    public async Task Over_budget_full_pipeline_output_is_not_published_and_retains_its_checkpoint()
    {
        using var fixture = new BuildFixture();
        const long budget = 64L << 20;
        var runner = new CommandResultRunner(fixture.Runner, args =>
        {
            if (!args.Contains("-udfver102")) return null;
            using var output = File.Create(args[^1]);
            output.Write(BuildFixture.IsoBytes());
            output.SetLength(budget + 2048);
            return new(0, "complete fixture");
        });
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(
            fixture.Recipe with { Mode = BuildMode.Custom, IsoSizeLimitBytes = budget }, null, CancellationToken.None));
        Assert.Contains("complete ISO", failure.Message);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "output.iso")));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, JobCheckpoint.FileName)));
    }

    [Fact]
    public async Task Boot_shrink_and_recipe_budget_cannot_silently_change_on_resume()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { Mode = BuildMode.Custom, ShrinkBootImage = true, IsoSizeLimitBytes = 700000000 };
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, recipe);
        foreach (var changed in new[] { recipe with { ShrinkBootImage = false }, recipe with { IsoSizeLimitBytes = null } })
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(
                JobScenarios.JobFile(failure), changed, null, CancellationToken.None));
            Assert.Contains("frozen", error.Message);
        }
    }

    [Theory]
    [InlineData(@"..\host")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"Windows\System32\config")]
    [InlineData(@"Windows\System32\config\SYSTEM")]
    [InlineData(@"Windows\System32")]
    [InlineData(@"Users\Default\NTUSER.DAT")]
    [InlineData(@"Windows\Fonts\..\System32")]
    [InlineData(@"Windows\file:stream")]
    [InlineData(@"Windows\System32\config.\SYSTEM")]
    [InlineData(@"Windows\System32\config \SYSTEM")]
    [InlineData("Windows/file.dll")]
    public void Direct_file_recipe_cannot_escape_the_mount_or_remove_ownership_hives(string path) =>
        Assert.Throws<InvalidDataException>(() => new BuildRecipe { ExpertAcknowledged = true,
            ExpertActions = [new(ImageItemKind.FilePayload, path, ExpertActionMode.Remove)] }.ValidateOptions());

    [Fact]
    public void Advanced_groups_and_boot_reduction_are_Custom_only()
    {
        var recipe = new BuildRecipe { Mode = BuildMode.Custom, ExpertAcknowledged = true,
            SelectedOptions = ["remove-wow64"], ShrinkBootImage = true };
        recipe.ValidateOptions();
        Assert.True(SetupInstaller.RequiresBootEdit(recipe));
        Assert.True(SetupInstaller.RequiresLegacy(recipe));
        Assert.Throws<InvalidDataException>(() => (recipe with { ExpertAcknowledged = false }).ValidateOptions());
        Assert.Throws<InvalidDataException>(() => (recipe with { Mode = BuildMode.Tiny11 }).ValidateOptions());
    }

    [Fact]
    public async Task Pending_servicing_refuses_portable_file_groups_before_file_removal()
    {
        using var fixture = new BuildFixture();
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Get-Packages") ?
            new(0, "Package Identity : Pending~31bf~amd64~~1.0\nState : Install Pending\n") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(
            fixture.Recipe with { Mode = BuildMode.Custom, ExpertAcknowledged = true,
                SelectedOptions = ["remove-desktop-netfx"] }, null, CancellationToken.None));
        Assert.Contains("pending operations", failure.Message);
        Assert.DoesNotContain("RemoveOriginalFiles", fixture.Events);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public void Simplified_options_are_Custom_only_and_have_no_preserved_image_or_external_installer_picker()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var card = Assert.Single(document.Descendants(), element => element.Attribute(x + "Name")?.Value == "CustomMediaSettings");
        Assert.Equal("Collapsed", card.Attribute("Visibility")?.Value);
        Assert.Contains(card.Descendants(), element => element.Attribute(x + "Name")?.Value == "ShrinkBootCheck");
        Assert.DoesNotContain(card.Descendants(), element => element.Name.LocalName is "TextBox" or "NumberBox");
        Assert.DoesNotContain(document.Descendants(), element => element.Attribute(x + "Name")?.Value is
            "ReuseSourceCheck" or "CustomSetupSourceBox" or "CustomIsoLimitBox");
    }
}
