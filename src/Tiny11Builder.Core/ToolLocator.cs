using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.Cryptography;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
public static class ToolLocator
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static string AdkDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools",
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64");

    public static string FindDism() => FindDism(null);

    public static string FindDism(Action<string>? log)
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\dism.exe");
        var adk = Path.Combine(AdkDirectory, @"DISM\dism.exe");
        return SelectDism([system, adk], path =>
        {
            Validate(path, "dism.exe");
            return ServicingVersionOf(path);
        }, log);
    }

    internal static string SelectDism(IEnumerable<string> candidates, Func<string, Version> inspect, Action<string>? log = null)
    {
        var available = new List<(string Path, Version Version)>();
        var rejected = new List<Exception>();
        var failures = new List<string>();
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { available.Add((path, inspect(path))); }
            catch (Exception error) when (error is ServicingToolsRequiredException or Win32Exception or InvalidDataException or
                                              FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or CryptographicException)
            {
                rejected.Add(error);
                var detail = $"Skipped installed DISM '{path}': {error.Message}";
                failures.Add(detail);
                log?.Invoke(detail);
            }
        }
        if (available.Count == 0)
            throw new ServicingToolsRequiredException("No trusted, complete Microsoft DISM installation is available. " +
                "Install or repair Windows ADK Deployment Tools and servicing updates, then reopen. " +
                string.Join(" ", failures),
                rejected.Count == 1 ? rejected[0] : new AggregateException(rejected));
        var selected = available.OrderByDescending(candidate => candidate.Version).First();
        log?.Invoke($"Selected trusted DISM '{selected.Path}' with servicing engine {selected.Version}.");
        return selected.Path;
    }
    public static string FindOscdimg()
    {
        var path = Path.Combine(AdkDirectory, @"Oscdimg\oscdimg.exe");
        return File.Exists(path) ? path : "";
    }
    public static Version VersionOf(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }
    public static Version ServicingVersionOf(string dism)
    {
        var directory = Path.GetDirectoryName(PathSafety.Full(dism))!;
        var core = new[] { Path.Combine(directory, "dismcore.dll"), Path.Combine(directory, @"Dism\dismcore.dll") }
            .FirstOrDefault(File.Exists);
        var api = Path.Combine(directory, "DismApi.dll");
        return ResolveServicingVersion(VersionOf(dism), File.Exists(api) ? VersionOf(api) : null,
            core is null ? null : VersionOf(core));
    }

    internal static Version ResolveServicingVersion(Version executable, Version? api, Version? core)
    {
        // The inbox executable can be an older shim; the servicing engine supplies the DISM banner.
        // When both providers exist, do not let one newer DLL hide an older servicing dependency.
        if (core is null) return executable;
        if (api is null) return core;
        return api < core ? api : core;
    }

    internal static int ServicingBase(int build) => WindowsImageIdentity.ServicingBase(build);
    public static void Validate(string path, string filename)
    {
        path = PathSafety.Full(path);
        PathSafety.RejectReparsePoints(path);
        if (!File.Exists(path) || !Path.GetFileName(path).Equals(filename, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Select Microsoft's {filename}. Install Windows ADK Deployment Tools if it is missing.", path);
        // Microsoft's symbol-server copy omits the certificate. Accept only this exact pinned binary.
        if (filename.Equals("oscdimg.exe", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.OpenRead(path);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)) == OscdimgAcquirer.ApprovedSha256) return;
        }
        try
        {
            ValidateSignedResource(path, filename);
            if (filename.Equals("dism.exe", StringComparison.OrdinalIgnoreCase)) ValidateServicingRoot(path);
        }
        catch (Exception error) when (filename.Equals("dism.exe", StringComparison.OrdinalIgnoreCase) &&
                                      error is Win32Exception or InvalidDataException or CryptographicException or FileNotFoundException)
        {
            throw new ServicingToolsRequiredException($"Cannot use Microsoft DISM at '{path}'. {error.Message} " +
                "Install or repair Windows ADK Deployment Tools and servicing updates; signature verification remains required.", error);
        }
    }

    private static void ValidateSignedResource(string path, string filename)
    {
        PathSafety.RejectReparsePoints(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"The DISM installation is incomplete: missing '{path}'.", path);
        MicrosoftToolTrust.Verify(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        var original = info.OriginalFilename;
        var resourceName = original?.EndsWith(".mui", StringComparison.OrdinalIgnoreCase) == true ? original[..^4] : original;
        // Signed ADK oscdimg 2.56 has a build-system placeholder instead of its original filename.
        var signedOscdimgResource = filename.Equals("oscdimg.exe", StringComparison.OrdinalIgnoreCase) &&
            resourceName == "TARGETNAMEWITHEXTENSION" && info.ProductName == "OSCDIMG" &&
            info.FileDescription == "Microsoft CD/DVD Premastering Utility" && info.CompanyName == "Microsoft Corporation";
        if (!signedOscdimgResource && (string.IsNullOrWhiteSpace(resourceName) ||
                                      !Path.GetFileName(resourceName).Equals(filename, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"'{path}' is not Microsoft's {filename} resource (original filename '{original}').");
    }

    private static void ValidateServicingRoot(string dism)
    {
        var root = Path.GetDirectoryName(dism)!;
        var providerRoot = File.Exists(Path.Combine(root, "dismcore.dll")) ? root : Path.Combine(root, "Dism");
        foreach (var file in new[] { "DismApi.dll", "wimgapi.dll" }.Select(name => Path.Combine(root, name))
                     .Concat(new[] { "dismcore.dll", "DismProv.dll", "ImagingProvider.dll", "LogProvider.dll", "WimProvider.dll" }
                         .Select(name => Path.Combine(providerRoot, name))))
            ValidateSignedResource(file, Path.GetFileName(file));
    }
    public static void EnsureServicingVersion(string dism, WindowsImage image)
    {
        EnsureServicingVersion(ServicingVersionOf(dism), image);
    }

    internal static void EnsureServicingVersion(Version version, WindowsImage image)
    {
        if (!SupportsServicing(version, image))
            throw new ServicingToolsRequiredException(
                $"Image build {image.Version.Build} needs servicing base {ServicingBase(image.Version.Build)}; installed tools are {version}. " +
                "Install current Windows ADK Deployment Tools and servicing updates, then reopen; the app detects them automatically. " +
                "Windows 10 can stay installed.");
    }

    internal static bool SupportsServicing(Version version, WindowsImage image) =>
        version.Major > image.Version.Major || version.Major == image.Version.Major &&
        (version.Minor > image.Version.Minor || version.Minor == image.Version.Minor &&
            ServicingBase(version.Build) >= ServicingBase(image.Version.Build));

    public static bool SupportsServicing(string dism, WindowsImage image) => SupportsServicing(ServicingVersionOf(dism), image);
}
