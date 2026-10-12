using System.Runtime.Versioning;
using System.Text;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class RedundantDataTests
{
    [Fact]
    public void Cleanup_removes_only_known_cache_and_diagnostic_data_and_counts_bytes()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        string[] remove =
        [
            @"Windows\Temp\scratch.tmp", @"Windows\SoftwareDistribution\Download\cache\patch.tmp",
            @"Windows\Logs\CBS\CbsPersist_123.cab", @"Windows\Logs\DISM\dism.log.bak",
            @"Windows\MEMORY.DMP", @"Windows\Minidump\failure.dmp",
            @"ProgramData\Microsoft\Windows\WER\ReportArchive\old\report.wer",
            @"Users\Default\AppData\Local\Temp\scratch.tmp"
        ];
        string[] keep =
        [
            @"Windows\Logs\CBS\CBS.log", @"Windows\Logs\DISM\dism.log",
            @"Windows\SoftwareDistribution\DataStore\DataStore.edb",
            @"Windows\Installer\app.msi", @"Windows\System32\DriverStore\FileRepository\driver.sys",
            @"Windows\WinSxS\Manifests\keep.manifest", @"Windows\System32\config\SOFTWARE"
        ];
        foreach (var path in remove.Concat(keep)) Write(mount, path, new byte[128]);
        var results = new OfflineDataCleaner(_ => false).Clean(mount, _ => { }, CancellationToken.None);
        Assert.Equal(remove.Length, results.Sum(r => r.Files));
        Assert.Equal(remove.Length * 128L, results.Sum(r => r.LogicalBytes));
        Assert.All(remove, path => Assert.False(File.Exists(Path.Combine(mount, path))));
        Assert.All(keep, path => Assert.True(File.Exists(Path.Combine(mount, path))));
        Assert.True(Directory.Exists(Path.Combine(mount, @"Windows\Temp")));
    }

    [Fact]
    public void Pending_servicing_preserves_download_and_temp_payloads()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        Write(mount, @"Windows\WinSxS\pending.xml", Encoding.UTF8.GetBytes("<pending/>"));
        Write(mount, @"Windows\Temp\pending.tmp", [1, 2, 3]);
        Write(mount, @"Windows\SoftwareDistribution\Download\pending.cab", [4]);
        Write(mount, @"Windows\Logs\CBS\CbsPersist_1.log", [5]);
        var results = new OfflineDataCleaner(_ => false).Clean(mount, _ => { }, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(mount, @"Windows\Temp\pending.tmp")));
        Assert.True(File.Exists(Path.Combine(mount, @"Windows\SoftwareDistribution\Download\pending.cab")));
        Assert.False(File.Exists(Path.Combine(mount, @"Windows\Logs\CBS\CbsPersist_1.log")));
        Assert.Contains(results, result => result.Path == @"Windows\Temp" && result.State == BuildOperationState.Skipped &&
            result.Detail.Contains("pending servicing", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleanup_refuses_symlink_escape_before_deleting_any_files_in_the_target()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        var outside = Path.Combine(fixture.Root, "keep");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var temp = Path.Combine(mount, @"Windows\Temp");
        Directory.CreateDirectory(temp);
        Write(mount, @"Windows\Temp\local.txt", [1]);
        Directory.CreateSymbolicLink(Path.Combine(temp, "escape"), outside);
        Assert.Throws<InvalidDataException>(() => new OfflineDataCleaner(_ => false).Clean(mount, _ => { }, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(temp, "local.txt")));
        Assert.Throws<InvalidDataException>(() => new OfflineDataCleaner(_ => false).Clean(fixture.Root, _ => { }, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 8)]
    public async Task Setup_payload_omission_is_explicit_and_preserves_boot_and_required_installation_files(bool omit, int workers)
    {
        using var fixture = new BuildFixture();
        Write(fixture.Source, @"sources\sxs\netfx.cab", new byte[512]);
        Write(fixture.Source, @"sources\setup.exe", new byte[128]);
        var destination = Path.Combine(fixture.Root, "stage");
        Directory.CreateDirectory(destination);
        var result = await MediaStager.CopyAsync(fixture.Source, destination, workers, omit, _ => { }, CancellationToken.None);
        Assert.Equal(!omit, File.Exists(Path.Combine(destination, @"sources\sxs\netfx.cab")));
        Assert.Equal(omit ? 512 : 0, result.OmittedBytes);
        Assert.False(File.Exists(Path.Combine(destination, @"sources\install.wim")));
        Assert.True(File.Exists(Path.Combine(destination, @"sources\boot.wim")));
        Assert.True(File.Exists(Path.Combine(destination, @"efi\microsoft\boot\efisys.bin")));
        Assert.True(File.Exists(Path.Combine(destination, @"sources\setup.exe")));
    }

    [Fact]
    public void Unknown_servicing_skips_only_sensitive_targets_and_probe_errors_are_not_hidden()
    {
        using var fixture = new BuildFixture();
        var mount = NativeTests.CreateOwnedMount(fixture.Root);
        Write(mount, @"Windows\Temp\keep.tmp", [1]);
        Write(mount, @"Windows\Logs\CBS\CbsPersist_1.log", [2]);
        var results = new OfflineDataCleaner(_ => new PendingAssessment(PendingState.Unknown, "Cannot verify state"))
            .Clean(mount, _ => { }, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(mount, @"Windows\Temp\keep.tmp")));
        Assert.False(File.Exists(Path.Combine(mount, @"Windows\Logs\CBS\CbsPersist_1.log")));
        Assert.Contains(results, item => item.Pending == PendingState.Unknown && item.Detail.Contains("Cannot verify state"));
        Assert.Throws<IOException>(() => new OfflineDataCleaner((Func<string, PendingAssessment>)(_ => throw new IOException("probe I/O failure")))
            .Clean(mount, _ => { }, CancellationToken.None));
    }

    private static void Write(string root, string relative, byte[] content)
    {
        var file = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
    }
}
