using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class BuildEngineTests
{
    [Theory]
    [InlineData(22621, "x64", Compression.Recovery)]
    [InlineData(22631, "arm64", Compression.Maximum)]
    [InlineData(26300, "x64", Compression.Recovery)]
    [InlineData(29689, "arm64", Compression.Recovery)]
    public async Task Pipeline_exports_selected_edition_uses_actual_build_and_keeps_source(int build, string arch, Compression compression)
    {
        using var fixture = new BuildFixture(build, arch);
        var original = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\install.wim"));
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with { Compression = compression }, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.Equal(64, result.Sha256.Length);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\install.wim")));
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Export-Image") && c.Contains("/SourceIndex:3"));
        Assert.Single(fixture.Runner.Calls, c => c.Contains("/Get-WimInfo") &&
            c.Contains("/WimFile:" + Path.Combine(fixture.Source, @"sources\install.wim")));
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Export-Image") && c.Contains("/SourceIndex:1") &&
            c.Any(a => a.EndsWith(compression == Compression.Recovery ? "install.esd" : "install.wim")));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c.Contains("/ResetBase"));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c.Any(a => a.StartsWith("/Image:") &&
            a[7..].Equals(fixture.Source, StringComparison.OrdinalIgnoreCase)));
        Assert.False(Directory.Exists(Path.Combine(result.WorkDirectory, "media")));
        Assert.Equal("completed", JsonSerializer.Deserialize<JobState>(File.ReadAllText(Path.Combine(result.WorkDirectory, "job.json")), JsonDefaults.Options)!.Status);
    }

    [Fact]
    public async Task Core_finishes_all_servicing_before_pruning_and_requires_no_servicing_afterward()
    {
        using var fixture = new BuildFixture();
        await fixture.Engine.BuildAsync(fixture.Recipe with { Flavor = BuildFlavor.Core, CoreAcknowledged = true }, null, CancellationToken.None);
        var reset = fixture.Events.FindIndex(e => e.Contains("/ResetBase"));
        var prune = fixture.Events.IndexOf("TrimCore");
        Assert.True(reset >= 0 && prune > reset);
        Assert.DoesNotContain(fixture.Events.Skip(prune + 1), e => e.Contains("/Cleanup-Image") || e.Contains("/Disable-Feature"));
        Assert.True(fixture.Registry.LastRecipe?.Flavor == BuildFlavor.Core);
    }

    [Fact]
    public async Task Missing_optional_features_are_reported_and_never_guessed()
    {
        using var fixture = new BuildFixture();
        var messages = new List<BuildProgress>();
        await fixture.Engine.BuildAsync(fixture.Recipe with { SelectedOptions = ["recall-feature"] },
            new ImmediateProgress<BuildProgress>(messages.Add), CancellationToken.None);
        Assert.Contains(messages, m => m.Message == "Recall optional feature" && m.State == BuildOperationState.Skipped &&
            m.Detail == "Not present in this image");
        Assert.DoesNotContain(fixture.Runner.Calls, c => c.Contains("/FeatureName:Recall"));
    }

    [Fact]
    public async Task Custom_protected_services_and_component_payloads_are_allowed_after_acknowledgement()
    {
        using var fixture = new BuildFixture();
        var actions = new[] { new ExpertAction(ImageItemKind.Service, "BootCriticalDriver", ExpertActionMode.Remove),
            new ExpertAction(ImageItemKind.ComponentPayload, "amd64_critical-component", ExpertActionMode.Remove) };
        await fixture.Engine.BuildAsync(fixture.Recipe with { ExpertAcknowledged = true, ExpertActions = actions }, null, CancellationToken.None);
        Assert.Equal(actions, fixture.Operations.LastActions);
    }

    [Fact]
    public async Task Protected_package_refusal_is_a_failure_not_a_success_shaped_fallback()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailPackages = true;
        var action = new ExpertAction(ImageItemKind.Package, "Protected-Package~31bf~amd64~~1.0", ExpertActionMode.Remove);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(
            fixture.Recipe with { ExpertAcknowledged = true, ExpertActions = [action] }, null, CancellationToken.None));
        Assert.Contains("DISM failed", failure.Message);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Unmount-Image") && c.Contains("/Discard"));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "build.log")));
    }

    [Fact]
    public async Task Cancellation_waits_for_mount_completion_then_discards_only_its_own_mount()
    {
        using var fixture = new BuildFixture();
        using var cancel = new CancellationTokenSource();
        fixture.Runner.OnCommand = args => { if (args.Contains("/Mount-Image")) cancel.Cancel(); };
        await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, cancel.Token));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        var discard = Assert.Single(fixture.Runner.Calls, c => c.Contains("/Discard"));
        Assert.Contains(discard, a => a.StartsWith("/MountDir:" + fixture.Root, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c.Contains("/Cleanup-Wim") || c.Contains("/Cleanup-Mountpoints"));
    }

    [Fact]
    public async Task Failed_iso_tool_never_publishes_output()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        await Assert.ThrowsAsync<BuildFailureException>(() => fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public async Task Hardware_bypass_modifies_setup_image_and_uses_UEFI_only_for_arm64()
    {
        using var fixture = new BuildFixture(29689, "arm64");
        await fixture.Engine.BuildAsync(fixture.Recipe with { SelectedOptions = ["hardware-bypass"] }, null, CancellationToken.None);
        Assert.True(fixture.Registry.SetupCalled);
        Assert.Contains(fixture.Runner.Calls, c => c.Any(a => a.StartsWith("-bootdata:1#pEF", StringComparison.Ordinal)));
        Assert.Equal(2, fixture.Runner.Calls.Count(c => c.Contains("/Mount-Image")));
    }

    [Fact]
    public async Task Recipe_round_trip_preserves_exact_choices()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { Flavor = BuildFlavor.Core, CoreAcknowledged = true,
            ExpertAcknowledged = true, ExpertActions = [new(ImageItemKind.Service, "Driver", ExpertActionMode.Disable)] };
        var file = Path.Combine(fixture.Root, "recipe.json");
        recipe.Write(file);
        var read = BuildRecipe.Read(file);
        Assert.Equal(recipe.SelectedOptions, read.SelectedOptions);
        Assert.Equal(recipe.ExpertActions, read.ExpertActions);
        Assert.Equal(recipe.Flavor, read.Flavor);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Recovery_refuses_completed_foreign_or_locked_jobs()
    {
        using var fixture = new BuildFixture();
        var result = await fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.RecoverAsync(
            Path.Combine(result.WorkDirectory, "job.json"), "dism.exe", CancellationToken.None));
        var foreign = Path.Combine(fixture.Root, "job.json");
        File.WriteAllText(foreign, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.RecoverAsync(foreign, "dism.exe", CancellationToken.None));
        var id = Guid.NewGuid().ToString("N");
        var active = Path.Combine(fixture.Root, "tiny11-" + id);
        Directory.CreateDirectory(active);
        var marker = Path.Combine(active, "job.json");
        File.WriteAllText(marker, JsonSerializer.Serialize(new JobState(id, "build", "running", "Mount", DateTimeOffset.UtcNow), JsonDefaults.Options));
        using var jobLock = new FileStream(Path.Combine(active, "job.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.RecoverAsync(marker, "dism.exe", CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_discards_the_interrupted_job_and_keeps_logs_recipe_and_source()
    {
        using var fixture = new BuildFixture();
        fixture.Runner.FailIso = true;
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        var marker = Path.Combine(failure.WorkDirectory, "job.json");
        // The failed ISO step leaves verified saved progress, so Recovery refuses until discarding it is explicit.
        var refused = await Assert.ThrowsAsync<CheckpointedJobException>(() =>
            fixture.Engine.RecoverAsync(marker, fixture.Recipe.DismPath, CancellationToken.None));
        Assert.Contains("Resume", refused.Message);
        Assert.True(Directory.Exists(Path.Combine(failure.WorkDirectory, "media")));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, JobCheckpoint.FileName)));
        await fixture.Engine.RecoverAsync(marker, fixture.Recipe.DismPath, CancellationToken.None, discardCommitted: true);
        Assert.Equal("recovered", JsonSerializer.Deserialize<JobState>(File.ReadAllText(marker), JsonDefaults.Options)!.Status);
        Assert.False(Directory.Exists(Path.Combine(failure.WorkDirectory, "media")));
        Assert.False(File.Exists(Path.Combine(failure.WorkDirectory, JobCheckpoint.FileName)));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "checkpoint.discarded.json")));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "build.log")));
        Assert.True(File.Exists(Path.Combine(failure.WorkDirectory, "recipe.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Source, @"sources\install.wim")));
    }

    [Fact]
    public async Task Inventory_rejects_system_workspaces_before_any_mount()
    {
        using var fixture = new BuildFixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Engine.InventoryAsync(
            fixture.Recipe with { WorkspacePath = Environment.GetFolderPath(Environment.SpecialFolder.Windows) },
            null, CancellationToken.None));
        Assert.Empty(fixture.Runner.Calls);
    }
}

internal sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

[SupportedOSPlatform("windows")]
internal sealed class BuildFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tiny11-tests-" + Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source");
    public BuildRecipe Recipe { get; }
    public List<string> Events { get; } = [];
    public FakeRunner Runner { get; }
    public FakeRegistry Registry { get; } = new();
    public FakeOperations Operations { get; }
    public BuildEngine Engine { get; }
    // Lazy (/Optimize) mounting is a host opt-in; only tests of that seam use this engine.
    public BuildEngine OptInOptimizedEngine => new(Runner, new FakeMediaFactory(), Registry, Operations, new FakeHost(),
        new BuildEngineOptions(AllowOptimizedMount: true));
    public bool AllowCleanup { get; set; } = true;
    public BuildFixture(int build = 29689, string architecture = "x64")
    {
        Directory.CreateDirectory(Path.Combine(Source, "sources"));
        Directory.CreateDirectory(Path.Combine(Source, @"efi\microsoft\boot"));
        Directory.CreateDirectory(Path.Combine(Source, "boot"));
        var header = new byte[64];
        "MSWIM\0\0\0"u8.CopyTo(header);
        BitConverter.GetBytes(0x00040082u).CopyTo(header, 16);
        File.WriteAllBytes(Path.Combine(Source, @"sources\install.wim"), header);
        File.WriteAllBytes(Path.Combine(Source, @"sources\boot.wim"), FakeWim.Create(images: 2, seed: 4));
        File.WriteAllBytes(Path.Combine(Source, @"efi\microsoft\boot\efisys.bin"), [7]);
        File.WriteAllBytes(Path.Combine(Source, @"boot\etfsboot.com"), [8]);
        Recipe = new() { SourcePath = Source, ImageIndex = 3, WorkspacePath = Path.Combine(Root, "workspace"),
            OutputPath = Path.Combine(Root, "output.iso"), DismPath = @"C:\mock\dism.exe",
            OscdimgPath = @"C:\mock\oscdimg.exe", SelectedOptions = ["copilot-app", "cleanup"] };
        Runner = new(build, architecture, Events);
        Operations = new(Events);
        Engine = new(Runner, new FakeMediaFactory(), Registry, Operations, new FakeHost());
    }
    public void Dispose() { if (AllowCleanup && Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    public static byte[] IsoBytes(int length = 65536)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes("CD001").CopyTo(bytes, 16 * 2048 + 1);
        return bytes;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class FakeRunner(int build, string architecture, List<string> events) : IProcessRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public bool FailPackages { get; set; }
    public bool FailIso { get; set; }
    public bool FailOptimizedMount { get; set; }
    public bool FailLazyAccess { get; set; }
    public bool FailCommit { get; set; }
    public bool FailDiscard { get; set; }
    public Action<IReadOnlyList<string>>? OnCommand { get; set; }
    public List<string> InstalledCapabilities { get; } = ["Language.OCR~~~en-US~0.0.1.0"];
    public string CapabilityStateAfterRemoval { get; set; } = "Not Present";
    public string FeatureStateAfterRemoval { get; set; } = "Disabled with Payload Removed";
    public bool IgnoreCapabilityRemoval { get; set; }
    // Reported by /Get-MountedWimInfo regardless of what this runner mounted (an interrupted, crashed earlier run).
    public (string MountDirectory, string ImageFile)? StaleMount { get; set; }
    private readonly HashSet<string> removedCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private string? mount;
    private string? mountedImage;
    private int exports;
    private bool lazyMount;
    private readonly HashSet<string> removedFeatures = new(StringComparer.OrdinalIgnoreCase);
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, Action<string>? output,
        CancellationToken token, bool finishOnCancellation = true)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add(args.ToArray());
        events.Add(string.Join(" ", args));
        string Value(string prefix) => args.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        if (args.Contains("/Mount-Image"))
        {
            if (args.Contains("/Optimize") && FailOptimizedMount)
                return Task.FromResult(new ProcessResult(87, "The optimize option is unknown."));
            mount = Value("/MountDir:");
            mountedImage = Value("/ImageFile:");
            lazyMount = args.Contains("/Optimize");
            if (mountedImage.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(mount, "sources"));
                Directory.CreateDirectory(Path.Combine(mount, @"Windows\System32\config"));
                File.WriteAllText(Path.Combine(mount, @"sources\setup.exe"), "classic Setup fixture");
                File.WriteAllText(Path.Combine(mount, @"Windows\System32\config\SYSTEM"), "fixture");
            }
        }
        if (args.Contains("/Unmount-Image") && args.Contains("/Commit") && FailCommit)
            return Task.FromResult(new ProcessResult(5, "The commit was refused.")); // the image stays mounted, as with real DISM
        if (args.Contains("/Unmount-Image") && args.Contains("/Discard") && FailDiscard)
            return Task.FromResult(new ProcessResult(1, "The mount could not be discarded.")); // the image stays mounted
        if (args.Contains("/Unmount-Image")) { mount = null; mountedImage = null; lazyMount = false; }
        if (args.Contains("/Unmount-Image") && StaleMount is { } leftover && Value("/MountDir:") == leftover.MountDirectory) StaleMount = null;
        if (args.Contains("/Disable-Feature"))
            foreach (var feature in args.Where(arg => arg.StartsWith("/FeatureName:", StringComparison.Ordinal)))
                removedFeatures.Add(feature["/FeatureName:".Length..]);
        if (args.Contains("/Remove-Capability") && !IgnoreCapabilityRemoval)
            foreach (var name in args.Where(arg => arg.StartsWith("/CapabilityName:", StringComparison.Ordinal)))
                removedCapabilities.Add(name["/CapabilityName:".Length..]);
        OnCommand?.Invoke(args);
        if (args.Contains("/Unmount-Image"))
        {
            var directory = Value("/MountDir:");
            foreach (var relative in new[] { @"sources\setup.exe", @"Windows\System32\winpeshl.ini", @"Windows\System32\config\SYSTEM" })
                if (File.Exists(Path.Combine(directory, relative))) File.Delete(Path.Combine(directory, relative));
            foreach (var relative in new[] { @"sources", @"Windows\System32\config", @"Windows\System32", "Windows" })
            {
                var entry = Path.Combine(directory, relative);
                if (Directory.Exists(entry) && !Directory.EnumerateFileSystemEntries(entry).Any()) Directory.Delete(entry);
            }
        }
        if (args.Contains("/Get-MountedWimInfo"))
        {
            var listed = new StringBuilder();
            if (StaleMount is { } stale) listed.Append($"Mount Dir : {stale.MountDirectory}\nImage File : {stale.ImageFile}\nImage Index : 1\n\n");
            if (mount is not null) listed.Append($"Mount Dir : {mount}\nImage File : {mountedImage}\nImage Index : 1\n");
            return Result(listed.ToString());
        }
        if (args.Contains("/Get-WimInfo"))
        {
            var file = Value("/WimFile:");
            var boot = file.EndsWith("boot.wim", StringComparison.OrdinalIgnoreCase);
            if (!args.Any(a => a.StartsWith("/Index:")))
                return Result(boot ? "Index : 1\nIndex : 2\n" : file.Contains(@"\source\") ? "Index : 3\n" : "Index : 1\n");
            return Result($"Deployment Image Servicing and Management tool\nVersion: 10.0.99999.1000\n" +
                $"Name : {(boot ? "Microsoft Windows Setup" : "Windows 11 Pro")}\nDescription : Image\n" +
                $"Architecture : {architecture}\nVersion : 10.0.{build}\nServicePack Build : 123\n" +
                $"Edition : Professional\nInstallation : Client\nSize : 16,000,000,000 bytes\n");
        }
        if (args.Contains("/Export-Image"))
            File.WriteAllBytes(Value("/DestinationImageFile:"), FakeWim.Create(seed: (byte)(0x20 + exports++)));
        if (args.Contains("/Get-ProvisionedAppxPackages")) return Result(
            "DisplayName : Microsoft.Copilot\nPackageName : Microsoft.Copilot_1.0_x64__8weky\n");
        if (args.Contains("/Get-Features") && lazyMount && FailLazyAccess)
            return Task.FromResult(new ProcessResult(4350, "The request is not supported."));
        if (args.Contains("/Get-Features")) return Result("Feature Name : NetFx3\nState : Disabled\n" +
            $"Feature Name : WorkFolders-Client\nState : {(removedFeatures.Contains("WorkFolders-Client") ? FeatureStateAfterRemoval : "Enabled")}\n" +
            $"Feature Name : Printing-XPSServices-Features\nState : {(removedFeatures.Contains("Printing-XPSServices-Features") ? FeatureStateAfterRemoval : "Enabled")}\n");
        if (args.Contains("/Get-Capabilities"))
        {
            var report = string.Concat(InstalledCapabilities.Select(name =>
                $"Capability Identity : {name}\nState : {(removedCapabilities.Contains(name) ? CapabilityStateAfterRemoval : "Installed")}\n"));
            return Result(report + "Capability Identity : Browser.InternetExplorer~~~~0.0.11.0\nState : Not Present\n");
        }
        if (args.Contains("/Get-Packages")) return Result("Package Identity : Protected-Package~31bf~amd64~~1.0\nState : Installed\n");
        if (args.Contains("/Remove-Package") && FailPackages) return Task.FromResult(new ProcessResult(50, "Protected package removal refused"));
        if (executable.EndsWith("oscdimg.exe"))
        {
            if (FailIso) return Task.FromResult(new ProcessResult(1, "No ISO created"));
            File.WriteAllBytes(args[^1], BuildFixture.IsoBytes());
        }
        return Result("The operation completed successfully.");
    }
    private static Task<ProcessResult> Result(string output) => Task.FromResult(new ProcessResult(0, output));
}
internal sealed class FakeMediaFactory : IMediaSourceFactory { public IMediaSource Open(string path) => new FolderMedia(path); }

/// <summary>Valid WIM headers (signature, size, image count, flags) so production checkpoint validation is never bypassed.</summary>
internal static class FakeWim
{
    public static byte[] Create(int images = 1, uint flags = 0x00040082u, int payload = 4096, byte seed = 0x5A)
    {
        var bytes = new byte[208 + payload];
        "MSWIM\0\0\0"u8.CopyTo(bytes);
        BitConverter.GetBytes(208u).CopyTo(bytes, 8);
        BitConverter.GetBytes(0x10D00u).CopyTo(bytes, 12);
        BitConverter.GetBytes(flags).CopyTo(bytes, 16);
        BitConverter.GetBytes(32768u).CopyTo(bytes, 20);
        BitConverter.GetBytes((uint)images).CopyTo(bytes, 44);
        for (var i = 208; i < bytes.Length; i++) bytes[i] = (byte)(seed + i * 7);
        return bytes;
    }
}
internal sealed class FakeHost : IBuildHost
{
    public void ValidateTools(BuildRecipe recipe) { }
    public void ValidateImage(BuildRecipe recipe, WindowsImage image) => image.Validate();
}
internal sealed class FakeRegistry : IOfflineRegistry
{
    public BuildRecipe? LastRecipe { get; private set; }
    public bool SetupCalled { get; private set; }
    public void Apply(string mount, BuildRecipe recipe, Action<string> log) => LastRecipe = recipe;
    public void ApplySetupBypass(string mount, Action<string> log) => SetupCalled = true;
}
internal sealed class FakeOperations(List<string> events) : IImageOperations
{
    public IReadOnlyList<ExpertAction> LastActions { get; private set; } = [];
    public IReadOnlyList<ImageInventoryItem> ReadNativeInventory(string mount) =>
        [new(ImageItemKind.Service, "BootCriticalDriver", "Boot", true),
         new(ImageItemKind.ComponentPayload, "amd64_critical-component", "Critical", true)];
    public void ApplyNativeActions(string mount, IReadOnlyList<ExpertAction> actions, Action<string> log, bool turboMode = false) => LastActions = actions;
    public void RemoveOriginalFiles(string mount, string architecture, IReadOnlyList<string> options, Action<string> log, bool turboMode = false) => events.Add("RemoveOriginalFiles");
    public void TrimCore(string mount, string architecture, Action<string> log, CancellationToken token, bool turboMode = false) => events.Add("TrimCore");
    public IReadOnlyList<ImageCleanupResult> CleanRedundantData(string mount, Action<string> log, CancellationToken token)
    { events.Add("CleanRedundantData"); return []; }
}
