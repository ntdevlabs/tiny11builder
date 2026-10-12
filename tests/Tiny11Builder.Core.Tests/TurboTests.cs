using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Tiny11Builder.Core;
using Xunit;
using Xunit.Abstractions;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class TurboTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false, BuildFlavor.Core, false, "/Compress:max")]
    [InlineData(true, false, false, BuildFlavor.Regular, false, "/Compress:max")]   // Turbo alone never starts a /Optimize probe
    [InlineData(true, true, true, BuildFlavor.Core, true, "/Compress:fast")]        // only an explicit host opt-in does
    [InlineData(false, true, false, BuildFlavor.Regular, false, "/Compress:max")]   // the opt-in cannot enable it without Turbo
    public void Turbo_changes_execution_not_final_compression_and_lazy_mount_is_opt_in(bool turbo, bool optIn, bool optimized,
        BuildFlavor flavor, bool fast, string working)
    {
        var recipe = new BuildRecipe { TurboMode = turbo, Flavor = flavor, Compression = Compression.Recovery };
        var plan = BuildExecutionPlan.Create(recipe, 16, optIn);
        Assert.Equal(optimized, plan.OptimizedMount);
        Assert.Equal(turbo, plan.BufferedLog);
        Assert.Equal(fast, plan.FastWorkingImage);
        Assert.Equal(working, plan.WorkingCompression("/Compress:max"));
        Assert.InRange(plan.CopyWorkers, 4, 8);
        Assert.Equal(Compression.Recovery, recipe.Compression);
        Assert.False(BuildExecutionPlan.Create(recipe, 16).OptimizedMount);
    }

    [Fact]
    public void Turbo_native_deletion_preserves_remaining_hard_link_attributes()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        var keep = Path.Combine(mount, @"Windows\System32\shared-runtime.dll");
        var removable = Path.Combine(mount, @"Windows\WinSxS\amd64_unused-component");
        Directory.CreateDirectory(removable);
        File.WriteAllText(keep, "shared fixture");
        var link = Path.Combine(removable, "shared-runtime.dll");
        if (!CreateHardLink(link, keep, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        File.SetAttributes(keep, FileAttributes.ReadOnly);
        using (var privileges = new ImageOperations.PrivilegeScope())
            ImageOperations.DeleteTree(removable, turboMode: true);
        Assert.False(Directory.Exists(removable));
        Assert.Equal("shared fixture", File.ReadAllText(keep));
        Assert.True((File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0);
        File.SetAttributes(keep, FileAttributes.Normal);
    }

    [Fact]
    public void Turbo_refuses_non_image_reparse_points_and_cancellation()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        var outside = Path.Combine(fixture.Root, "keep");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var link = Path.Combine(mount, "link");
        Directory.CreateSymbolicLink(link, outside);
        using var privileges = new ImageOperations.PrivilegeScope();
        Assert.Throws<InvalidDataException>(() => ImageOperations.DeleteTree(link, true));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageOperations.DeleteTree(outside, true, cancel.Token));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void Turbo_deletes_protected_long_path_files_without_changing_their_permissions()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        var directory = Path.Combine(mount, @"Windows\WinSxS", "amd64_payload_" + new string('a', 110));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, new string('b', 110) + ".dll");
        File.WriteAllText(file, "offline payload");
        var info = new FileInfo(file);
        var original = info.GetAccessControl();
        try
        {
            var restricted = new FileSecurity();
            restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            restricted.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            restricted.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            info.SetAccessControl(restricted);
            using var privileges = new ImageOperations.PrivilegeScope();
            ImageOperations.DeleteTree(directory, turboMode: true);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (File.Exists(file))
            {
                info.SetAccessControl(original);
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Native_file_deletion_benchmark_when_configured()
    {
        if (Environment.GetEnvironmentVariable("TINY11_TURBO_BENCHMARK") != "1") return;
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        var measurements = new List<object>();
        for (var run = 0; run < 3; run++)
            foreach (var turbo in new[] { false, true })
            {
                var directory = Path.Combine(mount, $"bench-{run}-{turbo}");
                for (var folder = 0; folder < 40; folder++)
                {
                    var child = Path.Combine(directory, folder.ToString());
                    Directory.CreateDirectory(child);
                    for (var file = 0; file < 100; file++)
                        File.WriteAllBytes(Path.Combine(child, file + ".tmp"), new byte[1024]);
                }
                var clock = Stopwatch.StartNew();
                using (var privileges = new ImageOperations.PrivilegeScope())
                    ImageOperations.DeleteTree(directory, turbo);
                clock.Stop();
                output.WriteLine($"Run {run + 1}, Turbo={turbo}: 4,000 files deleted in {clock.Elapsed.TotalMilliseconds:F1} ms");
                measurements.Add(new { Run = run + 1, Turbo = turbo, Files = 4000, Milliseconds = clock.Elapsed.TotalMilliseconds });
            }
        var report = Environment.GetEnvironmentVariable("TINY11_TURBO_REPORT");
        if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, JsonSerializer.Serialize(measurements, JsonDefaults.Options));
    }

    [Fact]
    public async Task Turbo_pipeline_preserves_integrity_batches_features_and_keeps_final_compression()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.InstalledCapabilities.AddRange(["App.StepsRecorder~~~~0.0.1.0", "MathRecognizer~~~~0.0.1.0"]);
        var result = await fixture.OptInOptimizedEngine.BuildAsync(fixture.Recipe with
        {
            TurboMode = true, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            Compression = Compression.Recovery, SelectedOptions = ["workfolders", "xps", "language-extras", "legacy-capabilities"],
            RemoveRedundantData = true, OmitNetFx3SetupPayload = true
        }, null, CancellationToken.None);
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Mount-Image") && call.Contains("/Optimize") && call.Contains("/CheckIntegrity"));
        Assert.All(fixture.Runner.Calls.Where(call => call.Contains("/Optimize")), call =>
            Assert.True(call.ToList().IndexOf("/Optimize") > call.ToList().IndexOf("/Mount-Image")));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Export-Image") && call.Contains("/Compress:fast") && call.Contains("/CheckIntegrity"));
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Export-Image") && call.Contains("/Compress:recovery") && call.Contains("/CheckIntegrity"));
        Assert.Single(fixture.Runner.Calls, call => call.Contains("/Disable-Feature") && call.Contains("/FeatureName:WorkFolders-Client") &&
            call.Contains("/FeatureName:Printing-XPSServices-Features"));
        // One verified capability batch for all three installed identities, then exactly one post-removal inventory.
        var removal = Assert.Single(fixture.Runner.Calls, call => call.Contains("/Remove-Capability"));
        Assert.Equal(3, removal.Count(arg => arg.StartsWith("/CapabilityName:", StringComparison.Ordinal)));
        Assert.Contains("/NoRestart", removal);
        var removalIndex = fixture.Runner.Calls.FindIndex(call => call.Contains("/Remove-Capability"));
        Assert.Contains("/Get-Capabilities", fixture.Runner.Calls[removalIndex + 1]);
        Assert.Contains("CleanRedundantData", fixture.Events);
        Assert.True(File.Exists(Path.Combine(result.WorkDirectory, "timings.json")));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.True(report.RootElement.GetProperty("TurboMode").GetBoolean());
        Assert.True(report.RootElement.GetProperty("OmitNetFx3SetupPayload").GetBoolean());
        Assert.Equal(3, report.RootElement.GetProperty("Capabilities").GetArrayLength());
        Assert.Equal(2, report.RootElement.GetProperty("OptionalFeatures").GetArrayLength());
    }

    [Fact]
    public async Task Failed_feature_batch_reports_every_member_as_failed_not_completed()
    {
        var events = new List<BuildProgress>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), _ => { });
        tracker.BeginStage("Optional features", "Remove features");
        await Assert.ThrowsAsync<IOException>(() => tracker.RunBatchAsync([("feature:A", "A"), ("feature:B", "B")],
            "Optional features", () => throw new IOException("Batch refused")));
        Assert.All(new[] { "feature:A", "feature:B" }, id =>
            Assert.Equal(BuildOperationState.Failed, events.Last(item => item.OperationId == id).State));
        Assert.DoesNotContain(events, item => item.OperationId?.StartsWith("feature:", StringComparison.Ordinal) == true &&
            item.State == BuildOperationState.Completed);
    }

    [Fact]
    public async Task Other_mount_errors_are_not_retried_as_success()
    {
        using var fixture = new BuildFixture();
        var runner = new DeniedMountRunner(fixture.Runner);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost(),
            new BuildEngineOptions(AllowOptimizedMount: true));
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            engine.BuildAsync(fixture.Recipe with { TurboMode = true }, null, CancellationToken.None));
        Assert.Contains("exit 5", failure.Message);
        Assert.Equal(1, runner.MountCalls);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Known_lazy_mount_incompatibilities_retry_normally_before_any_removal(bool mountRejected, bool accessRejected)
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailOptimizedMount = mountRejected;
        fixture.Runner.FailLazyAccess = accessRejected;
        var events = new List<BuildProgress>();
        var result = await fixture.OptInOptimizedEngine.BuildAsync(fixture.Recipe with { TurboMode = true },
            new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);
        Assert.Contains(events, item => item.OperationId == "mount:lazy-check" && item.State == BuildOperationState.Skipped);
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Mount-Image") && !call.Contains("/Optimize") &&
            call.Contains("/CheckIntegrity"));
        var mountPosition = fixture.Runner.Calls.FindIndex(call => call.Contains("/Mount-Image") && !call.Contains("/Optimize"));
        var removalPosition = fixture.Runner.Calls.FindIndex(call => call.Contains("/Remove-ProvisionedAppxPackage"));
        Assert.True(mountPosition < removalPosition);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.False(report.RootElement.GetProperty("UsedOptimizedMount").GetBoolean());
    }

    [Fact]
    public void Known_tool_version_cache_avoids_repeating_an_unsupported_lazy_mount()
    {
        using var fixture = new BuildFixture();
        var tool = ToolLocator.FindDism();
        Assert.False(MountOptimizationCache.IsUnavailable(fixture.Root, tool, _ => { }));
        MountOptimizationCache.RecordUnavailable(fixture.Root, tool, 4350);
        Assert.True(MountOptimizationCache.IsUnavailable(fixture.Root, tool, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MountOptimizationCache.RecordUnavailable(fixture.Root, tool, 5));
    }

    [Fact]
    public async Task Turbo_fast_output_and_cleanup_flags_do_not_weaken_integrity_checks()
    {
        using var fixture = new BuildFixture();
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with
        {
            TurboMode = true, Compression = Compression.Fast, RemoveRedundantData = true,
            OmitNetFx3SetupPayload = true, SelectedOptions = []
        }, null, CancellationToken.None);
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("/Export-Image") &&
            call.Any(arg => arg.EndsWith(@"\install.wim", StringComparison.Ordinal)) &&
            call.Contains("/Compress:fast") && call.Contains("/CheckIntegrity"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/ResetBase"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Get-ProvisionedAppxPackages") || call.Contains("/Get-Capabilities"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("/Online"));
        using var timings = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.WorkDirectory, "timings.json")));
        Assert.True(timings.RootElement.GetProperty("TotalMilliseconds").GetDouble() > 0);
        Assert.All(timings.RootElement.GetProperty("Stages").EnumerateArray(), stage =>
        {
            Assert.Equal("Completed", stage.GetProperty("State").GetString());
            Assert.True(stage.GetProperty("Milliseconds").GetDouble() >= 0);
        });
    }

    [Fact]
    public void Old_recipes_default_to_existing_behavior_and_new_execution_options_round_trip()
    {
        using var fixture = new BuildFixture();
        var file = Path.Combine(fixture.Root, "recipe.json");
        File.WriteAllText(file, """{"SchemaVersion":1,"SelectedOptions":[]}""");
        var original = BuildRecipe.Read(file);
        Assert.False(original.TurboMode);
        Assert.False(original.RemoveRedundantData);
        Assert.False(original.OmitNetFx3SetupPayload);
        var updated = original with { TurboMode = true, RemoveRedundantData = true, OmitNetFx3SetupPayload = true, Compression = Compression.Fast };
        updated.Write(file);
        var restored = BuildRecipe.Read(file);
        Assert.True(restored.TurboMode);
        Assert.True(restored.RemoveRedundantData);
        Assert.True(restored.OmitNetFx3SetupPayload);
        Assert.Equal(Compression.Fast, restored.Compression);
    }

    [Fact]
    public async Task Real_media_copy_benchmark_when_configured()
    {
        var iso = Environment.GetEnvironmentVariable("TINY11_TURBO_COPY_ISO");
        if (string.IsNullOrWhiteSpace(iso)) return;
        using var media = new MediaSourceFactory().Open(iso);
        var results = new List<object>();
        for (var round = 0; round < 3; round++)
            foreach (var workers in round % 2 == 0 ? new[] { 4, 8 } : new[] { 8, 4 })
            {
                using var fixture = new BuildFixture();
                var destination = Path.Combine(fixture.Root, "media-copy");
                Directory.CreateDirectory(destination);
                var clock = Stopwatch.StartNew();
                var copied = await MediaStager.CopyAsync(media.RootPath, destination, workers, false, _ => { }, CancellationToken.None);
                clock.Stop();
                output.WriteLine($"Round {round + 1}, {workers} workers: {copied.CopiedFiles} files, {copied.CopiedBytes} bytes, {clock.Elapsed.TotalMilliseconds:F0} ms");
                results.Add(new { Round = round + 1, Workers = workers, copied.CopiedFiles, copied.CopiedBytes, Milliseconds = clock.Elapsed.TotalMilliseconds });
            }
        using (var fixture = new BuildFixture())
        {
            var destination = Path.Combine(fixture.Root, "media-copy");
            Directory.CreateDirectory(destination);
            var omitted = await MediaStager.CopyAsync(media.RootPath, destination, 8, true, _ => { }, CancellationToken.None);
            output.WriteLine($"Optional .NET setup omission: {omitted.OmittedFiles} files, {omitted.OmittedBytes} bytes");
            results.Add(new { OmittedSetupFiles = omitted.OmittedFiles, OmittedSetupBytes = omitted.OmittedBytes });
        }
        var report = Environment.GetEnvironmentVariable("TINY11_TURBO_COPY_REPORT");
        if (!string.IsNullOrWhiteSpace(report)) File.WriteAllText(report, JsonSerializer.Serialize(results, JsonDefaults.Options));
    }

    [Fact]
    public async Task Real_Turbo_mount_staging_and_cleanup_benchmark_when_configured()
    {
        var wim = Environment.GetEnvironmentVariable("TINY11_TURBO_TEST_WIM");
        if (string.IsNullOrWhiteSpace(wim)) return;
        var configuredWorkspace = Environment.GetEnvironmentVariable("TINY11_TURBO_BENCHMARK_WORKSPACE");
        // Isolated by default: the advisory cache lives in this workspace, and a benchmark must never touch the real C:\tiny11 cache.
        var workspace = configuredWorkspace ?? Path.Combine(Path.GetTempPath(), "tiny11-benchmark-" + Guid.NewGuid().ToString("N"));
        var results = new List<object>();
        foreach (var optimized in new[] { false, true })
        {
            using var fixture = new BuildFixture();
            fixture.AllowCleanup = false;
            var mount = NativeTests.CreateOwnedMount(fixture.Root, fakeWindows: false);
            var tool = ToolLocator.FindDism();
            var useOptimized = optimized && !MountOptimizationCache.IsUnavailable(workspace, tool, output.WriteLine);
            var dism = new DismClient(tool, new ProcessRunner());
            output.WriteLine($"Benchmark job: {Path.GetDirectoryName(mount)}; optimized={optimized}");
            var index = int.TryParse(Environment.GetEnvironmentVariable("TINY11_TURBO_TEST_INDEX"), out var requested) ? requested : 1;
            var clock = Stopwatch.StartNew();
            var mounted = false;
            try
            {
                try
                {
                    await dism.RunAsync(CancellationToken.None, useOptimized ?
                        ["/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}", $"/MountDir:{mount}", "/Optimize", "/CheckIntegrity"] :
                        ["/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}", $"/MountDir:{mount}", "/CheckIntegrity"]);
                }
                catch (DismCommandException error) when (useOptimized && error.ExitCode == 87 &&
                    error.Output.Contains("optimize option is unknown", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine("Servicing tool rejected optimized mounting. Retrying standard mount.");
                    MountOptimizationCache.RecordUnavailable(workspace, tool, error.ExitCode);
                    useOptimized = false;
                    await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}",
                        $"/MountDir:{mount}", "/CheckIntegrity");
                }
                mounted = true;
                var mountMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                IReadOnlyDictionary<string, string> features;
                try { features = DismClient.ParseFeatures(await dism.RunAsync(CancellationToken.None, $"/Image:{mount}", "/Get-Features")); }
                catch (DismCommandException error) when (useOptimized && error.ExitCode == 4350)
                {
                    output.WriteLine("Image provider rejected optimized access. Discarding and retrying standard mount before changes.");
                    MountOptimizationCache.RecordUnavailable(workspace, tool, error.ExitCode);
                    await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
                    mounted = false;
                    useOptimized = false;
                    await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}",
                        $"/MountDir:{mount}", "/CheckIntegrity");
                    mounted = true;
                    features = DismClient.ParseFeatures(await dism.RunAsync(CancellationToken.None, $"/Image:{mount}", "/Get-Features"));
                }
                var firstReadMs = clock.Elapsed.TotalMilliseconds;
                Assert.NotEmpty(features);
                if (optimized)
                {
                    var targets = new[] { "WorkFolders-Client", "Printing-XPSServices-Features" }
                        .Where(features.ContainsKey).ToArray();
                    if (targets.Length > 1)
                    {
                        await dism.DisableFeaturesAsync(mount, targets, CancellationToken.None);
                        output.WriteLine("Verified real multi-feature removal batch.");
                    }
                }
                var cleanup = new OfflineDataCleaner().Clean(mount, output.WriteLine, CancellationToken.None);
                output.WriteLine($"RequestedOptimized={optimized}, UsedOptimized={useOptimized}: mount {mountMs:F0} ms; first feature query {firstReadMs:F0} ms; " +
                    $"redundant files {cleanup.Sum(item => item.Files)}, bytes {cleanup.Sum(item => item.LogicalBytes)}");
                results.Add(new { RequestedOptimized = optimized, UsedOptimized = useOptimized, MountMilliseconds = mountMs, FirstFeatureQueryMilliseconds = firstReadMs,
                    RemovedFiles = cleanup.Sum(item => item.Files), RemovedBytes = cleanup.Sum(item => item.LogicalBytes) });
            }
            finally
            {
                if (mounted)
                {
                    await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
                    fixture.AllowCleanup = true;
                }
            }
        }
        if (configuredWorkspace is null && Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        var report = Environment.GetEnvironmentVariable("TINY11_TURBO_MOUNT_REPORT");
        if (!string.IsNullOrWhiteSpace(report)) File.WriteAllText(report, JsonSerializer.Serialize(results, JsonDefaults.Options));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr security);

    private sealed class DeniedMountRunner(IProcessRunner inner) : IProcessRunner
    {
        public int MountCalls { get; private set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? callback,
            CancellationToken token, bool finishOnCancellation = true)
        {
            if (arguments.Contains("/Mount-Image"))
            {
                MountCalls++;
                return Task.FromResult(new ProcessResult(5, "Access denied."));
            }
            return inner.RunAsync(executable, arguments, callback, token, finishOnCancellation);
        }
    }
}
