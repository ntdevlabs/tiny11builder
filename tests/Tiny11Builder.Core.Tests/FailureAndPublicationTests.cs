using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class FailureOrderingTests
{
    private static int Discards(BuildFixture fixture) => fixture.Runner.Calls.Count(call => call.Contains("/Discard"));

    private static IEnumerable<Exception> Flatten(Exception? error) => error switch
    {
        null => [],
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten),
        _ => [error]
    };

    [Fact]
    public async Task A_progress_observer_that_fails_while_a_failure_is_reported_never_replaces_that_failure()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        // Cancellation lands inside the app-removal item, so the item's own failure report reaches the faulty observer.
        fixture.Runner.OnCommand = args => { if (args.Contains("/Get-ProvisionedAppxPackages")) cancel.Cancel(); };
        var observer = new ImmediateProgress<BuildProgress>(item =>
        {
            if (item.State is BuildOperationState.Cancelled or BuildOperationState.Failed)
                throw new InvalidOperationException("progress observer faulted");
        });
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, observer, cancel.Token));
        Assert.True(failure.IsCancellation);
        var causes = Flatten(failure.InnerException).ToArray();
        Assert.IsAssignableFrom<OperationCanceledException>(causes[0]);
        Assert.Contains(causes, cause => cause is InvalidOperationException { Message: "progress observer faulted" });
        Assert.Equal(1, Discards(fixture));
        Assert.Equal("cancelled", JobScenarios.ReadState(failure.WorkDirectory).Status);
    }

    [Fact]
    public async Task The_owned_mount_is_discarded_before_a_failing_progress_observer_runs_and_both_failures_are_preserved()
    {
        using var fixture = new BuildFixture();
        // A failure outside any item: the stage itself is reported failed from the engine's failure handler.
        fixture.Runner.OnCommand = args =>
        {
            if (args.Contains("/Get-ProvisionedAppxPackages")) throw new IOException("the package listing failed");
        };
        var discardsWhenObserverFaulted = -1;
        var observer = new ImmediateProgress<BuildProgress>(item =>
        {
            if (item.State is not (BuildOperationState.Cancelled or BuildOperationState.Failed)) return;
            if (discardsWhenObserverFaulted < 0) discardsWhenObserverFaulted = Discards(fixture);
            throw new InvalidOperationException("progress observer faulted");
        });
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, observer, CancellationToken.None));
        Assert.Equal(1, discardsWhenObserverFaulted);      // recovery happened BEFORE the fallible diagnostic ran
        Assert.Equal(1, Discards(fixture));
        Assert.False(failure.IsCancellation);
        Assert.False(failure.RecoveryRequired);
        var causes = Flatten(failure.InnerException).ToArray();
        Assert.IsType<IOException>(causes[0]);                                                      // the primary cause comes first
        Assert.Contains("the package listing failed", failure.Message);
        Assert.Contains(causes, cause => cause is InvalidOperationException { Message: "progress observer faulted" });
        Assert.Contains("progress observer faulted", failure.Message);
        Assert.Equal("failed", JobScenarios.ReadState(failure.WorkDirectory).Status);
    }

    [Fact]
    public async Task The_original_failure_wins_when_the_log_and_history_files_cannot_be_written_either()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailPackages = true;
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Mount-Image")) return;
            var job = Path.GetDirectoryName(args.Single(arg => arg.StartsWith("/MountDir:", StringComparison.Ordinal))["/MountDir:".Length..])!;
            Directory.CreateDirectory(Path.Combine(job, "operations.json"));   // a directory squats on the history file
            Directory.CreateDirectory(Path.Combine(job, "timings.json"));
        };
        var action = new ExpertAction(ImageItemKind.Package, "Protected-Package~31bf~amd64~~1.0", ExpertActionMode.Remove);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(
            fixture.Recipe with { ExpertAcknowledged = true, ExpertActions = [action] }, null, CancellationToken.None));
        Assert.StartsWith("Build failed.", failure.Message);
        Assert.Contains("DISM failed (exit 50)", failure.Message);               // the original error, not a secondary one
        Assert.Contains("Additional problems while recording the failure", failure.Message);
        Assert.False(failure.IsCancellation);
        Assert.Equal(1, Discards(fixture));
        var causes = Flatten(failure.InnerException).ToArray();
        Assert.IsType<DismCommandException>(causes[0]);
        Assert.Contains(causes.Skip(1), cause => cause is UnauthorizedAccessException or IOException);
        Assert.Equal("failed", JobScenarios.ReadState(failure.WorkDirectory).Status);
    }

    [Fact]
    public async Task A_mount_that_cannot_be_discarded_demands_recovery_without_losing_the_primary_cause()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        fixture.Runner.FailDiscard = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, cancel.Token));
        Assert.True(failure.RecoveryRequired);
        Assert.True(failure.IsCancellation);                                          // the cause was still cancellation
        Assert.Contains("Recovery is required", failure.Message);
        Assert.Contains("never use global DISM cleanup", failure.Message);
        var causes = Flatten(failure.InnerException).ToArray();
        Assert.IsAssignableFrom<OperationCanceledException>(causes[0]);
        Assert.Contains(causes, cause => cause is DismCommandException { ExitCode: 1 });
        var state = JobScenarios.ReadState(failure.WorkDirectory);
        Assert.Equal("failed", state.Status);
        Assert.Equal("Recovery required", state.Stage);
        Assert.Contains("DISCARD FAILED", File.ReadAllText(Path.Combine(failure.WorkDirectory, "build.log")));
    }

    [Fact]
    public async Task A_discard_failure_after_a_checkpoint_points_to_resume_not_to_destructive_recovery()
    {
        using var fixture = new BuildFixture();
        var committed = false;
        fixture.Runner.OnCommand = args =>
        {
            // After the installation image commits, break the Setup-image commit and make its discard fail too.
            if (args.Contains("/Unmount-Image") && args.Contains("/Commit") && args.Contains("/CheckIntegrity")) committed = true;
            else if (committed && args.Contains("/Mount-Image")) fixture.Runner.FailCommit = true;
        };
        fixture.Runner.FailDiscard = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(
            fixture.Recipe with { SelectedOptions = ["hardware-bypass"] }, null, CancellationToken.None));
        Assert.True(failure.RecoveryRequired);
        Assert.Contains("Resume tries to discard it again", failure.Message);
        Assert.DoesNotContain("choose Recover workspace", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(JobCheckpoint.Exists(failure.WorkDirectory));
    }

    [Fact]
    public async Task Inventory_reports_cancellation_after_discarding_its_mount_first()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.InventoryAsync(fixture.Recipe, null, cancel.Token));
        Assert.True(failure.IsCancellation);
        Assert.False(failure.RecoveryRequired);
        Assert.Equal(1, Discards(fixture));
        Assert.Equal("cancelled", JobScenarios.ReadState(failure.WorkDirectory).Status);
        Assert.StartsWith("Inventory cancelled.", failure.Message);
    }

    [Fact]
    public async Task Inventory_that_cannot_discard_demands_recovery()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        fixture.Runner.FailDiscard = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.InventoryAsync(fixture.Recipe, null, cancel.Token));
        Assert.True(failure.RecoveryRequired);
        Assert.True(failure.IsCancellation);
        Assert.Contains("Recovery is required", failure.Message);
        Assert.Equal("Recovery required", JobScenarios.ReadState(failure.WorkDirectory).Stage);
    }
}

[SupportedOSPlatform("windows")]
public sealed class PublicationTests
{
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string CreateVerifiedFile(string directory, string name = "verified.iso")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var bytes = BuildFixture.IsoBytes();
        bytes[40000] = 0x7E;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task An_output_race_preserves_the_verified_iso_names_it_and_leaves_the_build_resumable()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Count > 0 && args[^1].EndsWith("output.iso", StringComparison.Ordinal) && args.Contains("-m"))
                File.WriteAllText(fixture.Recipe.OutputPath, "racing writer");
        };
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        fixture.Runner.OnCommand = null;
        var preserved = Path.Combine(failure.WorkDirectory, "output.iso");
        Assert.Equal("racing writer", File.ReadAllText(fixture.Recipe.OutputPath));            // the foreign file is untouched
        Assert.True(File.Exists(preserved));
        Assert.Contains(preserved, failure.Message);
        Assert.Contains(Sha(preserved), failure.Message);
        Assert.False(failure.IsCancellation);
        Assert.False(File.Exists(Path.Combine(failure.WorkDirectory, "report.json")));          // no success report
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.partial"));
        Assert.Equal("failed", JobScenarios.ReadState(failure.WorkDirectory).Status);
        // the media is retained until publication, so the build can finish with a new output path
        Assert.Equal(ResumePhase.Compressed, CheckpointStore.Read(failure.WorkDirectory).Phase);
        var elsewhere = Path.Combine(fixture.Root, "published", "again.iso");
        var result = await fixture.Engine.ResumeAsync(Path.Combine(failure.WorkDirectory, "job.json"),
            JobScenarios.OriginalRecipe(failure.WorkDirectory) with { OutputPath = elsewhere }, null, CancellationToken.None);
        Assert.True(File.Exists(elsewhere));
        Assert.Equal(Sha(elsewhere), result.Sha256);
        Assert.Equal("racing writer", File.ReadAllText(fixture.Recipe.OutputPath));
    }

    [Theory]
    [InlineData("operations.json")]
    [InlineData("timings.json")]
    [InlineData("report.json")]
    public async Task A_failing_report_or_history_write_after_publication_is_a_warning_not_a_failed_build(string squatter)
    {
        using var fixture = new BuildFixture();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Count > 0 && args[^1].EndsWith("output.iso", StringComparison.Ordinal) && args.Contains("-m"))
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(args[^1])!, squatter));
        };
        var result = await fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
        Assert.True(File.Exists(fixture.Recipe.OutputPath));
        Assert.Equal(Sha(fixture.Recipe.OutputPath), result.Sha256);
        Assert.Contains(result.Warnings, warning => warning.Contains(squatter, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("completed", JobScenarios.ReadState(result.WorkDirectory).Status);
        Assert.Contains("POST-PUBLISH WARNING", File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log")));
        if (squatter != "report.json") Assert.True(File.Exists(result.ReportPath));
    }

    [Fact]
    public async Task A_cleanup_that_cannot_finish_after_publication_is_reported_as_warnings_and_never_fails_the_build()
    {
        using var fixture = new BuildFixture();
        FileStream? held = null;
        fixture.Runner.OnCommand = args =>
        {
            if (held is not null || args.Count == 0 || !args[^1].EndsWith("output.iso", StringComparison.Ordinal) || !args.Contains("-m")) return;
            held = new FileStream(Path.Combine(Path.GetDirectoryName(args[^1])!, @"media\boot\etfsboot.com"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        try
        {
            var result = await fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
            Assert.True(File.Exists(fixture.Recipe.OutputPath));
            Assert.Contains(result.Warnings, warning => warning.StartsWith("The media folder could not be removed", StringComparison.Ordinal));
            Assert.Equal("completed", JobScenarios.ReadState(result.WorkDirectory).Status);
            Assert.True(File.Exists(result.ReportPath));
            Assert.False(JobCheckpoint.Exists(result.WorkDirectory));
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task A_clean_build_publishes_with_no_warnings_and_removes_its_checkpoint_and_staging()
    {
        using var fixture = new BuildFixture();
        var result = await fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
        Assert.Empty(result.Warnings);
        Assert.False(JobCheckpoint.Exists(result.WorkDirectory));
        Assert.False(Directory.Exists(Path.Combine(result.WorkDirectory, "media")));
        Assert.False(File.Exists(Path.Combine(result.WorkDirectory, "working.wim")));
        Assert.False(File.Exists(Path.Combine(result.WorkDirectory, "output.iso")));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.False(report.RootElement.GetProperty("Resumed").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("Warnings").GetArrayLength());
    }

    [Fact]
    public async Task Keeping_working_files_keeps_the_image_and_media_but_retires_the_checkpoint()
    {
        using var fixture = new BuildFixture();
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with { KeepWorkingFiles = true }, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(result.WorkDirectory, "working.wim")));
        Assert.True(File.Exists(Path.Combine(result.WorkDirectory, @"media\sources\install.esd")));
        Assert.False(JobCheckpoint.Exists(result.WorkDirectory));
    }

    // ---- the publication primitive -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_copies_or_renames_without_ever_overwriting(bool forceCopy)
    {
        using var fixture = new BuildFixture();
        var source = CreateVerifiedFile(Path.Combine(fixture.Root, "job"));
        var sha = Sha(source);
        var output = Path.Combine(fixture.Root, "out", "published.iso");
        await BuildEngine.PublishFileAsync(source, output, "tag", sha, forceCopy, CancellationToken.None);
        Assert.Equal(sha, Sha(output));
        Assert.Equal(forceCopy, File.Exists(source));     // a rename consumes the source; a verified copy leaves it for the caller
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "out"), "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_never_replaces_an_existing_file_and_names_the_preserved_iso(bool forceCopy)
    {
        using var fixture = new BuildFixture();
        var source = CreateVerifiedFile(Path.Combine(fixture.Root, "job"));
        var output = Path.Combine(fixture.Root, "out", "taken.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "someone else's ISO");
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            BuildEngine.PublishFileAsync(source, output, "tag", Sha(source), forceCopy, CancellationToken.None));
        Assert.Contains(source, failure.Message);
        Assert.Contains("preserved", failure.Message);
        Assert.Equal("someone else's ISO", File.ReadAllText(output));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial"));
    }

    [Fact]
    public async Task A_copy_that_does_not_match_the_verified_hash_is_never_renamed_into_place()
    {
        using var fixture = new BuildFixture();
        var source = CreateVerifiedFile(Path.Combine(fixture.Root, "job"));
        var output = Path.Combine(fixture.Root, "out", "bad.iso");
        var failure = await Assert.ThrowsAsync<IOException>(() => BuildEngine.PublishFileAsync(
            source, output, "tag", new string('0', 64), forceCopy: true, CancellationToken.None));
        Assert.Contains("does not match the verified ISO", failure.InnerException!.Message);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial"));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_partial_that_this_run_did_not_create_is_never_deleted_or_overwritten()
    {
        using var fixture = new BuildFixture();
        var source = CreateVerifiedFile(Path.Combine(fixture.Root, "job"));
        var output = Path.Combine(fixture.Root, "out", "x.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var foreign = output + ".tag.partial";
        File.WriteAllText(foreign, "another process's partial");
        await Assert.ThrowsAsync<IOException>(() =>
            BuildEngine.PublishFileAsync(source, output, "tag", Sha(source), forceCopy: true, CancellationToken.None));
        Assert.Equal("another process's partial", File.ReadAllText(foreign));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Cancelling_a_copy_removes_the_partial_and_keeps_the_verified_iso()
    {
        using var fixture = new BuildFixture();
        var source = CreateVerifiedFile(Path.Combine(fixture.Root, "job"));
        var output = Path.Combine(fixture.Root, "out", "c.iso");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildEngine.PublishFileAsync(source, output, "tag", Sha(source), forceCopy: true, cancel.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial"));
        Assert.True(File.Exists(source));
    }
}
