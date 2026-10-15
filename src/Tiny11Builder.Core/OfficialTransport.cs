using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Tiny11Builder.Core;

/// <summary>
/// Microsoft's download service could not be used. A local ISO, or Microsoft's own page in a browser, still works.
/// The inner exception keeps the underlying failure and its stack.
/// </summary>
public sealed class DownloadServiceException(string message, Exception? inner = null) : IOException(message, inner)
{
    /// <summary>Microsoft's page for downloading the ISO in a browser. It never carries a session or link token.</summary>
    public string OfficialPage { get; init; } = OfficialImageDownloader.DownloadPage;
}

/// <summary>
/// Bounded retries for Microsoft's download hosts. The first attempt uses the operating system's TLS defaults. After the
/// secure handshake is reset, attempts alternate with TLS 1.2 only: its ClientHello is a fraction of the size and carries no
/// TLS 1.3 extensions, which some firewalls and proxies mishandle. Certificate validation, proxy and redirect handling never
/// change, and a rejected certificate is never retried. Messages name hosts only, so session ids and signed-link tokens stay
/// out of logs.
/// </summary>
internal sealed class OfficialTransport(IProgress<string>? progress, Func<TimeSpan, CancellationToken, Task>? delay, string officialPage)
{
    internal const int MaxAttempts = 4;
    private const int SlowAttempts = 2;
    private const string Choices = "Retry later, use a local ISO, or open Microsoft's official download page.";
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private static readonly HttpRequestOptionsKey<bool> Tls12Only = new("Tiny11Builder.Tls12Only");
    private bool tls12;   // the protocol that worked last is the first choice of the next request

    private sealed record Failure(string Problem, string Advice = "", bool Fatal = false, bool Handshake = false,
        bool Slow = false, TimeSpan? RetryAfter = null);

    private sealed class HttpStatusError(HttpStatusCode code, TimeSpan? retryAfter)
        : HttpRequestException(HttpRequestError.Unknown, $"Microsoft answered HTTP {(int)code}.", null, code)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    /// <summary>Routes a request to the TLS 1.2 handler when the request asks for it. Both handlers are disposed with the client.</summary>
    internal sealed class Routing : HttpMessageHandler
    {
        private readonly HttpMessageInvoker standard, restricted;

        public Routing(HttpMessageHandler standard, HttpMessageHandler tls12)
        {
            this.standard = new(standard, disposeHandler: true);
            restricted = new(tls12, disposeHandler: true);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            (UsesTls12(request) ? restricted : standard).SendAsync(request, token);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { standard.Dispose(); restricted.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static HttpClient CreateClient(TimeSpan timeout)
    {
        var (standard, restricted) = CreateHandlers();
        return new(new Routing(standard, restricted), disposeHandler: true) { Timeout = timeout };
    }

    // Both handlers share one cookie jar, so a session started on one protocol continues on the other.
    internal static (HttpClientHandler Standard, HttpClientHandler Tls12) CreateHandlers()
    {
        var cookies = new CookieContainer();
        HttpClientHandler Create(SslProtocols protocols) => new()
        { AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies, SslProtocols = protocols };
        return (Create(SslProtocols.None), Create(SslProtocols.Tls12));
    }

    internal static HttpRequestMessage Request(Uri uri, bool tls12)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (tls12) request.Options.Set(Tls12Only, true);
        return request;
    }

    internal static bool UsesTls12(HttpRequestMessage request) => request.Options.TryGetValue(Tls12Only, out var only) && only;

    internal static HttpRequestException StatusError(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var asked = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return new HttpStatusError(response.StatusCode, asked > TimeSpan.Zero ? asked : null);
    }

    internal DownloadServiceException Unavailable(string message, Exception? inner = null) =>
        new(message, inner) { OfficialPage = officialPage };

    /// <summary>True when the exception is a connection problem that left the transfer resumable, never a cancellation or a rejection.</summary>
    internal static bool IsInterruption(Exception error, CancellationToken token) => Classify(error, token) is { Fatal: false };

    /// <summary>
    /// Runs one request, and reads of its body if the attempt does them, until it succeeds or the bounded attempts are used up.
    /// The attempt must do transport work only: any IOException it throws is treated as a dropped connection.
    /// Cancellation and failures that are not transport problems pass through unchanged.
    /// </summary>
    internal async Task<T> RunAsync<T>(Uri uri, Func<bool, CancellationToken, Task<T>> attempt, CancellationToken token)
    {
        var current = tls12;
        var usedTls12 = false;
        for (var number = 1; ; number++)
        {
            usedTls12 |= current;
            try
            {
                var result = await attempt(current, token).ConfigureAwait(false);
                tls12 = current;
                return result;
            }
            catch (Exception error) when (Classify(error, token) is { } failure)
            {
                var wait = TimeSpan.FromSeconds(Math.Min(1 << (number - 1), 8));
                if (failure.RetryAfter is { } asked && asked > wait) wait = asked;
                if (failure.Fatal || number >= (failure.Slow ? SlowAttempts : MaxAttempts) || wait > MaxWait)
                    throw Describe(uri, failure, number, usedTls12, error);
                if (failure.Handshake) current = !current;
                var how = !failure.Handshake ? "" : current ? " with TLS 1.2" : " with the default TLS settings";
                progress?.Report($"Microsoft's download service {failure.Problem}. Retrying{how} in {Math.Ceiling(wait.TotalSeconds):0} s " +
                    $"(attempt {number + 1} of {MaxAttempts}).");
                await (delay?.Invoke(wait, token) ?? Task.Delay(wait, token)).ConfigureAwait(false);
            }
        }
    }

    private DownloadServiceException Describe(Uri uri, Failure failure, int attempts, bool usedTls12, Exception inner)
    {
        var count = !failure.Fatal && attempts > 1 ? $" on all {attempts} attempts{(failure.Handshake && usedTls12 ? ", including TLS 1.2" : "")}" : "";
        var advice = failure.Advice.Length > 0 ? failure.Advice + " " : "";
        return Unavailable($"Microsoft's download service ({uri.IdnHost}) {failure.Problem}{count}. {advice}{Choices}", inner);
    }

    // Returns null for anything that is not a transport problem, including cancellation by the caller.
    private static Failure? Classify(Exception error, CancellationToken token)
    {
        switch (error)
        {
            case DownloadServiceException:
                return null;
            case OperationCanceledException:
                return !token.IsCancellationRequested && error.InnerException is TimeoutException ?
                    new("did not respond in time", Slow: true) : null;
            case HttpStatusError { StatusCode: { } code } status:
                return Status(code, status.RetryAfter);
            case HttpRequestException { StatusCode: { } code }:
                return Status(code, null);
            case HttpRequestException http:
                var inner = Chain(http).Skip(1).ToArray();
                var transport = inner.Any(e => e is IOException or SocketException);
                return http.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => new("could not be found by name (DNS)",
                        "Check the network, VPN and DNS settings.", Fatal: true),
                    HttpRequestError.ProxyTunnelError => new("could not be reached through the configured proxy",
                        "Check the proxy settings.", Fatal: true),
                    HttpRequestError.SecureConnectionError when !transport => Rejected,
                    HttpRequestError.SecureConnectionError => new("closed the secure connection", "A firewall, proxy or antivirus may be blocking it.",
                        Handshake: true),
                    _ when inner.OfType<SocketException>().Any(e => e.SocketErrorCode == SocketError.TimedOut) =>
                        new("did not respond in time", Slow: true),
                    _ when transport => new("dropped the connection"),
                    _ => null
                };
            case AuthenticationException:
                return Rejected;
            case SocketException { SocketErrorCode: SocketError.TimedOut }:
                return new("did not respond in time", Slow: true);
            case IOException or SocketException:
                return new("dropped the connection");
            default:
                return null;
        }
    }

    private static readonly Failure Rejected = new("failed Windows' certificate or TLS security check",
        "The app never bypasses certificate checks; check the PC's date and any HTTPS-scanning proxy or antivirus.", Fatal: true);

    private static Failure Status(HttpStatusCode code, TimeSpan? retryAfter) =>
        (int)code is 408 or 429 or 500 or 502 or 503 or 504 ?
            new($"is busy or unavailable (HTTP {(int)code})", RetryAfter: retryAfter) :
            new($"refused the request (HTTP {(int)code})", Fatal: true);

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException) yield return current;
    }
}
