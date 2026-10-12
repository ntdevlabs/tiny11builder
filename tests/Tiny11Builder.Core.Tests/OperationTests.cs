using System.Runtime.Versioning;
using System.Text.Json;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class OperationTests
{
    [Theory]
    [InlineData("[================59.0%===    ]", true, 59d)]
    [InlineData(" [       100.0%====] ", true, 100d)]
    [InlineData("Exporting image: 4.5%", false, 4.5)]
    [InlineData("Feature Name : Recall", false, null)]
    public void Distinguishes_console_bars_from_readable_operation_text(string text, bool console, double? percent)
    {
        Assert.Equal(console, BuildProgressText.IsConsoleProgress(text));
        Assert.Equal(percent, BuildProgressText.Percent(text));
    }

    [Fact]
    public async Task Progress_keeps_item_identity_while_consuming_DISM_console_progress()
    {
        var events = new List<BuildProgress>();
        var raw = new List<string>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), raw.Add);
        tracker.BeginStage("Remove apps", "Remove apps");
        tracker.Plan("app:Copilot", "Remove apps", "Remove Microsoft.Copilot");
        await tracker.RunAsync("app:Copilot", "Remove apps", "Remove Microsoft.Copilot", () =>
        {
            tracker.Output("[================59.0%===    ]");
            tracker.Output("The operation completed successfully.");
            return Task.CompletedTask;
        });
        Assert.Contains(events, e => e.OperationId == "app:Copilot" && e.State == BuildOperationState.Pending);
        Assert.Contains(events, e => e.OperationId == "app:Copilot" && e.Percent == 59 && e.Message == "Remove Microsoft.Copilot" && e.Detail is null);
        Assert.Contains(events, e => e.OperationId == "app:Copilot" && e.State == BuildOperationState.Completed);
        Assert.DoesNotContain(events, e => BuildProgressText.IsConsoleProgress(e.Message));
        Assert.Contains(raw, line => BuildProgressText.IsConsoleProgress(line));
    }

    [Fact]
    public async Task Failure_preserves_completed_and_pending_operations()
    {
        var events = new List<BuildProgress>();
        var tracker = new BuildOperationTracker(new ImmediateProgress<BuildProgress>(events.Add), _ => { });
        tracker.BeginStage("Optional features", "Remove features");
        tracker.Plan("done", "Optional features", "Done feature");
        tracker.Plan("fail", "Optional features", "Blocked feature");
        tracker.Plan("next", "Optional features", "Next feature");
        await tracker.RunAsync("done", "Optional features", "Done feature", () => Task.CompletedTask);
        var failure = await Assert.ThrowsAsync<IOException>(() => tracker.RunAsync("fail", "Optional features", "Blocked feature",
            () => throw new IOException("Feature removal refused")));
        tracker.Fail(failure, false);
        Assert.Equal(BuildOperationState.Completed, events.Last(e => e.OperationId == "done").State);
        Assert.Equal(BuildOperationState.Failed, events.Last(e => e.OperationId == "fail").State);
        Assert.Equal(BuildOperationState.Pending, events.Last(e => e.OperationId == "next").State);
        Assert.Contains("Feature removal refused", events.Last(e => e.OperationId == "fail").Detail);
    }

    [Fact]
    public async Task Pipeline_tracks_exact_app_and_feature_and_retains_failure_report()
    {
        using var fixture = new BuildFixture();
        var events = new List<BuildProgress>();
        await fixture.Engine.BuildAsync(fixture.Recipe with { SelectedOptions = ["copilot-app", "workfolders"] },
            new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None);
        Assert.Contains(events, e => e.OperationId == "app:Microsoft.Copilot_1.0_x64__8weky" && e.State == BuildOperationState.Running);
        Assert.Contains(events, e => e.OperationId == "feature:WorkFolders-Client" && e.State == BuildOperationState.Completed);
        Assert.Contains(fixture.Runner.Calls, c => c.Contains("/Export-Image") && c.Contains("/Compress:max"));
        using var failed = new BuildFixture();
        failed.Runner.FailIso = true;
        var error = await Assert.ThrowsAsync<BuildFailureException>(() => failed.Engine.BuildAsync(failed.Recipe,
            new ImmediateProgress<BuildProgress>(events.Add), CancellationToken.None));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(error.WorkDirectory, "operations.json")));
        Assert.Contains(json.RootElement.EnumerateArray(), e => e.GetProperty("Id").GetString() == "stage:Create ISO" &&
            e.GetProperty("State").GetString() == "Failed");
    }

    [Fact]
    public void Image_file_checks_refuse_symbolic_links()
    {
        using var fixture = new BuildFixture();
        var original = Path.Combine(fixture.Root, "original.hive");
        var link = Path.Combine(fixture.Root, "escape.hive");
        File.WriteAllText(original, "fixture");
        File.CreateSymbolicLink(link, original);
        Assert.Throws<InvalidDataException>(() => ImageFileAccess.ValidateFile(link));
        ImageFileAccess.ValidateFile(original);
    }

    [Theory]
    [InlineData(0x00040082u, "/Compress:max")]
    [InlineData(0x00020082u, "/Compress:fast")]
    [InlineData(0x00080082u, "/Compress:fast")]
    [InlineData(0x00000080u, "/Compress:none")]
    public void Intermediate_export_preserves_WIM_compression(uint flags, string expected)
    {
        using var fixture = new BuildFixture();
        var path = Path.Combine(fixture.Source, @"sources\install.wim");
        var header = File.ReadAllBytes(path);
        BitConverter.GetBytes(flags).CopyTo(header, 16);
        File.WriteAllBytes(path, header);
        Assert.Equal(expected, InstallationMedia.WorkingCompression(path));
    }

    [Fact]
    public void UI_error_text_strips_console_bars_without_hiding_the_error()
    {
        var text = "DISM failed\n[=====59.0%====]\nError: The payload is protected.";
        var displayed = BuildProgressText.ForDisplay(text);
        Assert.DoesNotContain("====", displayed);
        Assert.Contains("DISM failed", displayed);
        Assert.Contains("The payload is protected", displayed);
    }

    [Fact]
    public void Stage_progress_has_separate_export_mount_and_removal_ranges()
    {
        Assert.Equal((8d, 15d), BuildProgressText.Range("Export edition"));
        Assert.Equal((15d, 25d), BuildProgressText.Range("Mount"));
        Assert.Equal((32d, 44d), BuildProgressText.Range("Remove apps"));
        Assert.Equal((44d, 53d), BuildProgressText.Range("Optional features"));
        Assert.Equal((68d, 72d), BuildProgressText.Range("Core pruning"));
    }
}
