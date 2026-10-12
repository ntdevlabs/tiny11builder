using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class NativeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Windows_inbox_DISM_is_accepted_as_a_trusted_Microsoft_tool()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\dism.exe");
        ToolLocator.Validate(path, "dism.exe");
        Assert.True(ToolLocator.VersionOf(path).Major >= 10);
        Assert.Throws<FileNotFoundException>(() => ToolLocator.Validate(path, "oscdimg.exe"));
    }

    [Fact]
    public void Native_registry_edits_are_verified_and_temporary_hives_are_unloaded()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        foreach (var relative in new[]
        {
            @"Windows\System32\config\SYSTEM", @"Windows\System32\config\SOFTWARE", @"Users\Default\NTUSER.DAT"
        })
        {
            var file = Path.Combine(mount, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file)) File.Delete(file);
            SaveFixtureHive(file);
        }
        new OfflineRegistry().Apply(mount, new BuildRecipe
        {
            Flavor = BuildFlavor.Core, CoreAcknowledged = true, SelectedOptions = ["ads", "privacy", "hardware-bypass"],
            PreferLocalAccount = true
        }, output.WriteLine);
        new ImageOperations().ApplyNativeActions(mount,
            [new(ImageItemKind.Service, "wuauserv", ExpertActionMode.Remove)], output.WriteLine);
        using (var hive = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SYSTEM"), false))
        using (var services = hive.Root.OpenSubKey(@"ControlSet001\Services"))
        {
            Assert.DoesNotContain("wuauserv", services!.GetSubKeyNames());
            using var defender = services.OpenSubKey("WinDefend");
            Assert.Equal(4, defender!.GetValue("Start"));
        }
        using (var hive = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SOFTWARE"), false))
        using (var key = hive.Root.OpenSubKey(@"Policies\Microsoft\Windows\DataCollection"))
            Assert.Equal(1, key!.GetValue("AllowTelemetry"));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(mount)!, "*.hive.json"));
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        Assert.DoesNotContain(machine.GetSubKeyNames(), key => key.StartsWith("Tiny11Builder_Offline_", StringComparison.Ordinal));
    }

    [Fact]
    public void Original_payload_removals_touch_only_the_owned_offline_mount()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var inside = Path.Combine(mount, @"Program Files (x86)\Microsoft\Edge\browser.exe");
        var outside = Path.Combine(fixture.Root, "browser.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "offline");
        File.WriteAllText(outside, "host fixture must remain");
        new ImageOperations().RemoveOriginalFiles(mount, "x64", ["edge-files"], _ => { });
        Assert.False(File.Exists(inside));
        Assert.Equal("host fixture must remain", File.ReadAllText(outside));
        Assert.Throws<InvalidDataException>(() => new ImageOperations().RemoveOriginalFiles(fixture.Root, "x64", ["edge-files"], _ => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deletion_preserves_surviving_hard_link_permissions_and_attributes(bool turbo)
    {
        using var fixture = new BuildFixture();
        var keep = Path.Combine(fixture.Root, "keep.dll");
        var remove = Path.Combine(fixture.Root, "remove");
        Directory.CreateDirectory(remove);
        File.WriteAllText(keep, "shared bytes");
        if (!CreateHardLink(Path.Combine(remove, "shared.dll"), keep, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new FileInfo(keep);
        var original = info.GetAccessControl();
        File.SetAttributes(keep, FileAttributes.ReadOnly | FileAttributes.Archive);
        var before = original.GetSecurityDescriptorSddlForm(AccessControlSections.All);
        try
        {
            using var privileges = new ImageOperations.PrivilegeScope();
            ImageOperations.DeleteTree(remove, turbo);
            Assert.False(Directory.Exists(remove));
            Assert.Equal("shared bytes", File.ReadAllText(keep));
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Archive, File.GetAttributes(keep));
        }
        finally { File.SetAttributes(keep, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deletion_can_enumerate_denied_readonly_directory_without_rewriting_its_acl(bool turbo)
    {
        using var fixture = new BuildFixture();
        var directory = Path.Combine(fixture.Root, "denied");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "payload.dll");
        File.WriteAllText(file, "remove");
        var info = new DirectoryInfo(directory);
        var original = info.GetAccessControl();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.ListDirectory, AccessControlType.Deny));
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            info.SetAccessControl(acl);
            using var privileges = new ImageOperations.PrivilegeScope();
            ImageOperations.DeleteTree(directory, turbo);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                info.SetAccessControl(original);
                if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Pending_probe_loads_a_private_copy_without_modifying_the_original_hive()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var software = Path.Combine(mount, @"Windows\System32\config\SOFTWARE");
        SaveFixtureHive(software);
        using (var loaded = OfflineRegistry.Load(software))
        using (var cbs = OfflineRegistry.OpenWriteKey(loaded.Root, @"Microsoft\Windows\CurrentVersion\Component Based Servicing")) { }
        var before = File.ReadAllBytes(software);
        var assessment = OfflineDataCleaner.AssessRegistryPending(mount);
        Assert.Equal(PendingState.Clear, assessment.State);
        Assert.Equal(before, File.ReadAllBytes(software));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(mount)!, "pending-probe-*"));
    }

    [Fact]
    public void Native_inventory_leaves_original_hive_unchanged_and_cleans_transaction_files()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var system = Path.Combine(mount, @"Windows\System32\config\SYSTEM");
        File.Delete(system);
        SaveFixtureHive(system);
        var before = File.ReadAllBytes(system);
        var items = new ImageOperations().ReadNativeInventory(mount);
        Assert.Contains(items, item => item.Kind == ImageItemKind.Service && item.Identity == "wuauserv");
        Assert.Equal(before, File.ReadAllBytes(system));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(mount)!, "inventory-hive-*"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void Unreadable_pending_state_retains_optional_temporary_payloads_with_a_warning(int length)
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        File.WriteAllBytes(Path.Combine(mount, @"Windows\System32\config\SOFTWARE"), new byte[length]);
        var temp = Path.Combine(mount, @"Windows\Temp");
        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, "pending.tmp"), "keep");
        var messages = new List<string>();
        var results = new OfflineDataCleaner().Clean(mount, messages.Add, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(temp, "pending.tmp")));
        Assert.Contains(results, item => item.Path == @"Windows\Temp" && item.State == BuildOperationState.Skipped &&
            item.Pending == PendingState.Unknown);
        Assert.Contains(messages, message => message.StartsWith("Warning:", StringComparison.Ordinal));
    }

    [Fact]
    public void Redundant_cleanup_keeps_payloads_when_registry_servicing_is_pending()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var software = Path.Combine(mount, @"Windows\System32\config\SOFTWARE");
        SaveFixtureHive(software);
        using (var hive = OfflineRegistry.Load(software))
        using (var pending = OfflineRegistry.OpenWriteKey(hive.Root,
                   @"Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        { }
        var temp = Path.Combine(mount, @"Windows\Temp");
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "servicing-payload.tmp");
        File.WriteAllText(file, "keep");
        var results = new OfflineDataCleaner().Clean(mount, _ => { }, CancellationToken.None);
        Assert.True(File.Exists(file));
        Assert.Contains(results, result => result.Path == @"Windows\Temp" && result.State == BuildOperationState.Skipped &&
            result.Detail.Contains("pending servicing", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_keeps_detected_runtime_families_and_prunes_only_other_offline_payloads()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var store = Path.Combine(mount, @"Windows\WinSxS");
        var names = new[]
        {
            "amd64_microsoft.windows.common-controls_newbuild",
            "amd64_microsoft.windows.gdiplus_newbuild",
            "amd64_microsoft-windows-servicingstack_newbuild",
            "Manifests", "amd64_optional-newbuild"
        };
        foreach (var name in names)
        {
            Directory.CreateDirectory(Path.Combine(store, name));
            File.WriteAllText(Path.Combine(store, name, "payload.dll"), "fixture");
        }
        new ImageOperations().TrimCore(mount, "x64", _ => { }, CancellationToken.None);
        Assert.All(names[..4], name => Assert.True(Directory.Exists(Path.Combine(store, name))));
        Assert.False(Directory.Exists(Path.Combine(store, names[4])));
    }

    [Fact]
    public void Core_refuses_an_unknown_runtime_layout_before_pruning_any_payload()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var path = Path.Combine(mount, @"Windows\WinSxS\arm64_optional_future");
        Directory.CreateDirectory(path);
        Assert.Throws<InvalidDataException>(() => new ImageOperations().TrimCore(mount, "arm64", _ => { }, CancellationToken.None));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Core_prunes_payload_files_beyond_the_legacy_MAX_PATH_limit()
    {
        using var fixture = new BuildFixture();
        var mount = CreateOwnedMount(fixture.Root);
        var store = Path.Combine(mount, @"Windows\WinSxS");
        foreach (var name in new[] { "amd64_common-controls", "amd64_gdiplus", "amd64_servicing" })
            Directory.CreateDirectory(Path.Combine(store, name));
        var optional = Path.Combine(store, "amd64_optional_" + new string('a', 100));
        Directory.CreateDirectory(optional);
        var file = Path.Combine(optional, new string('b', 120) + ".dll");
        Assert.True(file.Length > 260);
        File.WriteAllText(file, "test payload");
        new ImageOperations().TrimCore(mount, "x64", _ => { }, CancellationToken.None);
        Assert.False(Directory.Exists(optional));
    }

    [Fact]
    public void Reparse_points_cannot_redirect_source_or_owned_workspace_cleanup()
    {
        using var fixture = new BuildFixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var mount = CreateOwnedMount(fixture.Root);
        Directory.CreateSymbolicLink(Path.Combine(mount, "escape"), outside);
        Assert.Throws<InvalidDataException>(() => PathSafety.DeleteOwnedDirectory(mount, Path.GetDirectoryName(mount)!));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task Read_only_real_Windows_image_integration_when_configured()
    {
        var file = Environment.GetEnvironmentVariable("TINY11_TEST_WIM");
        if (string.IsNullOrEmpty(file)) return;
        var dism = new DismClient(ToolLocator.FindDism(), new ProcessRunner());
        var images = await dism.GetImagesAsync(file, CancellationToken.None);
        Assert.NotEmpty(images);
        var image = images[0];
        image.Validate();
        output.WriteLine($"Real source image: {image.Summary}");
        var originalWriteTime = File.GetLastWriteTimeUtc(file);
        using var fixture = new BuildFixture();
        fixture.AllowCleanup = false;
        var mount = CreateOwnedMount(fixture.Root, fakeWindows: false);
        var attempted = false;
        try
        {
            attempted = true;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{file}",
                $"/Index:{image.Index}", $"/MountDir:{mount}", "/ReadOnly");
            output.WriteLine("Read-only mount completed.");
            var inventory = await ImageInventory.ReadAsync(dism, new ImageOperations(), mount, CancellationToken.None);
            Assert.Contains(inventory, i => i.Kind == ImageItemKind.Service && i.Identity == "wuauserv");
            Assert.Contains(inventory, i => i.Kind == ImageItemKind.Feature);
            Assert.Contains(inventory, i => i.Kind == ImageItemKind.SystemApp);
            Assert.Contains(inventory, i => i.Kind == ImageItemKind.ComponentPayload);
            output.WriteLine($"Discovered {inventory.Count:N0} real image components.");
            var policyMount = CreateOwnedMount(fixture.Root, fakeWindows: false);
            foreach (var relative in new[]
            {
                @"Windows\System32\config\SYSTEM", @"Windows\System32\config\SOFTWARE", @"Users\Default\NTUSER.DAT"
            })
            {
                var target = Path.Combine(policyMount, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(mount, relative), target);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            }

            new OfflineRegistry().Apply(policyMount, new BuildRecipe
            {
                Flavor = BuildFlavor.Core, CoreAcknowledged = true,
                SelectedOptions = ["ads", "privacy", "copilot", "widgets", "search", "edge", "oobe", "hardware-bypass"],
                PreferLocalAccount = true
            }, output.WriteLine);
            new ImageOperations().ApplyNativeActions(policyMount,
                [new(ImageItemKind.Service, "wuauserv", ExpertActionMode.Remove)], output.WriteLine);
            using (var software = OfflineRegistry.Load(Path.Combine(policyMount, @"Windows\System32\config\SOFTWARE"), writable: false))
            using (var privacy = software.Root.OpenSubKey(@"Policies\Microsoft\Windows\DataCollection"))
                Assert.Equal(1, privacy?.GetValue("AllowTelemetry"));
            using (var system = OfflineRegistry.Load(Path.Combine(policyMount, @"Windows\System32\config\SYSTEM"), writable: false))
            {
                using var select = system.Root.OpenSubKey("Select");
                var current = (int)select!.GetValue("Current")!;
                using var services = system.Root.OpenSubKey($@"ControlSet{current:D3}\Services");
                Assert.DoesNotContain("wuauserv", services!.GetSubKeyNames());
                using var defender = services.OpenSubKey("WinDefend");
                Assert.Equal(4, defender?.GetValue("Start"));
            }
            output.WriteLine("Core policies and expert service removal verified on private copies of real registry hives.");
        }
        finally
        {
            if (attempted) await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
            output.WriteLine("Read-only mount discarded.");
            fixture.AllowCleanup = true;
        }
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task Shared_deletion_survives_a_synthetic_WIM_commit_when_configured()
    {
        if (Environment.GetEnvironmentVariable("TINY11_NATIVE_WIM_CHECK") != "1") return;
        using var fixture = new BuildFixture();
        var source = Path.Combine(fixture.Root, "capture");
        var component = Path.Combine(source, @"Windows\WinSxS\optional");
        Directory.CreateDirectory(component);
        Directory.CreateDirectory(Path.Combine(source, @"Windows\System32\config"));
        File.WriteAllText(Path.Combine(source, @"Windows\System32\config\SYSTEM"), "synthetic fixture");
        var survivor = Path.Combine(source, @"Windows\System32\shared.dll");
        File.WriteAllText(survivor, "shared fixture");
        if (!CreateHardLink(Path.Combine(component, "shared.dll"), survivor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        File.SetAttributes(survivor, FileAttributes.ReadOnly);
        var wim = Path.Combine(fixture.Root, "native-delete.wim");
        var mount = CreateOwnedMount(fixture.Root, fakeWindows: false);
        var dism = new DismClient(ToolLocator.FindDism(), new ProcessRunner(), output.WriteLine,
            new(fixture.Root, Path.Combine(fixture.Root, "native-dism.log"), false));
        await dism.RunAsync(CancellationToken.None, "/Capture-Image", $"/CaptureDir:{source}",
            $"/ImageFile:{wim}", "/Name:Native deletion fixture", "/Compress:fast", "/CheckIntegrity");
        var mounted = false;
        fixture.AllowCleanup = false;
        try
        {
            mounted = true;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{wim}", "/Index:1",
                $"/MountDir:{mount}", "/CheckIntegrity");
            var keep = Path.Combine(mount, @"Windows\System32\shared.dll");
            var before = new FileInfo(keep).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            new ImageOperations().ApplyNativeActions(mount,
                [new(ImageItemKind.ComponentPayload, "optional", ExpertActionMode.Remove)], output.WriteLine);
            Assert.Equal(before, new FileInfo(keep).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
            Assert.True((File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0);
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity");
            mounted = false;
            mounted = true;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{wim}", "/Index:1",
                $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity");
            Assert.False(Directory.Exists(Path.Combine(mount, @"Windows\WinSxS\optional")));
            Assert.Equal("shared fixture", File.ReadAllText(keep));
            Assert.True((File.GetAttributes(keep) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            if (mounted)
                await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
            fixture.AllowCleanup = true;
            File.SetAttributes(survivor, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Full_real_Core_build_when_configured()
    {
        var recipeFile = Environment.GetEnvironmentVariable("TINY11_BUILD_RECIPE");
        if (string.IsNullOrEmpty(recipeFile)) return;
        var turbo = Environment.GetEnvironmentVariable("TINY11_TEST_TURBO") == "1";
        using var fixture = new BuildFixture();
        var recipe = BuildRecipe.Read(recipeFile) with
        {
            WorkspacePath = Path.Combine(fixture.Root, "workspace"),
            OutputPath = Path.Combine(fixture.Root, turbo ? "Tiny11Core-turbo.iso" : "Tiny11Core-standard.iso"),
            KeepWorkingFiles = false, TurboMode = turbo, RemoveRedundantData = turbo, OmitNetFx3SetupPayload = turbo
        };
        var engine = new BuildEngine(new ProcessRunner(), new MediaSourceFactory(), new OfflineRegistry(), new ImageOperations(), new BuildHost());
        var result = await engine.BuildAsync(recipe, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        await using var input = File.OpenRead(result.IsoPath);
        await IsoStructure.ValidateAsync(input, CancellationToken.None);
        Assert.Equal(64, result.Sha256.Length);
        output.WriteLine($"Completed ISO: {result.IsoPath}");
        output.WriteLine($"SHA-256: {result.Sha256}");
        output.WriteLine($"Build logs: {result.WorkDirectory}");
    }

    [Fact]
    public async Task Writable_mount_permission_regression_when_configured()
    {
        var wim = Environment.GetEnvironmentVariable("TINY11_PERMISSION_WIM");
        if (string.IsNullOrEmpty(wim)) return;
        using var fixture = new BuildFixture();
        fixture.AllowCleanup = false;
        var mount = CreateOwnedMount(fixture.Root, fakeWindows: false);
        var dism = new DismClient(ToolLocator.FindDism(), new ProcessRunner());
        try
        {
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{wim}", "/Index:1", $"/MountDir:{mount}");
            new OfflineRegistry().Apply(mount, new BuildRecipe
            {
                Flavor = BuildFlavor.Core, CoreAcknowledged = true,
                SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core), PreferLocalAccount = true
            }, output.WriteLine);
            using var software = OfflineRegistry.Load(Path.Combine(mount, @"Windows\System32\config\SOFTWARE"), writable: false);
            using var widgets = software.Root.OpenSubKey(@"Policies\Microsoft\Dsh");
            Assert.Equal(0, widgets!.GetValue("AllowNewsAndInterests"));
        }
        finally
        {
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
            fixture.AllowCleanup = true;
        }
    }

    internal static string CreateOwnedMount(string parent, bool fakeWindows = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = Path.Combine(parent, "tiny11-" + id);
        var mount = Path.Combine(job, "mount");
        Directory.CreateDirectory(mount);
        if (fakeWindows)
        {
            Directory.CreateDirectory(Path.Combine(mount, @"Windows\System32\config"));
            Directory.CreateDirectory(Path.Combine(mount, @"Windows\WinSxS"));
            File.WriteAllText(Path.Combine(mount, @"Windows\System32\config\SYSTEM"), "fixture");
        }
        File.WriteAllText(Path.Combine(job, "job.json"), JsonSerializer.Serialize(new JobState(id, "test", "running", "", DateTimeOffset.UtcNow), JsonDefaults.Options));
        return mount;
    }

    private static void SaveFixtureHive(string file)
    {
        var name = @"Software\Tiny11BuilderTests_" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(name);
            using (var select = root.CreateSubKey("Select")) select.SetValue("Current", 1);
            foreach (var service in new[] { "wuauserv", "WinDefend" })
                using (var key = root.CreateSubKey(@"ControlSet001\Services\" + service)) key.SetValue("Start", 2);
            using var privileges = new ImageOperations.PrivilegeScope("SeBackupPrivilege");
            var code = RegSaveKeyEx(root.Handle, file, IntPtr.Zero, 2);
            if (code != 0) throw new Win32Exception(code);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSaveKeyExW")]
    private static extern int RegSaveKeyEx(SafeRegistryHandle key, string file, IntPtr security, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
