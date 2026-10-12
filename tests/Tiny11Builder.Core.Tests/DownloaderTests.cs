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

    private sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        public List<long> Offsets { get; } = [];
        public Uri? Redirect { get; init; }
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
            result.Headers.ETag = new EntityTagHeaderValue("\"stable-image\"");
            if (offset > 0) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return Task.FromResult(result);
        }
    }
}
