using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class AutomaticMediaTests
{
    private static readonly string[] CatalogHosts = ["www.microsoft.com", "vlscppe.microsoft.com", "ov-df.microsoft.com"];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static Task<OfficialMedia> Resolve(HttpClient client, Func<TimeSpan, CancellationToken, Task> wait, string architecture = "x64",
        IProgress<string>? progress = null, CancellationToken token = default) =>
        new LatestWindowsMedia(client, wait).ResolveAsync(architecture, CultureInfo.GetCultureInfo("en-US"), progress, token).WaitAsync(Bound);
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
        var refusal = await Assert.ThrowsAsync<DownloadServiceException>(() => new LatestWindowsMedia(client).ResolveAsync("x64",
            CultureInfo.GetCultureInfo("en-US"), null, CancellationToken.None));
        Assert.Contains("Download denied", refusal.Message);
        Assert.Contains("No third-party mirror will be used", refusal.Message);
        using var redirectHandler = new CatalogHandler("English", 1) { ExternalRedirect = true };
        using var redirected = new HttpClient(redirectHandler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LatestWindowsMedia(redirected).ResolveAsync("x64",
            CultureInfo.GetCultureInfo("en-US"), null, CancellationToken.None));
        Assert.Single(redirectHandler.Requests);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("aborted")]
    [InlineData("eof")]
    public async Task A_reset_TLS_handshake_is_retried_over_TLS12_and_the_resolution_completes_in_one_session(string kind)
    {
        using var catalog = new CatalogHandler("English", 1);
        using var faults = new FaultHandler(catalog).Throw(() => Faults.Handshake(kind));
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var messages = new List<string>();
        var media = await Resolve(client, waits.Wait, progress: new ImmediateProgress<string>(messages.Add));
        Assert.Equal("Windows11.iso", media.FileName);
        Assert.Equal(catalog.Requests.Count + 1, faults.Requests.Count);   // the reset attempt is the only extra request
        Assert.False(faults.Requests[0].Tls12);
        Assert.Equal(faults.Requests[0].Uri, faults.Requests[1].Uri);
        Assert.All(faults.Requests.Skip(1), request => Assert.True(request.Tls12));   // the protocol that worked is kept
        Assert.Equal([TimeSpan.FromSeconds(1)], waits.Waits);
        Assert.Contains(messages, message => message.Contains("closed the secure connection") &&
            message.Contains("Retrying with TLS 1.2") && message.Contains("attempt 2 of 4"));
        Assert.All(faults.Requests, request => Assert.Contains(request.Uri.IdnHost, CatalogHosts));
        var sessions = faults.Requests.Select(request => Regex.Match(request.Uri.Query, "(?i)session_?id=([0-9a-f-]+)"))
            .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct().ToArray();
        Assert.Single(sessions);   // retries never start a second session
        Assert.DoesNotContain(messages, message => message.Contains(sessions[0], StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task Resets_that_never_clear_stop_after_four_attempts_with_an_actionable_failure_that_keeps_its_cause(string architecture)
    {
        using var catalog = new CatalogHandler("English", architecture == "arm64" ? 2 : 1);
        using var faults = new FaultHandler(catalog).Throw(() => Faults.HandshakeReset(), count: 10);
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var messages = new List<string>();
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() =>
            Resolve(client, waits.Wait, architecture, new ImmediateProgress<string>(messages.Add)));
        Assert.Equal(4, faults.Requests.Count);
        Assert.Equal([false, true, false, true], faults.Requests.Select(request => request.Tls12));
        Assert.Equal([1, 2, 4], waits.Waits.Select(wait => (int)wait.TotalSeconds));
        Assert.Contains("(www.microsoft.com) closed the secure connection on all 4 attempts, including TLS 1.2", error.Message);
        Assert.Contains("local ISO", error.Message);
        Assert.Contains("official download page", error.Message);
        Assert.Equal(architecture == "arm64" ? OfficialImageDownloader.ArmDownloadPage : OfficialImageDownloader.DownloadPage, error.OfficialPage);
        var inner = Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(HttpRequestError.SecureConnectionError, inner.HttpRequestError);
        Assert.NotNull(inner.StackTrace);
        Assert.Contains(messages, message => message.Contains("attempt 4 of 4"));
        Assert.True(error.Message.Length <= 280, error.Message);   // fits one paragraph of a UI notice, so the advice is never clipped
    }

    [Fact]
    public async Task Busy_service_answers_are_retried_without_changing_protocol_and_Retry_After_is_honoured()
    {
        using var catalog = new CatalogHandler("English", 1);
        using var faults = new FaultHandler(catalog)
            .Answer(() => Faults.Status(HttpStatusCode.ServiceUnavailable, retryAfterSeconds: 3))
            .Answer(() => Faults.Status(HttpStatusCode.TooManyRequests));
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var messages = new List<string>();
        await Resolve(client, waits.Wait, progress: new ImmediateProgress<string>(messages.Add));
        Assert.Equal([3, 2], waits.Waits.Select(wait => (int)wait.TotalSeconds));   // Retry-After beats the 1 s step; the 429 has none
        Assert.All(faults.Requests, request => Assert.False(request.Tls12));
        Assert.Contains(messages, message => message.Contains("busy or unavailable (HTTP 503)") && message.Contains("in 3 s"));
        Assert.Contains(messages, message => message.Contains("busy or unavailable (HTTP 429)"));
    }

    [Fact]
    public async Task A_rejected_certificate_is_neither_retried_nor_downgraded()
    {
        using var catalog = new CatalogHandler("English", 1);
        using var faults = new FaultHandler(catalog).Throw(() => Faults.RejectedCertificate());
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => Resolve(client, waits.Wait));
        Assert.Single(faults.Requests);
        Assert.DoesNotContain(faults.Requests, request => request.Tls12);
        Assert.Empty(waits.Waits);
        Assert.Contains("never bypasses certificate checks", error.Message);
        var inner = Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.IsType<System.Security.Authentication.AuthenticationException>(inner.InnerException);
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("socket")]
    public async Task A_connection_reset_while_the_response_body_is_read_is_retried_on_the_same_protocol(string shape)
    {
        using var catalog = new CatalogHandler("English", 1);
        Exception Reset() => shape == "ended" ? Faults.ResponseEnded() :
            new IOException("Unable to read data from the transport connection.", new System.Net.Sockets.SocketException(10054));
        using var faults = new FaultHandler(catalog).Answer(() => Faults.BrokenBody("<select id=\"product-edition\"><option value=\"4321\">", Reset));
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var media = await Resolve(client, waits.Wait);
        Assert.Equal("Windows11.iso", media.FileName);
        Assert.Equal(faults.Requests[0].Uri, faults.Requests[1].Uri);
        Assert.All(faults.Requests, request => Assert.False(request.Tls12));
        Assert.Equal([TimeSpan.FromSeconds(1)], waits.Waits);
    }

    [Fact]
    public async Task Cancellation_is_immediate_while_a_request_hangs_and_while_waiting_to_retry()
    {
        using var catalog = new CatalogHandler("English", 1);
        using var hung = new FaultHandler(catalog).Hang();
        using var hungClient = new HttpClient(hung);
        using var duringRequest = new CancellationTokenSource();
        var waits = new RecordingDelay();
        var pending = Resolve(hungClient, waits.Wait, token: duringRequest.Token);
        await TestWait.Until(() => hung.Requests.Count == 1);
        duringRequest.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(hung.Requests);
        Assert.Empty(waits.Waits);

        using var resetting = new FaultHandler(catalog).Throw(() => Faults.HandshakeReset(), count: 10);
        using var resettingClient = new HttpClient(resetting);
        using var duringWait = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        Task Wait(TimeSpan wait, CancellationToken token)
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        }
        var delayed = Resolve(resettingClient, Wait, token: duringWait.Token);
        await waiting.Task.WaitAsync(Bound);
        duringWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delayed);
        Assert.Single(resetting.Requests);
    }

    [Fact]
    public async Task A_blocked_resolver_endpoint_is_reported_by_name_and_nothing_else_is_tried()
    {
        using var catalog = new CatalogHandler("English", 1);
        using var faults = new FaultHandler(catalog).Pass().Throw(() => Faults.Dns("vlscppe.microsoft.com"));
        using var client = new HttpClient(faults);
        var waits = new RecordingDelay();
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => Resolve(client, waits.Wait));
        Assert.Equal(2, faults.Requests.Count);
        Assert.Empty(waits.Waits);
        Assert.Contains("vlscppe.microsoft.com", error.Message);
        Assert.Contains("DNS", error.Message);
        Assert.All(faults.Requests, request => Assert.Contains(request.Uri.IdnHost, CatalogHosts));   // no ISO host, no mirror
    }

    [Fact]
    public async Task A_redirect_that_follows_a_retry_still_cannot_leave_the_approved_hosts()
    {
        using var catalog = new CatalogHandler("English", 1) { ExternalRedirect = true };
        using var faults = new FaultHandler(catalog).Throw(() => Faults.HandshakeReset());
        using var client = new HttpClient(faults);
        await Assert.ThrowsAsync<InvalidDataException>(() => Resolve(client, new RecordingDelay().Wait));
        Assert.Equal(2, faults.Requests.Count);
        Assert.Single(catalog.Requests);
        Assert.All(faults.Requests, request => Assert.Contains(request.Uri.IdnHost, CatalogHosts));
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
