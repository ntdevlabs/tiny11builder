using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

/// <summary>
/// The checkpoint check runs after every commit of every build, so it is verified against headers written by the real
/// DISM, not only against the synthetic headers of the fake runner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WimHeaderTests
{
    // First 208 bytes of an XPRESS WIM captured by DISM 10.0.29691 (version 0x10D00, flags 0x00020082, one image).
    private const string RealWimHeader =
        "TVNXSU0AAADQAAAAAA0BAIIAAgAAgAAAdACjBuDTDE65ateDrGO8cAEAAQABAAAATAQAAAAAAAKQLgAAAAAAAEwEAAAAAAAANAMAAAAAAALcMgAAAAAAADQDAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAAEDYAAAAAAAAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    // First 208 bytes of a solid LZMS ESD exported with /Compress:recovery (version 0xE00, flags 0x00080082, one image).
    private const string RealEsdHeader =
        "TVNXSU0AAADQAAAAAA4AAIIACAAAAAIAYCo3ahwevkiy8yJGbKbZGwEAAQABAAAAfgQAAAAAAAIkBAAAAAAAAH4EAAAAAAAAMgMAAAAAAAKiCAAAAAAAADIDAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAA1AsAAAAAAAAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    private static string Write(BuildFixture fixture, byte[] bytes)
    {
        var path = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N") + ".wim");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(RealWimHeader)]
    [InlineData(RealEsdHeader)]
    public void Headers_written_by_the_real_DISM_pass_the_completeness_check(string header)
    {
        using var fixture = new BuildFixture();
        var bytes = new byte[4096];
        Convert.FromBase64String(header).CopyTo(bytes, 0);
        Digest.RequireCompleteWim(Write(fixture, bytes), "The image");
    }

    [Theory]
    [InlineData(RealWimHeader, 16, 0x40, "mid-write")]            // WIM_HDR_FLAG_WRITE_IN_PROGRESS
    [InlineData(RealEsdHeader, 16, 0x40, "mid-write")]
    [InlineData(RealWimHeader, 0, 0x01, "no valid WIM header")]    // signature broken
    [InlineData(RealWimHeader, 8, 0x01, "unexpected WIM header size")]
    public void A_header_that_is_cut_off_or_not_a_WIM_is_refused(string header, int offset, int bit, string diagnostic)
    {
        using var fixture = new BuildFixture();
        var bytes = new byte[4096];
        Convert.FromBase64String(header).CopyTo(bytes, 0);
        bytes[offset] ^= (byte)bit;
        var failure = Assert.Throws<InvalidDataException>(() => Digest.RequireCompleteWim(Write(fixture, bytes), "The image"));
        Assert.Contains(diagnostic, failure.Message);
    }

    [Fact]
    public void An_image_with_no_images_or_too_few_bytes_is_refused()
    {
        using var fixture = new BuildFixture();
        var bytes = new byte[4096];
        Convert.FromBase64String(RealWimHeader).CopyTo(bytes, 0);
        BitConverter.GetBytes(0u).CopyTo(bytes, 44);
        Assert.Contains("no images", Assert.Throws<InvalidDataException>(() => Digest.RequireCompleteWim(Write(fixture, bytes), "The image")).Message);
        Assert.Contains("too small", Assert.Throws<InvalidDataException>(() => Digest.RequireCompleteWim(Write(fixture, new byte[100]), "The image")).Message);
    }
}
