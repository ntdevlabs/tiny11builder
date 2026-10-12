using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class CancellationCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closure_reports_owned_mount_discard_before_logs_even_when_the_observer_throws(bool throws)
    {
        using var fixture = new BuildFixture();
        using var cancellation = new CancellationTokenSource();
        var timeline = new List<string>();
        fixture.Runner.OnCommand = args =>
        {
            if (args.Contains("/Get-ProvisionedAppxPackages")) cancellation.Cancel();
            if (args.Contains("/Discard")) timeline.Add("discard");
        };
        var observer = new ImmediateProgress<BuildProgress>(p =>
        {
            if (p.Stage != "Cancel cleanup") return;
            timeline.Add(p.OperationId!);
            if (throws) throw new IOException("Closure observer failed");
        });
        var failure = await Assert.ThrowsAsync<BuildFailureException>(() =>
            fixture.Engine.BuildAsync(fixture.Recipe, observer, cancellation.Token));
        Assert.True(failure.IsCancellation);
        Assert.Equal(["cancel:mount", "discard", "cancel:logs"], timeline);
        Assert.False(failure.RecoveryRequired);
        if (throws) Assert.Contains("Closure observer failed", failure.ToString());
    }
}
