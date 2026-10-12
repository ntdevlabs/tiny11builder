using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

/// <summary>Every refusal happens before anything is changed: no mutating command, no file touched, job marker unchanged.</summary>
[SupportedOSPlatform("windows")]
public sealed class ResumeRefusalTests
{
    private static BuildRecipe Bypass(BuildFixture fixture) => fixture.Recipe with { SelectedOptions = ["hardware-bypass", "copilot-app"] };

    private static async Task<InvalidDataException> AssertRefusedAsync(BuildFixture fixture, BuildFailureException job, string fragment,
        BuildRecipe? recipe = null)
    {
        var directory = job.WorkDirectory;
        var marker = File.ReadAllBytes(Path.Combine(directory, "job.json"));
        var snapshot = JobScenarios.Snapshot(directory);
        var calls = fixture.Runner.Calls.Count;
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(
            JobScenarios.JobFile(job), recipe ?? JobScenarios.OriginalRecipe(directory), null, CancellationToken.None));
        Assert.Contains(fragment, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(calls), JobScenarios.Mutating);
        Assert.Equal(snapshot, JobScenarios.Snapshot(directory));
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(directory, "job.json")));
        return refused;
    }

    private static void Flip(string file, int offset)
    {
        var bytes = File.ReadAllBytes(file);
        bytes[offset] ^= 0x01;
        File.WriteAllBytes(file, bytes);
    }

    private static void Rewrite(string directory, Func<ResumeCheckpoint, ResumeCheckpoint> change) =>
        CheckpointStore.WriteAtomic(directory, change(CheckpointStore.Read(directory)));

    // ---- the saved image and media -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_working_image_with_a_changed_byte_is_refused_by_its_hash_not_by_its_timestamp()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var working = Path.Combine(job.WorkDirectory, "working.wim");
        var stamp = File.GetLastWriteTimeUtc(working);
        Flip(working, 500);                                  // same length, valid header
        File.SetLastWriteTimeUtc(working, stamp);            // the timestamp is identical; only the hash can tell
        await AssertRefusedAsync(fixture, job, "SHA-256");
    }

    [Fact]
    public async Task A_working_image_of_a_different_size_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        File.AppendAllText(Path.Combine(job.WorkDirectory, "working.wim"), "x");
        await AssertRefusedAsync(fixture, job, "different size");
    }

    [Fact]
    public async Task A_working_image_left_mid_write_is_refused_by_its_header()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var working = Path.Combine(job.WorkDirectory, "working.wim");
        var bytes = File.ReadAllBytes(working);
        bytes[16] |= 0x40;                                   // the WIM write-in-progress flag
        File.WriteAllBytes(working, bytes);
        await AssertRefusedAsync(fixture, job, "mid-write");
    }

    [Fact]
    public async Task A_missing_working_image_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        File.Delete(Path.Combine(job.WorkDirectory, "working.wim"));
        await AssertRefusedAsync(fixture, job, "working.wim");
    }

    [Fact]
    public async Task A_working_image_that_is_not_the_recorded_windows_image_is_refused()
    {
        using var fixture = new BuildFixture(26300, "x64");
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        // The runner now reports another build for every image, as if the file were a different image with the same bytes.
        using var other = new BuildFixture(22631, "x64");
        var engine = new BuildEngine(other.Runner, new FakeMediaFactory(), other.Registry, other.Operations, new FakeHost());
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => engine.ResumeAsync(
            JobScenarios.JobFile(job), JobScenarios.OriginalRecipe(job.WorkDirectory), null, CancellationToken.None));
        Assert.Contains("different Windows image", refused.Message);
        Assert.DoesNotContain(other.Runner.Calls, JobScenarios.Mutating);
    }

    [Theory]
    [InlineData("modified", "changed after the checkpoint")]
    [InlineData("missing", "missing")]
    [InlineData("extra", "unexpected file")]
    public async Task Staged_media_that_differs_from_the_manifest_in_any_way_is_refused(string kind, string fragment)
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        var media = Path.Combine(job.WorkDirectory, "media");
        switch (kind)
        {
            case "modified": File.AppendAllText(Path.Combine(media, "autounattend.xml"), "<!-- edited -->"); break;
            case "missing": File.Delete(Path.Combine(media, @"boot\etfsboot.com")); break;
            default: File.WriteAllText(Path.Combine(media, "rogue.txt"), "added after the checkpoint"); break;
        }
        await AssertRefusedAsync(fixture, job, fragment);
    }

    [Fact]
    public async Task A_boot_image_that_matches_neither_the_original_nor_the_recorded_edit_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        Rewrite(job.WorkDirectory, saved => saved with
        {
            SetupState = SetupProgress.Replacing, EditedBootSha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3]))
        });
        Flip(Path.Combine(job.WorkDirectory, @"media\sources\boot.wim"), 300);
        await AssertRefusedAsync(fixture, job, "boot.wim");
    }

    [Fact]
    public async Task A_second_install_image_next_to_the_compressed_one_is_refused_so_no_double_package_is_built()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.FailAtIsoAsync(fixture);
        File.WriteAllBytes(Path.Combine(job.WorkDirectory, @"media\sources\install.wim"), FakeWim.Create());
        await AssertRefusedAsync(fixture, job, "unexpected file");
    }

    [Fact]
    public async Task A_changed_compressed_image_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.FailAtIsoAsync(fixture);
        Flip(Path.Combine(job.WorkDirectory, @"media\sources\install.esd"), 700);
        await AssertRefusedAsync(fixture, job, "changed after the checkpoint");
    }

    // ---- reparse points ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reparse_point_inside_the_staged_media_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var target = Path.Combine(fixture.Root, "outside.bin");
        File.WriteAllBytes(target, [1]);
        var link = Path.Combine(job.WorkDirectory, @"media\boot\etfsboot.com");
        File.Delete(link);
        File.CreateSymbolicLink(link, target);
        await AssertRefusedAsync(fixture, job, "reparse point");
    }

    [Fact]
    public async Task A_working_image_replaced_by_a_link_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var working = Path.Combine(job.WorkDirectory, "working.wim");
        var copy = Path.Combine(fixture.Root, "copy.wim");
        File.Move(working, copy);
        File.CreateSymbolicLink(working, copy);
        await AssertRefusedAsync(fixture, job, "reparse");
    }

    // ---- identity of the saved work and of the request ------------------------------------------------------------------

    [Fact]
    public async Task An_original_recipe_edited_after_the_checkpoint_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        File.AppendAllText(Path.Combine(job.WorkDirectory, "recipe.json"), " ");
        await AssertRefusedAsync(fixture, job, "original recipe changed");
    }

    [Theory]
    [InlineData("SelectedOptions")]
    [InlineData("EnableNetFx3")]
    [InlineData("PreferLocalAccount")]
    [InlineData("TurboMode")]
    [InlineData("Flavor")]
    [InlineData("SourcePath")]
    [InlineData("ImageIndex")]
    [InlineData("RemoveRedundantData")]
    [InlineData("OmitNetFx3SetupPayload")]
    [InlineData("CompactInstallation")]
    public async Task Frozen_recipe_fields_cannot_change_on_resume(string field)
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var original = JobScenarios.OriginalRecipe(job.WorkDirectory);
        var changed = field switch
        {
            "SelectedOptions" => original with { SelectedOptions = ["copilot-app"] },
            "EnableNetFx3" => original with { EnableNetFx3 = true },
            "PreferLocalAccount" => original with { PreferLocalAccount = true },
            "TurboMode" => original with { TurboMode = true },
            "Flavor" => original with { Flavor = BuildFlavor.Core, CoreAcknowledged = true },
            "SourcePath" => original with { SourcePath = Path.Combine(fixture.Root, "other-source") },
            "ImageIndex" => original with { ImageIndex = 2 },
            "RemoveRedundantData" => original with { RemoveRedundantData = true },
            "OmitNetFx3SetupPayload" => original with { OmitNetFx3SetupPayload = true },
            _ => original with { CompactInstallation = !original.CompactInstallation }
        };
        var refused = await AssertRefusedAsync(fixture, job, "frozen", changed);
        Assert.Contains(field, refused.Message);
    }

    [Fact]
    public async Task Reordering_the_selected_options_is_not_a_change()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture, fixture.Recipe with { SelectedOptions = ["copilot-app", "cleanup"] });
        var original = JobScenarios.OriginalRecipe(job.WorkDirectory);
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(job),
            original with { SelectedOptions = [.. Enumerable.Reverse(original.SelectedOptions)] }, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
    }

    [Fact]
    public async Task An_existing_or_invalid_output_is_refused_and_never_overwritten()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var existing = Path.Combine(fixture.Root, "existing.iso");
        File.WriteAllText(existing, "someone else's ISO");
        var recipe = JobScenarios.OriginalRecipe(job.WorkDirectory) with { OutputPath = existing };
        var marker = JobScenarios.Snapshot(job.WorkDirectory);
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Engine.ResumeAsync(JobScenarios.JobFile(job), recipe, null, CancellationToken.None));
        Assert.Contains("new .iso", refused.Message);
        Assert.Equal("someone else's ISO", File.ReadAllText(existing));
        Assert.Equal(marker, JobScenarios.Snapshot(job.WorkDirectory));
        var wrongExtension = JobScenarios.OriginalRecipe(job.WorkDirectory) with { OutputPath = Path.Combine(fixture.Root, "out.wim") };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Engine.ResumeAsync(JobScenarios.JobFile(job), wrongExtension, null, CancellationToken.None));
        var noIsoTool = JobScenarios.OriginalRecipe(job.WorkDirectory) with { OscdimgPath = "" };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Engine.ResumeAsync(JobScenarios.JobFile(job), noIsoTool, null, CancellationToken.None));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("-m"));
    }

    // ---- the checkpoint file -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_damaged_checkpoint_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        File.WriteAllText(Path.Combine(job.WorkDirectory, JobCheckpoint.FileName), "{ this is not a checkpoint");
        await AssertRefusedAsync(fixture, job, "damaged");
    }

    [Fact]
    public async Task A_checkpoint_of_another_schema_or_job_or_phase_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var saved = CheckpointStore.Read(job.WorkDirectory);
        CheckpointStore.WriteAtomic(job.WorkDirectory, saved with { SchemaVersion = 2 });
        await AssertRefusedAsync(fixture, job, "schema");
        CheckpointStore.WriteAtomic(job.WorkDirectory, saved with { JobId = new string('b', 32) });
        await AssertRefusedAsync(fixture, job, "different job");
        CheckpointStore.WriteAtomic(job.WorkDirectory, saved with { Phase = "published" });
        await AssertRefusedAsync(fixture, job, "unknown phase");
        CheckpointStore.WriteAtomic(job.WorkDirectory, saved with { Phase = ResumePhase.Compressed });
        await AssertRefusedAsync(fixture, job, "compressed installation image");
    }

    [Fact]
    public async Task A_manifest_that_names_a_path_outside_the_job_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        Rewrite(job.WorkDirectory, saved => saved with
        {
            StagedMedia = [.. saved.StagedMedia, new(@"..\..\escape.bin", 1, new string('a', 64))]
        });
        await AssertRefusedAsync(fixture, job, "outside the job");
    }

    [Fact]
    public async Task A_checkpoint_hash_that_no_longer_matches_a_file_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        Rewrite(job.WorkDirectory, saved => saved with
        {
            StagedMedia = [.. saved.StagedMedia.Select(file => file.RelativePath == "autounattend.xml" ? file with { Sha256 = new string('0', 64) } : file)]
        });
        await AssertRefusedAsync(fixture, job, "changed after the checkpoint");
    }

    [Fact]
    public async Task A_recipe_and_checkpoint_that_agree_but_sit_in_the_wrong_workspace_are_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var file = Path.Combine(job.WorkDirectory, "recipe.json");
        var moved = JobScenarios.OriginalRecipe(job.WorkDirectory) with { WorkspacePath = Path.Combine(fixture.Root, "another-workspace") };
        File.WriteAllText(file, JsonSerializer.Serialize(moved, JsonDefaults.Options));
        Rewrite(job.WorkDirectory, saved => saved with { RecipeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) });
        await AssertRefusedAsync(fixture, job, "workspace", moved);
    }

    [Fact]
    public async Task A_Setup_state_that_contradicts_the_recipe_is_refused_so_the_bypass_cannot_be_silently_dropped()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        Rewrite(job.WorkDirectory, saved => saved with { SetupState = SetupProgress.NotRequired });
        await AssertRefusedAsync(fixture, job, "does not match");
    }

    // ---- state of the job -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_job_locked_by_a_running_build_or_another_app_instance_is_refused()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        using var hold = new FileStream(Path.Combine(job.WorkDirectory, "job.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var calls = fixture.Runner.Calls.Count;
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(
            JobScenarios.JobFile(job), JobScenarios.OriginalRecipe(job.WorkDirectory), null, CancellationToken.None));
        Assert.Contains("in use", refused.Message);
        Assert.Equal(calls, fixture.Runner.Calls.Count);
    }

    [Fact]
    public async Task A_job_from_before_checkpoints_is_never_adopted_and_is_reported_as_unavailable_for_resume()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        File.Delete(Path.Combine(job.WorkDirectory, JobCheckpoint.FileName));
        var snapshot = JobScenarios.Snapshot(job.WorkDirectory);
        await AssertRefusedAsync(fixture, job, "not available for resume");
        Assert.False(JobCheckpoint.Exists(job.WorkDirectory));          // no checkpoint is ever synthesised
        Assert.Equal(snapshot, JobScenarios.Snapshot(job.WorkDirectory));
        // Recovery of such a job stays an explicit, existing operation; nothing deletes it on its own.
        Assert.True(File.Exists(Path.Combine(job.WorkDirectory, "working.wim")));
    }

    [Fact]
    public async Task Completed_and_recovered_jobs_cannot_be_resumed()
    {
        using var fixture = new BuildFixture();
        var done = await fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(
            Path.Combine(done.WorkDirectory, "job.json"), JobScenarios.OriginalRecipe(done.WorkDirectory) with
            { OutputPath = Path.Combine(fixture.Root, "again.iso") }, null, CancellationToken.None));
        Assert.Contains("already completed", refused.Message);
        var job = await JobScenarios.InterruptAtCommitAsync(fixture, fixture.Recipe with { OutputPath = Path.Combine(fixture.Root, "second.iso") });
        await fixture.Engine.RecoverAsync(JobScenarios.JobFile(job), fixture.Recipe.DismPath, CancellationToken.None, discardCommitted: true);
        await AssertRefusedAsync(fixture, job, "recovered", JobScenarios.OriginalRecipe(job.WorkDirectory));
    }

    [Fact]
    public async Task Cancelling_while_the_saved_work_is_verified_changes_nothing()
    {
        using var fixture = new BuildFixture();
        var job = await JobScenarios.InterruptAtCommitAsync(fixture);
        var snapshot = JobScenarios.Snapshot(job.WorkDirectory);
        var marker = File.ReadAllBytes(Path.Combine(job.WorkDirectory, "job.json"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Engine.ResumeAsync(
            JobScenarios.JobFile(job), JobScenarios.OriginalRecipe(job.WorkDirectory), null, cancelled.Token));
        Assert.Equal(snapshot, JobScenarios.Snapshot(job.WorkDirectory));
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(job.WorkDirectory, "job.json")));
        Assert.True(JobCheckpoint.Exists(job.WorkDirectory));
    }

    // ---- an incomplete commit never becomes resumable ---------------------------------------------------------------------

    [Fact]
    public async Task A_commit_that_left_an_incomplete_image_never_produces_a_checkpoint()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Unmount-Image") || !args.Contains("/Commit") || !args.Contains("/CheckIntegrity")) return;
            var image = Path.Combine(fixture.Recipe.WorkspacePath, Directory.GetDirectories(fixture.Recipe.WorkspacePath).Single(), "working.wim");
            var bytes = File.ReadAllBytes(image);
            bytes[16] |= 0x40;                      // the commit stopped mid-write
            File.WriteAllBytes(image, bytes);
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        Assert.Contains("mid-write", failure.Message);
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));
        fixture.Runner.OnCommand = null;
        await AssertRefusedAsync(fixture, failure, "not available for resume");
    }

    [Fact]
    public async Task A_commit_that_failed_leaves_no_checkpoint_and_discards_its_mount()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailCommit = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        Assert.Contains("DISM failed", failure.Message);
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Unmount-Image") && call.Contains("/Discard"));
        fixture.Runner.FailCommit = false;
        await AssertRefusedAsync(fixture, failure, "not available for resume");
    }
}
