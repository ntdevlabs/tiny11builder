using System.Net;
using System.Runtime.Versioning;
using Tiny11Builder.App;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class DownloadRemediationTests
{
    [Fact]
    public void A_download_problem_is_an_actionable_marker_without_losing_the_original_cause()
    {
        var download = new DownloadServiceException("Microsoft connection reset", new IOException("original reset"));
        Assert.Same(download, OperationOutcomes.DownloadServiceProblem(download));
        Assert.Same(download, OperationOutcomes.DownloadServiceProblem(new BuildFailureException("Build stopped", "job", download)));
        Assert.Null(OperationOutcomes.DownloadServiceProblem(new OperationCanceledException()));
        Assert.Null(OperationOutcomes.DownloadServiceProblem(new IOException("Disk full")));
        Assert.Equal(OfficialImageDownloader.DownloadPage, download.OfficialPage);
    }

    [Fact]
    public async Task Tool_download_retries_the_secure_reset_but_never_accepts_wrong_hash_bytes()
    {
        using var fixture = new BuildFixture();
        using var handler = new FaultHandler(new ToolHandler()).Throw(() => Faults.HandshakeReset());
        using var client = new HttpClient(handler);
        var waits = new RecordingDelay();
        var status = new List<string>();
        var directory = Path.Combine(fixture.Root, "tool-download");
        await Assert.ThrowsAsync<InvalidDataException>(() => new OscdimgAcquirer(client, () => "", waits.Wait)
            .EnsureAsync(directory, new ImmediateProgress<string>(status.Add), CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(handler.Requests[0].Tls12);
        Assert.True(handler.Requests[1].Tls12);
        Assert.Contains(status, item => item.Contains("TLS 1.2", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(directory));
    }

    private sealed class ToolHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    }
}
