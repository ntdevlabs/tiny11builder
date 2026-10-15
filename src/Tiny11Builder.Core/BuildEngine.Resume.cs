using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private sealed record VerifiedResume(ResumeCheckpoint Checkpoint, bool SetupPending, bool SetupAlreadyApplied,
        bool StaleSetupMount, bool StaleHiveJournals);

    /// <summary>
    /// Finishes an interrupted build from its verified checkpoint without repeating the finished work: after the
    /// installation image was committed (Setup image, Compress, ISO) or after the final image was compressed (ISO only).
    /// Every check runs before anything is changed; a refusal is an <see cref="InvalidDataException"/> with a
    /// user-facing message. Only the output path, servicing tools, final compression (before it ran) and
    /// <see cref="BuildRecipe.KeepWorkingFiles"/> may differ from the job's original recipe.
    /// </summary>
    public async Task<BuildResult> ResumeAsync(string jobFile, BuildRecipe recipe, IProgress<BuildProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var elapsed = Stopwatch.StartNew();
        var directory = ResolveJobDirectory(jobFile);
        using var job = BuildJob.Open(directory, bufferedLog: recipe.TurboMode);
        var tracking = new BuildOperationTracker(progress, job.Log);
        VerifiedResume verified;
        try
        {
            job.Log($"=== Resume requested {DateTimeOffset.UtcNow:O}: output {recipe.OutputPath}; compression {recipe.Compression}; " +
                $"keep working files {recipe.KeepWorkingFiles}; dism {recipe.DismPath}; oscdimg {recipe.OscdimgPath} ===");
            verified = await VerifyResumeAsync(job, tracking, recipe, token).ConfigureAwait(false);
        }
        catch (Exception refusal)
        {
            // Nothing has been changed. The refusal is rethrown unchanged; a failing diagnostic is attached to it, never lost.
            try
            {
                job.Log("RESUME REFUSED: " + refusal.Message);
                job.Flush();
                tracking.Fail(refusal, BuildFailureException.IsCancellationCause(refusal));
            }
            catch (Exception diagnostic) { refusal.Data["Tiny11Builder.DiagnosticFailure"] = diagnostic.Message; }
            throw;
        }

        // ---- every check passed; changes start here ----
        var checkpoint = verified.Checkpoint;
        var mount = Path.Combine(directory, "mount");
        var scratch = Path.Combine(directory, "scratch");
        var dism = new DismClient(recipe.DismPath, runner, tracking.Output,
            new(scratch, Path.Combine(directory, "dism.log"), OptimizedMount: false));
        var session = new BuildSession
        {
            Recipe = recipe, Job = job, Tracking = tracking, CancellationProgress = progress, Dism = dism, Execution = checkpoint.Report.Execution, Image = checkpoint.Image,
            EfiRelative = checkpoint.EfiBootImage, Staging = Path.Combine(directory, "media"), Mount = mount,
            WorkingWim = Path.Combine(directory, "working.wim"), TemporaryIso = Path.Combine(directory, "output.iso"),
            ReportPath = Path.Combine(directory, "report.json"), Scratch = scratch, Elapsed = elapsed,
            PriorActive = TimeSpan.FromMilliseconds(checkpoint.ActiveMilliseconds), ResumedFrom = checkpoint.Phase,
            UsedOptimizedMount = checkpoint.Report.UsedOptimizedMount, StagingResult = checkpoint.Report.SetupMedia,
            Cleanup = checkpoint.Report.RedundantData, Checkpoint = checkpoint, SetupPending = verified.SetupPending,
            CompressPending = checkpoint.Phase == ResumePhase.Committed,
            StageName = BuildProgressText.ResumeCheckStage
        };
        session.Features.AddRange(checkpoint.Report.Features);
        session.Capabilities.AddRange(checkpoint.Report.Capabilities);
        // Warnings found before the commit (such as a skipped component cleanup) are part of this build's outcome.
        foreach (var warning in checkpoint.Report.Warnings ?? []) session.AddWarning(warning);
        try
        {
            job.State("running", BuildProgressText.ResumeCheckStage);
            Directory.CreateDirectory(scratch);
            Directory.CreateDirectory(mount);
            if (verified.StaleHiveJournals && OperatingSystem.IsWindows())
            {
                session.Log("Unloading offline registry mounts left by the interrupted run.");
                OfflineHive.RecoverJob(directory);
            }
            if (verified.StaleSetupMount)
            {
                session.Log("Discarding the Windows Setup image left mounted by the interrupted run. The committed installation image is not affected.");
                session.Mounted = true;
                await DiscardMountAsync(dism, mount).ConfigureAwait(false);
                session.Mounted = false;
            }
            if (verified.SetupAlreadyApplied)
            {
                session.Log("The edited Windows Setup image was already in place; recording that.");
                var bootWim = Path.Combine(session.Staging, @"sources\boot.wim");
                UpdateCheckpoint(session, saved => saved with
                {
                    SetupState = SetupProgress.Applied,
                    StagedMedia = ReplaceEntry(saved.StagedMedia, new(@"sources\boot.wim", new FileInfo(bootWim).Length, saved.EditedBootSha256!))
                });
            }
            return await RunTailAsync(session, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw await FailAsync(session, error).ConfigureAwait(false);
        }
    }

    private async Task<VerifiedResume> VerifyResumeAsync(BuildJob job, BuildOperationTracker tracking, BuildRecipe requested,
        CancellationToken token)
    {
        var directory = job.Directory;
        var state = job.Current;
        if (state.Id != Path.GetFileName(directory)["tiny11-".Length..] || state.Purpose != "build")
            throw new InvalidDataException("This job is not an interrupted build, so it cannot be resumed.");
        if (state.Status == "completed") throw new InvalidDataException("This build already completed, so there is nothing to resume.");
        if (state.Status == "recovered")
            throw new InvalidDataException("This job was recovered and its saved work was discarded, so it cannot be resumed.");
        var checkpoint = CheckpointStore.Read(directory);
        tracking.Restore(checkpoint.Operations);
        tracking.BeginStage(BuildProgressText.ResumeCheckStage, "Verifying the saved image, setup files and settings before continuing.");
        var committed = checkpoint.Phase == ResumePhase.Committed;
        if (!committed && (checkpoint.SetupState is SetupProgress.Pending or SetupProgress.Replacing) &&
            !SetupInstaller.RequiresLegacy(requested))
            throw new InvalidDataException("The saved progress is inconsistent: the image is compressed but Setup was never finished.");

        // ---- settings: the recipe identity, frozen fields, paths and tools (cheap checks first) ----
        var recipeFile = Path.Combine(directory, JobCheckpoint.RecipeFileName);
        PathSafety.RejectReparsePoints(recipeFile);
        if (!File.Exists(recipeFile))
            throw new InvalidDataException("The job's original recipe (recipe.json) is missing, so the saved work cannot be trusted.");
        var recipeBytes = await File.ReadAllBytesAsync(recipeFile, token).ConfigureAwait(false);
        if (!Digest.HashBytes(recipeBytes).Equals(checkpoint.RecipeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The job's original recipe changed after its progress was saved, so the saved work cannot be trusted.");
        BuildRecipe original;
        try
        {
            original = JsonSerializer.Deserialize<BuildRecipe>(recipeBytes, JsonDefaults.Options) ??
                throw new InvalidDataException("The job's original recipe is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The job's original recipe is damaged: " + error.Message, error); }
        original.ValidateOptions();
        var differences = FrozenDifferences(original, requested);
        if (differences.Length > 0)
            throw new InvalidDataException($"The resume recipe changes settings that are frozen for this job ({string.Join(", ", differences)}). " +
                "Only the output location, servicing tools, final compression and keeping working files can change.");
        if (!committed && requested.Compression != checkpoint.CompressedWith)
            throw new InvalidDataException($"The final image was already compressed with {checkpoint.CompressedWith}; " +
                $"it cannot be resumed with {requested.Compression}.");
        if (!Path.GetDirectoryName(directory)!.Equals(PathSafety.Full(original.WorkspacePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This job is not inside the workspace named by its recipe.");
        if (SetupInstaller.RequiresBootEdit(original) != (checkpoint.SetupState != SetupProgress.NotRequired) &&
            !(SetupInstaller.RequiresLegacy(original) && !original.SelectedOptions.Contains("hardware-bypass") &&
                checkpoint.SetupState == SetupProgress.NotRequired && !checkpoint.Report.LegacyInstallerEnabled))
            throw new InvalidDataException("The saved Setup-image state does not match the job's recipe, so the saved work cannot be trusted.");
        if (string.IsNullOrEmpty(requested.OscdimgPath))
            throw new InvalidDataException("Windows ADK oscdimg.exe is required to create a bootable ISO.");
        PathSafety.ValidateForResume(requested);
        host.ValidateTools(requested);
        host.ValidateResume(requested, checkpoint.Image, (committed ? checkpoint.WorkingImage.Length * 2 : checkpoint.CompressedImage!.Length) + (3L << 30));
        foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
            PathSafety.RejectReparsePoints(Path.Combine(directory, name));

        // ---- nothing of this job may still be mounted or active ----
        var jobMount = Path.Combine(directory, "mount");
        var dism = new DismClient(requested.DismPath, runner);
        var staleSetupMount = false;
        foreach (var (mountDirectory, imageFile) in ParseMounted(await dism.RunAsync(token, "/Get-MountedWimInfo").ConfigureAwait(false)))
        {
            if (!IsUnder(mountDirectory, directory) && !(imageFile is not null && IsUnder(imageFile, directory))) continue;
            // Only the disposable private copy of the Setup image may be a leftover mount; it is discarded, never committed.
            var transient = SamePath(mountDirectory, jobMount) && imageFile is not null &&
                SamePath(imageFile, Path.Combine(directory, "setup", "boot.wim"));
            if (!transient)
                throw new InvalidDataException($"An image of this job is still mounted ({mountDirectory}). " +
                    "Resume continues only from an unmounted, committed image; unmount it first.");
            staleSetupMount = true;
        }
        if (!staleSetupMount && Directory.Exists(jobMount) && Directory.EnumerateFileSystemEntries(jobMount).Any())
            throw new InvalidDataException("The job's mount folder is not empty, so a mounted image may remain. Resume was refused.");
        var staleHiveJournals = Directory.EnumerateFiles(directory, "Tiny11Builder_Offline_*.hive.json").Any();

        // ---- the saved image (positive proof: size, header, SHA-256 and identity, not a timestamp) ----
        if (committed)
        {
            var working = Path.Combine(directory, "working.wim");
            PathSafety.RejectReparsePoints(working);
            if (!File.Exists(working)) throw new InvalidDataException("The saved working image (working.wim) is missing.");
            if (new FileInfo(working).Length != checkpoint.WorkingImage.Length)
                throw new InvalidDataException("The saved working image has a different size than when it was committed.");
            Digest.RequireCompleteWim(working, "The saved working image");
            await tracking.RunAsync("resume:image", BuildProgressText.ResumeCheckStage, "Verify the saved Windows image", async () =>
            {
                var (_, sha) = await Digest.HashAsync(working, percent => tracking.Output($"Verifying the saved Windows image: {percent:0}%"), token)
                    .ConfigureAwait(false);
                if (!sha.Equals(checkpoint.WorkingImage.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The saved working image changed after it was committed (its SHA-256 differs), so it cannot be trusted.");
            }).ConfigureAwait(false);
            RequireSameImage(await dism.GetImagesAsync(working, token).ConfigureAwait(false), checkpoint.Image, "The saved working image");
        }

        // ---- the staged media: every file listed by length and SHA-256, nothing else present ----
        var alternates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (checkpoint.SetupState == SetupProgress.Replacing) alternates[@"sources\boot.wim"] = checkpoint.EditedBootSha256!;
        var tolerated = committed ?
            new HashSet<string>([@"sources\install.wim", @"sources\install.esd"], StringComparer.OrdinalIgnoreCase) :
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> matchedAlternate = [];
        var lastReported = 0d;
        await tracking.RunAsync("resume:media", BuildProgressText.ResumeCheckStage, "Verify the saved setup files", async () =>
        {
            matchedAlternate = await MediaManifest.VerifyAsync(Path.Combine(directory, "media"), checkpoint.StagedMedia, alternates, tolerated,
                checkpoint.Report.Execution.CopyWorkers, percent =>
                {
                    if (percent < lastReported + 5 && percent < 100) return;
                    lastReported = percent;
                    tracking.Output($"Verifying the saved setup files: {percent:0}%");
                }, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!committed)
        {
            var compressed = Path.Combine(directory, "media", checkpoint.CompressedImage!.RelativePath);
            Digest.RequireCompleteWim(compressed, "The saved compressed installation image");
            RequireSameImage(await dism.GetImagesAsync(compressed, token).ConfigureAwait(false), checkpoint.Image,
                "The saved compressed installation image");
        }
        var setupAlreadyApplied = checkpoint.SetupState == SetupProgress.Replacing &&
            matchedAlternate.Contains(@"sources\boot.wim", StringComparer.OrdinalIgnoreCase);
        var setupPending = checkpoint.SetupState == SetupProgress.Pending ||
            (checkpoint.SetupState == SetupProgress.Replacing && !setupAlreadyApplied) ||
            (SetupInstaller.RequiresLegacy(original) && !checkpoint.Report.LegacyInstallerEnabled);
        return new(checkpoint, setupPending, setupAlreadyApplied, staleSetupMount, staleHiveJournals);
    }

    private static string ResolveJobDirectory(string jobFile)
    {
        jobFile = PathSafety.Full(jobFile);
        var directory = Path.GetDirectoryName(jobFile)!;
        var leaf = Path.GetFileName(directory);
        PathSafety.RejectReparsePoints(jobFile);
        if (Path.GetFileName(jobFile) != "job.json" || !leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _))
            throw new InvalidDataException("Select job.json inside a tiny11-owned job directory.");
        return directory;
    }

    private static void RequireSameImage(IReadOnlyList<WindowsImage> images, WindowsImage expected, string what)
    {
        if (images.Count != 1) throw new InvalidDataException($"{what} must contain exactly one image but contains {images.Count}.");
        var actual = images[0];
        // File hashes already verify the saved bytes. Display names can change when a console-decoding bug is fixed.
        if (actual.Architecture != expected.Architecture || actual.Version != expected.Version ||
            !actual.Edition.Equals(expected.Edition, StringComparison.OrdinalIgnoreCase) ||
            !actual.Installation.Equals(expected.Installation, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{what} is a different Windows image ({actual.Summary}) than this job was building ({expected.Summary}).");
    }

    // Frozen by default: a field added to BuildRecipe later cannot change on resume unless it is listed as free here.
    private static BuildRecipe Freeze(BuildRecipe recipe) => recipe with
    {
        OutputPath = "", DismPath = "", OscdimgPath = "", Compression = default, KeepWorkingFiles = false,
        SourcePath = NormalizePath(recipe.SourcePath), WorkspacePath = NormalizePath(recipe.WorkspacePath),
        SelectedOptions = [.. recipe.SelectedOptions.Order(StringComparer.Ordinal)],
        ExpertActions = [.. recipe.ExpertActions.OrderBy(action => action.Kind).ThenBy(action => action.Identity, StringComparer.Ordinal)
            .ThenBy(action => action.Mode)]
    };

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant(); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string[] FrozenDifferences(BuildRecipe original, BuildRecipe requested)
    {
        var left = JsonSerializer.SerializeToNode(Freeze(original), JsonDefaults.Options)!.AsObject();
        var right = JsonSerializer.SerializeToNode(Freeze(requested), JsonDefaults.Options)!.AsObject();
        return left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key))
            .Where(name => left[name]?.ToJsonString() != right[name]?.ToJsonString()).ToArray();
    }

    private static IReadOnlyList<(string MountDirectory, string? ImageFile)> ParseMounted(string text)
    {
        var entries = new List<(string, string?)>();
        string? directory = null;
        string? file = null;
        foreach (var line in text.Split('\n'))
        {
            var mount = Regex.Match(line, @"^[ \t]*Mount Dir[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (mount.Success)
            {
                if (directory is not null) entries.Add((directory, file));
                directory = mount.Groups[1].Value;
                file = null;
                continue;
            }
            var image = Regex.Match(line, @"^[ \t]*Image File[ \t]*:[ \t]*(.+?)[ \t\r]*$");
            if (image.Success) file = image.Groups[1].Value;
        }
        if (directory is not null) entries.Add((directory, file));
        return entries;
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsUnder(string path, string directory)
    {
        try { return SamePath(path, directory) || PathSafety.IsInside(path, directory); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or InvalidDataException) { return false; }
    }
}
