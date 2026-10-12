using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ResumeValidationTests
{
    [Fact]
    public void Resume_validation_does_not_need_the_source_but_still_keeps_workspace_and_output_safe()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { SourcePath = Path.Combine(fixture.Root, "deleted-source") };
        PathSafety.ValidateForResume(recipe);                        // a missing source is fine once the image is committed
        Assert.Throws<FileNotFoundException>(() => PathSafety.Validate(recipe));   // a new build still requires it
        Assert.Throws<InvalidDataException>(() => PathSafety.ValidateForResume(
            recipe with { OutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "x.iso") }));
        Assert.Throws<InvalidDataException>(() => PathSafety.ValidateForResume(
            recipe with { WorkspacePath = Environment.GetFolderPath(Environment.SpecialFolder.Windows) }));
        Assert.Throws<InvalidDataException>(() => PathSafety.ValidateForResume(recipe with { OutputPath = Path.Combine(fixture.Root, "x.wim") }));
        File.WriteAllText(recipe.OutputPath, "exists");
        Assert.Throws<InvalidDataException>(() => PathSafety.ValidateForResume(recipe));
    }

    [Fact]
    public void Resume_validation_keeps_the_source_separate_when_the_source_exists()
    {
        using var fixture = new BuildFixture();
        Assert.Throws<InvalidDataException>(() => PathSafety.ValidateForResume(
            fixture.Recipe with { OutputPath = Path.Combine(fixture.Source, "inside.iso") }));
    }

    [Fact]
    public void The_real_host_holds_a_resume_to_its_own_free_space_requirement()
    {
        using var fixture = new BuildFixture();
        var image = new WindowsImage(1, "Windows 11 Pro", "", "x64", new Version(10, 0, 22621, 1), "Professional", "Client", 1);
        var recipe = fixture.Recipe with { DismPath = ToolLocator.FindDism(), OutputPath = "" };
        Directory.CreateDirectory(recipe.WorkspacePath);
        var host = new BuildHost();
        Assert.Throws<IOException>(() => host.ValidateResume(recipe, image, long.MaxValue));
        Assert.Throws<InvalidDataException>(() => host.ValidateResume(recipe,
            image with { Architecture = "x86" }, 1));                // an unsupported image is still refused
    }

    [Fact]
    public void Older_hosts_keep_working_because_resume_validation_falls_back_to_the_normal_image_check()
    {
        IBuildHost host = new FakeHost();
        var image = new WindowsImage(1, "Windows 11 Pro", "", "x64", new Version(10, 0, 22621, 1), "Professional", "Client", 1);
        host.ValidateResume(new BuildRecipe(), image, 1);
        Assert.Throws<InvalidDataException>(() => host.ValidateResume(new BuildRecipe(), image with { Installation = "Server" }, 1));
    }
}
