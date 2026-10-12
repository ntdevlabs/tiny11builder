using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class AutomaticMediaTests
{
    [Fact]
    public void Defaults_to_exact_Pro_then_index_one_and_preserves_restored_choice()
    {
        var home = CompatibilityTests.Image() with { Index = 1, Name = "Windows 11 Home", Edition = "Core" };
        var proN = home with { Index = 2, Name = "Windows 11 Pro N", Edition = "ProfessionalN" };
        var pro = home with { Index = 6, Name = "Windows 11 Pro", Edition = "Professional" };
        Assert.Equal(2, ImageDefaults.SelectPosition([home, proN, pro]));
        Assert.Equal(1, ImageDefaults.SelectPosition([proN, home]));
        Assert.Equal(0, ImageDefaults.SelectPosition([home, pro], 1));
        Assert.Throws<InvalidDataException>(() => ImageDefaults.SelectPosition([proN]));
        Assert.Throws<InvalidDataException>(() => ImageDefaults.SelectPosition([]));
    }

    [Theory]
    [InlineData("x64", "en-US", "English", 1)]
    [InlineData("arm64", "fi-FI", "Finnish", 2)]
    public async Task Resolves_current_catalog_language_and_architecture_without_user_input(string arch, string culture, string language, int type)
    {
        using var handler = new CatalogHandler(language, type);
        using var client = new HttpClient(handler);
        var media = await new LatestWindowsMedia(client).ResolveAsync(arch, CultureInfo.GetCultureInfo(culture), null, CancellationToken.None);
        Assert.Equal(arch, media.Architecture);
        Assert.Equal(language, media.Language);
        Assert.Equal("Windows11.iso", media.FileName);
        Assert.Equal("software.download.prss.microsoft.com", media.Url.Host);
        Assert.Contains(handler.Requests, uri => uri.Contains("productEditionId=4321", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.Contains("SKU=12345", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Microsoft_errors_and_external_redirects_are_not_success_fallbacks()
    {
        using var handler = new CatalogHandler("English", 1) { Refused = true };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new LatestWindowsMedia(client).ResolveAsync("x64",
            CultureInfo.GetCultureInfo("en-US"), null, CancellationToken.None));
        using var redirectHandler = new CatalogHandler("English", 1) { ExternalRedirect = true };
        using var redirected = new HttpClient(redirectHandler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LatestWindowsMedia(redirected).ResolveAsync("x64",
            CultureInfo.GetCultureInfo("en-US"), null, CancellationToken.None));
        Assert.Single(redirectHandler.Requests);
    }

    [Fact]
    public void Catalog_drift_is_reported_instead_of_using_a_stale_release_ID() =>
        Assert.Throws<InvalidDataException>(() => LatestWindowsMedia.ParseProductId("<html>No product selector</html>"));

    [Fact]
    public async Task Unapproved_ISO_tool_bytes_are_never_saved_or_executed()
    {
        using var fixture = new BuildFixture();
        using var handler = new ToolHandler(Encoding.ASCII.GetBytes("not oscdimg"));
        using var client = new HttpClient(handler);
        var folder = Path.Combine(fixture.Root, "tools");
        await Assert.ThrowsAsync<InvalidDataException>(() => new OscdimgAcquirer(client, () => "")
            .EnsureAsync(folder, null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(folder, "oscdimg.exe")));
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task Live_official_catalog_and_pinned_tool_when_configured(string architecture)
    {
        if (Environment.GetEnvironmentVariable("TINY11_TEST_DOWNLOADS") != "1") return;
        using var catalog = LatestWindowsMedia.CreateClient();
        var media = await new LatestWindowsMedia(catalog).ResolveAsync(architecture, CultureInfo.GetCultureInfo("en-US"), null, CancellationToken.None);
        Assert.EndsWith(".iso", media.Url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        using var fixture = new BuildFixture();
        using var toolClient = OfficialImageDownloader.CreateClient();
        var path = await new OscdimgAcquirer(toolClient, () => "").EnsureAsync(Path.Combine(fixture.Root, "tools"), null, CancellationToken.None);
        ToolLocator.Validate(path, "oscdimg.exe");
        Assert.True(File.Exists(path));
        using var cachedClient = new HttpClient(new ToolHandler([0]));
        var cached = await new OscdimgAcquirer(cachedClient, () => "").EnsureAsync(Path.GetDirectoryName(path)!, null, CancellationToken.None);
        Assert.Equal(path, cached);
    }

    private sealed class ToolHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }

    private sealed class CatalogHandler(string language, int downloadType) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool Refused { get; init; }
        public bool ExternalRedirect { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            if (ExternalRedirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
                redirect.Headers.Location = new("https://untrusted.example/catalog");
                return Task.FromResult(redirect);
            }
            var body = uri.Contains("/software-download/windows11", StringComparison.Ordinal) ?
                "<select id=\"product-edition\"><option value=\"null\">Select</option><option value=\"4321\">Latest</option></select>" :
                uri.Contains("/mdt.js", StringComparison.Ordinal) ? "?w=ABC123&rticks=\"12345" :
                uri.Contains("getskuinformationbyproductedition", StringComparison.Ordinal) ?
                    Refused ? """{"Errors":[{"Value":"Download denied"}]}""" :
                    $$"""{"Errors":null,"Skus":[{"Id":"12345","Language":"{{language}}"}]}""" :
                uri.Contains("GetProductDownloadLinksBySku", StringComparison.Ordinal) ?
                    $$"""{"ProductDownloadOptions":[{"DownloadType":{{downloadType}},"Uri":"https://software.download.prss.microsoft.com/Windows11.iso?temporary=token","LocalizedProductDisplayName":"Latest Windows 11"}]}""" : "ok";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) });
        }
    }
}
