using System.Runtime.Versioning;
using System.Xml.Linq;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class SetupProductKeyTests
{
    public static IEnumerable<object[]> ClientEditions()
    {
        string[] editions = ["Core", "CoreN", "CoreSingleLanguage", "CoreCountrySpecific", "Professional", "ProfessionalN",
            "ProfessionalEducation", "ProfessionalEducationN", "ProfessionalWorkstation", "ProfessionalWorkstationN",
            "Education", "EducationN", "Enterprise", "EnterpriseN", "EnterpriseS", "EnterpriseSN", "EnterpriseG",
            "EnterpriseGN", "IoTEnterprise", "IoTEnterpriseS", "CloudEdition", "CloudEditionN", "ServerRdsh"];
        foreach (var edition in editions)
        foreach (var architecture in new[] { "x64", "arm64" })
        foreach (var compact in new[] { false, true })
            yield return [edition, architecture, compact];
    }

    [Theory]
    [MemberData(nameof(ClientEditions))]
    public void Legacy_Setup_keeps_edition_and_compact_but_leaves_the_product_key_to_the_user(
        string edition, string architecture, bool compact)
    {
        var image = CompatibilityTests.Image(26300, architecture) with { Edition = edition };
        var xml = XDocument.Parse(UnattendBuilder.Create(image, new()
        {
            Flavor = BuildFlavor.Core, CompactInstallation = compact, PreferLocalAccount = true
        }));
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var setup = xml.Descendants(ns + "settings").Single(settings => (string?)settings.Attribute("pass") == "windowsPE")
            .Elements(ns + "component").Single(component => (string?)component.Attribute("name") == "Microsoft-Windows-Setup");
        var key = Assert.Single(setup.Elements(ns + "UserData").Elements(ns + "ProductKey"));
        Assert.Equal("Always", Assert.Single(key.Elements(ns + "WillShowUI")).Value);
        Assert.Single(key.Elements());
        Assert.Empty(key.Elements(ns + "Key"));
        Assert.Single(xml.Descendants(ns + "ProductKey"));
        var osImage = Assert.Single(setup.Elements(ns + "ImageInstall").Elements(ns + "OSImage"));
        Assert.Equal(compact.ToString().ToLowerInvariant(), osImage.Element(ns + "Compact")!.Value);
        Assert.Equal("OnError", osImage.Element(ns + "WillShowUI")!.Value);
        var metadata = Assert.Single(osImage.Descendants(ns + "MetaData"));
        Assert.Equal("/IMAGE/INDEX", metadata.Element(ns + "Key")!.Value);
        Assert.Equal("1", metadata.Element(ns + "Value")!.Value);
        foreach (var forbidden in new[] { "AcceptEula", "UserAccounts", "AutoLogon", "DiskConfiguration", "InstallTo",
                     "InstallToAvailablePartition", "RunSynchronous" })
            Assert.Empty(xml.Descendants(ns + forbidden));
        Assert.All(xml.Descendants(ns + "component"), component =>
            Assert.Equal(architecture == "x64" ? "amd64" : "arm64", (string?)component.Attribute("processorArchitecture")));
    }

    [Fact]
    public void Custom_component_deletion_also_uses_interactive_legacy_keys_but_regular_Setup_is_unchanged()
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var recipe = new BuildRecipe
        {
            ExpertActions = [new(ImageItemKind.ComponentPayload, "amd64_component", ExpertActionMode.Remove)]
        };
        var xml = XDocument.Parse(UnattendBuilder.Create(CompatibilityTests.Image(), recipe));
        Assert.Equal("Always", Assert.Single(xml.Descendants(ns + "ProductKey").Elements(ns + "WillShowUI")).Value);
        Assert.Empty(XDocument.Parse(UnattendBuilder.Create(CompatibilityTests.Image(), new())).Descendants(ns + "ProductKey"));
    }
}
