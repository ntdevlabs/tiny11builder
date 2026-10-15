using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class DownloaderTests
{
    private const string Url = "https://software.download.prss.microsoft.com/windows11.iso?token=expires";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("https://evil.example/windows11.iso")]
    [InlineData("https://software.download.prss.microsoft.com.evil.example/windows11.iso")]
    [InlineData("http://download.microsoft.com/windows11.iso")]
    [InlineData("https://user:password@download.microsoft.com/windows11.iso")]
    [InlineData("https://download.microsoft.com:444/windows11.iso")]
    [InlineData("https://download.microsoft.com/windows11.exe")]
    public void Rejects_unofficial_hosts_insecure_transport_credentials_and_non_ISOs(string url) =>
        Assert.Throws<InvalidDataException>(() => OfficialImageDownloader.ValidateUrl(url));

    [Fact]
    public void Accepts_only_correct_hash_shape()
    {
        Assert.Null(OfficialImageDownloader.ValidateHash(""));
        Assert.Equal(new string('A', 64), OfficialImageDownloader.ValidateHash(new string('a', 64)));
        Assert.Throws<InvalidDataException>(() => OfficialImageDownloader.ValidateHash("1234"));
        Assert.Throws<InvalidDataException>(() => OfficialImageDownloader.ValidateHash(new string('G', 64)));
    }

    [Fact]
    public async Task Downloads_to_partial_then_publishes_only_after_hash_matches()
    {
        using var fixture = new BuildFixture();
        var bytes = BuildFixture.IsoBytes();
        using var handler = new ImageHandler(bytes);
        using var client = new HttpClient(handler);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var result = await new OfficialImageDownloader(client).DownloadAsync(Url, fixture.Recipe.OutputPath, hash, null, CancellationToken.None);
        Assert.True(result.VerifiedAgainstPublishedHash);
        Assert.Equal(hash, result.Sha256);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.False(File.Exists(result.Path + ".part"));
        Assert.False(File.Exists(result.Path + ".part.json"));
    }

    [Fact]
    public async Task Checksum_mismatch_is_rejected_without_an_ISO_success_file()
    {
        using var fixture = new BuildFixture();
        using var handler = new ImageHandler(BuildFixture.IsoBytes());
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialImageDownloader(client).DownloadAsync(
            Url, fixture.Recipe.OutputPath, new string('A', 64), null, CancellationToken.None));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        Assert.True(File.Exists(fixture.Recipe.OutputPath + ".part"));
    }

    [Fact]
    public async Task Cancelled_download_resumes_with_ETag_and_a_refreshed_signed_URL()
    {
        using var fixture = new BuildFixture();
        var bytes = BuildFixture.IsoBytes(2 * 1024 * 1024);
        using var handler = new ImageHandler(bytes);
        using var client = new HttpClient(handler);
        var downloader = new OfficialImageDownloader(client);
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress<DownloadProgress>(p => { if (p.Bytes >= bytes.Length / 2) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Url, fixture.Recipe.OutputPath,
            null, progress, cancellation.Token));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        var offset = new FileInfo(fixture.Recipe.OutputPath + ".part").Length;
        Assert.InRange(offset, 1, bytes.Length - 1);
        Assert.DoesNotContain("expires", File.ReadAllText(fixture.Recipe.OutputPath + ".part.json"));
        var result = await downloader.DownloadAsync(Url.Replace("expires", "refreshed", StringComparison.Ordinal), fixture.Recipe.OutputPath,
            Convert.ToHexString(SHA256.HashData(bytes)), null, CancellationToken.None);
        Assert.Contains(offset, handler.Offsets);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.DoesNotContain("expires", File.Exists(result.Path + ".part.json") ? File.ReadAllText(result.Path + ".part.json") : "");
    }

    [Fact]
    public async Task Redirect_cannot_escape_the_Microsoft_allowlist()
    {
        using var fixture = new BuildFixture();
        using var handler = new ImageHandler(BuildFixture.IsoBytes()) { Redirect = new("https://evil.example/windows11.iso") };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialImageDownloader(client).DownloadAsync(
            Url, fixture.Recipe.OutputPath, null, null, CancellationToken.None));
        Assert.Single(handler.Offsets);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Fact]
    public async Task HTML_or_arbitrary_data_is_not_published_as_an_ISO()
    {
        using var fixture = new BuildFixture();
        using var handler = new ImageHandler(new byte[65536]);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialImageDownloader(client).DownloadAsync(
            Url, fixture.Recipe.OutputPath, null, null, CancellationToken.None));
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("eof")]
    public async Task A_reset_TLS_handshake_is_retried_over_TLS12_and_the_download_completes(string kind)
    {
        using var fixture = new BuildFixture();
        var bytes = BuildFixture.IsoBytes();
        using var image = new ImageHandler(bytes);
        using var faults = new FaultHandler(image).Throw(() => Faults.Handshake(kind));
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var messages = new List<string>();
        var result = await new OfficialImageDownloader(client, waits.Wait).DownloadAsync(Url, fixture.Recipe.OutputPath,
            Convert.ToHexString(SHA256.HashData(bytes)), null, CancellationToken.None, new ImmediateProgress<string>(messages.Add)).WaitAsync(Bound);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Equal([false, true], faults.Requests.Select(request => request.Tls12));
        Assert.Equal([TimeSpan.FromSeconds(1)], waits.Waits);
        Assert.Contains(messages, message => message.Contains("Retrying with TLS 1.2") && message.Contains("attempt 2 of 4"));
        Assert.DoesNotContain(messages, message => message.Contains("expires"));   // the signed link's token
    }

    [Fact]
    public async Task Busy_answers_before_the_first_byte_are_retried_on_the_same_protocol()
    {
        using var fixture = new BuildFixture();
        var bytes = BuildFixture.IsoBytes();
        using var image = new ImageHandler(bytes);
        using var faults = new FaultHandler(image)
            .Answer(() => Faults.Status(HttpStatusCode.ServiceUnavailable))
            .Answer(() => Faults.Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 2));
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var result = await new OfficialImageDownloader(client, waits.Wait).DownloadAsync(Url, fixture.Recipe.OutputPath,
            null, null, CancellationToken.None).WaitAsync(Bound);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
        Assert.Equal([1, 2], waits.Waits.Select(wait => (int)wait.TotalSeconds));
        Assert.All(faults.Requests, request => Assert.False(request.Tls12));
    }

    [Fact]
    public async Task A_rejected_certificate_stops_the_download_before_anything_is_written()
    {
        using var fixture = new BuildFixture();
        using var image = new ImageHandler(BuildFixture.IsoBytes());
        using var faults = new FaultHandler(image).Throw(() => Faults.RejectedCertificate());
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => new OfficialImageDownloader(client, waits.Wait)
            .DownloadAsync(Url, fixture.Recipe.OutputPath, null, null, CancellationToken.None).WaitAsync(Bound));
        Assert.Single(faults.Requests);
        Assert.False(faults.Requests[0].Tls12);
        Assert.Empty(waits.Waits);
        Assert.Contains("never bypasses certificate checks", error.Message);
        Assert.DoesNotContain("expires", error.Message);
        Assert.False(File.Exists(fixture.Recipe.OutputPath + ".part"));
        Assert.False(File.Exists(fixture.Recipe.OutputPath + ".part.json"));
    }

    [Fact]
    public async Task A_refusal_that_retrying_cannot_fix_is_reported_without_the_signed_link()
    {
        using var fixture = new BuildFixture();
        using var image = new ImageHandler(BuildFixture.IsoBytes());
        using var faults = new FaultHandler(image).Answer(() => Faults.Status(HttpStatusCode.Forbidden));
        using var client = new HttpClient(faults);
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => new OfficialImageDownloader(client, new RecordingDelay().Wait)
            .DownloadAsync(Url, fixture.Recipe.OutputPath, null, null, CancellationToken.None).WaitAsync(Bound));
        Assert.Single(faults.Requests);
        Assert.Contains("(software.download.prss.microsoft.com) refused the request (HTTP 403)", error.Message);
        Assert.DoesNotContain("expires", error.Message);
        Assert.DoesNotContain("token", error.Message);
        Assert.False(File.Exists(fixture.Recipe.OutputPath + ".part"));
    }

    [Fact]
    public async Task A_connection_cut_in_the_body_keeps_the_partial_file_and_the_next_attempt_resumes_it()
    {
        using var fixture = new BuildFixture();
        var bytes = BuildFixture.IsoBytes(2 * 1024 * 1024);
        using var image = new ImageHandler(bytes) { ResetAfter = 700_000 };
        using var client = new HttpClient(image);
        var downloader = new OfficialImageDownloader(client, new RecordingDelay().Wait);
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => downloader.DownloadAsync(Url, fixture.Recipe.OutputPath,
            null, null, CancellationToken.None).WaitAsync(Bound));
        Assert.Contains("partial download is kept", error.Message);
        Assert.DoesNotContain("expires", error.Message);
        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.Equal(700_000, new FileInfo(fixture.Recipe.OutputPath + ".part").Length);
        Assert.False(File.Exists(fixture.Recipe.OutputPath));
        var result = await downloader.DownloadAsync(Url, fixture.Recipe.OutputPath, Convert.ToHexString(SHA256.HashData(bytes)),
            null, CancellationToken.None).WaitAsync(Bound);
        Assert.Contains(700_000L, image.Offsets);
        Assert.Equal(bytes, File.ReadAllBytes(result.Path));
    }

    [Fact]
    public async Task Cancellation_is_immediate_while_connecting_and_while_waiting_to_retry()
    {
        using var fixture = new BuildFixture();
        using var image = new ImageHandler(BuildFixture.IsoBytes());
        using var hung = new FaultHandler(image).Hang();
        using var hungClient = new HttpClient(hung);
        using var duringRequest = new CancellationTokenSource();
        var connecting = new OfficialImageDownloader(hungClient, new RecordingDelay().Wait)
            .DownloadAsync(Url, fixture.Recipe.OutputPath, null, null, duringRequest.Token);
        await TestWait.Until(() => hung.Requests.Count == 1);
        duringRequest.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Bound));
        Assert.Single(hung.Requests);

        using var resetting = new FaultHandler(image).Throw(() => Faults.HandshakeReset(), count: 10);
        using var resettingClient = new HttpClient(resetting);
        using var duringWait = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        Task Wait(TimeSpan wait, CancellationToken token)
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        }
        var retrying = new OfficialImageDownloader(resettingClient, Wait)
            .DownloadAsync(Url, fixture.Recipe.OutputPath, null, null, duringWait.Token);
        await waiting.Task.WaitAsync(Bound);
        duringWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retrying.WaitAsync(Bound));
        Assert.Single(resetting.Requests);
        Assert.False(File.Exists(fixture.Recipe.OutputPath + ".part"));
    }

    [Fact]
    public async Task A_redirect_that_follows_a_retry_still_cannot_leave_the_Microsoft_allowlist()
    {
        using var fixture = new BuildFixture();
        using var image = new ImageHandler(BuildFixture.IsoBytes()) { Redirect = new("https://evil.example/windows11.iso") };
        using var faults = new FaultHandler(image).Throw(() => Faults.HandshakeReset());
        using var client = new HttpClient(faults);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialImageDownloader(client, new RecordingDelay().Wait)
            .DownloadAsync(Url, fixture.Recipe.OutputPath, null, null, CancellationToken.None).WaitAsync(Bound));
        Assert.Equal(2, faults.Requests.Count);
        Assert.Single(image.Offsets);
        Assert.DoesNotContain(faults.Requests, request => request.Uri.IdnHost == "evil.example");
    }

    private sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        private bool cut;
        public List<long> Offsets { get; } = [];
        public Uri? Redirect { get; init; }
        // The first full response is cut after this many bytes, the way a connection reset in the body cuts it.
        public int? ResetAfter { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            Offsets.Add(offset);
            if (Redirect is not null)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = Redirect;
                return Task.FromResult(redirect);
            }
            var result = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes[(int)offset..]) };
            if (ResetAfter is int limit && offset == 0 && !cut)
            {
                cut = true;
                result.Content = new StreamContent(new ResettingStream(bytes[..limit], Faults.ResponseEnded));
                result.Content.Headers.ContentLength = bytes.Length;
            }
            result.Headers.ETag = new EntityTagHeaderValue("\"stable-image\"");
            if (offset > 0) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return Task.FromResult(result);
        }
    }
}
