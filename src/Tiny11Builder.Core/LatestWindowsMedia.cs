using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tiny11Builder.Core;

public sealed record OfficialMedia(Uri Url, string FileName, string Description, string Language, string Architecture, string? Sha256);

public sealed class LatestWindowsMedia(HttpClient client)
{
    private const string Profile = "606624d44113";
    private const string Instance = "560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true })
    { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<OfficialMedia> ResolveAsync(string architecture, CultureInfo culture,
        IProgress<string>? progress, CancellationToken token)
    {
        if (architecture is not ("x64" or "arm64")) throw new InvalidDataException("Only x64 and ARM64 downloads are supported.");
        var page = architecture == "arm64" ? OfficialImageDownloader.ArmDownloadPage : OfficialImageDownloader.DownloadPage;
        progress?.Report("Checking Microsoft's latest Windows 11 release.");
        var html = await ReadAsync(new Uri(page.Replace("/software-download/", "/en-us/software-download/", StringComparison.Ordinal)), token);
        var product = ParseProductId(html);
        var session = Guid.NewGuid().ToString();
        await ReadAsync(new($"https://vlscppe.microsoft.com/tags?org_id=y6jn8c31&session_id={session}"), token);
        var response = await ReadAsync(new($"https://ov-df.microsoft.com/mdt.js?instanceId={Instance}&PageId=si&session_id={session}"), token);
        var w = Regex.Match(response, @"[?&]w=([A-F0-9]+)").Groups[1].Value;
        var ticks = Regex.Match(response, "rticks=\\\"\\+?(\\d+)").Groups[1].Value;
        if (w.Length == 0 || ticks.Length == 0) throw new IOException("Microsoft did not provide a download session. Retry later or use a local ISO.");
        await ReadAsync(new($"https://ov-df.microsoft.com/?session_id={session}&CustomerId={Instance}&PageId=si&w={w}" +
            $"&mdt={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&rticks={ticks}"), token);
        var languageJson = await ReadAsync(Api("getskuinformationbyproductedition", product, "undefined", session), token);
        using var languages = JsonDocument.Parse(languageJson);
        CheckErrors(languages.RootElement);
        var sku = SelectLanguage(languages.RootElement.GetProperty("Skus"), culture, out var language);
        progress?.Report($"Getting the latest {architecture.ToUpperInvariant()} ISO in {language}.");
        using var links = JsonDocument.Parse(await ReadAsync(Api("GetProductDownloadLinksBySku", "undefined", sku, session), token));
        CheckErrors(links.RootElement);
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
        throw new IOException("Microsoft did not return an ISO for this architecture. Use a local ISO or retry later.");
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

    private async Task<string> ReadAsync(Uri uri, CancellationToken token)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                uri.IdnHost is not ("www.microsoft.com" or "vlscppe.microsoft.com" or "ov-df.microsoft.com"))
                throw new InvalidDataException("Download catalog request left Microsoft's approved hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = new(OfficialImageDownloader.DownloadPage);
            using var result = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)result.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = result.Headers.Location ?? throw new IOException("Microsoft returned an empty redirect.");
                uri = location.IsAbsoluteUri ? location : new(uri, location);
                continue;
            }
            result.EnsureSuccessStatusCode();
            if (result.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new IOException("Microsoft's catalog response is too large.");
            await using var input = await result.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + count > 4 * 1024 * 1024) throw new IOException("Microsoft's catalog response is too large.");
                memory.Write(buffer, 0, count);
            }
            return System.Text.Encoding.UTF8.GetString(memory.ToArray());
        }
        throw new IOException("Too many Microsoft catalog redirects.");
    }

    private static void CheckErrors(JsonElement root)
    {
        if (root.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new IOException("Microsoft refused the download request. " + WebUtility.HtmlDecode(errors[0].GetProperty("Value").GetString()) +
                " Retry later or select a local ISO. No third-party mirror will be used.");
    }
}
