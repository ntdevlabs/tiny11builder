using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Tiny11Builder.App;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class InventoryHostTests
{
    [Fact]
    public async Task Inventory_uses_read_only_preflight_and_does_not_apply_build_version_gate_or_output_requirements()
    {
        using var fixture = new BuildFixture(26300);
        var host = new InventoryOnlyHost();
        var engine = new BuildEngine(fixture.Runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, host);
        var result = await engine.InventoryAsync(fixture.Recipe with { OutputPath = "not an output path" }, null, CancellationToken.None);
        Assert.True(host.Checked);
        Assert.NotEmpty(result);
        Assert.Contains(fixture.Runner.Calls, args => args.Contains("/Mount-Image") && args.Contains("/ReadOnly"));
        Assert.DoesNotContain(fixture.Runner.Calls, args => args.Any(arg => arg is "/Commit" or "/Remove-Package" or "/Cleanup-Image"));
        Assert.Empty(Directory.EnumerateFiles(fixture.Source, "*.hive.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Real_inventory_preflight_is_not_a_writable_image_or_tool_version_check()
    {
        using var fixture = new BuildFixture();
        var image = new WindowsImage(1, "Windows 11 Pro", "", "x64", new(10, 0, 26300, 1), "Professional", "Client", 1);
        new BuildHost().ValidateInventory(fixture.Recipe with { DismPath = "missing tool checked separately" }, image);
        Assert.Throws<InvalidDataException>(() => new BuildHost().ValidateInventory(
            fixture.Recipe, image with { Architecture = "x86" }));
    }

    [Fact]
    public void Install_tool_action_follows_the_primary_cause_without_confusing_cancellation_or_other_errors()
    {
        var cause = new ServicingToolsRequiredException("Install matching tools.");
        Assert.True(OperationOutcomes.RequiresServicingTools(cause));
        Assert.True(OperationOutcomes.RequiresServicingTools(new BuildFailureException("Inventory failed", "job",
            new AggregateException(cause, new IOException("diagnostic error")))));
        Assert.False(OperationOutcomes.RequiresServicingTools(new OperationCanceledException()));
        Assert.False(OperationOutcomes.RequiresServicingTools(new DismCommandException(5, "access denied")));
        Assert.Equal("https://learn.microsoft.com/windows-hardware/get-started/adk-install", ServicingToolsRequiredException.DownloadPage);
    }

    [Theory]
    [InlineData(OperationKind.Check)]
    [InlineData(OperationKind.Build)]
    public void Windows_10_tool_instructions_remain_readable_in_the_actual_operation_notice(OperationKind kind)
    {
        var image = new WindowsImage(1, "Windows 11 Pro", "", "x64", new(10, 0, 26300, 9457), "Professional", "Client", 1);
        var error = Assert.Throws<ServicingToolsRequiredException>(() =>
            ToolLocator.EnsureServicingVersion(new Version(10, 0, 19041, 3636), image));
        var outcome = OperationOutcomes.Classify(kind, error);
        Assert.Equal(error.Message, outcome.FullMessage);
        Assert.Equal(error.Message, ErrorText.Summary(outcome.FullMessage));
        Assert.True(OperationOutcomes.RequiresServicingTools(error));
        Assert.Contains("10.0.19041.3636", outcome.FullMessage);
        Assert.Contains("Windows 10 can stay installed", outcome.FullMessage);
    }

    private sealed class InventoryOnlyHost : IBuildHost
    {
        public bool Checked { get; private set; }
        public void ValidateTools(BuildRecipe recipe) { }
        public void ValidateImage(BuildRecipe recipe, WindowsImage image) => throw new InvalidOperationException("Writable preflight should not run.");
        public void ValidateInventory(BuildRecipe recipe, WindowsImage image) { image.Validate(); Checked = true; }
    }
}
