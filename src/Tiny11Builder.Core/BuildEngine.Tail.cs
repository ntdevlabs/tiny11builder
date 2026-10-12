using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    /// <summary>Everything the part of a build after the image is committed needs; shared by Build and Resume.</summary>
    private sealed class BuildSession
    {
        private readonly object warningGate = new();
        public required BuildRecipe Recipe { get; init; }
        public required BuildJob Job { get; init; }
        public required BuildOperationTracker Tracking { get; init; }
        public IProgress<BuildProgress>? CancellationProgress { get; init; }
        public required DismClient Dism { get; init; }
        public required BuildExecutionPlan Execution { get; init; }
        public required WindowsImage Image { get; init; }
        public required string EfiRelative { get; init; }
        public required string Staging { get; init; }
        public required string Mount { get; init; }
        public required string WorkingWim { get; init; }
        public required string TemporaryIso { get; init; }
        public required string ReportPath { get; init; }
        public required string Scratch { get; init; }
        public required Stopwatch Elapsed { get; init; }
        public TimeSpan PriorActive { get; init; }
        public string? ResumedFrom { get; init; }
        public bool UsedOptimizedMount { get; set; }
        public bool SetupPending { get; set; }
        public bool CompressPending { get; set; } = true;
        public string StageName { get; set; } = "Prepare";
        public bool Mounted { get; set; }
        public MediaStagingResult? StagingResult { get; set; }
        public IReadOnlyList<ImageCleanupResult> Cleanup { get; set; } = [];
        public List<FeatureRemovalVerification> Features { get; } = [];
        public List<RemovalVerification> Capabilities { get; } = [];
        public ResumeCheckpoint? Checkpoint { get; set; }
        public List<string> Warnings { get; } = [];
        public TimeSpan TotalActive => PriorActive + Elapsed.Elapsed;

        public void Log(string text) => Tracking.Output(text);

        public void Warn(string message)
        {
            AddWarning(message);
            Log("WARNING: " + message);
        }

        public void AddWarning(string message)
        {
            lock (warningGate) Warnings.Add(message);
        }

        public void Stage(string next, string description, CancellationToken token)
        {
            // Close the stage whose body has returned BEFORE observing cancellation, so finished work is never labelled cancelled.
            Tracking.EndStage();
            token.ThrowIfCancellationRequested();
            StageName = next;
            Job.Flush();
            Job.State("running", next);
            Tracking.BeginStage(next, description);
        }
    }

    // ---- checkpoints --------------------------------------------------------------------------------------------

    private static async Task SaveCommittedCheckpointAsync(BuildSession s, SourceIdentity source)
    {
        string? failure = null;
        await s.Tracking.RunAsync("checkpoint:committed", "Commit", "Save verified progress so an interrupted build can resume",
            async () =>
            {
                try
                {
                    // The commit has finished. Hashing and writing are not cancellable: the record must exist before a
                    // cancellation request is observed at the next stage boundary.
                    s.Checkpoint = await CreateCommittedCheckpointAsync(s, source).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failure = error.Message;
                    s.Warn($"Saved progress could not be written ({error.Message}). This build cannot be resumed if it is interrupted.");
                }
            },
            () => failure is null ? null : $"Not saved: {failure}. This build cannot be resumed if it is interrupted.").ConfigureAwait(false);
    }

    private static async Task<ResumeCheckpoint> CreateCommittedCheckpointAsync(BuildSession s, SourceIdentity source)
    {
        var directory = s.Job.Directory;
        Digest.RequireCompleteWim(s.WorkingWim, "The committed working image");
        var recipeHash = Digest.HashBytes(await File.ReadAllBytesAsync(Path.Combine(directory, JobCheckpoint.RecipeFileName)).ConfigureAwait(false));
        var working = Digest.HashAsync(s.WorkingWim, percent => s.Log($"Hashing the committed image: {percent:0}%"), CancellationToken.None);
        var media = MediaManifest.CreateAsync(s.Staging, s.Execution.CopyWorkers, CancellationToken.None);
        await Task.WhenAll(working, media).ConfigureAwait(false);
        var (length, sha) = await working.ConfigureAwait(false);
        var hasSetup = SetupInstaller.RequiresBootEdit(s.Recipe);
        var checkpoint = new ResumeCheckpoint
        {
            SchemaVersion = ResumeCheckpoint.CurrentSchema,
            JobId = Path.GetFileName(directory)["tiny11-".Length..],
            Phase = ResumePhase.Committed,
            CreatedUtc = DateTimeOffset.UtcNow,
            RecipeSha256 = recipeHash,
            Source = source,
            Image = s.Image,
            WorkingImage = new("working.wim", length, sha),
            SetupState = hasSetup ? SetupProgress.Pending : SetupProgress.NotRequired,
            EfiBootImage = s.EfiRelative,
            StagedMedia = await media.ConfigureAwait(false),
            Report = new()
            {
                UsedOptimizedMount = s.UsedOptimizedMount, Execution = s.Execution, SetupMedia = s.StagingResult,
                RedundantData = s.Cleanup, Features = s.Features.ToArray(), Capabilities = s.Capabilities.ToArray()
            },
            ActiveMilliseconds = s.TotalActive.TotalMilliseconds,
            Operations = s.Tracking.SnapshotCompleting("stage:Commit", "checkpoint:committed")
        };
        CheckpointStore.WriteAtomic(directory, checkpoint);
        return checkpoint;
    }

    /// <returns>True when the update is on disk, or when there is no checkpoint to keep consistent; false after a reported write failure.</returns>
    private static bool UpdateCheckpoint(BuildSession s, Func<ResumeCheckpoint, ResumeCheckpoint> change)
    {
        // Without a first checkpoint the job is not resumable, and a partial record would only mislead.
        if (s.Checkpoint is null) return true;
        s.Checkpoint = change(s.Checkpoint) with { CreatedUtc = DateTimeOffset.UtcNow, ActiveMilliseconds = s.TotalActive.TotalMilliseconds };
        try
        {
            CheckpointStore.WriteAtomic(s.Job.Directory, s.Checkpoint);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            s.Warn($"Saved progress could not be updated ({error.Message}); an interruption may have to repeat the last step.");
            return false;
        }
    }

    private static IReadOnlyList<CheckpointFile> ReplaceEntry(IReadOnlyList<CheckpointFile> files, CheckpointFile entry) =>
        files.Where(file => !file.RelativePath.Equals(entry.RelativePath, StringComparison.OrdinalIgnoreCase)).Append(entry)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();

    // ---- the tail shared by Build and Resume --------------------------------------------------------------------

    private async Task<BuildResult> RunTailAsync(BuildSession s, CancellationToken token)
    {
        if (s.SetupPending) await ApplySetupImageAsync(s, token).ConfigureAwait(false);
        if (s.CompressPending) await CompressImageAsync(s, token).ConfigureAwait(false);
        await CreateIsoAsync(s, token).ConfigureAwait(false);
        return await VerifyAndPublishAsync(s, token).ConfigureAwait(false);
    }

    private async Task ApplySetupImageAsync(BuildSession s, CancellationToken token)
    {
        s.Stage("Setup image", SetupInstaller.RequiresLegacy(s.Recipe) ?
            "Enabling the legacy Windows installer required by component-store removals." :
            "Applying hardware-requirement settings to the Windows Setup boot image.", token);
        var bootWim = Path.Combine(s.Staging, @"sources\boot.wim");
        // Edit a private copy. The staged original is replaced atomically only after the copy committed and validated,
        // so an interruption can never leave a half-edited boot.wim in the media.
        var privateDirectory = Path.Combine(s.Job.Directory, "setup");
        PathSafety.DeleteOwnedDirectory(privateDirectory, s.Job.Directory);
        Directory.CreateDirectory(privateDirectory);
        var privateBoot = Path.Combine(privateDirectory, "boot.wim");
        File.Copy(bootWim, privateBoot, overwrite: false);
        var bootImages = await s.Dism.GetImagesAsync(privateBoot, token).ConfigureAwait(false);
        var setup = bootImages.FirstOrDefault(i => i.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) ??
            bootImages.FirstOrDefault(i => i.Index == 2) ??
            throw new InvalidDataException("Cannot identify Windows Setup in boot.wim.");
        s.Dism.DisableOptimizedMount();
        s.Mounted = true;
        await s.Dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{privateBoot}", $"/Index:{setup.Index}",
            $"/MountDir:{s.Mount}").ConfigureAwait(false);
        if (s.Recipe.SelectedOptions.Contains("hardware-bypass"))
            registry.ApplySetupBypass(s.Mount, s.Log);
        if (SetupInstaller.RequiresLegacy(s.Recipe))
            SetupInstaller.ApplyLegacy(s.Mount, s.Log);
        await s.Dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{s.Mount}", "/Commit").ConfigureAwait(false);
        s.Mounted = false;
        Digest.RequireCompleteWim(privateBoot, "The edited Windows Setup image");
        // The edit has committed; validating and placing it is not interruptible, so finished work is not discarded.
        var edited = await s.Dism.GetImagesAsync(privateBoot, CancellationToken.None).ConfigureAwait(false);
        if (edited.Count != bootImages.Count ||
            !edited.Select(image => image.Index).Order().SequenceEqual(bootImages.Select(image => image.Index).Order()))
            throw new InvalidDataException("The edited Windows Setup image no longer lists its original indexes. The staged boot.wim was left unchanged.");
        var (editedLength, editedSha) = await Digest.HashAsync(privateBoot, null, CancellationToken.None).ConfigureAwait(false);
        // The replacement is announced durably BEFORE it happens, so the checkpoint can always describe the media. If that
        // record cannot be written, the staged original is simply left as it is.
        if (!UpdateCheckpoint(s, saved => saved with
        {
            SetupState = SetupProgress.Replacing, EditedBootSha256 = editedSha,
            Report = saved.Report with { LegacyInstallerEnabled = SetupInstaller.RequiresLegacy(s.Recipe) }
        }))
            throw new IOException("The saved progress could not record the Setup image replacement, so the staged boot.wim was left unchanged.");
        File.Move(privateBoot, bootWim, overwrite: true);
        UpdateCheckpoint(s, saved => saved with
        {
            SetupState = SetupProgress.Applied, EditedBootSha256 = editedSha,
            StagedMedia = ReplaceEntry(saved.StagedMedia, new(@"sources\boot.wim", editedLength, editedSha))
        });
        s.SetupPending = false;
        try { PathSafety.DeleteOwnedDirectory(privateDirectory, s.Job.Directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            s.Warn($"The temporary Setup image folder could not be removed ({error.Message}).");
        }
    }

    private async Task CompressImageAsync(BuildSession s, CancellationToken token)
    {
        var recipe = s.Recipe;
        s.Stage("Compress", "Exporting the final single-edition image with integrity checking.", token);
        // DISM adds images to an existing destination, so an image left by an interrupted attempt must never be reused,
        // and a second format must never ride along: remove exactly the owned candidates, then export to a fresh folder.
        var sources = Path.Combine(s.Staging, "sources");
        foreach (var stale in new[] { "install.wim", "install.esd" })
        {
            var file = Path.Combine(sources, stale);
            if (!File.Exists(file)) continue;
            PathSafety.RejectReparsePoints(file);
            File.Delete(file);
        }
        var partialDirectory = Path.Combine(s.Job.Directory, "compress");
        PathSafety.DeleteOwnedDirectory(partialDirectory, s.Job.Directory);
        Directory.CreateDirectory(partialDirectory);
        var leaf = recipe.Compression == Compression.Recovery ? "install.esd" : "install.wim";
        var partial = Path.Combine(partialDirectory, leaf);
        await s.Dism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{s.WorkingWim}", "/SourceIndex:1",
            $"/DestinationImageFile:{partial}", recipe.Compression switch
            {
                Compression.Recovery => "/Compress:recovery", Compression.Maximum => "/Compress:max", _ => "/Compress:fast"
            },
            "/CheckIntegrity").ConfigureAwait(false);
        // The export has finished. Checking, placing and checkpointing its result must not be interrupted by a cancellation
        // request, or the finished (and slowest) work would be lost; cancellation is observed at the next stage boundary.
        var finalImages = await s.Dism.GetImagesAsync(partial, CancellationToken.None).ConfigureAwait(false);
        if (finalImages.Count != 1 || finalImages[0].Version.Build != s.Image.Version.Build ||
            finalImages[0].Architecture != s.Image.Architecture)
            throw new InvalidDataException("The final installation image failed its edition/build/architecture check.");
        Digest.RequireCompleteWim(partial, "The compressed installation image");
        File.Move(partial, Path.Combine(sources, leaf), overwrite: false);
        PathSafety.DeleteOwnedDirectory(partialDirectory, s.Job.Directory);
        var saved = await SaveCompressedCheckpointAsync(s, @"sources\" + leaf, recipe.Compression).ConfigureAwait(false);
        s.CompressPending = false;
        // The working image is the only copy of the servicing work until a verified compressed checkpoint exists.
        if (!recipe.KeepWorkingFiles && (saved || s.Checkpoint is null))
            try { DeleteWorkingWim(s.Job.Directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                s.Warn($"The working image could not be deleted yet ({error.Message}); it is retained in the job folder.");
            }
    }

    private static async Task<bool> SaveCompressedCheckpointAsync(BuildSession s, string relative, Compression compression)
    {
        var saved = false;
        string? failure = null;
        await s.Tracking.RunAsync("checkpoint:compressed", "Compress", "Save verified compressed-image progress",
            async () =>
            {
                try
                {
                    if (s.Checkpoint is null) throw new IOException("there is no earlier checkpoint for this job");
                    var (length, sha) = await Digest.HashAsync(Path.Combine(s.Staging, relative),
                        percent => s.Log($"Hashing the compressed image: {percent:0}%"), CancellationToken.None).ConfigureAwait(false);
                    var entry = new CheckpointFile(relative, length, sha);
                    var next = s.Checkpoint with
                    {
                        Phase = ResumePhase.Compressed, CompressedImage = entry, CompressedWith = compression,
                        StagedMedia = ReplaceEntry(s.Checkpoint.StagedMedia, entry), CreatedUtc = DateTimeOffset.UtcNow,
                        ActiveMilliseconds = s.TotalActive.TotalMilliseconds,
                        Operations = s.Tracking.SnapshotCompleting("stage:Compress", "checkpoint:compressed")
                    };
                    CheckpointStore.WriteAtomic(s.Job.Directory, next);
                    // Read back what was written: only a checkpoint that parses and validates may release the working image.
                    var written = CheckpointStore.Read(s.Job.Directory);
                    if (written.Phase != ResumePhase.Compressed || written.CompressedImage?.Sha256 != sha)
                        throw new InvalidDataException("the checkpoint read back differently from what was written");
                    s.Checkpoint = next;
                    saved = true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    failure = error.Message;
                    s.Warn($"The compressed-image checkpoint could not be saved ({error.Message}); the working image is kept until the ISO is published.");
                }
            },
            () => failure is null ? null : $"Not saved: {failure}.").ConfigureAwait(false);
        return saved;
    }

    private async Task CreateIsoAsync(BuildSession s, CancellationToken token)
    {
        s.Stage("Create ISO", "Creating a UEFI-bootable ISO using Microsoft's oscdimg.", token);
        if (File.Exists(s.TemporaryIso))
        {
            PathSafety.RejectReparsePoints(s.TemporaryIso);
            File.Delete(s.TemporaryIso);   // a leftover from an interrupted attempt is never reused
        }
        var bios = Path.Combine(s.Staging, @"boot\etfsboot.com");
        var efi = Path.Combine(s.Staging, s.EfiRelative);
        var boot = s.Image.Architecture == "x64" && File.Exists(bios) ?
            $"-bootdata:2#p0,e,b{bios}#pEF,e,b{efi}" : $"-bootdata:1#pEF,e,b{efi}";
        var isoResult = await runner.RunAsync(s.Recipe.OscdimgPath,
            ["-m", "-o", "-u2", "-udfver102", boot, s.Staging, s.TemporaryIso], s.Log, token, finishOnCancellation: false).ConfigureAwait(false);
        if (isoResult.ExitCode != 0 || !File.Exists(s.TemporaryIso) || new FileInfo(s.TemporaryIso).Length < 34816)
            throw new IOException($"oscdimg did not produce an ISO (exit {isoResult.ExitCode}). {isoResult.Output}");
    }

    private static async Task<BuildResult> VerifyAndPublishAsync(BuildSession s, CancellationToken token)
    {
        s.Stage("Verify output", "Hashing the completed ISO and publishing it without overwriting existing files.", token);
        string sha;
        long isoBytes;
        await using (var input = File.OpenRead(s.TemporaryIso))
        {
            isoBytes = input.Length;
            await IsoStructure.ValidateAsync(input, token).ConfigureAwait(false);
            sha = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        }
        token.ThrowIfCancellationRequested();
        await PublishIsoAsync(s, sha, token).ConfigureAwait(false);
        // From here the verified ISO exists at the requested path. Nothing below may turn that into a failed build.
        return CompleteAfterPublish(s, sha, isoBytes);
    }

    private static Task PublishIsoAsync(BuildSession s, string sha, CancellationToken token) =>
        PublishFileAsync(s.TemporaryIso, s.Recipe.OutputPath, Path.GetFileName(s.Job.Directory), sha, forceCopy: false, token);

    /// <summary>
    /// Publishes a verified file without ever overwriting. Same volume: one atomic no-overwrite rename. Otherwise the file is
    /// copied to a fresh partial, flushed to disk and re-hashed against <paramref name="sha"/> BEFORE its no-overwrite rename.
    /// On any failure the verified source stays where it is and the error names it.
    /// </summary>
    internal static async Task PublishFileAsync(string verifiedFile, string output, string partialTag, string sha, bool forceCopy,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var sameVolume = !forceCopy && Path.GetPathRoot(verifiedFile)!.Equals(Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase);
        var partial = output + "." + partialTag + ".partial";
        var ownsPartial = false;
        try
        {
            if (sameVolume)
                File.Move(verifiedFile, output, overwrite: false);   // atomic, and it fails if the name appeared meanwhile
            else
            {
                await using (var input = File.OpenRead(verifiedFile))
                await using (var copy = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                {
                    ownsPartial = true;
                    await input.CopyToAsync(copy, token).ConfigureAwait(false);
                    copy.Flush(flushToDisk: true);   // on disk, not just in the OS cache, before it is verified and renamed
                }
                var (_, copiedSha) = await Digest.HashAsync(partial, null, token).ConfigureAwait(false);
                if (!copiedSha.Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("the copy on the output volume does not match the verified ISO");
                File.Move(partial, output, overwrite: false);
            }
        }
        catch (Exception error)
        {
            string? stray = null;
            if (ownsPartial && File.Exists(partial))
                try { File.Delete(partial); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { stray = partial; }
            if (error is OperationCanceledException) throw;
            throw new IOException($"The ISO was built and verified (SHA-256 {sha}) but could not be published to {output}: {error.Message} " +
                $"The verified ISO is preserved at {verifiedFile}." +
                (stray is null ? "" : $" A partial copy could not be removed: {stray}."), error);
        }
    }

    private static BuildResult CompleteAfterPublish(BuildSession s, string sha, long isoBytes)
    {
        var recipe = s.Recipe;
        // Each step reports its own failure as a warning and never prevents the next one.
        void Best(string what, Action step)
        {
            try { step(); }
            catch (Exception error)
            {
                s.AddWarning($"{what}: {error.Message}");
                try { s.Job.Log($"POST-PUBLISH WARNING: {what}: {error.Message}"); }
                catch (Exception logFailure) { s.AddWarning($"The warning above could not be logged: {logFailure.Message}"); }
            }
        }
        Best("The job marker could not be marked completed", () => s.Job.State("completed", "Done"));
        Best("The publication could not be logged", () => s.Log($"ISO published: {recipe.OutputPath}; SHA-256: {sha}"));
        if (File.Exists(s.TemporaryIso)) Best("The verified ISO copy in the job folder could not be deleted", () => File.Delete(s.TemporaryIso));
        if (!recipe.KeepWorkingFiles)
        {
            foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
                Best($"The {name} folder could not be removed", () => PathSafety.DeleteOwnedDirectory(Path.Combine(s.Job.Directory, name), s.Job.Directory));
            Best("The working image could not be deleted", () => DeleteWorkingWim(s.Job.Directory));
        }
        Best("The saved-progress checkpoint could not be removed", () => CheckpointStore.Delete(s.Job.Directory));
        Best("The build report (report.json) could not be written", () => File.WriteAllText(s.ReportPath,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, CompletedUtc = DateTimeOffset.UtcNow, Image = s.Image, recipe.Flavor,
                LegacyInstallerEnabled = SetupInstaller.RequiresLegacy(recipe),
                recipe.SelectedOptions, recipe.ExpertActions, recipe.Compression, recipe.EnableNetFx3,
                recipe.TurboMode, recipe.RemoveRedundantData, recipe.OmitNetFx3SetupPayload,
                Execution = s.Execution, UsedOptimizedMount = s.UsedOptimizedMount, SetupMedia = s.StagingResult,
                RedundantData = s.Cleanup, RemovedLogicalBytes = s.Cleanup.Sum(item => item.LogicalBytes),
                OptionalFeatures = s.Features, Capabilities = s.Capabilities,
                Resumed = s.ResumedFrom is not null, ResumedFrom = s.ResumedFrom,
                Output = recipe.OutputPath, Sha256 = sha, BootValidated = false, OperationLog = "operations.json",
                TimingLog = "timings.json", OutputBytes = isoBytes, Warnings = s.Warnings.ToArray(),
                Notes = new[] { "Consult build.log for every applied, missing or failed operation.",
                    "Offline registry writes do not prove policy enforcement by this Windows build/edition.",
                    "Core/expert and unsupported-hardware images require isolated VM boot testing." }
            }, JsonDefaults.Options)));
        Best("The operation history could not be completed", () => s.Tracking.Finish());
        Best("The operation history (operations.json) could not be written", () => File.WriteAllText(
            Path.Combine(s.Job.Directory, "operations.json"), JsonSerializer.Serialize(s.Tracking.Snapshot, JsonDefaults.Options)));
        Best("The stage timings (timings.json) could not be written", () => File.WriteAllText(
            Path.Combine(s.Job.Directory, "timings.json"), TimingsJson(s)));
        Best("The log could not be flushed", s.Job.Flush);
        return new(recipe.OutputPath, sha, s.ReportPath, s.Job.Directory, s.TotalActive,
            s.Cleanup.Sum(item => item.LogicalBytes), s.StagingResult?.OmittedBytes ?? 0) { Warnings = s.Warnings.ToArray() };
    }

    private static string TimingsJson(BuildSession s) => JsonSerializer.Serialize(new
    {
        s.Recipe.TurboMode, TotalMilliseconds = s.TotalActive.TotalMilliseconds,
        Resumed = s.ResumedFrom is not null, ResumedFrom = s.ResumedFrom, Stages = s.Tracking.Timings
    }, JsonDefaults.Options);

    // ---- failure handling ---------------------------------------------------------------------------------------

    private async Task<BuildFailureException> FailAsync(BuildSession s, Exception error)
    {
        var cancelled = BuildFailureException.IsCancellationCause(error);
        var secondary = new List<Exception>();
        Exception? discardFailure = null;
        void Guard(Action step)
        {
            try { step(); }
            catch (Exception diagnostic) { secondary.Add(diagnostic); }
        }
        void Closing(string id, string message)
        {
            if (cancelled) Guard(() => s.CancellationProgress?.Report(new("Cancel cleanup", message,
                OperationId: id, State: BuildOperationState.Running)));
        }
        // Recovery first: an owned mount is discarded before anything fallible (log, progress observer, files) can throw.
        if (s.Mounted)
        {
            Closing("cancel:mount", "Discarding the working mount");
            discardFailure = await TryDiscardAsync(s.Dism, s.Mount).ConfigureAwait(false);
            if (discardFailure is null) s.Mounted = false;
            else secondary.Add(discardFailure);
        }
        // A progress observer that failed while the original failure was being reported travels with it, never replaces it.
        secondary.AddRange(error.Data.Keys.OfType<string>()
            .Where(key => key.StartsWith(BuildOperationTracker.ObserverFailureKey, StringComparison.Ordinal))
            .Select(key => error.Data[key]).OfType<Exception>());
        Closing("cancel:logs", "Saving logs and stopped-job status");
        Guard(() => s.Job.Log(error.ToString()));
        if (discardFailure is not null) Guard(() => s.Job.Log(DiscardFailureText(discardFailure)));
        Guard(s.Job.Flush);
        Guard(() => s.Tracking.Fail(error, cancelled));
        Guard(() => File.WriteAllText(Path.Combine(s.Job.Directory, "operations.json"),
            JsonSerializer.Serialize(s.Tracking.Snapshot, JsonDefaults.Options)));
        Guard(() => File.WriteAllText(Path.Combine(s.Job.Directory, "timings.json"), TimingsJson(s)));
        Guard(() => s.Job.State(discardFailure is not null ? "failed" : cancelled ? "cancelled" : "failed",
            discardFailure is not null ? "Recovery required" : s.StageName));
        var resumable = JobCheckpoint.Exists(s.Job.Directory);
        var kind = cancelled ? "cancelled" : "failed";
        var message = $"Build {kind}. The source was not changed. Working files and logs are retained at {s.Job.Directory}. {error.Message}" +
            (resumable && discardFailure is null ? " Verified saved progress exists; use Resume to continue without repeating the finished work." : "") +
            DiscardText(discardFailure, resumable) + SecondaryText(secondary, discardFailure);
        return new BuildFailureException(message, s.Job.Directory,
            secondary.Count == 0 ? error : new AggregateException([error, .. secondary]), cancelled, discardFailure is not null);
    }
}
