using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Tiny11Builder.Core;

[SupportedOSPlatform("windows")]
internal static class MicrosoftToolTrust
{
    internal const int CacheOnlyUrlRetrieval = 0x1000;
    private const int SubjectNotTrusted = unchecked((int)0x800B0004);
    private static readonly Guid GenericVerify = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal sealed record Verification(int Status, string Source, string? Publisher = null, string? Subject = null,
        bool Timestamped = false)
    {
        public bool TrustedMicrosoft => Status == 0 && IsMicrosoftPublisher(Publisher, Subject);
        public string Detail => $"{Source}: 0x{Status:X8} " + (Status == 0
            ? $"publisher '{Publisher}'{(TrustedMicrosoft ? "" : " is not an approved Microsoft publisher")}" : new Win32Exception(Status).Message);
    }

    internal static bool IsMicrosoftPublisher(string? publisher, string? subject) =>
        new[] { "Microsoft Corporation", "Microsoft Windows", "Microsoft Windows Kits Publisher" }
            .Contains(publisher, StringComparer.OrdinalIgnoreCase) && subject is not null &&
        Regex.IsMatch(subject, @"(?:^|,\s*)O=Microsoft Corporation(?:,|$)", RegexOptions.CultureInvariant);

    internal static Verification Verify(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var attempts = new List<Verification>();
        using (var file = new NativeStructure<FileInfoNative>(new()
               { Size = Marshal.SizeOf<FileInfoNative>(), Path = path, Handle = input.SafeFileHandle.DangerousGetHandle() }))
        using (var settings = new NativeStructure<SignatureSettings>(new()
               { Size = Marshal.SizeOf<SignatureSettings>(), Flags = 3 }))
        {
            var data = NewTrustData(file.Pointer, 1);
            data.SignatureSettings = settings.Pointer;
            var result = Verify(ref data, "embedded signature 0");
            if (result.TrustedMicrosoft) return result;
            attempts.Add(result);
            var count = settings.Value.SecondaryCount;
            for (uint index = 1; index <= count; index++)
            {
                settings.Value = new() { Size = Marshal.SizeOf<SignatureSettings>(), Flags = 1, Index = index };
                data = NewTrustData(file.Pointer, 1);
                data.SignatureSettings = settings.Pointer;
                result = Verify(ref data, $"embedded signature {index}");
                if (result.TrustedMicrosoft) return result;
                attempts.Add(result);
            }
        }
        // System tools may be catalog-signed, or have a newer trusted catalog than their embedded signature.
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            var catalog = VerifyCatalogs(path, input.SafeFileHandle, algorithm, attempts);
            if (catalog is not null) return catalog;
        }
        var failure = attempts.FirstOrDefault(attempt => attempt.Status != 0)?.Status ?? SubjectNotTrusted;
        throw new Win32Exception(failure, $"'{path}' does not have a trusted Microsoft signature. " +
            string.Join("; ", attempts.Select(attempt => attempt.Detail)) +
            (failure == unchecked((int)0x800B0101)
                ? ". Windows rejected a certificate validity period or timestamp; an expired signer needs a valid trusted timestamp. No time or trust-policy overrides are applied." : "."));
    }

    private static Verification? VerifyCatalogs(string path, SafeFileHandle file, string algorithm, List<Verification> attempts)
    {
        if (!CryptCATAdminAcquireContext2(out var administrator, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
            throw NativeError("Cannot open the Windows catalog verifier", path);
        IntPtr catalog = IntPtr.Zero;
        try
        {
            uint length = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(administrator, file, ref length, null, 0))
                throw NativeError("Cannot size the Windows catalog member hash", path);
            var hash = new byte[checked((int)length)];
            if (!CryptCATAdminCalcHashFromFileHandle2(administrator, file, ref length, hash, 0))
                throw NativeError("Cannot calculate the Windows catalog member hash", path);
            var pinned = GCHandle.Alloc(hash, GCHandleType.Pinned);
            try
            {
                while (true)
                {
                    Marshal.SetLastPInvokeError(0);
                    catalog = CryptCATAdminEnumCatalogFromHash(administrator, hash, length, 0, ref catalog);
                    if (catalog == IntPtr.Zero)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error is not (0 or 1168 or 259) && error != unchecked((int)0x80092004))
                            throw new Win32Exception(error, $"Cannot enumerate Windows catalogs for '{path}' (0x{error:X8}).");
                        break;
                    }
                    var info = new CatalogInfo { Size = Marshal.SizeOf<CatalogInfo>() };
                    if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0))
                        throw NativeError("Cannot read the Windows catalog path", path);
                    PathSafety.RejectReparsePoints(info.Path);
                    using var member = new NativeStructure<CatalogMember>(new()
                    {
                        Size = Marshal.SizeOf<CatalogMember>(), CatalogPath = info.Path,
                        MemberTag = Convert.ToHexString(hash), MemberPath = path, MemberFile = file.DangerousGetHandle(),
                        Hash = pinned.AddrOfPinnedObject(), HashLength = length, Administrator = administrator
                    });
                    var data = NewTrustData(member.Pointer, 2);
                    var result = Verify(ref data, $"{algorithm} catalog '{info.Path}'");
                    if (result.TrustedMicrosoft) return result;
                    attempts.Add(result);
                }
            }
            finally { pinned.Free(); }
        }
        finally
        {
            try
            {
                if (catalog != IntPtr.Zero && !CryptCATAdminReleaseCatalogContext(administrator, catalog, 0))
                    throw NativeError("Cannot close the Windows catalog member", path);
            }
            finally
            {
                if (!CryptCATAdminReleaseContext(administrator, 0)) throw NativeError("Cannot close the Windows catalog verifier", path);
            }
        }
        return null;
    }

    private static TrustData NewTrustData(IntPtr subject, int choice) => new()
    {
        Size = Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = choice, Subject = subject,
        StateAction = 1, ProviderFlags = CacheOnlyUrlRetrieval
    };

    private static Verification Verify(ref TrustData data, string source)
    {
        var action = GenericVerify;
        try
        {
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) return new(status, source);
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero)
                throw new InvalidDataException($"Windows trust verification returned no signing certificate for {source}.");
            var context = Marshal.PtrToStructure<ProviderCertificate>(certificate).Certificate;
            if (context == IntPtr.Zero) throw new InvalidDataException($"Windows trust verification returned an empty certificate for {source}.");
            using var cert = new X509Certificate2(context);
            return new(0, source, cert.GetNameInfo(X509NameType.SimpleName, false), cert.Subject,
                WTHelperGetProvSignerFromChain(provider, 0, true, 0) != IntPtr.Zero);
        }
        finally
        {
            data.StateAction = 2;
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) throw new Win32Exception(status, $"Cannot close Windows trust verification for {source} (0x{status:X8}).");
        }
    }

    private static Win32Exception NativeError(string operation, string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new(error, $"{operation} for '{path}' (0x{error:X8}).");
    }

    private sealed class NativeStructure<T> : IDisposable where T : struct
    {
        public IntPtr Pointer { get; } = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        public T Value
        {
            get => Marshal.PtrToStructure<T>(Pointer);
            set => Marshal.StructureToPtr(value, Pointer, false);
        }
        public NativeStructure(T value) => Value = value;
        public void Dispose() { Marshal.DestroyStructure<T>(Pointer); Marshal.FreeHGlobal(Pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoNative
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr Subject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public int Size; public IntPtr Policy; public IntPtr Sip; public int UiChoice; public int Revocation;
        public int UnionChoice; public IntPtr Subject; public int StateAction; public IntPtr StateData;
        public IntPtr Url; public int ProviderFlags; public int UiContext; public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SignatureSettings
    {
        public int Size; public uint Index; public uint Flags; public uint SecondaryCount;
        public uint VerifiedIndex; public IntPtr CryptoPolicy;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogMember
    {
        public int Size; public int Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string CatalogPath;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string MemberPath;
        public IntPtr MemberFile; public IntPtr Hash; public uint HashLength;
        public IntPtr CatalogContext; public IntPtr Administrator;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { public uint Size; public IntPtr Certificate; }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counter);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr administrator, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr administrator, SafeFileHandle file, ref uint size, [Out] byte[]? hash, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr administrator, byte[] hash, uint length, uint flags, ref IntPtr previous);
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr administrator, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr administrator, uint flags);
}
