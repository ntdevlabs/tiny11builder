using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
public sealed class OscdimgAcquirer(HttpClient client, Func<string>? findInstalled = null)
{
    public const string DownloadUrl = "https://msdl.microsoft.com/download/symbols/oscdimg.exe/688CABB065000/oscdimg.exe";
    public const string ApprovedSha256 = "2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861";

    public async Task<string> EnsureAsync(string toolsDirectory, IProgress<string>? progress, CancellationToken token)
    {
        var installed = findInstalled is null ? ToolLocator.FindOscdimg() : findInstalled();
        if (installed.Length > 0) { ToolLocator.Validate(installed, "oscdimg.exe"); return installed; }
        toolsDirectory = PathSafety.Full(toolsDirectory);
        PathSafety.RejectReparsePoints(toolsDirectory);
        var destination = Path.Combine(toolsDirectory, "oscdimg.exe");
        if (File.Exists(destination)) { ToolLocator.Validate(destination, "oscdimg.exe"); return destination; }
        Directory.CreateDirectory(toolsDirectory);
        progress?.Report("Downloading Microsoft's ISO creation tool.");
        using var response = await DownloadAsync(token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await content.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > 1024 * 1024) throw new InvalidDataException("The ISO tool exceeds its expected size.");
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != ApprovedSha256)
            throw new InvalidDataException("The Microsoft ISO tool checksum does not match. It has not been saved or executed.");
        var temporary = Path.Combine(toolsDirectory, "oscdimg-" + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        ToolLocator.Validate(destination, "oscdimg.exe");
        progress?.Report("ISO creation tool ready.");
        return destination;
    }

    private async Task<HttpResponseMessage> DownloadAsync(CancellationToken token)
    {
        var uri = new Uri(DownloadUrl);
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                uri.IdnHost is not ("msdl.microsoft.com" or "vsblobprodscussu5shard61.blob.core.windows.net"))
                throw new InvalidDataException("The ISO tool download left its approved Microsoft symbol hosts.");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null) throw new IOException("Microsoft returned an empty ISO tool redirect.");
            uri = next.IsAbsoluteUri ? next : new(uri, next);
        }
        throw new IOException("Too many Microsoft ISO tool redirects.");
    }
}
