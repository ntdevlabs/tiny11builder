using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Tiny11Builder.Core;
using Xunit;
using Xunit.Abstractions;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class MicrosoftToolTrustTests(ITestOutputHelper output)
{
    [Fact]
    public void A_catalog_only_Microsoft_system_file_is_not_treated_as_unsigned()
    {
        var path = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        var verified = MicrosoftToolTrust.Verify(path);
        Assert.True(verified.TrustedMicrosoft, verified.Detail);
        Assert.Contains("catalog", verified.Source);
        ToolLocator.Validate(path, "whoami.exe");
    }

    [Fact]
    public void Installed_inbox_DISM_and_its_complete_provider_root_are_trusted()
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\dism.exe");
        ToolLocator.Validate(dism, "dism.exe");
    }

    [Fact]
    public void Installed_arm64_ADK_Microsoft_Windows_Kits_publisher_and_timestamp_are_accepted_when_present()
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\arm64\DISM\dism.exe");
        if (!File.Exists(dism)) return;
        var verified = MicrosoftToolTrust.Verify(dism);
        Assert.True(verified.TrustedMicrosoft, verified.Detail);
        Assert.True(verified.Timestamped);
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(dism));
        Assert.True(MicrosoftToolTrust.IsMicrosoftPublisher(signer.GetNameInfo(X509NameType.SimpleName, false), signer.Subject));
        output.WriteLine($"ARM64 ADK signer expires {signer.NotAfter:O}; WinVerifyTrust status 0x{verified.Status:X8}; verified timestamp={verified.Timestamped}.");
        ToolLocator.Validate(dism, "dism.exe");
    }

    [Theory]
    [InlineData("Microsoft Corporation", "CN=Microsoft Corporation, O=Microsoft Corporation, C=US", true)]
    [InlineData("Microsoft Windows", "CN=Microsoft Windows, O=Microsoft Corporation, C=US", true)]
    [InlineData("Microsoft Windows Kits Publisher", "CN=Microsoft Windows Kits Publisher, O=Microsoft Corporation, C=US", true)]
    [InlineData("Microsoft Corporation", "CN=Microsoft Corporation, O=Microsoft Corporation Impersonator, C=US", false)]
    [InlineData("Unrelated Company", "CN=Unrelated Company, O=Microsoft Corporation, C=US", false)]
    [InlineData("Microsoft Corporation", "CN=Microsoft Corporation, OU=Microsoft Corporation, C=US", false)]
    [InlineData(null, null, false)]
    public void A_Microsoft_named_subject_is_not_enough_without_the_exact_publisher_and_organization(string? publisher, string? subject, bool accepted)
    {
        Assert.Equal(accepted, MicrosoftToolTrust.IsMicrosoftPublisher(publisher, subject));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(unchecked((int)0x800B0101), false)]
    [InlineData(unchecked((int)0x80096010), false)]
    [InlineData(unchecked((int)0x800B0109), false)]
    public void Only_WinVerifyTrust_zero_is_success_even_for_a_Microsoft_certificate(int status, bool accepted)
    {
        Assert.Equal(accepted, new MicrosoftToolTrust.Verification(status, "test", "Microsoft Corporation",
            "CN=Microsoft Corporation, O=Microsoft Corporation, C=US").TrustedMicrosoft);
        Assert.Equal(0x1000, MicrosoftToolTrust.CacheOnlyUrlRetrieval);
        Assert.Equal(0, MicrosoftToolTrust.CacheOnlyUrlRetrieval & 0x800);
    }

    [Theory]
    [InlineData("dism.exe")]
    [InlineData("oscdimg.exe")]
    public void Arbitrary_unsigned_tools_are_not_a_trust_fallback_or_a_pinned_oscdimg_exception(string filename)
    {
        using var files = new IssueFixTestDirectory();
        var path = Path.Combine(files.Root, filename);
        File.WriteAllText(path, "not an approved Microsoft tool");
        var error = Record.Exception(() => ToolLocator.Validate(path, filename));
        Assert.NotNull(error);
        Assert.Contains(path, error.Message);
        Assert.Contains("0x", error.Message);
        if (filename == "dism.exe") Assert.IsType<ServicingToolsRequiredException>(error);
        else Assert.IsType<Win32Exception>(error);
    }

    [Fact]
    public void Modifying_a_catalog_member_changes_its_hash_and_is_not_accepted()
    {
        using var files = new IssueFixTestDirectory();
        var path = Path.Combine(files.Root, "whoami.exe");
        var bytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "whoami.exe"));
        Assert.True(bytes.Length > 4096);
        bytes[4096] ^= 1;
        File.WriteAllBytes(path, bytes);
        var error = Assert.Throws<Win32Exception>(() => ToolLocator.Validate(path, "whoami.exe"));
        Assert.NotEqual(0, error.NativeErrorCode);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void A_different_Microsoft_signed_executable_renamed_to_DISM_is_not_the_expected_tool_resource()
    {
        using var files = new IssueFixTestDirectory();
        var path = Path.Combine(files.Root, "dism.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), path);
        var error = Assert.Throws<ServicingToolsRequiredException>(() => ToolLocator.Validate(path, "dism.exe"));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("resource", error.Message);
    }

    [Fact]
    public void A_trusted_executable_without_its_matching_provider_root_is_rejected()
    {
        using var files = new IssueFixTestDirectory();
        var root = Environment.SystemDirectory;
        var path = Path.Combine(files.Root, "dism.exe");
        File.Copy(Path.Combine(root, "dism.exe"), path);
        foreach (var resource in Directory.EnumerateDirectories(root)
                     .Select(directory => Path.Combine(directory, "dism.exe.mui")).Where(File.Exists))
        {
            var language = Path.GetFileName(Path.GetDirectoryName(resource)!);
            Directory.CreateDirectory(Path.Combine(files.Root, language));
            File.Copy(resource, Path.Combine(files.Root, language, "dism.exe.mui"));
        }
        var error = Assert.Throws<ServicingToolsRequiredException>(() => ToolLocator.Validate(path, "dism.exe"));
        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.Contains("DismApi.dll", error.Message);
        Assert.Contains("incomplete", error.Message);
    }

    [Fact]
    public void The_installed_oscdimg_signature_or_existing_exact_hash_exception_still_works_when_present()
    {
        var path = ToolLocator.FindOscdimg();
        if (string.IsNullOrEmpty(path)) return;
        ToolLocator.Validate(path, "oscdimg.exe");
    }
}
