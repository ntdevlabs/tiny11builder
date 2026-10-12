using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

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

    public static string FindDism()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\dism.exe");
        var adk = Path.Combine(AdkDirectory, @"DISM\dism.exe");
        return File.Exists(adk) && VersionOf(adk) > VersionOf(system) ? adk : system;
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
        using var file = new TrustFile(path);
        var data = new TrustData { Size = Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = file.Pointer, ProviderFlags = 0x1000 };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        if (result != 0) throw new Win32Exception(result, $"{filename} does not have a trusted Authenticode signature.");
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
            System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
        var publisher = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        if (!new[] { "Microsoft Corporation", "Microsoft Windows" }.Contains(publisher, StringComparer.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(cert.Subject, @"(?:^|,\s*)O=Microsoft Corporation(?:,|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException($"{filename} is not signed by Microsoft Corporation.");
    }
    public static void EnsureServicingVersion(string dism, WindowsImage image)
    {
        var version = VersionOf(dism);
        if (version.Build < image.Version.Build)
            throw new InvalidOperationException($"This image is build {image.Version.Build}, but DISM is build {version.Build}. " +
                "Select a matching or newer Microsoft DISM from Windows ADK. Insider/Canary images may require a preview ADK or newer Windows host.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative { public int Size; public IntPtr Path; public IntPtr Handle; public IntPtr Subject; }
    private sealed class TrustFile : IDisposable
    {
        private readonly IntPtr name;
        public IntPtr Pointer { get; }
        public TrustFile(string path)
        {
            name = Marshal.StringToCoTaskMemUni(path);
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
            Marshal.StructureToPtr(new FileInfoNative { Size = Marshal.SizeOf<FileInfoNative>(), Path = name }, Pointer, false);
        }
        public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeCoTaskMem(name); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public int Size; public IntPtr Policy; public IntPtr Sip; public int UiChoice; public int Revocation;
        public int UnionChoice; public IntPtr File; public int StateAction; public IntPtr StateData;
        public IntPtr Url; public int ProviderFlags; public int UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
