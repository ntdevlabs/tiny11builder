using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class SetupInstallerTests
{
    [Theory]
    [InlineData(BuildFlavor.Core, true)]
    [InlineData(BuildFlavor.Regular, false)]
    public void Legacy_Setup_is_required_by_Core_not_regular_removals(BuildFlavor flavor, bool required)
    {
        Assert.Equal(required, SetupInstaller.RequiresLegacy(new() { Flavor = flavor }));
        Assert.True(SetupInstaller.RequiresLegacy(new()
        {
            ExpertActions = [new(ImageItemKind.ComponentPayload, "amd64_component", ExpertActionMode.Remove)]
        }));
        Assert.False(SetupInstaller.RequiresLegacy(new()
        {
            ExpertActions = [new(ImageItemKind.Service, "example", ExpertActionMode.Remove)]
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_startup_initializes_PE_then_runs_the_matching_classic_installer(bool namedOld)
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        Directory.CreateDirectory(Path.Combine(mount, "sources"));
        File.WriteAllText(Path.Combine(mount, @"sources\setup.exe"), "same-build classic binary");
        if (namedOld) File.WriteAllText(Path.Combine(mount, @"sources\setup_old.exe"), "explicit classic binary");
        var configuration = Path.Combine(mount, @"Windows\System32\winpeshl.ini");
        File.WriteAllText(configuration, "[LaunchApp]\r\nAppPath=%SYSTEMDRIVE%\\sources\\SetupHost.exe\r\n");
        File.SetAttributes(configuration, FileAttributes.ReadOnly);
        var messages = new List<string>();
        SetupInstaller.ApplyLegacy(mount, messages.Add);
        var ini = File.ReadAllText(Path.Combine(mount, @"Windows\System32\winpeshl.ini"));
        Assert.Equal("[LaunchApps]\r\n%SYSTEMROOT%\\System32\\wpeinit.exe\r\n%SYSTEMDRIVE%\\sources\\" +
            (namedOld ? "setup_old.exe" : "setup.exe") + "\r\n", ini);
        Assert.DoesNotContain("SetupHost", ini);
        Assert.Single(messages);
        Assert.Throws<InvalidDataException>(() => SetupInstaller.ApplyLegacy(fixture.Root, messages.Add));
    }

    [Fact]
    public void Missing_classic_installer_is_a_loud_failure_not_a_new_installer_fallback()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        Directory.CreateDirectory(Path.Combine(mount, "sources"));
        File.WriteAllText(Path.Combine(mount, @"sources\SetupHost.exe"), "new installer only");
        Assert.Throws<FileNotFoundException>(() => SetupInstaller.ApplyLegacy(mount, _ => { }));
        Assert.False(File.Exists(Path.Combine(mount, @"Windows\System32\winpeshl.ini")));
    }

    [Fact]
    public void Installer_symlinks_are_refused_instead_of_following_a_host_executable()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        Directory.CreateDirectory(Path.Combine(mount, "sources"));
        var outside = Path.Combine(fixture.Root, "outside.exe");
        File.WriteAllText(outside, "must not run");
        File.CreateSymbolicLink(Path.Combine(mount, @"sources\setup.exe"), outside);
        Assert.Throws<InvalidDataException>(() => SetupInstaller.ApplyLegacy(mount, _ => { }));
        Assert.False(File.Exists(Path.Combine(mount, @"Windows\System32\winpeshl.ini")));
    }

    [Theory]
    [InlineData(false, 22621, "x64")]
    [InlineData(false, 26100, "arm64")]
    [InlineData(false, 26300, "x64")]
    [InlineData(true, 29691, "x64")]
    [InlineData(true, 26300, "arm64")]
    public async Task Core_and_custom_component_deletion_force_boot_edit_without_hardware_bypass(bool custom, int build, string architecture)
    {
        using var fixture = new BuildFixture(build, architecture);
        var recipe = fixture.Recipe with
        {
            SelectedOptions = [], KeepWorkingFiles = true,
            Flavor = custom ? BuildFlavor.Regular : BuildFlavor.Core, CoreAcknowledged = !custom,
            ExpertAcknowledged = custom,
            ExpertActions = custom ? [new(ImageItemKind.ComponentPayload, "amd64_critical-component", ExpertActionMode.Remove)] : []
        };
        var observed = false;
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Unmount-Image") || !args.Contains("/Commit")) return;
            var mount = args.Single(arg => arg.StartsWith("/MountDir:"))["/MountDir:".Length..];
            var ini = Path.Combine(mount, @"Windows\System32\winpeshl.ini");
            if (!File.Exists(ini)) return;
            observed = true;
            Assert.Contains("%SYSTEMDRIVE%\\sources\\setup.exe", File.ReadAllText(ini));
        };
        var result = await fixture.Engine.BuildAsync(recipe, null, CancellationToken.None);
        Assert.True(observed);
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.True(report.RootElement.GetProperty("LegacyInstallerEnabled").GetBoolean());
        Assert.False(fixture.Registry.SetupCalled);
    }

    [Fact]
    public async Task Resuming_a_compressed_Core_job_from_before_the_setting_re_edits_Setup_without_recompressing()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { Flavor = BuildFlavor.Core, CoreAcknowledged = true, SelectedOptions = [] };
        var failed = await JobScenarios.FailAtIsoAsync(fixture, recipe);
        var checkpoint = CheckpointStore.Read(failed.WorkDirectory);
        CheckpointStore.WriteAtomic(failed.WorkDirectory, checkpoint with
        {
            SetupState = SetupProgress.NotRequired,
            Report = checkpoint.Report with { LegacyInstallerEnabled = false }
        });
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failed), recipe, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        var calls = fixture.Runner.Calls.Skip(before).ToArray();
        Assert.Single(calls, args => args.Contains("/Mount-Image"));
        Assert.DoesNotContain(calls, args => args.Contains("/Export-Image"));
    }

    [Fact]
    public async Task Legacy_startup_survives_a_real_Setup_image_commit_when_configured()
    {
        var iso = Environment.GetEnvironmentVariable("TINY11_SETUP_ISO_CHECK");
        if (string.IsNullOrEmpty(iso)) return;
        using var fixture = new BuildFixture();
        using var media = new MediaSourceFactory().Open(iso);
        var mount = NativeTests.CreateOwnedMount(fixture.Root, fakeWindows: false);
        var job = Path.GetDirectoryName(mount)!;
        var working = Path.Combine(job, "boot.wim");
        File.Copy(Path.Combine(media.RootPath, @"sources\boot.wim"), working);
        File.SetAttributes(working, File.GetAttributes(working) & ~FileAttributes.ReadOnly);
        var dism = new DismClient(ToolLocator.FindDism(), new ProcessRunner(), null,
            new(job, Path.Combine(job, "dism.log"), false));
        var indexes = await dism.GetImagesAsync(working, CancellationToken.None);
        var setup = indexes.First(image => image.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        var attempted = false;
        fixture.AllowCleanup = false;
        try
        {
            attempted = true;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{working}",
                $"/Index:{setup.Index}", $"/MountDir:{mount}", "/CheckIntegrity");
            SetupInstaller.ApplyLegacy(mount, _ => { });
            var expected = File.ReadAllText(Path.Combine(mount, @"Windows\System32\winpeshl.ini"));
            Assert.Contains("%SYSTEMDRIVE%\\sources\\setup.exe", expected);
            Assert.True(File.Exists(Path.Combine(mount, @"Windows\System32\wpeinit.exe")));
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity");
            attempted = false;
            attempted = true;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{working}",
                $"/Index:{setup.Index}", $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity");
            Assert.Equal(expected, File.ReadAllText(Path.Combine(mount, @"Windows\System32\winpeshl.ini")));
            Assert.Equal(indexes.Count, (await dism.GetImagesAsync(working, CancellationToken.None)).Count);
        }
        finally
        {
            if (attempted)
            {
                var mounted = await dism.RunAsync(CancellationToken.None, "/Get-MountedWimInfo");
                if (mounted.Contains(mount, StringComparison.OrdinalIgnoreCase))
                    await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
            }
            fixture.AllowCleanup = true;
        }
    }
}
