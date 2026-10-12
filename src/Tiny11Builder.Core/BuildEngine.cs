using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private readonly IProcessRunner runner;
    private readonly IMediaSourceFactory mediaFactory;
    private readonly IOfflineRegistry registry;
    private readonly IImageOperations imageOperations;
    private readonly IBuildHost host;
    private readonly BuildEngineOptions options;

    public BuildEngine(IProcessRunner runner, IMediaSourceFactory mediaFactory, IOfflineRegistry registry,
        IImageOperations imageOperations, IBuildHost host, BuildEngineOptions? options = null)
    {
        this.runner = runner;
        this.mediaFactory = mediaFactory;
        this.registry = registry;
        this.imageOperations = imageOperations;
        this.host = host;
        this.options = options ?? new();
    }
    public async Task<IReadOnlyList<WindowsImage>> InspectAsync(string source, string dismPath, CancellationToken token)
    {
        PathSafety.RejectReparsePoints(source);
        using var media = mediaFactory.Open(PathSafety.Full(source));
        return await new DismClient(dismPath, runner).GetImagesAsync(InstallationMedia.ImageFile(media.RootPath), token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageInventoryItem>> InventoryAsync(BuildRecipe recipe,
        IProgress<BuildProgress>? progress, CancellationToken token)
    {
        recipe.ValidateOptions();
        PathSafety.ValidateWorkspace(recipe.SourcePath, recipe.WorkspacePath);
        host.ValidateTools(recipe with { OscdimgPath = "" });
        using var media = mediaFactory.Open(recipe.SourcePath);
        var dism = new DismClient(recipe.DismPath, runner);
        var sourceWim = InstallationMedia.ImageFile(media.RootPath);
        var image = await dism.GetImageAsync(sourceWim, recipe.ImageIndex, token).ConfigureAwait(false);
        host.ValidateImage(recipe with { OutputPath = "" }, image);
        // A read-only inventory never uses lazy mounting: it has no 87/4350 fallback, and a failed probe costs minutes.
        var execution = BuildExecutionPlan.Create(recipe, Environment.ProcessorCount, allowOptimizedMount: false);
        using var job = BuildJob.Create(recipe.WorkspacePath, "inventory", execution.BufferedLog);
        var scratch = Path.Combine(job.Directory, "scratch");
        Directory.CreateDirectory(scratch);
        var inventoryDism = new DismClient(recipe.DismPath, runner, text =>
        {
            job.Log(text);
            progress?.Report(new("Inventory", text));
        }, new(scratch, Path.Combine(job.Directory, "dism.log"), OptimizedMount: false));
        var mount = Path.Combine(job.Directory, "mount");
        Directory.CreateDirectory(mount);
        var attempted = false;
        try
        {
            progress?.Report(new("Inventory", "Mounting the image read-only to list its components."));
            var index = recipe.ImageIndex;
            var wim = sourceWim;
            if (Path.GetExtension(sourceWim).Equals(".esd", StringComparison.OrdinalIgnoreCase))
            {
                wim = Path.Combine(job.Directory, "working.wim");
                await inventoryDism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{sourceWim}", $"/SourceIndex:{index}",
                    $"/DestinationImageFile:{wim}", "/Compress:fast", "/CheckIntegrity").ConfigureAwait(false);
                index = 1;
            }
            attempted = true;
            await inventoryDism.RunAsync(token, "/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}",
                $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity").ConfigureAwait(false);
            var result = await ImageInventory.ReadAsync(inventoryDism, imageOperations, mount, token).ConfigureAwait(false);
            await inventoryDism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
            attempted = false;
            token.ThrowIfCancellationRequested();
            job.State("completed");
            PathSafety.DeleteOwnedDirectory(mount, job.Directory);
            PathSafety.DeleteOwnedDirectory(scratch, job.Directory);
            DeleteWorkingWim(job.Directory);
            return result;
        }
        catch (Exception error)
        {
            // Recovery first: nothing fallible (log, flush, job marker) may run before an owned mount is discarded.
            var cancelled = BuildFailureException.IsCancellationCause(error);
            var secondary = new List<Exception>();
            Exception? discardFailure = null;
            void Guard(Action step)
            {
                try { step(); }
                catch (Exception diagnostic) { secondary.Add(diagnostic); }
            }
            if (attempted)
            {
                if (cancelled) Guard(() => progress?.Report(new("Cancel cleanup", "Discarding the read-only working mount",
                    OperationId: "cancel:mount", State: BuildOperationState.Running)));
                discardFailure = await TryDiscardAsync(inventoryDism, mount).ConfigureAwait(false);
                if (discardFailure is not null) secondary.Add(discardFailure);
            }
            if (cancelled) Guard(() => progress?.Report(new("Cancel cleanup", "Saving logs and stopped-job status",
                OperationId: "cancel:logs", State: BuildOperationState.Running)));
            Guard(() => job.Log(error.Message));
            if (discardFailure is not null) Guard(() => job.Log(DiscardFailureText(discardFailure)));
            Guard(job.Flush);
            Guard(() => job.State(discardFailure is not null ? "failed" : cancelled ? "cancelled" : "failed",
                discardFailure is not null ? "Recovery required" : null));
            var kind = cancelled ? "cancelled" : "failed";
            throw new BuildFailureException(
                $"Inventory {kind}. The source was not changed. Logs and working files are retained at {job.Directory}. {error.Message}" +
                DiscardText(discardFailure, resumable: false) + SecondaryText(secondary, discardFailure),
                job.Directory, secondary.Count == 0 ? error : new AggregateException([error, .. secondary]),
                cancelled, discardFailure is not null);
        }
    }

    public async Task<BuildResult> BuildAsync(BuildRecipe recipe, IProgress<BuildProgress>? progress, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        PathSafety.Validate(recipe);
        if (string.IsNullOrEmpty(recipe.OscdimgPath)) throw new FileNotFoundException("Windows ADK oscdimg.exe is required to create a bootable ISO.");
        host.ValidateTools(recipe);
        using var media = mediaFactory.Open(recipe.SourcePath);
        var sourceWim = InstallationMedia.ImageFile(media.RootPath);
        var workingCompression = InstallationMedia.WorkingCompression(sourceWim);
        var execution = BuildExecutionPlan.Create(recipe, Environment.ProcessorCount, options.AllowOptimizedMount);
        var preparationNotes = new List<string>();
        if (execution.OptimizedMount && MountOptimizationCache.IsUnavailable(recipe.WorkspacePath, recipe.DismPath, preparationNotes.Add))
            execution = execution with { OptimizedMount = false };
        workingCompression = execution.WorkingCompression(workingCompression);
        var metadataDism = new DismClient(recipe.DismPath, runner);
        var image = await metadataDism.GetImageAsync(sourceWim, recipe.ImageIndex, token).ConfigureAwait(false);
        host.ValidateImage(recipe, image);
        var efiRelative = new[] { @"efi\microsoft\boot\efisys.bin", @"efi\microsoft\boot\efisys_noprompt.bin" }
            .FirstOrDefault(p => File.Exists(Path.Combine(media.RootPath, p))) ??
            throw new InvalidDataException("The source does not contain a supported UEFI boot image.");
        var sourceInstall = new FileInfo(sourceWim);
        using var job = BuildJob.Create(recipe.WorkspacePath, "build", execution.BufferedLog);
        var staging = Path.Combine(job.Directory, "media");
        var mount = Path.Combine(job.Directory, "mount");
        var workingWim = Path.Combine(job.Directory, "working.wim");
        var temporaryIso = Path.Combine(job.Directory, "output.iso");
        var reportPath = Path.Combine(job.Directory, "report.json");
        var scratch = Path.Combine(job.Directory, "scratch");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(scratch);
        IReadOnlyDictionary<string, string>? prefetchedFeatures = null;
        var tracking = new BuildOperationTracker(progress, job.Log);
        var dism = new DismClient(recipe.DismPath, runner, tracking.Output,
            new(scratch, Path.Combine(job.Directory, "dism.log"), execution.OptimizedMount));
        var session = new BuildSession
        {
            Recipe = recipe, Job = job, Tracking = tracking, CancellationProgress = progress, Dism = dism, Execution = execution, Image = image,
            EfiRelative = efiRelative, Staging = staging, Mount = mount, WorkingWim = workingWim, TemporaryIso = temporaryIso,
            ReportPath = reportPath, Scratch = scratch, Elapsed = elapsed, UsedOptimizedMount = execution.OptimizedMount,
            SetupPending = SetupInstaller.RequiresBootEdit(recipe)
        };
        void Log(string text) => session.Log(text);
        void Stage(string next, string description) => session.Stage(next, description, token);
        try
        {
            foreach (var note in preparationNotes)
            {
                job.Log(note);
                progress?.Report(new("Prepare", note));
            }
            recipe.Write(Path.Combine(job.Directory, JobCheckpoint.RecipeFileName));
            var servicingActions = recipe.ExpertActions.Where(action => action.Kind is ImageItemKind.Package or
                ImageItemKind.ProvisionedApp or ImageItemKind.Capability or ImageItemKind.Feature).ToArray();
            foreach (var (name, title) in new[]
            {
                ("Copy media", "Copy installation media"), ("Export edition", "Export selected edition"),
                ("Mount", "Mount working image"), ("Inventory", "Validate individual image actions"),
                ("Custom servicing", "Apply individual servicing actions"), ("Remove apps", "Remove selected apps"),
                ("Optional features", "Remove optional features and capabilities"),
                (".NET 3.5", "Include .NET Framework 3.5"), ("Windows settings", "Apply Windows settings"),
                ("Component cleanup", "Clean superseded component files"), ("Advanced payloads", "Remove selected payload files"),
                ("Core pruning", "Trim Core component payloads"), ("Redundant data", "Remove redundant offline data"),
                ("Commit", "Save image changes"),
                ("Setup image", "Apply Windows Setup settings"), ("Compress", "Compress the final image"),
                ("Create ISO", "Create bootable ISO"), ("Verify output", "Verify and publish the ISO")
            }.Where(entry => entry.Item1 switch
            {
                "Inventory" => recipe.ExpertActions.Length > 0,
                "Custom servicing" => servicingActions.Length > 0,
                ".NET 3.5" => recipe.EnableNetFx3,
                "Component cleanup" => recipe.SelectedOptions.Contains("cleanup") || recipe.Flavor == BuildFlavor.Core,
                "Core pruning" => recipe.Flavor == BuildFlavor.Core,
                "Redundant data" => recipe.RemoveRedundantData,
                "Setup image" => SetupInstaller.RequiresBootEdit(recipe),
                _ => true
            })) tracking.Plan("stage:" + name, name, title);
            Log($"Target: {image.Summary}. Source media is never modified.");
            Log($"Execution: {(recipe.TurboMode ? "Turbo" : "Standard")}; optimized mount={execution.OptimizedMount} " +
                $"({(!recipe.TurboMode ? "Turbo is off" : !options.AllowOptimizedMount ? "lazy mounting is opt-in and not enabled" : "host opt-in")}); " +
                $"copy workers={execution.CopyWorkers}; intermediate={workingCompression}.");
            Log("Policies are verified as offline registry values, not as guarantees of Windows behavior. Build/edition support varies.");
            if (image.Version.Build > 26100) Log("Newer/Insider builds require matching tools; no boot certification is implied.");
            if (recipe.Flavor == BuildFlavor.Core || recipe.ExpertActions.Length > 0)
                Log("WARNING: Core/expert output may not boot, update, recover, or provide antivirus protection. Test in an isolated VM.");
            Stage("Copy media", "Copying setup media, excluding the original multi-edition installation image.");
            session.StagingResult = await MediaStager.CopyAsync(media.RootPath, staging, execution.CopyWorkers,
                recipe.OmitNetFx3SetupPayload, Log, token).ConfigureAwait(false);
            Stage("Export edition", "Exporting only the selected edition into a writable WIM.");
            Log(execution.FastWorkingImage ?
                "Turbo: using fast intermediate compression to accelerate commit. Final compression remains as selected." :
                $"Intermediate compression: {workingCompression}. Existing WIM compression is retained to avoid recompressing the source.");
            await dism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{sourceWim}", $"/SourceIndex:{recipe.ImageIndex}",
                $"/DestinationImageFile:{workingWim}", workingCompression, "/CheckIntegrity").ConfigureAwait(false);
            Stage("Mount", "Mounting the working copy. Cancellation waits for active DISM operations.");
            session.Mounted = true;
            try
            {
                await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1", $"/MountDir:{mount}",
                    "/CheckIntegrity").ConfigureAwait(false);
            }
            catch (DismCommandException error) when (execution.OptimizedMount && error.ExitCode == 87 &&
                Regex.IsMatch(error.Output, @"optimize\s+option\s+is\s+unknown", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tracking.Skip("mount:lazy-check", "Mount", "Validate optimized mount",
                    "This servicing tool does not support optimized mounting. Retrying normally with integrity checks.");
                await DiscardMountAsync(dism, mount).ConfigureAwait(false);
                session.Mounted = false;
                token.ThrowIfCancellationRequested();
                dism.DisableOptimizedMount();
                session.UsedOptimizedMount = false;
                MountOptimizationCache.TryRecordUnavailable(recipe.WorkspacePath, recipe.DismPath, error.ExitCode, session.Warn);
                session.Mounted = true;
                await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1", $"/MountDir:{mount}",
                    "/CheckIntegrity").ConfigureAwait(false);
            }
            if (session.UsedOptimizedMount)
            {
                try
                {
                    prefetchedFeatures = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
                }
                catch (DismCommandException error) when (error.ExitCode == 4350)
                {
                    tracking.Skip("mount:lazy-check", "Mount", "Validate optimized mount",
                        "This host rejected lazy image access (DISM 4350). Retrying a normal mount before any image changes.");
                    await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
                    session.Mounted = false;
                    token.ThrowIfCancellationRequested();
                    dism.DisableOptimizedMount();
                    session.UsedOptimizedMount = false;
                    MountOptimizationCache.TryRecordUnavailable(recipe.WorkspacePath, recipe.DismPath, error.ExitCode, session.Warn);
                    await tracking.RunAsync("mount:normal-retry", "Mount", "Lazy mounting unavailable; mount normally with integrity checks",
                        async () =>
                        {
                            session.Mounted = true;
                            await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1",
                                $"/MountDir:{mount}", "/CheckIntegrity").ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    prefetchedFeatures = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
                }
            }
            IReadOnlyList<ImageInventoryItem> inventory = [];
            if (recipe.ExpertActions.Length > 0)
            {
                Stage("Inventory", "Revalidating expert actions against the actual mounted image.");
                inventory = await ImageInventory.ReadAsync(dism, imageOperations, mount, token).ConfigureAwait(false);
                ImageInventory.ValidateActions(recipe.ExpertActions, inventory);
            }
            if (servicingActions.Length > 0)
            {
                Stage("Custom servicing", "Applying selected package, app, capability and feature actions.");
                foreach (var action in servicingActions)
                    tracking.Plan($"expert:{action.Kind}:{action.Identity}", "Custom servicing", $"{action.Mode} {action.Kind}: {action.Identity}");
                foreach (var action in servicingActions)
                    await tracking.RunAsync($"expert:{action.Kind}:{action.Identity}", "Custom servicing",
                        $"{action.Mode} {action.Kind}: {action.Identity}",
                        () => ApplyDismActionAsync(dism, mount, action, token)).ConfigureAwait(false);
            }
            Stage("Remove apps", "Removing selected provisioned apps by exact identity.");
            var appOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.App && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            IReadOnlyList<ProvisionedApp> apps = appOptions.Length > 0 ?
                DismClient.ParseApps(await dism.RunAsync(token, $"/Image:{mount}", "/Get-ProvisionedAppxPackages").ConfigureAwait(false)) : [];
            var appsToRemove = apps.Where(app => appOptions.Any(option => CleanupCatalog.MatchesApp(option, app)))
                .DistinctBy(app => app.PackageName).ToArray();
            foreach (var app in appsToRemove) tracking.Plan("app:" + app.PackageName, "Remove apps", $"Remove {app.DisplayName} ({app.PackageName})");
            foreach (var option in appOptions)
            {
                var matching = apps.Where(a => CleanupCatalog.MatchesApp(option, a)).ToArray();
                if (matching.Length == 0) tracking.Skip("missing-app:" + option.Id, "Remove apps", option.Title, "Not present in this image");
            }
            foreach (var app in appsToRemove)
                await tracking.RunAsync("app:" + app.PackageName, "Remove apps", $"Remove {app.DisplayName} ({app.PackageName})",
                    () => dism.RunAsync(token, $"/Image:{mount}", "/Remove-ProvisionedAppxPackage", $"/PackageName:{app.PackageName}")).ConfigureAwait(false);
            Stage("Optional features", "Detecting actual features and capabilities; unavailable choices are reported, not guessed.");
            var featureOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.Feature && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            var capabilityOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.Capability && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            IReadOnlyDictionary<string, string> features = featureOptions.Length > 0 ?
                prefetchedFeatures is not null && servicingActions.Length == 0 ? prefetchedFeatures :
                    DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false)) :
                new Dictionary<string, string>();
            IReadOnlyList<ImageInventoryItem> capabilities = capabilityOptions.Length > 0 ?
                ImageInventory.ParseIdentityStates(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Capabilities").ConfigureAwait(false),
                    "Capability Identity", ImageItemKind.Capability) : [];
            var featureNames = featureOptions.SelectMany(o => o.Targets).Where(features.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var removalCapabilities = capabilities.Where(c => c.Detail.Equals("Installed", StringComparison.OrdinalIgnoreCase) &&
                capabilityOptions.Any(o => o.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                             c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))))
                .DistinctBy(c => c.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in featureNames) tracking.Plan("feature:" + name, "Optional features", $"Remove optional feature: {name}");
            foreach (var capability in removalCapabilities)
                tracking.Plan("capability:" + capability.Identity, "Optional features", $"Remove capability: {capability.Identity}");
            foreach (var option in featureOptions)
            {
                var matching = option.Targets.Where(features.ContainsKey).ToArray();
                if (matching.Length == 0) tracking.Skip("missing-feature:" + option.Id, "Optional features", option.Title, "Not present in this image");
            }
            var activeFeatures = featureNames.Where(feature => !features[feature].Equals("Disabled with Payload Removed", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var feature in featureNames.Except(activeFeatures, StringComparer.OrdinalIgnoreCase))
                tracking.Skip("feature:" + feature, "Optional features", feature, "Payload already removed");
            if (recipe.TurboMode && activeFeatures.Length > 1)
            {
                Log($"Turbo feature batch: {string.Join(", ", activeFeatures)}");
                FeatureBatchResult? featureBatch = null;
                await tracking.RunBatchAsync(activeFeatures.Select(feature => ("feature:" + feature, $"Remove optional feature: {feature}")).ToArray(),
                    "Optional features", async () =>
                    {
                        featureBatch = await dism.DisableFeaturesWithStatesAsync(mount, activeFeatures, token).ConfigureAwait(false);
                    },
                    detailFor: id => featureBatch?.Items.FirstOrDefault(item => string.Equals("feature:" + item.Name, id, StringComparison.Ordinal))
                        is { } verified ? Sentence(verified.Describe()) : null).ConfigureAwait(false);
                // Plain "Disabled" is reported as such, never as "payload removed": DISM keeps the payload of some features.
                foreach (var verified in featureBatch!.Items) Log($"Feature {verified.Name}: {verified.Describe()}.");
                session.Features.AddRange(featureBatch.Items);
            }
            else
                foreach (var feature in activeFeatures)
                    await tracking.RunAsync("feature:" + feature, "Optional features", $"Remove optional feature: {feature}",
                        () => dism.RunAsync(token, $"/Image:{mount}", "/Disable-Feature", $"/FeatureName:{feature}", "/Remove", "/NoRestart")).ConfigureAwait(false);
            foreach (var option in capabilityOptions)
            {
                if (!removalCapabilities.Any(c => option.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                                         c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))))
                    tracking.Skip("missing-capability:" + option.Id, "Optional features", option.Title, "No matching installed capability");
            }
            if (recipe.TurboMode && removalCapabilities.Length > 0)
            {
                var capabilityNames = removalCapabilities.Select(c => c.Identity).ToArray();
                Log($"Turbo capability batch (one command with repeated /CapabilityName): {string.Join(", ", capabilityNames)}");
                BatchRemovalResult? capabilityBatch = null;
                await tracking.RunBatchAsync(removalCapabilities.Select(c => ("capability:" + c.Identity, $"Remove capability: {c.Identity}")).ToArray(),
                    "Optional features", async () =>
                    {
                        capabilityBatch = await dism.RemoveCapabilitiesAsync(mount, capabilityNames, token).ConfigureAwait(false);
                    }, "capabilities",
                    id => capabilityBatch?.Items.FirstOrDefault(item => string.Equals("capability:" + item.Name, id, StringComparison.OrdinalIgnoreCase))
                        is { } verified ? Sentence("verified " + verified.Describe()) : null).ConfigureAwait(false);
                foreach (var verified in capabilityBatch!.Items) Log($"Verified capability removal: {verified.Name} => {verified.Describe()}");
                session.Capabilities.AddRange(capabilityBatch.Items);
            }
            else
                foreach (var capability in removalCapabilities)
                    await tracking.RunAsync("capability:" + capability.Identity, "Optional features", $"Remove capability: {capability.Identity}",
                        () => dism.RunAsync(token, $"/Image:{mount}", "/Remove-Capability", $"/CapabilityName:{capability.Identity}", "/NoRestart")).ConfigureAwait(false);
            if (recipe.EnableNetFx3)
            {
                Stage(".NET 3.5", "Enabling .NET 3.5 from matching source media, without Windows Update.");
                var sxs = Path.Combine(media.RootPath, @"sources\sxs");
                if (!Directory.Exists(sxs)) throw new DirectoryNotFoundException("The source has no sources\\sxs payload for .NET 3.5.");
                await dism.RunAsync(token, $"/Image:{mount}", "/Enable-Feature", "/FeatureName:NetFx3", "/All",
                    "/LimitAccess", $"/Source:{sxs}", "/NoRestart").ConfigureAwait(false);
            }
            Stage("Windows settings", "Applying selected registry settings to the offline image.");
            await tracking.RunAsync("registry:settings", "Windows settings", "Apply and verify offline Windows settings",
                () =>
                {
                    registry.Apply(mount, recipe, Log);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(staging, "autounattend.xml"), UnattendBuilder.Create(image, recipe));
            if (recipe.SelectedOptions.Contains("cleanup") || recipe.Flavor == BuildFlavor.Core)
            {
                Stage("Component cleanup", recipe.Flavor == BuildFlavor.Core ?
                    "Cleaning and resetting the component base before Core payload pruning." : "Cleaning superseded components without ResetBase.");
                await dism.RunAsync(token, recipe.Flavor == BuildFlavor.Core ?
                    [$"/Image:{mount}", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"] :
                    [$"/Image:{mount}", "/Cleanup-Image", "/StartComponentCleanup"]).ConfigureAwait(false);
            }
            Stage("Advanced payloads", "Applying original-builder and expert payload removals to the offline copy only.");
            imageOperations.RemoveOriginalFiles(mount, image.Architecture, recipe.SelectedOptions, Log, recipe.TurboMode);
            if (recipe.Flavor == BuildFlavor.Core)
            {
                Stage("Core pruning", "Trimming Core component payloads and recovery files.");
                imageOperations.TrimCore(mount, image.Architecture, Log, token, recipe.TurboMode);
            }
            imageOperations.ApplyNativeActions(mount, recipe.ExpertActions, Log, recipe.TurboMode);
            if (recipe.RemoveRedundantData)
            {
                Stage("Redundant data", "Removing offline cache, archived logs and crash reports.");
                session.Cleanup = imageOperations.CleanRedundantData(mount, Log, token);
                foreach (var item in session.Cleanup.Where(item => item.State == BuildOperationState.Skipped))
                    tracking.Skip("cleanup:" + item.Path, "Redundant data", $"Clean {item.Path}", item.Detail);
            }
            Stage("Commit", "Committing the modified working image.");
            await dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity").ConfigureAwait(false);
            session.Mounted = false;
            // The checkpoint is durable before any later stage can observe a cancellation request.
            await SaveCommittedCheckpointAsync(session, new SourceIdentity
            {
                Location = recipe.SourcePath, ImageIndex = recipe.ImageIndex, InstallFile = Path.GetFileName(sourceWim),
                InstallLength = sourceInstall.Length, InstallLastWriteUtc = new(sourceInstall.LastWriteTimeUtc, TimeSpan.Zero)
            }).ConfigureAwait(false);
            return await RunTailAsync(session, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw await FailAsync(session, error).ConfigureAwait(false);
        }
    }
    public Task RecoverAsync(string jobFile, string dismPath, CancellationToken token) =>
        RecoverAsync(jobFile, dismPath, token, discardCommitted: false);

    /// <summary>
    /// Discards an interrupted job's mounted working copy and staged media. A job holding verified saved progress is
    /// refused unless <paramref name="discardCommitted"/> explicitly accepts losing it; Resume is the way to keep it.
    /// Jobs from before checkpoints existed are never adopted or deleted automatically.
    /// </summary>
    public async Task RecoverAsync(string jobFile, string dismPath, CancellationToken token, bool discardCommitted)
    {
        jobFile = PathSafety.Full(jobFile);
        var directory = Path.GetDirectoryName(jobFile)!;
        var leaf = Path.GetFileName(directory);
        PathSafety.RejectReparsePoints(jobFile);
        if (Path.GetFileName(jobFile) != "job.json" || !leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _))
            throw new InvalidDataException("Select job.json inside a tiny11-owned job directory.");
        var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(jobFile), JsonDefaults.Options)
            ?? throw new InvalidDataException("Invalid job marker.");
        if (state.Id != leaf[7..] || state.Status == "completed") throw new InvalidDataException("This is not an interrupted job.");
        PathSafety.RejectReparsePoints(Path.Combine(directory, "job.lock"));
        // An active build holds this lock, including across separate app instances.
        using var jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Refuse before anything is touched: a checkpoint means a verified committed image that Resume can finish.
        var checkpointFile = Path.Combine(directory, JobCheckpoint.FileName);
        PathSafety.RejectReparsePoints(checkpointFile);
        var checkpointed = File.Exists(checkpointFile);
        if (checkpointed && !discardCommitted)
            throw new CheckpointedJobException("This job holds verified saved progress (a committed Windows image and its staged setup files). " +
                "Use Resume to finish it without repeating that work. Recovery would permanently delete the saved image, so it needs explicit confirmation.");
        if (OperatingSystem.IsWindows()) OfflineHive.RecoverJob(directory);
        var mount = Path.Combine(directory, "mount");
        var dism = new DismClient(dismPath, runner);
        if (await IsMountedAsync(dism, mount).ConfigureAwait(false))
            await dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // The checkpoint leaves its resumable name BEFORE the data it describes is deleted, so no resume can see a half-deleted job.
        if (checkpointed) CheckpointStore.Retire(directory, "checkpoint.discarded.json");
        foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
            PathSafety.DeleteOwnedDirectory(Path.Combine(directory, name), directory);
        DeleteWorkingWim(directory);
        File.WriteAllText(jobFile, JsonSerializer.Serialize(state with { Status = "recovered", Stage = "Discarded working copy" }, JsonDefaults.Options));
    }

    private static async Task ApplyDismActionAsync(DismClient dism, string mount, ExpertAction action, CancellationToken token)
    {
        var args = action.Kind switch
        {
            ImageItemKind.ProvisionedApp => new[] { "/Remove-ProvisionedAppxPackage", $"/PackageName:{action.Identity}" },
            ImageItemKind.Package => ["/Remove-Package", $"/PackageName:{action.Identity}", "/NoRestart"],
            ImageItemKind.Capability => ["/Remove-Capability", $"/CapabilityName:{action.Identity}", "/NoRestart"],
            ImageItemKind.Feature => action.Mode == ExpertActionMode.Remove ?
                ["/Disable-Feature", $"/FeatureName:{action.Identity}", "/Remove", "/NoRestart"] :
                ["/Disable-Feature", $"/FeatureName:{action.Identity}", "/NoRestart"],
            _ => throw new InvalidDataException("Not a DISM action.")
        };
        await dism.RunAsync(token, [$"/Image:{mount}", .. args]).ConfigureAwait(false);
    }

    /// <summary>Discards an owned mount without throwing: the caller records a failure as a secondary problem and demands recovery.</summary>
    private static async Task<Exception?> TryDiscardAsync(DismClient dism, string mount)
    {
        try
        {
            await DiscardMountAsync(dism, mount).ConfigureAwait(false);
            return null;
        }
        catch (Exception cleanup)
        {
            return cleanup;
        }
    }

    private static async Task DiscardMountAsync(DismClient dism, string mount)
    {
        if (await IsMountedAsync(dism, mount).ConfigureAwait(false))
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
    }

    private static string DiscardFailureText(Exception discardFailure) =>
        $"DISCARD FAILED: {discardFailure.Message}. Use Recover workspace with this job.json; never use global DISM cleanup.";

    private static string DiscardText(Exception? discardFailure, bool resumable) => discardFailure is null ? "" : resumable ?
        $" Its mounted Setup image could not be discarded ({discardFailure.Message}). Resume tries to discard it again before it continues; " +
        "never use global DISM cleanup." :
        $" Recovery is required: its mounted image could not be discarded ({discardFailure.Message}). " +
        "Choose Recover workspace for this job; never use global DISM cleanup.";

    private static string SecondaryText(IReadOnlyList<Exception> secondary, Exception? discardFailure)
    {
        var diagnostics = secondary.Where(problem => !ReferenceEquals(problem, discardFailure)).Select(problem => problem.Message).ToArray();
        return diagnostics.Length == 0 ? "" : " Additional problems while recording the failure: " + string.Join("; ", diagnostics);
    }

    private static string Sentence(string text) => char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static async Task<bool> IsMountedAsync(DismClient dism, string mount)
    {
        var text = await dism.RunAsync(CancellationToken.None, "/Get-MountedWimInfo").ConfigureAwait(false);
        return Regex.Matches(text, @"(?m)^[ \t]*Mount Dir[ \t]*:[ \t]*(.+?)[ \t\r]*$")
            .Any(m => Path.GetFullPath(m.Groups[1].Value).TrimEnd('\\')
                .Equals(Path.GetFullPath(mount).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteWorkingWim(string jobDirectory)
    {
        var file = Path.Combine(jobDirectory, "working.wim");
        if (File.Exists(file)) File.Delete(file);
    }
}
