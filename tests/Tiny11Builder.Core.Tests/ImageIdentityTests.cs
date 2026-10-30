using System.Text.Json;
using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ImageIdentityTests
{
    [Theory]
    [InlineData(22621, 22631)]
    [InlineData(22631, 22621)]
    [InlineData(26100, 26200)]
    [InlineData(26100, 26300)]
    [InlineData(26300, 26100)]
    public void Enablement_identity_changes_preserve_revision_edition_and_architecture(int before, int after)
    {
        var expected = CompatibilityTests.Image(before) with { Version = new(10, 0, before, 4037) };
        var actual = expected with { Index = 1, Version = new(10, 0, after, 4037) };
        Assert.True(WindowsImageIdentity.Matches(actual, expected));
        Assert.False(WindowsImageIdentity.Matches(actual with { Edition = "Enterprise" }, expected));
        Assert.False(WindowsImageIdentity.Matches(actual with { Architecture = "arm64" }, expected));
        Assert.False(WindowsImageIdentity.Matches(actual with { Installation = "Server" }, expected));
        Assert.False(WindowsImageIdentity.Matches(actual with { Version = new(10, 0, after, 4038) }, expected));
    }

    [Theory]
    [InlineData(22621, 26100)]
    [InlineData(26300, 26340)]
    [InlineData(26340, 26341)]
    public void Unknown_or_different_servicing_families_remain_different_images(int before, int after)
    {
        var expected = CompatibilityTests.Image(before);
        Assert.False(WindowsImageIdentity.Matches(expected with { Version = new(10, 0, after, expected.Version.Revision) }, expected));
    }

    [Fact]
    public async Task Issue669_export_can_report_enabled_23H2_and_publishes_actual_identity()
    {
        using var fixture = new BuildFixture(22621);
        var runner = new CommandResultRunner(fixture.Runner, args =>
            args.Any(arg => arg.EndsWith(@"compress\install.esd", StringComparison.OrdinalIgnoreCase)) &&
            args.Contains("/Get-WimInfo") && args.Contains("/Index:1") ?
                new(0, "Name : Windows 11 Pro\nDescription : Image\nArchitecture : x64\n" +
                    "Version : 10.0.22631\nServicePack Build : 123\nEdition : Professional\nInstallation : Client\nSize : 16000000000 bytes\n") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        var result = await engine.BuildAsync(fixture.Recipe, null, CancellationToken.None);
        Assert.True(File.Exists(result.IsoPath));
        using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
        Assert.Equal("10.0.22631.123", report.RootElement.GetProperty("Image").GetProperty("Version").GetString());
    }

    [Theory]
    [InlineData("Enterprise", "x64", 22621, 123)]
    [InlineData("Professional", "arm64", 22621, 123)]
    [InlineData("Professional", "x64", 22621, 124)]
    [InlineData("Professional", "x64", 26340, 123)]
    public async Task Final_export_still_rejects_wrong_edition_architecture_revision_or_unknown_build(
        string edition, string arch, int build, int revision)
    {
        using var fixture = new BuildFixture(22621);
        var runner = new CommandResultRunner(fixture.Runner, args =>
            args.Any(arg => arg.EndsWith(@"compress\install.esd", StringComparison.OrdinalIgnoreCase)) &&
            args.Contains("/Get-WimInfo") && args.Contains("/Index:1") ?
                new(0, $"Name : Windows 11 Pro\nDescription : Image\nArchitecture : {arch}\nVersion : 10.0.{build}\n" +
                    $"ServicePack Build : {revision}\nEdition : {edition}\nInstallation : Client\nSize : 16000000000 bytes\n") : null);
        var engine = new BuildEngine(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost());
        await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(fixture.Recipe, null, CancellationToken.None));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }
}
