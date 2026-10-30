using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class StockBootImageTests
{
    [Theory]
    [InlineData(22621, "x64")]
    [InlineData(22631, "arm64")]
    [InlineData(26300, "x64")]
    [InlineData(29691, "arm64")]
    public async Task Stock_Setup_is_rebuilt_as_one_bootable_index_with_integrity_checks(int build, string arch)
    {
        using var fixture = new BuildFixture(build, arch);
        var original = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\boot.wim"));
        var result = await fixture.Engine.BuildAsync(fixture.Recipe with { Mode = BuildMode.Custom,
            ShrinkBootImage = true, KeepWorkingFiles = true }, null, CancellationToken.None);
        var export = Assert.Single(fixture.Runner.Calls, args => args.Contains("/Export-Image") && args.Contains("/Bootable"));
        Assert.Contains("/SourceIndex:2", export);
        Assert.Contains("/Compress:max", export);
        Assert.Contains("/CheckIntegrity", export);
        var rebuilt = Path.Combine(result.WorkDirectory, @"media\sources\boot.wim");
        var header = File.ReadAllBytes(rebuilt);
        Assert.Equal(1u, BitConverter.ToUInt32(header, 44));
        Assert.Equal(1u, BitConverter.ToUInt32(header, 120));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\boot.wim")));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.True(report.RootElement.GetProperty("ShrinkBootImage").GetBoolean());
        Assert.True(report.RootElement.GetProperty("BootImageBytesBefore").GetInt64() > 0);
        Assert.True(report.RootElement.GetProperty("BootImageBytesAfter").GetInt64() > 0);
    }

    [Fact]
    public async Task Rebuilt_stock_Setup_is_not_exported_again_when_resuming_after_compression()
    {
        using var fixture = new BuildFixture();
        var recipe = fixture.Recipe with { Mode = BuildMode.Custom, ShrinkBootImage = true };
        var failure = await JobScenarios.FailAtIsoAsync(fixture, recipe);
        var before = fixture.Runner.Calls.Count;
        var result = await fixture.Engine.ResumeAsync(JobScenarios.JobFile(failure), recipe, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        Assert.DoesNotContain(fixture.Runner.Calls.Skip(before), args => args.Contains("/Export-Image"));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.True(report.RootElement.GetProperty("BootImageBytesAfter").GetInt64() > 0);
    }

    [Fact]
    public async Task A_failed_stock_export_does_not_replace_the_staged_boot_image()
    {
        using var fixture = new BuildFixture();
        var original = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\boot.wim"));
        var runner = new CommandResultRunner(fixture.Runner, args => args.Contains("/Bootable") ? new(5, "export refused") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(
            fixture.Recipe with { Mode = BuildMode.Custom, ShrinkBootImage = true }, null, CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(failure.WorkDirectory, @"media\sources\boot.wim")));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public async Task Real_stock_boot_reduction_preserves_Setup_GUI_and_native_storage_files_when_configured()
    {
        var iso = Environment.GetEnvironmentVariable("TINY11_STOCK_BOOT_CHECK");
        if (string.IsNullOrEmpty(iso)) return;
        using var fixture = new BuildFixture();
        using var media = new MediaSourceFactory().Open(iso);
        var source = Path.Combine(media.RootPath, @"sources\boot.wim");
        var destination = Path.Combine(fixture.Root, "boot.wim");
        var runner = new ProcessRunner();
        var dism = new DismClient(ToolLocator.FindDism(), runner);
        var sourceImages = await dism.GetImagesAsync(source, CancellationToken.None);
        var setup = sourceImages.First(image => image.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        var originalLength = new FileInfo(source).Length;
        await SetupInstaller.ExportSmallerBootImageAsync(dism, source, setup, destination, CancellationToken.None);
        Assert.True(new FileInfo(destination).Length < originalLength, "Stock export did not reduce the complete boot.wim.");
        var mount = NativeTests.CreateOwnedMount(fixture.Root, fakeWindows: false);
        var attempted = false;
        try
        {
            attempted = true;
            await dism.RunAsync(CancellationToken.None, "/Mount-Image", $"/ImageFile:{destination}", "/Index:1",
                $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity");
            foreach (var file in new[] { @"sources\setup.exe", @"sources\winsetup.dll", @"Windows\System32\wpeinit.exe",
                         @"Windows\System32\winload.efi", @"Windows\System32\drivers\disk.sys", @"Windows\System32\drivers\storport.sys" })
                Assert.True(File.Exists(Path.Combine(mount, file)), "Missing retained stock file: " + file);
        }
        finally
        {
            if (attempted) await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard");
        }
        var evidence = Environment.GetEnvironmentVariable("TINY11_STOCK_BOOT_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
            File.WriteAllText(evidence, JsonSerializer.Serialize(new { Source = iso, BeforeBytes = originalLength,
                AfterBytes = new FileInfo(destination).Length, SingleBootableIndex = true, IntegrityReadOnlyRemountPassed = true,
                GuiAndNativeStorageFilesRetained = true, OriginalIsoUnchanged = true, BootOrInstallTested = false }, JsonDefaults.Options));
    }
}
