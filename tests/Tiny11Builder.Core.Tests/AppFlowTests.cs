using System.ComponentModel;
using System.Runtime.Versioning;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

// Window-independent behaviour of the app: how operations are classified, what the user is shown,
// and how progress, logs and resume offers are paced and validated.
[SupportedOSPlatform("windows")]
public sealed class AppFlowTests
{
    [Fact]
    public async Task A_real_cancelled_build_is_a_cancellation_and_a_real_failed_build_is_not()
    {
        using var cancelled = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        cancelled.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        var stopped = await Assert.ThrowsAsync<BuildFailureException>(() => cancelled.Engine.BuildAsync(cancelled.Recipe, null, cancel.Token));
        var outcome = OperationOutcomes.Classify(OperationKind.Build, stopped);
        Assert.True(outcome.Cancelled);
        Assert.Equal("Build cancelled.", outcome.Title);
        Assert.Equal(NoticeLevel.Warning, outcome.Level);
        Assert.Equal(stopped.WorkDirectory, outcome.WorkDirectory);
        Assert.True(outcome.IsBuildRun);

        using var failed = new BuildFixture();
        failed.Runner.FailIso = true;
        var error = await Assert.ThrowsAsync<BuildFailureException>(() => failed.Engine.BuildAsync(failed.Recipe, null, CancellationToken.None));
        var failure = OperationOutcomes.Classify(OperationKind.Build, error, canResume: true);
        Assert.False(failure.Cancelled);
        Assert.Equal("Build failed.", failure.Title);
        Assert.Equal(NoticeLevel.Error, failure.Level);
        Assert.Contains("oscdimg did not produce an ISO", failure.Message);
        Assert.Contains("Saved progress can be resumed.", failure.Message);
        Assert.True(failure.Message.Length <= ErrorText.NoticeLimit + 80);
        Assert.Contains("   at ", failure.Details);   // the full diagnostics stay available
        Assert.Equal(error.WorkDirectory, failure.WorkDirectory);
    }

    [Theory]
    [InlineData(OperationKind.Recover, "Recovery cancelled.")]
    [InlineData(OperationKind.Download, "Download cancelled.")]
    [InlineData(OperationKind.Inspect, "Inspection cancelled.")]
    [InlineData(OperationKind.Inventory, "Component list cancelled.")]
    [InlineData(OperationKind.Resume, "Resume cancelled.")]
    [InlineData(OperationKind.Other, "Cancelled.")]
    public void Only_a_build_is_ever_called_a_build_cancellation(OperationKind kind, string title)
    {
        var outcome = OperationOutcomes.Classify(kind, new OperationCanceledException());
        Assert.True(outcome.Cancelled);
        Assert.Equal(title, outcome.Title);
        Assert.DoesNotContain("Build", outcome.Title);
        Assert.DoesNotContain("Build", outcome.Message);
    }

    [Theory]
    [InlineData(OperationKind.Recover, "Recovery did not complete.")]
    [InlineData(OperationKind.Download, "The download did not complete.")]
    [InlineData(OperationKind.Elevate, "The app could not restart as administrator.")]
    [InlineData(OperationKind.Resume, "Resume failed.")]
    [InlineData(OperationKind.Check, "The operation did not complete.")]
    public void Failures_are_named_for_what_failed(OperationKind kind, string title)
    {
        var outcome = OperationOutcomes.Classify(kind, new IOException("Disk is full."));
        Assert.False(outcome.Cancelled);
        Assert.Equal(title, outcome.Title);
        Assert.Contains("Disk is full.", outcome.Message);
    }

    [Fact]
    public void Declining_the_elevation_prompt_is_a_cancellation_only_for_the_restart_action()
    {
        var declined = new Win32Exception(1223, "The operation was canceled by the user.");
        Assert.True(OperationOutcomes.Classify(OperationKind.Elevate, declined).Cancelled);
        Assert.False(OperationOutcomes.Classify(OperationKind.Other, declined).Cancelled);
    }

    [Fact]
    public void A_cancelled_build_keeps_its_cancellation_identity_when_cleanup_also_fails()
    {
        var cleanup = new BuildFailureException("The build failed and its mount could not be discarded. Recovery is required.", @"C:\tiny11\job",
            new AggregateException(new OperationCanceledException(), new IOException("discard failed")), recoveryRequired: true);
        var outcome = OperationOutcomes.Classify(OperationKind.Build, cleanup);
        Assert.True(outcome.Cancelled);
        Assert.True(outcome.RecoveryRequired);
        Assert.Equal(NoticeLevel.Error, outcome.Level);
        Assert.Contains("Recover workspace", outcome.Message);

        var failed = new BuildFailureException("The build failed and its mount could not be discarded. Recovery is required.", @"C:\tiny11\job",
            new AggregateException(new IOException("DISM failed"), new OperationCanceledException()), recoveryRequired: true);
        var failure = OperationOutcomes.Classify(OperationKind.Build, failed);
        Assert.False(failure.Cancelled);
        Assert.True(failure.RecoveryRequired);
    }

    [Fact]
    public void Visible_reason_drops_wrappers_banners_and_stack_frames()
    {
        var dism = new DismCommandException(50, "Deployment Image Servicing and Management tool\nVersion: 10.0.26100.1\n\n" +
            "Image Version: 10.0.26300.9457\n\nError: 50\n\nThe request is not supported.\n\n" +
            "For more information, refer to the log file.\nThe DISM log file can be found at C:\\Windows\\Logs\\DISM\\dism.log");
        var wrapped = new BuildFailureException("Build failed. The source was not changed. Working files and logs are retained at C:\\tiny11\\job. " + dism.Message,
            @"C:\tiny11\job", dism);
        var reason = ErrorText.Reason(wrapped);
        Assert.Equal("DISM failed (exit 50). Error: 50 The request is not supported.", reason);

        var nested = new InvalidOperationException("outer", new IOException("inner cause")).ToString();
        Assert.Equal("outer", ErrorText.Summary(nested));
        Assert.Equal("Feature removal refused", ErrorText.Summary(new IOException("Feature removal refused").ToString()));
    }

    [Fact]
    public void Visible_reason_is_bounded_and_console_bars_never_appear()
    {
        var longText = "Failure: " + string.Join(" ", Enumerable.Repeat("detail", 2000));
        var summary = ErrorText.Summary(longText);
        Assert.True(summary.Length <= ErrorText.NoticeLimit);
        Assert.EndsWith("…", summary);
        Assert.Equal("Done", ErrorText.Summary("[=====59.0%====]\nDone"));
        Assert.Equal("", ErrorText.Summary("  \n "));
        Assert.Equal("No further details were reported.", ErrorText.Reason(new Exception("")));
        Assert.Equal("ab…", ErrorText.Clip("abcdefghij", 3));
        Assert.Equal("short", ErrorText.Clip("short", 10));
    }

    [Fact]
    public void The_live_log_is_bounded_and_never_rewritten_under_the_reader()
    {
        var log = new LiveLog(capacity: 3);
        foreach (var line in new[] { "one", "two", "three", "four" }) log.Add(line);
        Assert.Equal(3, log.Count);
        Assert.False(log.TryRender(visible: false, reading: false, force: false, out _));   // collapsed
        Assert.False(log.TryRender(visible: true, reading: true, force: false, out _));   // text selected
        Assert.True(log.Dirty);
        Assert.True(log.TryRender(visible: true, reading: false, force: false, out var text));
        Assert.Equal($"two{Environment.NewLine}three{Environment.NewLine}four", text);
        Assert.False(log.Dirty);
        Assert.False(log.TryRender(visible: true, reading: false, force: false, out _));   // nothing new
        log.Add("five");
        Assert.True(log.TryRender(visible: false, reading: true, force: true, out var forced));   // e.g. expanding it
        Assert.EndsWith("five", forced);
        log.Clear();
        Assert.True(log.TryRender(visible: true, reading: false, force: false, out var cleared));
        Assert.Equal("", cleared);
    }

    [Fact]
    public void Latest_progress_keeps_only_the_newest_value_and_never_drops_the_final_one()
    {
        var progress = new LatestProgress<DownloadProgress>();
        Assert.False(progress.TryTake(out _));
        for (var bytes = 1; bytes <= 1000; bytes++) progress.Report(new(bytes, 1000, 1));
        Assert.True(progress.TryTake(out var latest));
        Assert.Equal(1000, latest.Bytes);
        Assert.False(progress.TryTake(out _));   // a tick without news does nothing
        progress.Report(new(1000, 1000, 2));   // the final report arrives between ticks ...
        Assert.True(progress.TryTake(out var final));   // ... and is still delivered by the last explicit take
        Assert.Equal(2, final.BytesPerSecond);
    }

    [Fact]
    public async Task A_job_without_saved_progress_is_explained_and_never_adopted()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        var job = Path.Combine(failure.WorkDirectory, "job.json");
        var legacy = ResumeSupport.Inspect(job, _ => false);
        Assert.Null(legacy.Recipe);
        Assert.Equal(ResumeSupport.NoCheckpoint, legacy.Problem);
        Assert.Equal(failure.WorkDirectory, legacy.JobDirectory);
    }

    [Fact]
    public async Task Resume_reads_the_original_recipe_only_when_progress_is_advertised()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        var job = Path.Combine(failure.WorkDirectory, "job.json");
        var ready = ResumeSupport.Inspect(job, directory => directory == failure.WorkDirectory);
        Assert.Null(ready.Problem);
        Assert.Equal(fixture.Recipe.OutputPath, ready.Recipe!.OutputPath);
        Assert.Equal(fixture.Recipe.SelectedOptions, ready.Recipe.SelectedOptions);

        Assert.Contains("job.json", ResumeSupport.Inspect(Path.Combine(failure.WorkDirectory, "recipe.json"), _ => true).Problem);
        File.Delete(Path.Combine(failure.WorkDirectory, "recipe.json"));
        Assert.Contains("recipe is missing", ResumeSupport.Inspect(job, _ => true).Problem);
        File.WriteAllText(Path.Combine(failure.WorkDirectory, "recipe.json"), "{ not json");
        Assert.Contains("could not be read", ResumeSupport.Inspect(job, _ => true).Problem);
    }

    [Fact]
    public void A_path_that_cannot_be_read_as_a_job_file_is_reported_not_thrown()
    {
        var invalid = ResumeSupport.Inspect("C:\\tiny11\\bad\0name\\job.json", _ => true);
        Assert.Null(invalid.Recipe);
        Assert.Equal("That is not a valid job file path.", invalid.Problem);
    }

    [Fact]
    public void The_resume_prompt_lists_the_frozen_choices_and_the_original_warnings()
    {
        var recipe = new BuildRecipe
        {
            OutputPath = @"C:\tiny11\out.iso", Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core), Compression = Compression.Maximum
        };
        var text = ResumeSupport.Describe(recipe);
        Assert.Contains(@"Output: C:\tiny11\out.iso", text);
        Assert.Contains("Tiny11 Core", text);
        Assert.Contains("Maximum WIM", text);
        Assert.Contains("can't be changed", text);
        Assert.Contains("Core: no Windows Update", text);
        Assert.DoesNotContain("Individual removals include protected items", text);
        var custom = ResumeSupport.Describe(new BuildRecipe
        {
            Mode = BuildMode.Custom, ExpertAcknowledged = true, ExpertActions = [new(ImageItemKind.Service, "Driver", ExpertActionMode.Remove)]
        });
        Assert.Contains("Individual removals include protected items", custom);
        Assert.DoesNotContain("Core: no Windows Update", custom);
    }

    [Fact]
    public void The_legacy_installer_is_reported_from_what_Core_derives_and_is_never_a_choice()
    {
        var plain = new BuildRecipe();
        var core = new BuildRecipe { Flavor = BuildFlavor.Core, CoreAcknowledged = true };
        var payload = new BuildRecipe { ExpertActions = [new(ImageItemKind.ComponentPayload, "Windows.Cache", ExpertActionMode.Remove)] };
        Assert.Null(SetupLabels.Legacy(plain));
        Assert.Equal("Legacy installer: automatic (needed for Core removals)", SetupLabels.Legacy(core));
        Assert.Equal("Legacy installer: automatic (needed for component-payload removals)", SetupLabels.Legacy(payload));
        Assert.Equal("Legacy installer: automatic (needed for Core and component-payload removals)",
            SetupLabels.Legacy(core with { ExpertActions = payload.ExpertActions }));
        Assert.Null(SetupLabels.Legacy(new BuildRecipe { ExpertActions = [new(ImageItemKind.Service, "Driver", ExpertActionMode.Disable)] }));
        // The label only follows Core's own rule, so it cannot disagree with what the engine does.
        foreach (var recipe in new[] { plain, core, payload })
            Assert.Equal(SetupInstaller.RequiresLegacy(recipe), SetupLabels.Legacy(recipe) is not null);
        Assert.Contains("Legacy installer: automatic (needed for Core removals).", ResumeSupport.Describe(core));
        Assert.DoesNotContain("Legacy installer", ResumeSupport.Describe(plain));
    }

    // ---- cancelling an operation ---------------------------------------------------------------------------------

    private static CancellationFlow Begin(OperationRun run) => CancellationFlow.TryBegin(run)!;

    private static OperationEnd Classified(OperationKind kind, Exception error, bool canResume = false) =>
        OperationEnd.From(OperationOutcomes.Classify(kind, error, canResume));

    private static BuildFailureException CleanCancellation(string directory = @"C:\tiny11\job") =>
        new("Build cancelled.", directory, new OperationCanceledException());

    // Opens the prompt, optionally confirms it, lets the run end the given way and returns what the dialog then shows.
    private static CancelView Ended(OperationEnd end, bool confirm = true, OperationKind kind = OperationKind.Build)
    {
        var run = new OperationRun(kind);
        var flow = Begin(run);
        if (confirm) flow.Confirm();
        run.Record(end);
        run.Settle();
        flow.RunSettled();
        return flow.View;
    }

    [Fact]
    public void Asking_sends_nothing_and_only_the_confirm_button_sends_the_cancel()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        var asking = flow.View;
        Assert.Equal(CancelStage.Confirming, asking.Stage);
        Assert.Equal("Cancel this build?", asking.Title);
        Assert.Equal("Cancel build", asking.ConfirmText);   // the critical action ...
        Assert.Equal("Keep building", asking.CloseText);   // ... and the safe answer
        Assert.True(asking.CanClose);
        Assert.False(asking.ShowProgress);
        flow.Observe(new("Compress", "Compress the final image", 12, "stage:Compress"));
        Assert.False(run.Token.IsCancellationRequested);
        Assert.False(run.CancelRequested);

        Assert.True(flow.Confirm());
        Assert.True(run.Token.IsCancellationRequested);
        var stopping = flow.View;
        Assert.Equal(CancelStage.Cancelling, stopping.Stage);
        Assert.Equal("Cancelling the build", stopping.Title);
        Assert.Equal("Waiting for the current operation to finish", stopping.Status);
        Assert.Equal("Current operation: Compress the final image · 12%", stopping.Detail);   // seen while it was still asking
        Assert.True(stopping.ShowProgress);
        Assert.True(stopping.Indeterminate);   // a wait, not a percentage of the shutdown
        Assert.Null(stopping.ConfirmText);
        Assert.Null(stopping.CloseText);
        Assert.False(stopping.CanClose);
    }

    [Fact]
    public void Declining_keeps_the_operation_running_and_the_prompt_can_be_opened_again()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        flow.Decline();
        Assert.Equal(CancelStage.Closed, flow.Stage);
        Assert.False(flow.Confirm());   // a closed prompt sends nothing
        Assert.False(run.Token.IsCancellationRequested);
        Assert.False(run.CancelRequested);

        var again = Begin(run);
        Assert.True(again.Confirm());
        Assert.True(run.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_second_request_neither_sends_a_second_cancel_nor_opens_a_second_prompt()
    {
        var run = new OperationRun(OperationKind.Build);
        var sent = 0;
        using var registration = run.Token.Register(() => Interlocked.Increment(ref sent));
        var first = Begin(run);
        Assert.Null(CancellationFlow.TryBegin(run));   // one prompt per run
        Assert.True(first.Confirm());
        Assert.False(first.Confirm());   // pressed twice
        Assert.Equal(CancelRequest.AlreadyRequested, run.RequestCancel());
        first.Close();
        var second = Begin(run);
        Assert.False(second.Confirm());   // already asked: this prompt only reports it
        Assert.Equal(CancelStage.Cancelling, second.Stage);
        Assert.Equal(1, sent);
    }

    [Fact]
    public void A_run_that_ends_while_the_question_is_open_is_reported_and_nothing_is_cancelled()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        run.Record(OperationEnd.Succeeded(OperationKind.Build));
        run.Settle();
        flow.RunSettled();
        var view = flow.View;
        Assert.Equal(CancelStage.Ended, view.Stage);
        Assert.Equal("The build already finished", view.Title);
        Assert.Contains("finished before it could be cancelled", view.Body);
        Assert.Null(view.ConfirmText);   // nothing is left to cancel
        Assert.Equal("Done", view.CloseText);
        Assert.True(view.ShowProgress);
        Assert.False(view.Indeterminate);
        Assert.Equal(100, view.Percent);
        Assert.Equal(CancelTone.Done, view.Tone);

        var next = new OperationRun(OperationKind.Build);   // whatever runs next has a token source of its own
        Assert.False(flow.Confirm());
        Assert.Equal(CancelRequest.Settled, run.RequestCancel());
        Assert.False(run.Token.IsCancellationRequested);
        Assert.False(next.Token.IsCancellationRequested);
    }

    [Fact]
    public void Pressing_confirm_just_as_the_run_settles_is_answered_with_the_result_not_a_cancel()
    {
        var run = new OperationRun(OperationKind.Download);
        var flow = Begin(run);
        run.Record(OperationEnd.Succeeded(OperationKind.Download));
        run.Settle();   // the prompt has not been told yet
        Assert.False(flow.Confirm());
        Assert.Equal(CancelStage.Ended, flow.Stage);
        Assert.Equal("The download already finished", flow.View.Title);
        Assert.False(run.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_prompt_opened_for_a_run_that_has_already_settled_starts_at_the_result()
    {
        var run = new OperationRun(OperationKind.Inspect);
        run.Record(OperationEnd.Succeeded(OperationKind.Inspect));
        run.Settle();
        var flow = Begin(run);
        Assert.Equal(CancelStage.Ended, flow.Stage);
        Assert.Equal("The inspection already finished", flow.View.Title);
        Assert.Equal("The image was inspected. It finished before it could be cancelled, so nothing was cancelled.", flow.View.Body);
    }

    [Fact]
    public void A_cancel_that_arrives_too_late_is_reported_as_a_finish_not_a_cancellation()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        Assert.True(flow.Confirm());
        run.Record(OperationEnd.Succeeded(OperationKind.Build));
        run.Settle();
        flow.RunSettled();
        var view = flow.View;
        Assert.Equal("Finished before it could be cancelled", view.Title);
        Assert.Contains("arrived too late", view.Body);
        Assert.Equal("Finished.", view.Status);
        Assert.Equal(CancelTone.Done, view.Tone);
        Assert.Equal(100, view.Percent);
        Assert.Equal("Done", view.CloseText);
        Assert.True(flow.CanClose);
    }

    [Fact]
    public void Follow_up_warnings_of_a_run_that_finished_anyway_stay_in_the_final_dialog()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        flow.Confirm();
        run.NoteWarning("The ISO was saved, but some follow-up steps reported problems.");
        run.NoteWarning("The ISO was saved, but some follow-up steps reported problems.");   // the same problem is listed once
        run.NoteWarning("A second problem.");   // and a later one is added, not dropped
        run.Record(OperationEnd.Succeeded(OperationKind.Build));
        run.Settle();
        flow.RunSettled();
        var view = flow.View;
        Assert.Equal(CancelTone.Warning, view.Tone);
        Assert.Equal("The ISO was saved, but some follow-up steps reported problems. A second problem.", view.Warning);
        Assert.True(view.ShowProgress);   // it did finish
        Assert.Equal("Done", view.CloseText);
    }

    [Fact]
    public void A_cancel_callback_that_fails_is_a_visible_warning_while_the_token_is_still_cancelled()
    {
        var run = new OperationRun(OperationKind.Build);
        using var registration = run.Token.Register(() => throw new InvalidOperationException("The progress hook broke."));
        var flow = Begin(run);
        Assert.True(flow.Confirm());   // it was sent ...
        Assert.True(run.Token.IsCancellationRequested);   // ... and the token is cancelled whatever a callback does
        Assert.Contains("The cancel was sent, but a step that reacts to it failed: The progress hook broke.", run.Warning);
        Assert.Equal(run.Warning, flow.View.Warning);   // shown while the stop is still running

        run.Record(Classified(OperationKind.Build, CleanCancellation()));
        run.Settle();
        flow.RunSettled();
        var view = flow.View;
        Assert.Equal("Build cancelled", view.Title);
        Assert.Equal(CancelTone.Warning, view.Tone);   // the cancellation finished, with a problem beside it
        Assert.Contains("The progress hook broke.", view.Warning);
        Assert.True(view.ShowProgress);
    }

    [Fact]
    public void A_token_source_that_cannot_be_disposed_still_settles_and_says_so()
    {
        var run = new OperationRun(OperationKind.Build, new StubbornSource());
        run.Record(OperationEnd.Succeeded(OperationKind.Build));
        run.Settle();   // must neither throw nor leave the run unsettled
        Assert.True(run.Settled.IsCompletedSuccessfully);
        Assert.Contains("The cancel token could not be released: The handle is busy.", run.Warning);
        Assert.Equal(RunResult.Succeeded, run.End!.Result);   // the outcome is unchanged ...
        Assert.Contains("could not be released", run.End.Warning);   // ... and the problem travels with it
        var view = Begin(run).View;
        Assert.Equal(CancelTone.Warning, view.Tone);
        Assert.Contains("The handle is busy.", view.Warning);
    }

    private sealed class StubbornSource : CancellationTokenSource
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) throw new IOException("The handle is busy.");
        }
    }

    [Fact]
    public void What_a_run_notes_is_added_to_any_outcome_without_changing_it()
    {
        var run = new OperationRun(OperationKind.Build);
        run.NoteWarning("A cancel callback failed.");
        run.Record(Classified(OperationKind.Build, new IOException("Disk is full.")));
        run.Settle();
        var end = run.End!;
        Assert.Equal(RunResult.Failed, end.Result);
        Assert.Contains("Disk is full.", end.Message);
        Assert.Equal("A cancel callback failed.", end.Warning);
        var view = Begin(run).View;
        Assert.Equal(CancelTone.Error, view.Tone);   // a warning never turns a failure into something milder
        Assert.Equal("A cancel callback failed.", view.Warning);
        Assert.False(view.ShowProgress);
    }

    [Fact]
    public void A_clean_cancellation_ends_with_a_completed_bar_and_a_Done_button()
    {
        var view = Ended(Classified(OperationKind.Build, CleanCancellation(), canResume: true));
        Assert.Equal("Build cancelled", view.Title);
        Assert.Contains("Saved progress can be resumed.", view.Body);
        Assert.Equal("Cleanup finished.", view.Status);
        Assert.True(view.ShowProgress);
        Assert.False(view.Indeterminate);
        Assert.Equal(100, view.Percent);
        Assert.Equal(CancelTone.Done, view.Tone);
        Assert.Equal("Done", view.CloseText);
        Assert.Equal(CancelLink.None, view.Link);
        Assert.Equal("", view.Warning);
    }

    [Fact]
    public void A_cancel_whose_mount_could_not_be_discarded_is_an_error_with_the_logs_never_a_safe_close()
    {
        var failure = new BuildFailureException("The build failed and its mount could not be discarded.", @"C:\tiny11\job",
            new AggregateException(new OperationCanceledException(), new IOException("discard failed")), recoveryRequired: true);
        var view = Ended(Classified(OperationKind.Build, failure));
        Assert.Equal(CancelTone.Error, view.Tone);
        Assert.Equal("Build cancelled, but recovery is needed", view.Title);   // still a cancellation, not a failure
        Assert.Contains("Recover workspace", view.Body);
        Assert.False(view.ShowProgress);   // no completed bar
        Assert.Equal(0, view.Percent);
        Assert.Equal("", view.Status);   // nothing says it closed cleanly
        Assert.Equal("Close", view.CloseText);
        Assert.Equal(CancelLink.OpenLogs, view.Link);
        Assert.Equal("Open logs", view.LinkText);
        Assert.Contains(@"C:\tiny11\job", view.Footnote);
        Assert.DoesNotContain("safe", view.Title + view.Body + view.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(view.CanClose);   // the user can always leave a finished dialog
    }

    [Fact]
    public void Recovery_keeps_the_cause_it_followed_whether_a_cancellation_or_a_failure()
    {
        BuildFailureException Failure(Exception cause) => new("Build stopped.", @"C:\tiny11\job",
            new AggregateException(cause, new IOException("discard failed")), recoveryRequired: true);
        var cancelled = Classified(OperationKind.Build, Failure(new OperationCanceledException()));
        var failed = Classified(OperationKind.Build, Failure(new IOException("DISM failed")));
        Assert.Equal((RunResult.Cancelled, true), (cancelled.Result, cancelled.RecoveryRequired));
        Assert.Equal((RunResult.Failed, true), (failed.Result, failed.RecoveryRequired));
        Assert.Equal("Build cancelled, but recovery is needed", Ended(cancelled).Title);
        Assert.Equal("Build failed, and recovery is needed", Ended(failed).Title);
        Assert.All(new[] { cancelled, failed }, end => Assert.True(end.NeedsAttention));
    }

    [Fact]
    public void A_failure_while_the_stop_was_pending_is_a_failure_with_details_to_copy()
    {
        var view = Ended(Classified(OperationKind.Build, new IOException("Disk is full.")));
        Assert.Equal(CancelTone.Error, view.Tone);
        Assert.Equal("Build failed", view.Title);
        Assert.Contains("Disk is full.", view.Body);
        Assert.False(view.ShowProgress);
        Assert.Equal(CancelLink.CopyDetails, view.Link);   // there is no job folder to open
        Assert.Equal("Copy details", view.LinkText);
        Assert.Equal("Close", view.CloseText);
    }

    [Fact]
    public void Cleanup_problems_beside_a_clean_cancellation_are_a_warning_never_a_silent_success()
    {
        var failure = new BuildFailureException("Build cancelled.", @"C:\tiny11\job",
            new AggregateException(new OperationCanceledException(), new IOException("The log is locked.")));
        var outcome = OperationOutcomes.Classify(OperationKind.Build, failure);
        Assert.True(outcome.Cancelled);
        Assert.False(outcome.RecoveryRequired);
        Assert.Contains("The log is locked.", outcome.Warning);
        Assert.DoesNotContain("The log is locked.", outcome.Message);   // reported beside the outcome, never as its cause
        Assert.Equal(outcome.Message + " " + outcome.Warning, outcome.FullMessage);

        var view = Ended(OperationEnd.From(outcome));
        Assert.Equal("Build cancelled", view.Title);
        Assert.Equal(CancelTone.Warning, view.Tone);
        Assert.Contains("The log is locked.", view.Warning);
        Assert.Equal(CancelLink.OpenLogs, view.Link);
        Assert.True(view.ShowProgress);   // the cancellation itself did finish

        var failed = OperationOutcomes.Classify(OperationKind.Build, new BuildFailureException("Build failed. DISM failed.", @"C:\tiny11\job",
            new AggregateException(new IOException("DISM failed."), new IOException("The log is locked."))));
        Assert.False(failed.Cancelled);
        Assert.Contains("DISM failed.", failed.Message);
        Assert.StartsWith("Some cleanup steps reported problems: The log is locked.", failed.Warning);
        Assert.Equal("", OperationOutcomes.Classify(OperationKind.Build, CleanCancellation()).Warning);
    }

    [Fact]
    public async Task The_first_recorded_outcome_is_the_real_one_and_a_run_always_settles()
    {
        var run = new OperationRun(OperationKind.Build);
        run.Record(Classified(OperationKind.Build, CleanCancellation()));
        run.Record(OperationEnd.Succeeded(OperationKind.Build));   // later page code cannot rewrite how it ended
        run.Settle();
        run.Settle();
        await run.Settled;
        Assert.Equal(RunResult.Cancelled, run.End!.Result);
        Assert.Equal(CancelRequest.Settled, run.RequestCancel());

        var silent = new OperationRun(OperationKind.Inspect);
        silent.Settle();   // nothing was ever recorded
        await silent.Settled;
        Assert.Equal(RunResult.Failed, silent.End!.Result);
        Assert.True(silent.End.NeedsAttention);   // never shown as finished
        var view = Begin(silent).View;
        Assert.Equal(CancelTone.Error, view.Tone);
        Assert.False(view.ShowProgress);
    }

    [Fact]
    public void Once_the_run_has_settled_late_events_and_repeated_signals_change_nothing()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        flow.Confirm();
        run.Record(Classified(OperationKind.Build, CleanCancellation()));
        run.Settle();
        flow.RunSettled();
        var before = flow.View;
        var changes = 0;
        flow.Changed += () => changes++;
        flow.RunSettled();
        flow.Observe(new("Compress", "A late event", 50, "stage:Compress"));
        flow.Observe(new(CancelCleanup.Stage, "A late cleanup step", OperationId: CancelCleanup.LogsOperation, State: BuildOperationState.Running));
        Assert.Equal(before, flow.View);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Progress_during_the_stop_is_the_current_operations_own_clamped_and_never_a_shutdown_percentage()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        flow.Confirm();
        BuildProgress Event(double? percent, BuildOperationState? state = null) =>
            new("Compress", "Compress the final image", percent, "stage:Compress", state);
        flow.Observe(Event(null, BuildOperationState.Running));
        flow.Observe(Event(42.7));
        Assert.Equal("Current operation: Compress the final image · 42%", flow.View.Detail);
        Assert.True(flow.View.Indeterminate);   // the bar is a wait; the percentage is only text
        Assert.Equal(0, flow.View.Percent);
        flow.Observe(Event(-5));
        Assert.EndsWith("· 0%", flow.View.Detail);
        flow.Observe(Event(double.NaN));
        flow.Observe(Event(double.PositiveInfinity));
        Assert.EndsWith("· 0%", flow.View.Detail);   // not percentages: ignored
        flow.Observe(Event(250));   // clamped to 100, which means that operation is done
        Assert.Equal("Finishing the current operation", flow.View.Status);
        Assert.Equal("Current operation: Compress the final image", flow.View.Detail);
        flow.Observe(Event(30));   // a later percentage (the discard of a mount, say) belongs to something with no name here
        Assert.Equal("Finishing the current operation", flow.View.Status);
        Assert.DoesNotContain("%", flow.View.Detail);
        flow.Observe(new("Create ISO", "Create bootable ISO", null, "stage:Create ISO", BuildOperationState.Running));
        Assert.Equal("Waiting for the current operation to finish", flow.View.Status);   // a new operation starts the wait again
        Assert.Equal("Current operation: Create bootable ISO", flow.View.Detail);
        flow.Observe(new("Create ISO", "A console line", 10));   // no operation id: not an operation's progress
        Assert.DoesNotContain("%", flow.View.Detail);
    }

    [Fact]
    public void The_dialog_is_only_redrawn_when_what_it_says_changes()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        flow.Confirm();
        var redraws = 0;
        flow.Changed += () => redraws++;
        flow.Observe(new("Compress", "Compress the final image", null, "stage:Compress", BuildOperationState.Running));
        for (var tenth = 0; tenth < 10; tenth++)
            flow.Observe(new("Compress", "Compress the final image", 20 + tenth / 10d, "stage:Compress"));
        Assert.Equal(2, redraws);   // the operation appearing, then the first whole percent; the other nine say the same thing
    }

    [Fact]
    public void Core_cleanup_progress_moves_the_wait_forward_and_never_back()
    {
        // The events Core sends from FailAsync (and the inventory's cancel path) for a cancelled run.
        BuildProgress Mount() => new("Cancel cleanup", "Discarding the working mount", null, "cancel:mount", BuildOperationState.Running);
        BuildProgress Logs() => new("Cancel cleanup", "Saving logs and stopped-job status", null, "cancel:logs", BuildOperationState.Running);
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        flow.Observe(Mount());
        flow.Confirm();
        Assert.Equal("Waiting for the current operation to finish", flow.View.Status);   // before the cancel was sent: ignored
        flow.Observe(Mount());
        Assert.Equal("Discarding the working mount", flow.View.Status);
        flow.Observe(Logs());
        Assert.Equal("Saving logs", flow.View.Status);
        flow.Observe(Mount());
        Assert.Equal("Saving logs", flow.View.Status);   // never back
        Assert.Equal("", flow.View.Detail);   // the operation that was running is long over

        // With nothing mounted Core sends only the second event, and the wait jumps straight to it.
        var unmounted = Begin(new OperationRun(OperationKind.Build));
        unmounted.Confirm();
        unmounted.Observe(Logs());
        Assert.Equal("Saving logs", unmounted.View.Status);
    }

    [Fact]
    public void An_operation_without_image_cleanup_stops_without_the_mount_and_log_wording()
    {
        var run = new OperationRun(OperationKind.Download);
        var flow = Begin(run);
        Assert.Equal("Cancel this download?", flow.View.Title);
        Assert.Contains("partial file is kept", flow.View.Body);
        Assert.Equal("Keep downloading", flow.View.CloseText);
        flow.Confirm();
        Assert.Equal("Cancelling the download", flow.View.Title);
        Assert.Equal("Stopping the download", flow.View.Status);
        Assert.Equal("Do not close the app until this finishes.", flow.View.Footnote);
        flow.Observe(new(CancelCleanup.Stage, "Discarding the working mount", OperationId: CancelCleanup.MountOperation, State: BuildOperationState.Running));
        Assert.Equal("Stopping the download", flow.View.Status);   // a download has no mount to discard

        run.Record(Classified(OperationKind.Download, new OperationCanceledException()));
        run.Settle();
        flow.RunSettled();
        var view = flow.View;
        Assert.Equal("Download cancelled", view.Title);
        Assert.Equal("Stopped.", view.Status);
        var text = view.Title + view.Body + view.Status + view.Footnote;
        Assert.DoesNotContain("build", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mount", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Closing_the_dialog_is_refused_only_while_the_stop_is_running()
    {
        var run = new OperationRun(OperationKind.Build);
        var flow = Begin(run);
        Assert.True(flow.CanClose);   // Escape on the question simply keeps building
        flow.Confirm();
        Assert.False(flow.CanClose);   // Escape, the close button and closing the app are refused
        run.Record(OperationEnd.Succeeded(OperationKind.Build));
        run.Settle();
        flow.RunSettled();
        Assert.True(flow.CanClose);
        Assert.True(flow.View.CanClose);
    }

    [Fact]
    public void The_shell_button_keeps_the_caption_the_user_asked_for_and_no_prompt_says_the_working_copy_is_discarded()
    {
        Assert.Equal("Cancel safely", CancelPrompts.ButtonLabel);
        Assert.Contains("The mounted copy is safely unmounted; working files, saved progress and logs are kept.",
            CancelPrompts.For(OperationKind.Build).Explanation);
        foreach (var kind in Enum.GetValues<OperationKind>())
        {
            var copy = CancelPrompts.For(kind);
            Assert.DoesNotContain("working copy", copy.Explanation + copy.Hint, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static IEnumerable<object[]> OperationKinds() => Enum.GetValues<OperationKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(OperationKinds))]
    public void Every_busy_operation_is_cancelled_with_the_same_complete_set_of_labels(OperationKind kind)
    {
        var copy = CancelPrompts.For(kind);
        var all = new[] { copy.Noun, copy.Question, copy.Explanation, copy.Confirm, copy.Keep, copy.Hint,
            copy.Stopping, copy.Waiting, copy.Finished };
        Assert.All(all, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.EndsWith("?", copy.Question);
        Assert.StartsWith("Cancel", copy.Confirm);
        Assert.NotEqual(copy.Confirm, copy.Keep);
        Assert.Equal(kind is OperationKind.Build or OperationKind.Resume or OperationKind.Inventory, copy.CleansUp);
        if (kind is not (OperationKind.Build or OperationKind.Resume))
            Assert.All(all.Except([copy.Finished]), text => Assert.DoesNotContain("build", text, StringComparison.OrdinalIgnoreCase));
    }
}
