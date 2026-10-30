namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    /// <summary>CBS_E_PENDING: DISM will not clean the component store while operations are pending (https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options).</summary>
    internal const int PendingOperationsExitCode = unchecked((int)0x800F0806);

    private const string PendingSkipReason = "DISM reported pending operations (0x800F0806). They stay pending until first boot and nothing was reverted. " +
        "Superseded components remain, so the image may be larger.";

    private static bool RemovesComponentPayloads(BuildRecipe recipe) => SetupInstaller.RequiresLegacy(recipe);

    private static async Task CleanComponentsAsync(BuildSession s, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (s.Capabilities.Any(item => item.Deferred) || HasPendingUpdates(s))
        {
            if (RemovesComponentPayloads(s.Recipe)) throw Refuse(s, DeferredServicingEvidence(s), null);
            s.CleanupDeferredForPending = true;
            const string reason = "Servicing operations are deferred until first boot. Superseded components were not cleaned; pending payloads are retained.";
            s.Warn("Component cleanup was skipped: " + reason);
            s.Tracking.SkipStage(reason);
            return;
        }
        try
        {
            await s.Dism.RunAsync(token, s.Recipe.Flavor == BuildFlavor.Core ?
                [$"/Image:{s.Mount}", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"] :
                [$"/Image:{s.Mount}", "/Cleanup-Image", "/StartComponentCleanup"]).ConfigureAwait(false);
        }
        catch (DismCommandException error) when (error.ExitCode == PendingOperationsExitCode)
        {
            token.ThrowIfCancellationRequested();
            if (RemovesComponentPayloads(s.Recipe))
                throw Refuse(s, [$"DISM exit code {error.ExitCode} (0x{error.ExitCode:X8}) during component cleanup"], error);
            s.CleanupDeferredForPending = true;
            s.Warn("Component cleanup was skipped: " + PendingSkipReason);
            s.Tracking.SkipStage(PendingSkipReason);
        }
    }

    private static IReadOnlyList<string> OriginalFileOptions(BuildSession s)
    {
        var options = s.Recipe.SelectedOptions;
        if ((!s.CleanupDeferredForPending && !s.Capabilities.Any(item => item.Deferred) && !HasPendingUpdates(s)) || !options.Contains("edge-files")) return options;
        s.Tracking.Skip("retained:edge-files", "Advanced payloads", CleanupCatalog.Options.Single(option => option.Id == "edge-files").Title,
            "Retained because pending servicing may reference these component files");
        var pending = s.Capabilities.Any(item => item.Deferred) ? "pending capability removals" :
            HasPendingUpdates(s) ? "pending Windows updates" : "pending operations (0x800F0806)";
        s.Warn($"Edge and WebView2 removal was selected but not applied: {pending} may reference their component files, " +
            "so all Edge files were kept.");
        return options.Where(option => option != "edge-files").ToArray();
    }

    private static async Task EnsureNoPendingOperationsAsync(BuildSession s, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Checking an offline image for pending operations requires Windows.");
        s.Log("Checking the image for pending operations before any component file is removed.");
        token.ThrowIfCancellationRequested();
        if (s.Capabilities.Any(item => item.Deferred) || HasPendingUpdates(s)) throw Refuse(s, DeferredServicingEvidence(s), null);
        var scan = await PendingOperations.ScanAsync(s.Dism, s.Mount, token).ConfigureAwait(false);
        if (scan.Pending.Count > 0) throw Refuse(s, scan.Pending, null);
        s.Log($"No pending operations found ({scan.Packages} packages and {scan.Features} features checked).");
        token.ThrowIfCancellationRequested();
    }

    private static bool HasPendingUpdates(BuildSession s) => s.UpdatePackageStates.Any(item => PendingOperations.IsPending(item.Detail));

    private static string[] DeferredServicingEvidence(BuildSession s) => s.Capabilities.Where(item => item.Deferred)
        .Select(item => $"capability {item.Name}: {item.State}")
        .Concat(s.UpdatePackageStates.Where(item => PendingOperations.IsPending(item.Detail))
            .Select(item => $"package {item.Identity}: {item.Detail}")).ToArray();

    private static PendingOperationsException Refuse(BuildSession s, IReadOnlyList<string> evidence, Exception? inner)
    {
        foreach (var item in evidence) s.Log("Pending operation: " + item);
        return new(PendingOperations.Refusal(s.Recipe), evidence, inner);
    }
}

internal sealed class PendingOperationsException(string message, IReadOnlyList<string> evidence, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public IReadOnlyList<string> Evidence { get; } = evidence;
}
