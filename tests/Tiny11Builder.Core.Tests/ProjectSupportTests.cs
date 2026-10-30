using System.Xml.Linq;
using Tiny11Builder.App;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class ProjectSupportTests
{
    [Theory]
    [InlineData("Sponsors", "https://github.com/sponsors/ntdevlabs")]
    [InlineData("Kofi", "https://ko-fi.com/ntdev")]
    [InlineData("Patreon", "https://www.patreon.com/ntdev")]
    [InlineData("PayPal", "https://paypal.me/ntdev2")]
    public void Support_uses_only_the_existing_project_destinations(string destination, string expected)
    {
        var uri = ProjectSupport.Link(Enum.Parse<SupportDestination>(destination));
        Assert.Equal(expected, uri.AbsoluteUri);
        Assert.Equal("https", uri.Scheme);
        Assert.True(uri.IsDefaultPort);
        Assert.Empty(uri.Query);
        Assert.Empty(uri.UserInfo);
    }

    [Fact]
    public void Unsupported_links_are_not_guessed_and_support_never_unlocks_features()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectSupport.Link((SupportDestination)99));
        Assert.Contains("All app features remain free", ProjectSupport.Purpose);
    }

    [Fact]
    public void Support_is_only_a_user_opened_panel_and_the_success_link_stays_inside_the_result()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "MainWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dialog = Assert.Single(document.Descendants(ns + "ContentDialog"),
            element => element.Attribute(x + "Name")?.Value == "SupportDialog");
        Assert.Equal("SupportDialog", dialog.Attribute(x + "Name")?.Value);
        Assert.Equal("Close", dialog.Attribute("DefaultButton")?.Value);
        Assert.Null(dialog.Attribute("IsOpen"));
        var triggers = document.Descendants(ns + "HyperlinkButton")
            .Where(element => element.Attribute("Click")?.Value == "OnSupport").ToArray();
        Assert.Equal(2, triggers.Length);
        var result = Assert.Single(document.Descendants(), element => element.Attribute(x + "Name")?.Value == "ResultPanel");
        Assert.Contains(triggers, element => element.Ancestors().Contains(result));
        var links = dialog.Descendants(ns + "HyperlinkButton").ToArray();
        Assert.Equal(4, links.Length);
        Assert.All(links, element => Assert.True(Enum.TryParse<SupportDestination>(element.Attribute("Tag")?.Value, out _)));
        Assert.All(links, element => Assert.Equal("OnSupportDestination", element.Attribute("Click")?.Value));
    }
}
