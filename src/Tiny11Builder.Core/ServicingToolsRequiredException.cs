namespace Tiny11Builder.Core;

public sealed class ServicingToolsRequiredException(string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public const string DownloadPage = "https://learn.microsoft.com/windows-hardware/get-started/adk-install";
}
