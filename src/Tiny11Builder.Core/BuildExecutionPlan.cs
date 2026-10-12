namespace Tiny11Builder.Core;

/// <summary>Host-level switches that are deliberately NOT part of the persisted <see cref="BuildRecipe"/>.</summary>
/// <param name="AllowOptimizedMount">
/// Development seam. Lazy (<c>/Optimize</c>) mounting is never used unless the host opts in. A blind probe cost a real
/// build about 12 minutes (8m31 mount, DISM 4350 on first access, 3m53 discard) on a host without support.
/// </param>
public sealed record BuildEngineOptions(bool AllowOptimizedMount = false);

public sealed record BuildExecutionPlan(bool OptimizedMount, bool FastWorkingImage, int CopyWorkers, bool BufferedLog)
{
    public static BuildExecutionPlan Create(BuildRecipe recipe, int logicalProcessors, bool allowOptimizedMount = false)
    {
        if (logicalProcessors < 1) throw new ArgumentOutOfRangeException(nameof(logicalProcessors));
        return new(recipe.TurboMode && allowOptimizedMount,
            recipe.TurboMode && (recipe.Flavor == BuildFlavor.Core ||
                recipe.Compression == Compression.Fast ||
                recipe.ExpertActions.Any(a => a.Kind == ImageItemKind.ComponentPayload)),
            recipe.TurboMode ? Math.Clamp(logicalProcessors / 2, 4, 8) : 4,
            recipe.TurboMode);
    }

    public string WorkingCompression(string sourceCompression) =>
        FastWorkingImage ? "/Compress:fast" : sourceCompression;
}

public sealed record DismExecutionSettings(string ScratchDirectory, string LogPath, bool OptimizedMount);
