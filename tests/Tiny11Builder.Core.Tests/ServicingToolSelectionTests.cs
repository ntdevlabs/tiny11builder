using System.ComponentModel;
using System.Runtime.Versioning;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ServicingToolSelectionTests
{
    [Theory]
    [InlineData(unchecked((int)0x800B0101))]
    [InlineData(unchecked((int)0x80096010))]
    [InlineData(unchecked((int)0x800B0109))]
    [InlineData(unchecked((int)0x800B0100))]
    public void An_untrusted_newer_installation_cannot_hide_a_trusted_complete_older_root(int trustError)
    {
        var diagnostics = new List<string>();
        var chosen = ToolLocator.SelectDism(["new ADK", "trusted inbox"], path => path == "new ADK"
            ? throw new Win32Exception(trustError, $"new ADK signature rejected: 0x{trustError:X8}")
            : new Version(10, 0, 26100, 1), diagnostics.Add);
        Assert.Equal("trusted inbox", chosen);
        Assert.Contains(diagnostics, line => line.Contains("new ADK", StringComparison.Ordinal) && line.Contains($"0x{trustError:X8}", StringComparison.Ordinal));
        Assert.Contains(diagnostics, line => line.Contains("Selected trusted DISM 'trusted inbox'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_newest_trusted_complete_engine_wins_not_the_executable_wrapper_or_search_order()
    {
        var versions = new Dictionary<string, Version>
        {
            ["older"] = new(10, 0, 22621, 1), ["newest"] = new(10, 0, 29697, 1), ["middle"] = new(10, 0, 26100, 1)
        };
        Assert.Equal("newest", ToolLocator.SelectDism(["older", "newest", "middle"], path => versions[path]));
    }

    [Fact]
    public void An_incomplete_ADK_root_does_not_borrow_system_providers_or_block_a_complete_installed_root()
    {
        var diagnostics = new List<string>();
        Assert.Equal("complete", ToolLocator.SelectDism(["incomplete", "complete"], path => path == "incomplete"
            ? throw new FileNotFoundException("incomplete: missing matching WimProvider.dll", "WimProvider.dll")
            : new Version(10, 0, 26100, 1), diagnostics.Add));
        Assert.Contains(diagnostics, line => line.Contains("WimProvider.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void No_trusted_installation_is_an_actionable_failure_with_native_code_and_path_not_a_success_fallback()
    {
        var native = new Win32Exception(unchecked((int)0x800B0101), "expired ADK: 0x800B0101");
        var error = Assert.Throws<ServicingToolsRequiredException>(() => ToolLocator.SelectDism(["expired ADK"], _ => throw native));
        Assert.Contains("No trusted, complete", error.Message);
        Assert.Contains("expired ADK", error.Message);
        Assert.Contains("0x800B0101", error.Message);
        Assert.Contains("repair", error.Message);
        Assert.Same(native, error.InnerException);
    }

    [Fact]
    public void Multiple_known_failures_retain_every_reason_for_the_tools_action()
    {
        var error = Assert.Throws<ServicingToolsRequiredException>(() => ToolLocator.SelectDism(["untrusted", "missing"], path => path == "untrusted"
            ? throw new Win32Exception(unchecked((int)0x800B0101), "untrusted: 0x800B0101")
            : throw new FileNotFoundException("missing provider root")));
        Assert.Equal(2, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions.Count);
        Assert.Contains("0x800B0101", error.Message);
        Assert.Contains("missing provider root", error.Message);
    }

    [Fact]
    public void Unexpected_IO_errors_and_diagnostic_sink_errors_are_not_caught_as_trust_fallbacks()
    {
        var unexpected = new IOException("unexpected storage failure");
        Assert.Same(unexpected, Assert.Throws<IOException>(() => ToolLocator.SelectDism(["first", "second"], _ => throw unexpected)));
        var sink = new InvalidDataException("diagnostic sink failed");
        Assert.Same(sink, Assert.Throws<InvalidDataException>(() => ToolLocator.SelectDism(["untrusted", "trusted"], path => path == "untrusted"
            ? throw new Win32Exception(5) : new Version(10, 0, 26100, 1), _ => throw sink)));
    }

    [Fact]
    public void Actual_auto_detection_returns_a_trusted_complete_tool_and_logs_its_identity()
    {
        var diagnostics = new List<string>();
        var path = ToolLocator.FindDism(diagnostics.Add);
        ToolLocator.Validate(path, "dism.exe");
        Assert.Contains(diagnostics, line => line.Contains(path, StringComparison.Ordinal) && line.StartsWith("Selected trusted", StringComparison.Ordinal));
    }
}
