using System.Runtime.Versioning;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

/// <summary>Core and component-payload removal delete WinSxS files, so they refuse an image that still has pending operations.</summary>
[SupportedOSPlatform("windows")]
public sealed class PendingPruneGuardTests
{
    private const string NetFx3Package = "Microsoft-Windows-NetFx3-OnDemand-Package~31bf3856ad364e35~amd64~~10.0.26300.1";
    private const string RollupPackage = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26300.9457.1.7";

    private static readonly (string Name, string State)[] SettledPackages = [(RollupPackage, "Installed"), (NetFx3Package, "Installed")];

    private static readonly (string Name, string State)[] SettledFeatures =
        [("NetFx3", "Enabled"), ("WorkFolders-Client", "Disabled with Payload Removed"), ("Printing-XPSServices-Features", "Disabled")];

    private static readonly ExpertAction Payload = new(ImageItemKind.ComponentPayload, "amd64_critical-component", ExpertActionMode.Remove);

    private static string Packages(params (string Name, string State)[] items) =>
        "Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.8972\r\n\r\nImage Version: 10.0.26300.9457\r\n\r\nPackages listing:\r\n\r\n" +
        string.Concat(items.Select(item => $"Package Identity : {item.Name}\r\nState : {item.State}\r\nRelease Type : Feature Pack\r\nInstall Time : \r\n\r\n")) +
        "The operation completed successfully.\r\n";

    private static string Features(params (string Name, string State)[] items) =>
        "Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.8972\r\n\r\nImage Version: 10.0.26300.9457\r\n\r\n" +
        "Features listing for package : Microsoft-Windows-Client-Desktop-Required-Package~31bf3856ad364e35~amd64~~10.0.26300.1\r\n\r\n" +
        string.Concat(items.Select(item => $"Feature Name : {item.Name}\r\nState : {item.State}\r\n\r\n")) +
        "The operation completed successfully.\r\n";

    private static Func<IReadOnlyList<string>, ProcessResult?> AfterNetFx3(string? packages, string? features)
    {
        var enabled = false;
        return args =>
        {
            enabled |= args.Contains("/Enable-Feature") && args.Contains("/FeatureName:NetFx3");
            if (!enabled) return null;
            if (packages is not null && args.Contains("/Get-Packages")) return new(0, packages);
            if (features is not null && args.Contains("/Get-Features")) return new(0, features);
            return null;
        };
    }

    private static (BuildEngine Engine, OverlapRunner Runner) EngineFor(BuildFixture fixture, Func<IReadOnlyList<string>, ProcessResult?> replace)
    {
        var runner = new OverlapRunner(new CommandResultRunner(fixture.Runner, replace));
        return (new(runner, new FakeMediaFactory(), fixture.Registry, fixture.Operations, new FakeHost()), runner);
    }

    private static void AddNetFx3Source(BuildFixture fixture)
    {
        var sxs = Path.Combine(fixture.Source, "sources", "sxs");
        Directory.CreateDirectory(sxs);
        File.WriteAllText(Path.Combine(sxs, "netfx3.cab"), "fixture");
    }

    private static BuildRecipe CoreRecipe(BuildFixture fixture) => fixture.Recipe with
    {
        Mode = BuildMode.Tiny11Core, Flavor = BuildFlavor.Core, CoreAcknowledged = true,
        SelectedOptions = CleanupCatalog.ForMode(BuildMode.Tiny11Core), EnableNetFx3 = true
    };

    private static BuildRecipe PayloadRecipe(BuildFixture fixture) => fixture.Recipe with
    {
        SelectedOptions = ["copilot-app"], ExpertAcknowledged = true, ExpertActions = [Payload], EnableNetFx3 = true
    };

    private static int NetFx3Index(BuildFixture fixture) => fixture.Events.FindIndex(e => e.Contains("/FeatureName:NetFx3"));

    private static void AssertNothingWasRemoved(BuildFixture fixture, BuildFailureException failure)
    {
        Assert.DoesNotContain(fixture.Events, e => e is "TrimCore" or "RemoveOriginalFiles" or "CleanRedundantData");
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Commit"));
        Assert.DoesNotContain(fixture.Events, e => e.Contains("RevertPendingActions"));
        Assert.Contains(fixture.Events, e => e.Contains("/Unmount-Image") && e.Contains("/Discard"));
        Assert.Empty(fixture.Operations.LastActions);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.False(JobCheckpoint.Exists(failure.WorkDirectory));
    }

    [Theory]
    [InlineData("feature", "NetFx3", "Enable Pending")]
    [InlineData("feature", "WorkFolders-Client", "Disable Pending")]
    [InlineData("package", NetFx3Package, "Install Pending")]
    [InlineData("package", RollupPackage, "Uninstall Pending")]
    public async Task Core_is_stopped_after_NetFx3_and_before_any_removal_when_DISM_reports_a_pending_state(string kind, string name, string state)
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var sourceBefore = File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\install.wim"));
        var packages = kind == "package" ? SettledPackages.Where(item => item.Name != name).Prepend((name, state)).ToArray() : SettledPackages;
        var features = kind == "feature" ? SettledFeatures.Where(item => item.Name != name).Prepend((name, state)).ToArray() : SettledFeatures;
        var (engine, runner) = EngineFor(fixture, AfterNetFx3(Packages(packages), Features(features)));
        var recipe = CoreRecipe(fixture);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, CancellationToken.None));

        Assert.False(failure.IsCancellation);
        var cause = Assert.IsType<PendingOperationsException>(failure.InnerException);
        Assert.Null(cause.InnerException);
        Assert.Equal($"{kind} {name}: {state}", Assert.Single(cause.Evidence));
        Assert.Equal(PendingOperations.Refusal(recipe), cause.Message);
        Assert.Contains("Pending operation: " + cause.Evidence[0], File.ReadAllText(Path.Combine(failure.WorkDirectory, "build.log")));
        Assert.Equal(cause.Message, OperationOutcomes.Classify(OperationKind.Build, failure).Message);

        var cleanup = fixture.Events.FindIndex(e => e.Contains("/StartComponentCleanup"));
        var netFx3 = NetFx3Index(fixture);
        var listedPackages = fixture.Events.FindIndex(netFx3 + 1, e => e.Contains("/Get-Packages"));
        var listedFeatures = fixture.Events.FindIndex(netFx3 + 1, e => e.Contains("/Get-Features"));
        Assert.True(cleanup >= 0 && cleanup < netFx3 && netFx3 < listedPackages && listedPackages < listedFeatures);
        Assert.Equal(1, runner.MaximumActive);
        AssertNothingWasRemoved(fixture, failure);
        Assert.DoesNotContain(fixture.Events.Skip(listedFeatures + 1), e => e.Contains("/Get-Packages") || e.Contains("/Get-Features"));
        Assert.Equal(sourceBefore, File.ReadAllBytes(Path.Combine(fixture.Source, @"sources\install.wim")));
        var stages = JobScenarios.StageStates(failure.WorkDirectory);
        Assert.Equal("Completed", stages["Component cleanup"]);
        Assert.Equal("Completed", stages[".NET 3.5"]);
        Assert.Equal("Completed", stages["Windows settings"]);
        Assert.Equal("Failed", stages["Advanced payloads"]);
        Assert.Equal("Pending", stages["Core pruning"]);
    }

    [Fact]
    public async Task Custom_payload_removal_is_stopped_before_native_removals_even_with_cleanup_off()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var (engine, _) = EngineFor(fixture, AfterNetFx3(null, Features(("NetFx3", "Enable Pending"), ("WorkFolders-Client", "Disabled"))));
        var recipe = PayloadRecipe(fixture);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(recipe, null, CancellationToken.None));

        var cause = Assert.IsType<PendingOperationsException>(failure.InnerException);
        Assert.Equal("feature NetFx3: Enable Pending", Assert.Single(cause.Evidence));
        Assert.Equal(PendingOperations.Refusal(recipe), cause.Message);
        Assert.StartsWith("Component payloads were not removed", cause.Message);
        AssertNothingWasRemoved(fixture, failure);
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Cleanup-Image"));
        Assert.Equal("Failed", JobScenarios.StageStates(failure.WorkDirectory)["Advanced payloads"]);
    }

    [Theory]
    [InlineData("pending.xml")]
    [InlineData("reboot.xml")]
    public async Task A_pending_or_reboot_file_in_the_image_stops_the_removal_without_further_queries(string marker)
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        fixture.Runner.OnCommand = args =>
        {
            if (!args.Contains("/Enable-Feature")) return;
            var directory = Path.Combine(args.Single(arg => arg.StartsWith("/Image:", StringComparison.Ordinal))["/Image:".Length..], @"Windows\WinSxS");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, marker), "<pending/>");
        };
        var (engine, _) = EngineFor(fixture, _ => null);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(CoreRecipe(fixture), null, CancellationToken.None));

        var cause = Assert.IsType<PendingOperationsException>(failure.InnerException);
        Assert.Equal($@"Windows\WinSxS\{marker} exists", Assert.Single(cause.Evidence));
        Assert.DoesNotContain(fixture.Events.Skip(NetFx3Index(fixture) + 1), e => e.Contains("/Get-Packages") || e.Contains("/Get-Features"));
        AssertNothingWasRemoved(fixture, failure);
    }

    [Theory]
    [InlineData("/Get-Packages", "The operation completed successfully.")]
    [InlineData("/Get-Packages", "Package Identity : A\r\nPackage Identity : B\r\nState : Installed\r\n")]
    [InlineData("/Get-Packages", "Fehler: Das Abbild konnte nicht gelesen werden.")]
    [InlineData("/Get-Features", "The operation completed successfully.")]
    [InlineData("/Get-Features", "Feature Name : A\r\nFeature Name : B\r\nState : Enabled\r\n")]
    [InlineData("/Get-Features", "")]
    public async Task An_unreadable_DISM_listing_is_an_error_and_never_means_zero_pending_operations(string command, string output)
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var enabled = false;
        var (engine, _) = EngineFor(fixture, args =>
        {
            enabled |= args.Contains("/Enable-Feature");
            return enabled && args.Contains(command) ? new ProcessResult(0, output) : null;
        });
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(CoreRecipe(fixture), null, CancellationToken.None));

        var error = Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Contains("unreadable", error.Message);
        Assert.Contains("cannot be ruled out", error.Message);
        AssertNothingWasRemoved(fixture, failure);
        Assert.Equal("Failed", JobScenarios.StageStates(failure.WorkDirectory)["Advanced payloads"]);
    }

    [Fact]
    public async Task A_failing_DISM_query_is_the_normal_error_and_removes_nothing()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var enabled = false;
        var (engine, _) = EngineFor(fixture, args =>
        {
            enabled |= args.Contains("/Enable-Feature");
            return enabled && args.Contains("/Get-Packages") ? new ProcessResult(5, "Access is denied.") : null;
        });
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(CoreRecipe(fixture), null, CancellationToken.None));

        Assert.Equal(5, Assert.IsType<DismCommandException>(failure.InnerException).ExitCode);
        AssertNothingWasRemoved(fixture, failure);
    }

    [Fact]
    public async Task Only_exact_pending_states_block_and_the_check_runs_once_and_serially_before_every_removal()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var packages = Packages(("Pending-Package~31bf3856ad364e35~amd64~~1.0", "Installed"), (RollupPackage, "Superseded"),
            (NetFx3Package, "Installed"), ("Other~31bf3856ad364e35~amd64~~1.0", "Staged"));
        var features = Features(("NetFx3", "Enabled"), ("Pending-Feature", "Disabled"), ("WorkFolders-Client", "Disabled with Payload Removed"));
        var (engine, runner) = EngineFor(fixture, AfterNetFx3(packages, features));
        var result = await engine.BuildAsync(CoreRecipe(fixture), null, CancellationToken.None);

        Assert.Empty(result.Warnings);
        var netFx3 = NetFx3Index(fixture);
        var listedPackages = fixture.Events.FindIndex(netFx3 + 1, e => e.Contains("/Get-Packages"));
        var listedFeatures = fixture.Events.FindIndex(netFx3 + 1, e => e.Contains("/Get-Features"));
        Assert.Single(fixture.Events.Skip(netFx3 + 1), e => e.Contains("/Get-Packages"));
        Assert.Single(fixture.Events.Skip(netFx3 + 1), e => e.Contains("/Get-Features"));
        Assert.True(netFx3 < listedPackages && listedPackages < listedFeatures);
        Assert.True(listedFeatures < fixture.Events.IndexOf("RemoveOriginalFiles"));
        Assert.True(fixture.Events.IndexOf("RemoveOriginalFiles") < fixture.Events.IndexOf("TrimCore"));
        Assert.Equal(1, runner.MaximumActive);
        Assert.Contains("No pending operations found (4 packages and 3 features checked).",
            File.ReadAllText(Path.Combine(result.WorkDirectory, "build.log")));
    }

    [Fact]
    public async Task Custom_payload_removal_proceeds_when_nothing_is_pending_and_keeps_cleanup_off()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var (engine, _) = EngineFor(fixture, AfterNetFx3(Packages(SettledPackages), Features(SettledFeatures)));
        var result = await engine.BuildAsync(PayloadRecipe(fixture), null, CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { Payload }, fixture.Operations.LastActions);
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Cleanup-Image"));
        var listedFeatures = fixture.Events.FindIndex(NetFx3Index(fixture) + 1, e => e.Contains("/Get-Features"));
        Assert.True(listedFeatures > NetFx3Index(fixture) && listedFeatures < fixture.Events.IndexOf("RemoveOriginalFiles"));
    }

    [Fact]
    public async Task A_serviceable_build_with_NetFx3_and_cleanup_off_is_not_checked_and_keeps_its_Edge_removal()
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        var (engine, _) = EngineFor(fixture, AfterNetFx3(Packages((NetFx3Package, "Install Pending")), Features(("NetFx3", "Enable Pending"))));
        var recipe = fixture.Recipe with { SelectedOptions = ["copilot-app", "edge-files"], EnableNetFx3 = true };
        var result = await engine.BuildAsync(recipe, null, CancellationToken.None);

        Assert.True(File.Exists(result.IsoPath));
        Assert.Empty(result.Warnings);
        Assert.DoesNotContain(fixture.Events.Skip(NetFx3Index(fixture) + 1), e => e.Contains("/Get-Packages") || e.Contains("/Get-Features"));
        Assert.DoesNotContain(fixture.Events, e => e.Contains("/Cleanup-Image"));
        Assert.Contains("RemoveOriginalFiles", fixture.Events);
        Assert.All(JobScenarios.StageStates(result.WorkDirectory).Values, state => Assert.Equal("Completed", state));
    }

    [Theory]
    [InlineData("/Get-Packages")]
    [InlineData("/Get-Features")]
    public async Task Cancellation_during_the_check_never_reaches_the_removals(string command)
    {
        using var fixture = new BuildFixture();
        AddNetFx3Source(fixture);
        using var cancel = new CancellationTokenSource();
        var enabled = false;
        fixture.Runner.OnCommand = args =>
        {
            enabled |= args.Contains("/Enable-Feature");
            if (enabled && args.Contains(command)) cancel.Cancel();
        };
        var (engine, _) = EngineFor(fixture, _ => null);
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() => engine.BuildAsync(CoreRecipe(fixture), null, cancel.Token));

        Assert.True(failure.IsCancellation);
        Assert.Equal("Cancelled", JobScenarios.StageStates(failure.WorkDirectory)["Advanced payloads"]);
        AssertNothingWasRemoved(fixture, failure);
    }

    [Theory]
    [InlineData("Install Pending", true)]
    [InlineData("Uninstall Pending", true)]
    [InlineData("Enable Pending", true)]
    [InlineData("Disable Pending", true)]
    [InlineData("disable   PENDING", true)]
    [InlineData("Installed", false)]
    [InlineData("Superseded", false)]
    [InlineData("Staged", false)]
    [InlineData("Disabled with Payload Removed", false)]
    [InlineData("Pending Removal Complete", false)]
    public void Only_a_state_that_ends_in_Pending_is_pending(string state, bool pending) =>
        Assert.Equal(pending, PendingOperations.IsPending(state));

    [Fact]
    public async Task The_scan_reports_packages_before_features_and_markers_before_any_query()
    {
        using var fixture = new BuildFixture();
        var runner = new ListingRunner(Packages((NetFx3Package, "Install Pending")), Features(("WorkFolders-Client", "Disable Pending")));
        var dism = new DismClient(@"C:\mock\dism.exe", runner);
        var mount = Path.Combine(fixture.Root, "mount");
        Directory.CreateDirectory(Path.Combine(mount, @"Windows\WinSxS"));

        var scan = await PendingOperations.ScanAsync(dism, mount, CancellationToken.None);
        Assert.Equal(new[] { $"package {NetFx3Package}: Install Pending", "feature WorkFolders-Client: Disable Pending" }, scan.Pending);
        Assert.Equal((1, 1), (scan.Packages, scan.Features));
        Assert.Equal(new[] { "/English /Image:" + mount + " /Get-Packages", "/English /Image:" + mount + " /Get-Features" }, runner.Commands);

        File.WriteAllText(Path.Combine(mount, @"Windows\WinSxS\reboot.xml"), "<reboot/>");
        File.WriteAllText(Path.Combine(mount, @"Windows\WinSxS\pending.xml"), "<pending/>");
        runner.Commands.Clear();
        var marked = await PendingOperations.ScanAsync(dism, mount, CancellationToken.None);
        Assert.Equal(new[] { @"Windows\WinSxS\pending.xml exists", @"Windows\WinSxS\reboot.xml exists" }, marked.Pending);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public void The_refusal_is_one_message_that_fits_the_notice_and_explains_the_way_out()
    {
        foreach (var flavor in new[] { BuildFlavor.Core, BuildFlavor.Regular })
        {
            var message = PendingOperations.Refusal(new() { Flavor = flavor });
            Assert.True(message.Length <= ErrorText.NoticeLimit, message);
            Assert.Contains("first boot", message);
            Assert.Contains("Build without .NET 3.5", message);
            Assert.Contains("serviceable Tiny11", message);
            Assert.EndsWith("Pending actions are never reverted.", message);
        }
        Assert.StartsWith("Core stopped before trimming any component", PendingOperations.Refusal(new() { Flavor = BuildFlavor.Core }));
        Assert.StartsWith("Component payloads were not removed", PendingOperations.Refusal(new()));
    }

    private sealed class ListingRunner(string packages, string features) : IProcessRunner
    {
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output,
            CancellationToken cancellationToken, bool finishOnCancellation = true)
        {
            Commands.Add(string.Join(" ", arguments));
            return Task.FromResult(new ProcessResult(0, arguments.Contains("/Get-Packages") ? packages : features));
        }
    }
}

/// <summary>Delays the two listing commands and records how many DISM commands ever ran at the same moment.</summary>
internal sealed class OverlapRunner(IProcessRunner inner) : IProcessRunner
{
    private int active;
    private int maximum;

    public int MaximumActive => Volatile.Read(ref maximum);

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output,
        CancellationToken cancellationToken, bool finishOnCancellation = true)
    {
        var now = Interlocked.Increment(ref active);
        int seen;
        while (now > (seen = Volatile.Read(ref maximum))) Interlocked.CompareExchange(ref maximum, now, seen);
        try
        {
            if (arguments.Contains("/Get-Packages") || arguments.Contains("/Get-Features")) await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
            return await inner.RunAsync(executable, arguments, output, cancellationToken, finishOnCancellation).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref active); }
    }
}
