using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class BuildSpaceTests
{
    [Theory]
    [InlineData(700000000L)]
    [InlineData(8921073664L)]
    public void Required_build_space_is_exactly_twice_the_ISO_not_expanded_Windows_metadata(long bytes)
    {
        using var fixture = new BuildFixture();
        var path = Path.Combine(fixture.Root, "input.iso");
        using (var file = File.Create(path)) file.SetLength(bytes);
        Assert.Equal(checked(bytes * 2), BuildHost.RequiredBuildSpace(fixture.Recipe with { SourcePath = path }));
    }

    [Fact]
    public void Extracted_source_counts_all_files_recursively_and_does_not_include_the_workspace()
    {
        using var fixture = new BuildFixture();
        var bytes = Directory.EnumerateFiles(fixture.Source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        Directory.CreateDirectory(fixture.Recipe.WorkspacePath);
        File.WriteAllBytes(Path.Combine(fixture.Recipe.WorkspacePath, "unrelated.bin"), new byte[1024]);
        Assert.Equal(bytes * 2, BuildHost.RequiredBuildSpace(fixture.Recipe));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    [InlineData(2001, true)]
    public void Exact_threshold_is_accepted_without_a_hidden_40_GiB_floor(long available, bool accepted)
    {
        if (accepted) BuildHost.RequireBuildSpace(available, 2000);
        else Assert.Throws<IOException>(() => BuildHost.RequireBuildSpace(available, 2000));
    }

    [Fact]
    public void Selected_updates_keep_their_staging_allowance_on_top_of_twice_the_source()
    {
        using var fixture = new BuildFixture();
        var package = Path.Combine(fixture.Root, "update.msu");
        File.WriteAllBytes(package, new byte[123]);
        Assert.Equal(BuildHost.RequiredBuildSpace(fixture.Recipe) + 123 * 3,
            BuildHost.RequiredBuildSpace(fixture.Recipe with { UpdatePackages = [package] }));
    }

    [Fact]
    public void New_build_preflight_does_not_multiply_the_expanded_image_size()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { DismPath = ToolLocator.FindDism(), OutputPath = "" };
        var image = CompatibilityTests.Image(22621) with { Size = 100L << 30 };
        new BuildHost().ValidateImage(recipe, image);
    }

    [Fact]
    public void Source_reparse_points_and_missing_or_empty_media_are_not_used_for_space_calculation()
    {
        using var fixture = new BuildFixture();
        var link = Path.Combine(fixture.Root, "link.iso");
        var empty = Path.Combine(fixture.Root, "empty.iso");
        File.WriteAllBytes(empty, []);
        Assert.Throws<InvalidDataException>(() => BuildHost.RequiredBuildSpace(fixture.Recipe with { SourcePath = empty }));
        Assert.Throws<FileNotFoundException>(() => BuildHost.RequiredBuildSpace(fixture.Recipe with { SourcePath = empty + ".missing" }));
        File.CreateSymbolicLink(link, empty);
        Assert.Throws<InvalidDataException>(() => BuildHost.RequiredBuildSpace(fixture.Recipe with { SourcePath = link }));
        File.Delete(link);
        var directoryLink = Path.Combine(fixture.Source, "nested-link");
        Directory.CreateSymbolicLink(directoryLink, fixture.Root);
        Assert.Throws<InvalidDataException>(() => BuildHost.RequiredBuildSpace(fixture.Recipe));
        Directory.Delete(directoryLink);
    }
}
