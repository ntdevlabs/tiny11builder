using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

internal static class JobScenarios
{
    /// <summary>Cancels while the installation image commits, so the next stage boundary observes the cancellation.</summary>
    public static async Task<BuildFailureException> InterruptAtCommitAsync(BuildFixture fixture, BuildRecipe? recipe = null,
        IProgress<BuildProgress>? progress = null)
    {
        using var cancel = new CancellationTokenSource();
        var committed = false;
        fixture.Runner.OnCommand = args =>
        {
            if (committed || !args.Contains("/Unmount-Image") || !args.Contains("/Commit")) return;
            committed = true;
            cancel.Cancel();
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(recipe ?? fixture.Recipe, progress, cancel.Token));
        fixture.Runner.OnCommand = null;
        return failure;
    }

    public static async Task<BuildFailureException> FailAtIsoAsync(BuildFixture fixture, BuildRecipe? recipe = null)
    {
        fixture.Runner.FailIso = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(recipe ?? fixture.Recipe, null, CancellationToken.None));
        fixture.Runner.FailIso = false;
        return failure;
    }

    public static string JobFile(BuildFailureException failure) => Path.Combine(failure.WorkDirectory, "job.json");

    /// <summary>The UI's contract: the job's original recipe, with only tools/output replaced by freshly validated values.</summary>
    public static BuildRecipe OriginalRecipe(string jobDirectory) =>
        BuildRecipe.Read(Path.Combine(jobDirectory, JobCheckpoint.RecipeFileName));

    public static bool Mutating(IReadOnlyList<string> call) =>
        call.Any(arg => arg is "/Export-Image" or "/Mount-Image" or "/Unmount-Image" or "/Remove-ProvisionedAppxPackage" or
            "/Remove-Capability" or "/Remove-Package" or "/Disable-Feature" or "/Enable-Feature" or "/Cleanup-Image" or "-m");

    /// <summary>Content hash of every entry except the append-only log, so "nothing was changed" is a positive statement.</summary>
    public static SortedDictionary<string, string> Snapshot(string directory)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path);
            if (relative.Equals("build.log", StringComparison.OrdinalIgnoreCase)) continue;
            entries[relative] = Directory.Exists(path) ? "<directory>" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return entries;
    }

    public static JobState ReadState(string jobDirectory) =>
        JsonSerializer.Deserialize<JobState>(File.ReadAllText(Path.Combine(jobDirectory, "job.json")), JsonDefaults.Options)!;

    public static Dictionary<string, string?> StageStates(string jobDirectory)
    {
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(jobDirectory, "timings.json")));
        return timings.RootElement.GetProperty("Stages").EnumerateArray()
            .ToDictionary(stage => stage.GetProperty("Stage").GetString()!, stage => stage.GetProperty("State").GetString());
    }
}

[SupportedOSPlatform("windows")]
public sealed class ResumeTests
{
    private static BuildRecipe Bypass(BuildFixture fixture) => fixture.Recipe with { SelectedOptions = ["hardware-bypass", "copilot-app"] };

    // ---- the checkpoint itself --------------------------------------------------------------------------------------

    [Fact]
    public async Task Commit_writes_a_checkpoint_with_exact_hashes_the_staged_media_manifest_efi_path_and_report_inputs()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        var directory = failure.WorkDirectory;
        var checkpoint = CheckpointStore.Read(directory);
        Assert.Equal(ResumePhase.Committed, checkpoint.Phase);
        Assert.Equal(SetupProgress.Pending, checkpoint.SetupState);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "recipe.json")))), checkpoint.RecipeSha256);
        var working = File.ReadAllBytes(Path.Combine(directory, "working.wim"));
        Assert.Equal(working.Length, checkpoint.WorkingImage.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(working)), checkpoint.WorkingImage.Sha256);
        Assert.Equal(@"efi\microsoft\boot\efisys.bin", checkpoint.EfiBootImage);
        var manifest = checkpoint.StagedMedia.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { @"sources\boot.wim", "autounattend.xml", checkpoint.EfiBootImage, @"boot\etfsboot.com" })
        {
            Assert.True(manifest.ContainsKey(required), required);
            var bytes = File.ReadAllBytes(Path.Combine(directory, "media", required));
            Assert.Equal(bytes.Length, manifest[required].Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), manifest[required].Sha256);
        }
        Assert.Equal(fixture.Recipe.ImageIndex, checkpoint.Source.ImageIndex);
        Assert.Equal("install.wim", checkpoint.Source.InstallFile);
        Assert.Equal(29689, checkpoint.Image.Version.Build);
        Assert.False(checkpoint.Report.UsedOptimizedMount);
        Assert.Equal(checkpoint.StagedMedia.Count, checkpoint.StagedMedia.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var commit = Assert.Single(checkpoint.Operations, item => item.Id == "stage:Commit");
        Assert.Equal(BuildOperationState.Completed, commit.State);
        Assert.Contains(checkpoint.Operations, item => item.Id.StartsWith("app:", StringComparison.Ordinal) && item.State == BuildOperationState.Completed);
        Assert.True(checkpoint.ActiveMilliseconds > 0);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void Checkpoint_files_are_replaced_atomically_and_unreadable_ones_are_refused()
    {
        using var fixture = new BuildFixture();
        var directory = Path.Combine(fixture.Root, "tiny11-" + new string('a', 32));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, JobCheckpoint.FileName), "{ not json");
        Assert.Throws<InvalidDataException>(() => CheckpointStore.Read(directory));
        File.WriteAllText(Path.Combine(directory, JobCheckpoint.FileName), "{}");
        Assert.Throws<InvalidDataException>(() => CheckpointStore.Read(directory));
        File.Delete(Path.Combine(directory, JobCheckpoint.FileName));
        Assert.Throws<InvalidDataException>(() => CheckpointStore.Read(directory));
        Assert.False(JobCheckpoint.Exists(directory));
    }

    // ---- cancellation and the honest stage state ----------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_after_a_finished_commit_keeps_Commit_completed_and_leaves_a_resumable_job()
    {
        using var fixture = new BuildFixture();
        var events = new List<BuildProgress>();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture), new ImmediateProgress<BuildProgress>(events.Add));
        Assert.True(failure.IsCancellation);
        Assert.False(failure.RecoveryRequired);
        Assert.Contains("cancelled", failure.Message);
        Assert.Contains("Resume", failure.Message);
        Assert.Equal(BuildOperationState.Completed, events.Last(item => item.OperationId == "stage:Commit").State);
        Assert.DoesNotContain(events, item => item.OperationId == "stage:Setup image" &&
            item.State is BuildOperationState.Running or BuildOperationState.Cancelled or BuildOperationState.Failed);
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.Equal("Completed", stages["Commit"]);
        Assert.Equal("Pending", stages["Setup image"]);
        Assert.Equal("cancelled", JobScenarios.ReadState(failure.WorkDirectory).Status);
        Assert.True(JobCheckpoint.Exists(failure.WorkDirectory));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "working.wim")));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public async Task Cancelling_between_stages_never_marks_the_finished_stage_cancelled()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Export-Image")) cancel.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, cancel.Token));
        Assert.True(failure.IsCancellation);
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.Equal("Completed", stages["Export edition"]);
        Assert.Equal("Pending", stages["Mount"]);
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory)); // nothing committed yet: there is nothing to resume
    }

    [Fact]
    public async Task Cancellation_inside_a_running_stage_is_still_reported_as_cancelled()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Get-ProvisionedAppxPackages")) cancel.Cancel(); };
        var events = new List<BuildProgress>();
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(
            fixture.Recipe, new ImmediateProgress<BuildProgress>(events.Add), cancel.Token));
        Assert.True(failure.IsCancellation);
        Assert.Contains(events, item => item.State == BuildOperationState.Cancelled);
        Assert.Equal("Cancelled", JobScenarios.StageStates(failure.WorkDirectory)["Remove apps"]);
    }

    [Fact]
    public void IsCancellation_follows_the_primary_cause_even_inside_an_aggregate()
    {
        var cancelled = new OperationCanceledException();
        var secondary = new IOException("disk full");
        Assert.True(new BuildFailureException("m", "d", new AggregateException(cancelled, secondary)).IsCancellation);
        Assert.False(new BuildFailureException("m", "d", new AggregateException(secondary, cancelled)).IsCancellation);
        Assert.True(new BuildFailureException("m", "d", cancelled).IsCancellation);
        Assert.False(new BuildFailureException("m", "d", secondary).IsCancellation);
        Assert.True(new BuildFailureException("m", "d", secondary, isCancellation: true).IsCancellation);
    }

    // ---- resume from the committed image ------------------------------------------------------------------------------

    [Fact]
    public async Task Resuming_after_commit_runs_only_the_tail_and_repeats_no_mount_removal_or_source_export()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        var before = fixture.Runner.Calls.Count;
        var events = new List<BuildProgress>();
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory),
            new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);
        var calls = fixture.Runner.Calls.Skip(before).ToArray();
        Assert.True(File.Exists(fixture.Recipe.OutputPath));
        Assert.Equal(64, result.Sha256.Length);
        Assert.Empty(result.Warnings);
        // the only mount is the private copy of the Setup image; nothing of the installation image is touched
        var mounts = calls.Where(call => call.Contains("/Mount-Image")).ToArray();
        var mount = Assert.Single(mounts);
        Assert.Contains(mount, arg => arg.StartsWith("/ImageFile:", StringComparison.Ordinal) &&
            arg.EndsWith(@"setup\boot.wim", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(calls, call => call.Any(arg => arg is "/Remove-ProvisionedAppxPackage" or "/Remove-Capability" or
            "/Disable-Feature" or "/Get-ProvisionedAppxPackages" or "/Get-Features" or "/Get-Capabilities" or "/Cleanup-Image"));
        Assert.DoesNotContain(calls, call => call.Contains("/Export-Image") && call.Contains("/SourceIndex:3"));
        Assert.Contains(calls, call => call.Contains("/Export-Image") && call.Contains("/Compress:recovery") && call.Contains("/CheckIntegrity"));
        Assert.Single(calls, call => call.Contains("-m"));
        // progress covers only the tail, starting with the verification stage
        var stagesSeen = events.Where(item => item.OperationId?.StartsWith("stage:", StringComparison.Ordinal) == true &&
            item.State == BuildOperationState.Running).Select(item => item.OperationId!).ToArray();
        Assert.Equal(["stage:" + BuildProgressText.ResumeCheckStage, "stage:Setup image", "stage:Compress", "stage:Create ISO", "stage:Verify output"], stagesSeen);
        // the original job: completed, report says resumed, history and timings merged, checkpoint retired
        Assert.Equal("completed", JobScenarios.ReadState(failure.WorkDirectory).Status);
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.True(report.RootElement.GetProperty("Resumed").GetBoolean());
        Assert.Equal("committed", report.RootElement.GetProperty("ResumedFrom").GetString());
        using var operations = JsonDocument.Parse(File.ReadAllText(Path.Combine(failure.WorkDirectory, "operations.json")));
        var ids = operations.RootElement.EnumerateArray().ToDictionary(item => item.GetProperty("Id").GetString()!, item => item.GetProperty("State").GetString());
        Assert.Equal("Completed", ids["stage:Mount"]);
        Assert.Equal("Completed", ids["stage:Commit"]);
        Assert.Equal("Completed", ids["stage:Verify output"]);
        Assert.Contains(ids, pair => pair.Key.StartsWith("app:", StringComparison.Ordinal) && pair.Value == "Completed");
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.All(stages.Values, state => Assert.Equal("Completed", state));
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(failure.WorkDirectory, "timings.json")));
        Assert.True(timings.RootElement.GetProperty("Resumed").GetBoolean());
        Assert.False(File.Exists(Path.Combine(failure.WorkDirectory, "working.wim")));
        Assert.False(Directory.Exists(Path.Combine(failure.WorkDirectory, "media")));
        Assert.True(File.Exists(Path.Combine(fixture.Source, @"sources\install.wim")));
    }

    [Fact]
    public async Task Resume_does_not_need_the_original_source_media()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        Directory.Delete(fixture.Source, recursive: true);
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory),
            null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), call => call.Any(arg => arg.Contains(@"\source\", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Resume_may_change_only_the_output_tools_final_compression_and_keep_flag()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var newOutput = Path.Combine(fixture.Root, "elsewhere", "resumed.iso");
        var recipe = JobScenarios.OriginalRecipe(failure.WorkDirectory) with
        {
            OutputPath = newOutput, DismPath = @"C:\mock2\dism.exe", OscdimgPath = @"C:\mock2\oscdimg.exe",
            Compression = Compression.Maximum, KeepWorkingFiles = true
        };
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None);
        Assert.Equal(newOutput, result.IsoPath);
        Assert.True(File.Exists(newOutput));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        var calls = fixture.Runner.Calls.Skip(before).ToArray();
        Assert.Contains(calls, call => call.Contains("/Export-Image") && call.Contains("/Compress:max") &&
            call.Any(arg => arg.EndsWith(@"compress\install.wim", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "working.wim")));      // keep flag honoured
        Assert.True(Directory.Exists(Path.Combine(failure.WorkDirectory, "media")));
        Assert.Contains(@"C:\mock2\dism.exe", File.ReadAllText(Path.Combine(failure.WorkDirectory, "build.log")));
        // the original recipe is never rewritten
        Assert.Equal(fixture.Recipe.OutputPath, JobScenarios.OriginalRecipe(failure.WorkDirectory).OutputPath);
    }

    [Fact]
    public async Task Resuming_a_job_without_hardware_bypass_never_mounts_anything()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var before = fixture.Runner.Calls.Count;
        await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), call => call.Contains("/Mount-Image") || call.Contains("/Unmount-Image"));
    }

    [Fact]
    public async Task Cancelling_during_the_slowest_export_keeps_its_finished_result_and_checkpoints_it()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Export-Image") && args.Contains("/Compress:recovery")) cancel.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, cancel.Token));
        fixture.Runner.OnCommand = null;
        Assert.True(failure.IsCancellation);
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.Equal("Completed", stages["Compress"]);
        Assert.Equal("Pending", stages["Create ISO"]);
        var checkpoint = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(ResumePhase.Compressed, checkpoint.Phase);
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, @"media\sources\install.esd")));
        var before = fixture.Runner.Calls.Count;
        await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), call => call.Contains("/Export-Image"));
    }

    [Fact]
    public async Task Cancelling_during_the_setup_image_commit_keeps_the_finished_edit_and_records_it()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Contains("/Unmount-Image") && args.Contains("/Commit") && !args.Contains("/CheckIntegrity")) cancel.Cancel();
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(Bypass(fixture), null, cancel.Token));
        fixture.Runner.OnCommand = null;
        Assert.True(failure.IsCancellation);
        Assert.Equal("Completed", JobScenarios.StageStates(failure.WorkDirectory)["Setup image"]);
        var checkpoint = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(ResumePhase.Committed, checkpoint.Phase);
        Assert.Equal(SetupProgress.Applied, checkpoint.SetupState);
        var before = fixture.Runner.Calls.Count;
        await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), call => call.Contains("/Mount-Image"));
    }

    [Fact]
    public async Task A_job_left_marked_running_by_a_crash_can_be_resumed_even_without_its_scratch_and_mount_folders()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var directory = failure.WorkDirectory;
        var marker = JobScenarios.ReadState(directory) with { Status = "running", Stage = "Setup image" };   // the process died
        File.WriteAllText(Path.Combine(directory, "job.json"), JsonSerializer.Serialize(marker, JsonDefaults.Options));
        Directory.Delete(Path.Combine(directory, "scratch"), recursive: true);
        Directory.Delete(Path.Combine(directory, "mount"), recursive: true);
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(directory), null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.Equal("completed", JobScenarios.ReadState(directory).Status);
    }

    [Fact]
    public async Task A_failed_resume_can_be_resumed_again_and_keeps_the_checkpoint_chain_and_history()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        var recipe = JobScenarios.OriginalRecipe(failure.WorkDirectory);
        fixture.Runner.FailIso = true;
        var second = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None));
        Assert.Equal(failure.WorkDirectory, second.WorkDirectory);
        Assert.Equal(ResumePhase.Compressed, CheckpointStore.Read(failure.WorkDirectory).Phase);       // the resumed run advanced it
        fixture.Runner.FailIso = false;
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None);
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), call => call.Contains("/Export-Image") || call.Contains("/Mount-Image"));
        using var operations = JsonDocument.Parse(File.ReadAllText(Path.Combine(failure.WorkDirectory, "operations.json")));
        var ids = operations.RootElement.EnumerateArray().Select(item => item.GetProperty("Id").GetString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());                                               // nothing planned twice
        Assert.Contains("stage:Remove apps", ids);                                                       // the first run's history survived both resumes
        Assert.True(File.Exists(result.IsoPath));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(failure.WorkDirectory, "build.log")).Count(line => line.Contains("=== Resume requested")));
    }

    [Fact]
    public async Task Resume_reports_the_total_active_time_of_both_runs()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var saved = TimeSpan.FromMilliseconds(CheckpointStore.Read(failure.WorkDirectory).ActiveMilliseconds);
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        Assert.True(result.Duration > saved);
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(failure.WorkDirectory, "timings.json")));
        Assert.True(timings.RootElement.GetProperty("TotalMilliseconds").GetDouble() >= saved.TotalMilliseconds);
    }

    // ---- resume from the compressed image --------------------------------------------------------------------------

    [Fact]
    public async Task Resuming_after_compression_only_rebuilds_and_publishes_the_iso()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.FailAtIsoAsync(fixture, Bypass(fixture));
        var checkpoint = CheckpointStore.Read(failure.WorkDirectory);
        Assert.Equal(ResumePhase.Compressed, checkpoint.Phase);
        Assert.Equal(SetupProgress.Applied, checkpoint.SetupState);
        Assert.Equal(Compression.Recovery, checkpoint.CompressedWith);
        Assert.False(File.Exists(Path.Combine(failure.WorkDirectory, "working.wim"))); // released only after the verified checkpoint
        Assert.Equal(@"sources\install.esd", checkpoint.CompressedImage!.RelativePath);
        var bytes = File.ReadAllBytes(Path.Combine(failure.WorkDirectory, "media", @"sources\install.esd"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), checkpoint.CompressedImage.Sha256);
        var boot = checkpoint.StagedMedia.Single(file => file.RelativePath == @"sources\boot.wim");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(failure.WorkDirectory, "media", @"sources\boot.wim")))), boot.Sha256);
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        var calls = fixture.Runner.Calls.Skip(before).ToArray();
        Assert.DoesNotContain(calls, call => call.Contains("/Export-Image") || call.Contains("/Mount-Image") || call.Contains("/Unmount-Image"));
        Assert.Single(calls, call => call.Contains("-m"));
        Assert.True(File.Exists(result.IsoPath));
        Assert.Equal("completed", JobScenarios.ReadState(failure.WorkDirectory).Status);
    }

    [Fact]
    public async Task A_compressed_job_cannot_be_resumed_with_a_different_final_compression()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.FailAtIsoAsync(fixture);
        var recipe = JobScenarios.OriginalRecipe(failure.WorkDirectory) with { Compression = Compression.Maximum };
        var before = JobScenarios.Snapshot(failure.WorkDirectory);
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None));
        Assert.Contains("already compressed", refused.Message);
        Assert.Equal(before, JobScenarios.Snapshot(failure.WorkDirectory));
    }

    [Fact]
    public async Task Resume_never_leaves_a_stale_second_install_image_or_reuses_partial_exports()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var sources = Path.Combine(failure.WorkDirectory, "media", "sources");
        File.WriteAllText(Path.Combine(sources, "install.wim"), "stale wim from an interrupted export");
        File.WriteAllText(Path.Combine(sources, "install.esd"), "stale esd from an interrupted export");
        Directory.CreateDirectory(Path.Combine(failure.WorkDirectory, "compress"));
        File.WriteAllText(Path.Combine(failure.WorkDirectory, "compress", "install.esd"), "stale partial");
        string[] packaged = [];
        byte[] packagedBytes = [];
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("-m")) return;
            packaged = Directory.GetFiles(sources, "install.*").Select(Path.GetFileName).OfType<string>().Order().ToArray();
            packagedBytes = File.ReadAllBytes(Path.Combine(sources, "install.esd"));
        };
        await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        Assert.Equal(["install.esd"], packaged);
        Assert.Equal("MSWIM\0\0\0", System.Text.Encoding.ASCII.GetString(packagedBytes, 0, 8)); // DISM's fresh output, not the stale text
        var export = fixture.Runner.Calls.Last(call => call.Contains("/Export-Image"));
        Assert.Contains(export, arg => arg.StartsWith("/DestinationImageFile:", StringComparison.Ordinal) &&
            arg.EndsWith(@"compress\install.esd", StringComparison.Ordinal));
    }

    // ---- the Setup image is edited on a private copy ------------------------------------------------------------------

    [Fact]
    public async Task The_setup_image_is_edited_on_a_private_copy_and_replaced_atomically_after_validation()
    {
        using var fixture = new BuildFixture();
        string? mountedImage = null;
        var stagedBefore = Array.Empty<byte>();
        fixture.Runner.OnCommand = args =>
        {
            var image = args.FirstOrDefault(arg => arg.StartsWith("/ImageFile:", StringComparison.Ordinal));
            if (!args.Contains("/Mount-Image") || image is null || !image.EndsWith("boot.wim", StringComparison.Ordinal)) return;
            mountedImage = image["/ImageFile:".Length..];
            stagedBefore = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(mountedImage))!, @"media\sources\boot.wim"));
        };
        var failure = await JobScenarios.FailAtIsoAsync(fixture, Bypass(fixture));
        Assert.NotNull(mountedImage);
        Assert.EndsWith(@"setup\boot.wim", mountedImage);
        Assert.False(Directory.Exists(Path.Combine(failure.WorkDirectory, "setup")));      // temporary copy removed after the replace
        Assert.Equal(stagedBefore, File.ReadAllBytes(Path.Combine(failure.WorkDirectory, @"media\sources\boot.wim"))); // fake commit keeps the bytes
        Assert.True(fixture.Registry.SetupCalled);
        Assert.Equal(SetupProgress.Applied, CheckpointStore.Read(failure.WorkDirectory).SetupState);
    }

    [Fact]
    public async Task A_failed_setup_commit_leaves_the_staged_boot_image_byte_identical_and_the_mount_discarded()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Contains("/Unmount-Image") && args.Contains("/Commit") && args.Contains("/CheckIntegrity")) fixture.Runner.FailCommit = true;
        };
        var original = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\boot.wim"));
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(Bypass(fixture), null, CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(failure.WorkDirectory, @"media\sources\boot.wim")));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Unmount-Image") && call.Contains("/Discard"));
        Assert.Equal(SetupProgress.Pending, CheckpointStore.Read(failure.WorkDirectory).SetupState);
        fixture.Runner.FailCommit = false;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(failure.WorkDirectory), null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
    }

    [Fact]
    public async Task If_the_replacement_cannot_be_recorded_durably_the_staged_boot_image_is_left_untouched()
    {
        using var fixture = new BuildFixture();
        var original = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\boot.wim"));
        fixture.Runner.OnCommand = args =>
        {
            var image = args.FirstOrDefault(arg => arg.StartsWith("/ImageFile:", StringComparison.Ordinal));
            if (!args.Contains("/Mount-Image") || image is null || !image.EndsWith(@"setup\boot.wim", StringComparison.Ordinal)) return;
            var job = Path.GetDirectoryName(Path.GetDirectoryName(image["/ImageFile:".Length..]))!;
            File.Delete(Path.Combine(job, JobCheckpoint.FileName));
            Directory.CreateDirectory(Path.Combine(job, JobCheckpoint.FileName));   // a directory now squats on the checkpoint's name
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(Bypass(fixture), null, CancellationToken.None));
        Assert.Contains("could not record the Setup image replacement", failure.Message);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(failure.WorkDirectory, @"media\sources\boot.wim")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_crash_between_the_atomic_replace_and_the_checkpoint_update_is_resolved_by_hash(bool replaceAlreadyDone)
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        var directory = failure.WorkDirectory;
        var checkpoint = CheckpointStore.Read(directory);
        var staged = Path.Combine(directory, @"media\sources\boot.wim");
        var edited = FakeWim.Create(images: 2, seed: 99);
        var editedSha = Convert.ToHexString(SHA256.HashData(edited));
        CheckpointStore.WriteAtomic(directory, checkpoint with { SetupState = SetupProgress.Replacing, EditedBootSha256 = editedSha });
        if (replaceAlreadyDone) File.WriteAllBytes(staged, edited);
        var before = fixture.Runner.Calls.Count;
        await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(directory), null, CancellationToken.None);
        var mounts = fixture.Runner.Calls.Skip(before).Count(call => call.Contains("/Mount-Image"));
        Assert.Equal(replaceAlreadyDone ? 0 : 1, mounts); // already replaced: never edited twice; not replaced yet: edited once
    }

    // ---- a stale private Setup mount is cleaned; any other mount is refused ------------------------------------------

    [Fact]
    public async Task A_leftover_private_setup_mount_is_discarded_before_resuming_but_the_committed_image_is_never_touched()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture, Bypass(fixture));
        var directory = failure.WorkDirectory;
        fixture.Runner.StaleMount = (Path.Combine(directory, "mount"), Path.Combine(directory, @"setup\boot.wim"));
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(directory), null, CancellationToken.None);
        var calls = fixture.Runner.Calls.Skip(before).ToArray();
        var discard = calls.First(call => call.Contains("/Unmount-Image"));
        Assert.Contains("/Discard", discard);
        Assert.DoesNotContain(calls, call => call.Any(arg => arg.StartsWith("/ImageFile:", StringComparison.Ordinal) &&
            arg.EndsWith("working.wim", StringComparison.Ordinal)));
        Assert.True(File.Exists(result.IsoPath));
    }

    [Fact]
    public async Task Resume_refuses_when_the_committed_image_or_any_other_job_image_is_mounted()
    {
        using var fixture = new BuildFixture();
        var failure = await JobScenarios.InterruptAtCommitAsync(fixture);
        var directory = failure.WorkDirectory;
        var snapshot = JobScenarios.Snapshot(directory);
        foreach (var (mountDirectory, image) in new[]
        {
            (Path.Combine(directory, "mount"), Path.Combine(directory, "working.wim")),
            (Path.Combine(directory, "mount"), Path.Combine(fixture.Root, "foreign.wim")),
            (Path.Combine(fixture.Root, "elsewhere"), Path.Combine(directory, "working.wim"))
        })
        {
            fixture.Runner.StaleMount = (mountDirectory, image);
            var calls = fixture.Runner.Calls.Count;
            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.ResumeAsync(
                JobScenarios.JobFile(failure), JobScenarios.OriginalRecipe(directory), null, CancellationToken.None));
            Assert.Contains("still mounted", refused.Message);
            Assert.DoesNotContain(fixture.Runner.Calls.Skip(calls), JobScenarios.Mutating);
            Assert.Equal(snapshot, JobScenarios.Snapshot(directory));
        }
        Assert.Equal("cancelled", JobScenarios.ReadState(directory).Status);
    }
}
