using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class LocalizedResumeTests
{
    [Fact]
    public async Task Previously_misdecoded_display_name_does_not_reject_a_hash_verified_image_on_resume()
    {
        using var fixture = new BuildFixture();
        var failed = await JobScenarios.InterruptAtCommitAsync(fixture);
        var saved = CheckpointStore.Read(failed.WorkDirectory);
        CheckpointStore.WriteAtomic(failed.WorkDirectory, saved with
        {
            Image = saved.Image with { Name = "Previously incorrectly decoded display name" }
        });
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failed), fixture.Recipe, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
    }

    [Fact]
    public async Task Revision_mismatch_still_refuses_a_resume_before_any_mutation()
    {
        using var fixture = new BuildFixture();
        var failed = await JobScenarios.InterruptAtCommitAsync(fixture);
        var saved = CheckpointStore.Read(failed.WorkDirectory);
        CheckpointStore.WriteAtomic(failed.WorkDirectory, saved with
        {
            Image = saved.Image with { Version = new(saved.Image.Version.Major, saved.Image.Version.Minor, saved.Image.Version.Build, 9999) }
        });
        var before = fixture.Runner.Calls.Count;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Engine.ResumeAsync(JobScenarios.JobFile(failed), fixture.Recipe, null, CancellationToken.None));
        Assert.Contains("different Windows image", error.Message);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), JobScenarios.Mutating);
    }
}
