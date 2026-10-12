using System.Xml.Linq;
using Xunit;

namespace Tiny11Builder.Core.Tests;

public sealed class DeploymentTests
{
    [Fact]
    public void Application_always_requests_administrator_approval_without_UI_access()
    {
        var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Deployment", "app.manifest"));
        XNamespace ns = "urn:schemas-microsoft-com:asm.v3";
        var level = Assert.Single(manifest.Descendants(ns + "requestedExecutionLevel"));
        Assert.Equal("requireAdministrator", level.Attribute("level")?.Value);
        Assert.Equal("false", level.Attribute("uiAccess")?.Value);
    }
}
