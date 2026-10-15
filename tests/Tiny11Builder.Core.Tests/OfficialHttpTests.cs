using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

// None of these tests opens a socket or performs a TLS handshake: failures are scripted through fake handlers, using
// exception shapes captured from .NET 8 on Windows. Every await that could wait on a fake is bounded by a timeout.
public sealed class OfficialHttpTests
{
    private static readonly Uri Resource = new("https://www.microsoft.com/page?token=secret");
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_TLS12_route_is_used_only_when_a_request_asks_for_it_and_both_handlers_are_released()
    {
        var standard = new CountingHandler();
        var restricted = new CountingHandler();
        var client = new HttpClient(new OfficialTransport.Routing(standard, restricted));
        using (var normal = OfficialTransport.Request(Resource, tls12: false)) await client.SendAsync(normal).WaitAsync(Bound);
        using (var narrow = OfficialTransport.Request(Resource, tls12: true)) await client.SendAsync(narrow).WaitAsync(Bound);
        Assert.Equal(1, standard.Sent);
        Assert.Equal(1, restricted.Sent);
        client.Dispose();
        Assert.True(standard.Disposed);
        Assert.True(restricted.Disposed);
    }

    // The handlers are only configured and disposed here; they never send a request, so they open no connection pool.
    [Fact]
    public void The_fallback_handler_keeps_certificate_validation_redirects_cookies_and_never_goes_below_TLS12()
    {
        var (standard, restricted) = OfficialTransport.CreateHandlers();
        using (standard)
        using (restricted)
        {
            Assert.Equal(SslProtocols.None, standard.SslProtocols);
            Assert.Equal(SslProtocols.Tls12, restricted.SslProtocols);
            Assert.Same(standard.CookieContainer, restricted.CookieContainer);
            foreach (var handler in new[] { standard, restricted })
            {
                Assert.False(handler.AllowAutoRedirect);
                Assert.True(handler.UseCookies);
                Assert.Null(handler.ServerCertificateCustomValidationCallback);
            }
        }
    }

    public static TheoryData<string, int> Scripts => new()
    {
        { "reset", 2 }, { "aborted", 2 }, { "eof", 2 }, { "refused", 2 }, { "ended", 2 }, { "status503", 2 }, { "timeout", 2 },
        { "certificate", 1 }, { "alert", 1 }, { "dns", 1 }, { "proxy", 1 }, { "status403", 1 }
    };

    private static Exception Failure(string kind) => kind switch
    {
        "reset" => Faults.HandshakeReset(SocketError.ConnectionReset),            // 10054, as reported
        "aborted" => Faults.HandshakeReset(SocketError.ConnectionAborted),        // 10053, a reset seen by the local stack
        "eof" => Faults.HandshakeEof(),                                           // a reset right after the ClientHello
        "refused" => new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.",
            new SocketException((int)SocketError.ConnectionRefused)),
        "ended" => Faults.ResponseEnded(),
        "status503" => new HttpRequestException(HttpRequestError.Unknown, "503", null, HttpStatusCode.ServiceUnavailable),
        "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        "certificate" => Faults.RejectedCertificate(),
        "alert" => Faults.Alert(),
        "dns" => Faults.Dns("www.microsoft.com"),
        "proxy" => new HttpRequestException(HttpRequestError.ProxyTunnelError, "proxy refused"),
        "status403" => new HttpRequestException(HttpRequestError.Unknown, "403", null, HttpStatusCode.Forbidden),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // Only transport problems that another attempt can fix are repeated. Certificate and TLS-alert failures, DNS, proxy and
    // plain refusals stop at once: nothing is retried after a rejected certificate and nothing is downgraded.
    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Only_transient_transport_failures_are_repeated(string kind, int attempts)
    {
        var waits = new RecordingDelay();
        var transport = new OfficialTransport(null, waits.Wait, OfficialImageDownloader.DownloadPage);
        var used = new List<bool>();
        Task<int> Attempt(bool tls12, CancellationToken token)
        {
            used.Add(tls12);
            if (used.Count < attempts || attempts == 1) throw Failure(kind);
            return Task.FromResult(7);
        }
        if (attempts == 1)
        {
            var error = await Assert.ThrowsAsync<DownloadServiceException>(() => transport.RunAsync(Resource, Attempt, CancellationToken.None).WaitAsync(Bound));
            Assert.Single(used);
            Assert.Empty(waits.Waits);
            Assert.NotNull(error.InnerException);
            Assert.Contains("www.microsoft.com", error.Message);
            Assert.DoesNotContain("secret", error.Message);
        }
        else
        {
            Assert.Equal(7, await transport.RunAsync(Resource, Attempt, CancellationToken.None).WaitAsync(Bound));
            Assert.Equal(attempts, used.Count);
            Assert.Single(waits.Waits);
            // Only a reset handshake changes the protocol; a busy server or a timeout retries the same way.
            Assert.Equal(kind is "reset" or "aborted" or "eof", used[1]);
        }
    }

    [Fact]
    public async Task A_server_that_asks_for_a_long_wait_is_not_waited_for()
    {
        var waits = new RecordingDelay();
        var transport = new OfficialTransport(null, waits.Wait, OfficialImageDownloader.DownloadPage);
        var calls = 0;
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => transport.RunAsync<int>(Resource, (_, _) =>
        {
            calls++;
            throw Faults.StatusError(HttpStatusCode.TooManyRequests, retryAfterSeconds: 600);
        }, CancellationToken.None).WaitAsync(Bound));
        Assert.Equal(1, calls);
        Assert.Empty(waits.Waits);
        Assert.Contains("HTTP 429", error.Message);
    }

    [Fact]
    public async Task Timeouts_are_retried_once_and_other_drops_use_all_four_attempts()
    {
        var waits = new RecordingDelay();
        var transport = new OfficialTransport(null, waits.Wait, OfficialImageDownloader.DownloadPage);
        var timeouts = 0;
        var slow = await Assert.ThrowsAsync<DownloadServiceException>(() => transport.RunAsync<int>(Resource, (_, _) =>
        {
            timeouts++;
            throw Failure("timeout");
        }, CancellationToken.None).WaitAsync(Bound));
        Assert.Equal(2, timeouts);
        Assert.Contains("did not respond in time on all 2 attempts", slow.Message);
        var drops = 0;
        var dropped = await Assert.ThrowsAsync<DownloadServiceException>(() => transport.RunAsync<int>(Resource, (_, _) =>
        {
            drops++;
            throw Failure("refused");
        }, CancellationToken.None).WaitAsync(Bound));
        Assert.Equal(OfficialTransport.MaxAttempts, drops);
        Assert.Contains("dropped the connection on all 4 attempts", dropped.Message);
    }

    [Fact]
    public async Task Failures_that_are_not_transport_problems_pass_through_unchanged()
    {
        var waits = new RecordingDelay();
        var transport = new OfficialTransport(null, waits.Wait, OfficialImageDownloader.DownloadPage);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.RunAsync<int>(Resource,
            (_, _) => throw new InvalidDataException("not a transport problem"), CancellationToken.None).WaitAsync(Bound));
        var typed = new DownloadServiceException("already actionable");
        Assert.Same(typed, await Assert.ThrowsAsync<DownloadServiceException>(() => transport.RunAsync<int>(Resource,
            (_, _) => throw typed, CancellationToken.None).WaitAsync(Bound)));
        Assert.Empty(waits.Waits);
    }

    [Fact]
    public async Task Cancellation_passes_through_at_once_whether_it_hits_an_attempt_or_the_wait_between_attempts()
    {
        using var duringAttempt = new CancellationTokenSource();
        var transport = new OfficialTransport(null, new RecordingDelay().Wait, OfficialImageDownloader.DownloadPage);
        var attempts = 0;
        var cancelled = transport.RunAsync<int>(Resource, async (_, token) =>
        {
            attempts++;
            duringAttempt.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, duringAttempt.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        Assert.Equal(1, attempts);

        using var duringWait = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        Task Wait(TimeSpan wait, CancellationToken token)
        {
            waiting.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        }
        var retries = 0;
        var waited = new OfficialTransport(null, Wait, OfficialImageDownloader.DownloadPage).RunAsync<int>(Resource, (_, _) =>
        {
            retries++;
            throw Failure("reset");
        }, duringWait.Token);
        await waiting.Task.WaitAsync(Bound);
        duringWait.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waited.WaitAsync(Bound));
        Assert.Equal(1, retries);
    }

    [Fact]
    public async Task Retry_messages_name_the_protocol_the_attempt_and_never_the_link_token()
    {
        var messages = new List<string>();
        var transport = new OfficialTransport(new ImmediateProgress<string>(messages.Add), new RecordingDelay().Wait, OfficialImageDownloader.DownloadPage);
        var calls = 0;
        await transport.RunAsync(Resource, (tls12, _) => ++calls < 3 ? throw Failure("reset") : Task.FromResult(1), CancellationToken.None).WaitAsync(Bound);
        Assert.Equal(
            ["Microsoft's download service closed the secure connection. Retrying with TLS 1.2 in 1 s (attempt 2 of 4).",
             "Microsoft's download service closed the secure connection. Retrying with the default TLS settings in 2 s (attempt 3 of 4)."],
            messages);
        Assert.DoesNotContain(messages, message => message.Contains("secret") || message.Contains("token"));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sent { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sent++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

/// <summary>
/// Exceptions shaped like the ones .NET 8.0 on Windows reports, captured from loopback sockets (a reset or close during the
/// handshake, an untrusted certificate, a TLS alert, a body cut short): nothing here touches the network.
/// </summary>
internal static class Faults
{
    private const string HandshakeText = "The SSL connection could not be established, see inner exception.";

    public static HttpRequestException Handshake(string kind) => kind switch
    {
        "reset" => HandshakeReset(SocketError.ConnectionReset),       // 10054, as reported
        "aborted" => HandshakeReset(SocketError.ConnectionAborted),   // 10053, the same reset seen by the local stack
        "eof" => HandshakeEof(),                                      // a reset right after the ClientHello
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static HttpRequestException HandshakeReset(SocketError error = SocketError.ConnectionReset) => new(
        HttpRequestError.SecureConnectionError, HandshakeText,
        new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
            new SocketException((int)error)));

    public static HttpRequestException HandshakeEof() => new(HttpRequestError.SecureConnectionError, HandshakeText,
        new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));

    public static HttpRequestException RejectedCertificate() => new(HttpRequestError.SecureConnectionError, HandshakeText,
        new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

    public static HttpRequestException Alert() => new(HttpRequestError.SecureConnectionError, HandshakeText,
        new AuthenticationException("Authentication failed because the remote party sent a TLS alert: 'HandshakeFailure'.",
            new System.ComponentModel.Win32Exception(unchecked((int)0x80090326))));

    public static HttpRequestException Dns(string host) => new(HttpRequestError.NameResolutionError,
        $"No such host is known. ({host}:443)", new SocketException((int)SocketError.HostNotFound));

    public static HttpResponseMessage Status(HttpStatusCode code, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(code);
        if (retryAfterSeconds is int seconds) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    // The exception the transport itself builds for a non-success answer, Retry-After included.
    public static HttpRequestException StatusError(HttpStatusCode code, int? retryAfterSeconds = null)
    {
        using var response = Status(code, retryAfterSeconds);
        return OfficialTransport.StatusError(response);
    }

    public static HttpResponseMessage BrokenBody(string prefix, Func<Exception> failure)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(prefix);
        var content = new StreamContent(new ResettingStream(bytes, failure));
        content.Headers.ContentLength = bytes.Length + 1000;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpIOException ResponseEnded() => new(HttpRequestError.ResponseEnded, "The response ended prematurely. (ResponseEnded)");
}

/// <summary>Passes requests to a real handler, except where a script entry fails or answers them first.</summary>
internal sealed class FaultHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    private readonly HttpMessageInvoker real = new(inner, disposeHandler: false);
    private readonly List<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>?> script = [];
    public List<(Uri Uri, bool Tls12)> Requests { get; } = [];

    public FaultHandler Pass(int count = 1)
    {
        for (var i = 0; i < count; i++) script.Add(null);
        return this;
    }

    public FaultHandler Throw(Func<Exception> error, int count = 1)
    {
        for (var i = 0; i < count; i++) script.Add(async (_, _) => { await Task.Yield(); throw error(); });
        return this;
    }

    public FaultHandler Answer(Func<HttpResponseMessage> response)
    {
        script.Add((_, _) => Task.FromResult(response()));
        return this;
    }

    public FaultHandler Hang()
    {
        script.Add(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var index = Requests.Count;
        Requests.Add((request.RequestUri!, OfficialTransport.UsesTls12(request)));
        return index < script.Count && script[index] is { } fault ? fault(request, token) : real.SendAsync(request, token);
    }
}

/// <summary>Stands in for the wait between attempts, so retries never sleep.</summary>
internal sealed class RecordingDelay
{
    public List<TimeSpan> Waits { get; } = [];

    public Task Wait(TimeSpan wait, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Waits.Add(wait);
        return Task.CompletedTask;
    }
}

internal static class TestWait
{
    // Polls a fake's state, and fails the test rather than waiting for ever.
    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The awaited state was never reached.");
            await Task.Delay(10);
        }
    }
}

/// <summary>Delivers its prefix, then fails the way a connection reset in the middle of a response body does.</summary>
internal sealed class ResettingStream(byte[] prefix, Func<Exception> failure) : Stream
{
    private int position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (position >= prefix.Length) throw failure();
        var count = Math.Min(buffer.Length, prefix.Length - position);
        prefix.AsSpan(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        try { return new(Read(buffer.Span)); }
        catch (Exception error) { return ValueTask.FromException<int>(error); }
    }
}
