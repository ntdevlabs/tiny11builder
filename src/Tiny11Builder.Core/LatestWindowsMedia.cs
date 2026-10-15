using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed record OfficialMedia(Uri Url, string FileName, string Description, string Language, string Architecture, string? Sha256);

public sealed class LatestWindowsMedia
{
    private const string Profile = "606624d44113";
    private const string Instance = "560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175";
    private const int MaxResponse = 4 * 1024 * 1024;
    private const string TooLarge = "Microsoft's catalog response is too large.";
    private readonly HttpClient client;
    private readonly Func<TimeSpan, CancellationToken, Task>? delay;

    public LatestWindowsMedia(HttpClient client) : this(client, null) { }

    // Tests replace the wait between attempts so retries never sleep.
    internal LatestWindowsMedia(HttpClient client, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        this.client = client;
        this.delay = delay;
    }

    public static HttpClient CreateClient() => OfficialTransport.CreateClient(TimeSpan.FromSeconds(60));

    public async Task<OfficialMedia> ResolveAsync(string architecture, CultureInfo culture,
        IProgress<string>? progress, CancellationToken token)
    {
        if (architecture is not ("x64" or "arm64")) throw new InvalidDataException("Only x64 and ARM64 downloads are supported.");
        var page = architecture == "arm64" ? OfficialImageDownloader.ArmDownloadPage : OfficialImageDownloader.DownloadPage;
        var transport = new OfficialTransport(progress, delay, page);
        progress?.Report("Checking Microsoft's latest Windows 11 release.");
        var html = await ReadAsync(new Uri(page.Replace("/software-download/", "/en-us/software-download/", StringComparison.Ordinal)), transport, token);
        var product = ParseProductId(html);
        var session = Guid.NewGuid().ToString();
        await ReadAsync(new($"https://vlscppe.microsoft.com/tags?org_id=y6jn8c31&session_id={session}"), transport, token);
        var response = await ReadAsync(new($"https://ov-df.microsoft.com/mdt.js?instanceId={Instance}&PageId=si&session_id={session}"), transport, token);
        var w = Regex.Match(response, @"[?&]w=([A-F0-9]+)").Groups[1].Value;
        var ticks = Regex.Match(response, "rticks=\\\"\\+?(\\d+)").Groups[1].Value;
        if (w.Length == 0 || ticks.Length == 0) throw transport.Unavailable("Microsoft did not provide a download session. Retry later or use a local ISO.");
        await ReadAsync(new($"https://ov-df.microsoft.com/?session_id={session}&CustomerId={Instance}&PageId=si&w={w}" +
            $"&mdt={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&rticks={ticks}"), transport, token);
        var languageJson = await ReadAsync(Api("getskuinformationbyproductedition", product, "undefined", session), transport, token);
        using var languages = JsonDocument.Parse(languageJson);
        CheckErrors(languages.RootElement, transport);
        var sku = SelectLanguage(languages.RootElement.GetProperty("Skus"), culture, out var language);
        progress?.Report($"Getting the latest {architecture.ToUpperInvariant()} ISO in {language}.");
        using var links = JsonDocument.Parse(await ReadAsync(Api("GetProductDownloadLinksBySku", "undefined", sku, session), transport, token));
        CheckErrors(links.RootElement, transport);
        var type = architecture == "arm64" ? 2 : 1;
        foreach (var item in links.RootElement.GetProperty("ProductDownloadOptions").EnumerateArray())
        {
            if (item.GetProperty("DownloadType").GetInt32() != type) continue;
            var url = OfficialImageDownloader.ValidateUrl(item.GetProperty("Uri").GetString()!);
            var filename = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
            if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Microsoft returned an invalid ISO filename.");
            var hash = item.TryGetProperty("Sha256", out var sha) && sha.ValueKind == JsonValueKind.String ?
                OfficialImageDownloader.ValidateHash(sha.GetString()) : null;
            return new(url, filename, item.GetProperty("LocalizedProductDisplayName").GetString() ?? filename, language, architecture, hash);
        }
        throw transport.Unavailable("Microsoft did not return an ISO for this architecture. Use a local ISO or retry later.");
    }

    public static string ParseProductId(string html)
    {
        var select = Regex.Match(html, """(?is)<select\b[^>]*\bid=["']product-edition["'][^>]*>(.*?)</select>""");
        var option = Regex.Match(select.Groups[1].Value, """(?is)<option\b[^>]*\bvalue=["'](\d+)["'][^>]*>""");
        return option.Success ? option.Groups[1].Value : throw new InvalidDataException("Microsoft's download catalog has changed. Use a local ISO while the downloader is updated.");
    }

    public static string SelectLanguage(JsonElement skus, CultureInfo culture, out string language)
    {
        var preferred = culture.Name switch
        {
            "en-US" => "English", var value when value.StartsWith("en-", StringComparison.OrdinalIgnoreCase) => "English International",
            "pt-BR" => "Brazilian Portuguese", "zh-CN" or "zh-SG" => "Chinese Simplified", "zh-TW" or "zh-HK" => "Chinese Traditional",
            "fr-CA" => "French Canadian", "es-MX" => "Spanish (Mexico)",
            _ => culture.Parent.Name.Length > 0 ? culture.Parent.EnglishName : culture.EnglishName
        };
        JsonElement? english = null;
        foreach (var item in skus.EnumerateArray())
        {
            var name = item.GetProperty("Language").GetString();
            if (name is not null && Regex.Replace(name, @"[^\p{L}]", "").Equals(
                Regex.Replace(preferred, @"[^\p{L}]", ""), StringComparison.OrdinalIgnoreCase))
            { language = name; return item.GetProperty("Id").ToString(); }
            if (name == "English") english = item;
        }
        if (english is { } fallback)
        { language = $"English (Microsoft has no {preferred} ISO)"; return fallback.GetProperty("Id").ToString(); }
        throw new IOException("Microsoft has no ISO matching the display language and no English ISO.");
    }

    private static Uri Api(string action, string product, string sku, string session) => new(
        $"https://www.microsoft.com/software-download-connector/api/{action}?profile={Profile}&productEditionId={product}" +
        $"&SKU={sku}&friendlyFileName=undefined&Locale=en-US&sessionID={session}");

    private async Task<string> ReadAsync(Uri uri, OfficialTransport transport, CancellationToken token)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                uri.IdnHost is not ("www.microsoft.com" or "vlscppe.microsoft.com" or "ov-df.microsoft.com"))
                throw new InvalidDataException("Download catalog request left Microsoft's approved hosts.");
            var current = uri;
            var (location, body) = await transport.RunAsync<(Uri? Location, string? Body)>(current, async (tls12, attempt) =>
            {
                using var request = OfficialTransport.Request(current, tls12);
                request.Headers.Referrer = new(OfficialImageDownloader.DownloadPage);
                using var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt);
                if ((int)result.StatusCode is 301 or 302 or 303 or 307 or 308)
                    return (result.Headers.Location ?? throw transport.Unavailable("Microsoft returned an empty redirect."), null);
                if (!result.IsSuccessStatusCode) throw OfficialTransport.StatusError(result);
                if (result.Content.Headers.ContentLength > MaxResponse) throw transport.Unavailable(TooLarge);
                await using var input = await result.Content.ReadAsStreamAsync(attempt);
                using var memory = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await input.ReadAsync(buffer, attempt)) > 0)
                {
                    if (memory.Length + count > MaxResponse) throw transport.Unavailable(TooLarge);
                    memory.Write(buffer, 0, count);
                }
                return (null, System.Text.Encoding.UTF8.GetString(memory.ToArray()));
            }, token);
            if (body is not null) return body;
            uri = location!.IsAbsoluteUri ? location : new(uri, location);
        }
        throw transport.Unavailable("Too many Microsoft catalog redirects.");
    }

    private static void CheckErrors(JsonElement root, OfficialTransport transport)
    {
        if (root.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw transport.Unavailable("Microsoft refused the download request. " + WebUtility.HtmlDecode(errors[0].GetProperty("Value").GetString()) +
                " Retry later, use a local ISO, or open Microsoft's official download page. No third-party mirror will be used.");
    }
}
