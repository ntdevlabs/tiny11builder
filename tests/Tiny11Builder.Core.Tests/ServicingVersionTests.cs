using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ServicingVersionTests
{
    private static WindowsImage Image(int build) =>
        new(1, "Windows 11 Pro", "", "x64", new(10, 0, build, 9457), "Professional", "Client", 1);

    [Theory]
    [InlineData(26100, 6584, 26300)]
    [InlineData(26100, 9457, 26300)]
    [InlineData(26100, 1, 26200)]
    [InlineData(22621, 1, 22631)]
    [InlineData(26100, 1, 22631)]
    [InlineData(29691, 1, 26300)]
    [InlineData(29691, 1, 29691)]
    public void Known_shared_servicing_families_are_accepted_without_changing_image_identity(int tool, int revision, int build)
    {
        var image = Image(build);
        ToolLocator.EnsureServicingVersion(new Version(10, 0, tool, revision), image);
        Assert.Equal(build, image.Version.Build);
        Assert.Equal(9457, image.Version.Revision);
    }

    [Theory]
    [InlineData(19041, 22621)]
    [InlineData(22621, 26100)]
    [InlineData(26100, 28000)]
    [InlineData(26100, 29691)]
    [InlineData(26100, 26301)]
    [InlineData(26200, 26201)]
    [InlineData(29691, 99999)]
    public void Older_servicing_families_and_unknown_newer_builds_are_not_silently_allowed(int tool, int build)
    {
        var error = Assert.Throws<ServicingToolsRequiredException>(() =>
            ToolLocator.EnsureServicingVersion(new Version(10, 0, tool, 1000), Image(build)));
        Assert.Contains($"build {build}", error.Message);
        Assert.Contains("detects them automatically", error.Message);
        Assert.Contains("Windows 10", error.Message);
        Assert.DoesNotContain("Select a matching", error.Message);
    }

    [Theory]
    [InlineData(19041, 3636)]
    [InlineData(22621, 1)]
    public void Reported_old_tools_get_complete_instructions_without_requiring_a_Windows_upgrade(int build, int revision)
    {
        var installed = new Version(10, 0, build, revision);
        var error = Assert.Throws<ServicingToolsRequiredException>(() =>
            ToolLocator.EnsureServicingVersion(installed, Image(26300)));
        Assert.Contains(installed.ToString(), error.Message);
        Assert.Contains("build 26300", error.Message);
        Assert.Contains("base 26100", error.Message);
        Assert.Contains("Deployment Tools", error.Message);
        Assert.Contains("Windows 10 can stay installed", error.Message);
        Assert.True(error.Message.Length <= 280, error.Message);
        Assert.False(ToolLocator.SupportsServicing(installed, Image(26300)));
        // Installing compatible tools, without changing the host OS, is the supported remediation.
        ToolLocator.EnsureServicingVersion(new Version(10, 0, 26100, 9457), Image(26300));
    }

    [Fact]
    public void A_newer_executable_wrapper_cannot_hide_an_older_or_missing_engine()
    {
        var wrapper = new Version(10, 0, 28000, 2113);
        var api = new Version(10, 0, 29691, 1000);
        Assert.Equal(api, ToolLocator.ResolveServicingVersion(wrapper, api, api));
        Assert.Equal(wrapper, ToolLocator.ResolveServicingVersion(wrapper, api, null));
        var older = new Version(10, 0, 26100, 6584);
        Assert.Equal(older, ToolLocator.ResolveServicingVersion(wrapper, api, older));
        Assert.Equal(older, ToolLocator.ResolveServicingVersion(wrapper, older, api));
        Assert.Throws<ServicingToolsRequiredException>(() => ToolLocator.EnsureServicingVersion(new Version(6, 3, 99999), Image(26300)));
    }

    [Fact]
    public async Task Installed_inbox_provider_build_agrees_with_the_actual_DISM_banner()
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\dism.exe");
        var version = ToolLocator.ServicingVersionOf(dism);
        var process = await new ProcessRunner().RunAsync(dism, ["/English", "/?"], null, CancellationToken.None);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains($"Version: {version.Major}.{version.Minor}.{version.Build}.", process.Output);
    }

    [Fact]
    public async Task Existing_26100_ADK_accepts_real_26300_metadata_without_rewriting_it_when_configured()
    {
        var iso = Environment.GetEnvironmentVariable("TINY11_SERVICING_ISO");
        var tool = Environment.GetEnvironmentVariable("TINY11_SERVICING_DISM");
        if (string.IsNullOrEmpty(iso) || string.IsNullOrEmpty(tool)) return;
        ToolLocator.Validate(tool, "dism.exe");
        Assert.Equal(26100, ToolLocator.ServicingVersionOf(tool).Build);
        var originalTime = File.GetLastWriteTimeUtc(iso);
        using (var source = new MediaSourceFactory().Open(iso))
        {
            var images = await new DismClient(tool, new ProcessRunner()).GetImagesAsync(
                InstallationMedia.ImageFile(source.RootPath), CancellationToken.None);
            var image = Assert.Single(images);
            Assert.Equal(26300, image.Version.Build);
            ToolLocator.EnsureServicingVersion(tool, image);
            Assert.Equal(26300, image.Version.Build);
            Assert.Throws<ServicingToolsRequiredException>(() => ToolLocator.EnsureServicingVersion(
                new Version(10, 0, 22621, 1), image));
        }
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(iso));
    }
}
