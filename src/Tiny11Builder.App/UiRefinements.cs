using System.Collections.ObjectModel;
using System.Text;
using Tiny11Builder.Core;

namespace Tiny11Builder.App;

internal static class RecipeSelection
{
    public static BuildRecipe Read(string path, bool useCustom)
    {
        var recipe = BuildRecipe.Read(path);
        return useCustom ? recipe with { Mode = BuildMode.Custom } : recipe;
    }

    public static BuildRecipe BindPaths(BuildRecipe recipe, BuildRecipe current) => recipe with
    {
        SourcePath = string.IsNullOrWhiteSpace(recipe.SourcePath) ? current.SourcePath : recipe.SourcePath,
        WorkspacePath = string.IsNullOrWhiteSpace(recipe.WorkspacePath) ? current.WorkspacePath : recipe.WorkspacePath,
        OutputPath = string.IsNullOrWhiteSpace(recipe.OutputPath) ? current.OutputPath : recipe.OutputPath,
        ImageIndex = string.IsNullOrWhiteSpace(recipe.SourcePath) ? current.ImageIndex : recipe.ImageIndex
    };

    public static void ApplyGroups(IEnumerable<OptionChoice> choices, IEnumerable<string> selected)
    {
        var ids = selected.ToHashSet(StringComparer.Ordinal);
        foreach (var choice in choices) choice.Selected = ids.Contains(choice.Option.Id);
    }
}

internal static class StepNavigation
{
    public const int StepCount = 6;

    public static bool CanSelect(int target, int current, int reached, bool imageReady, bool expertReady,
        bool busy = false, bool completed = false, bool modalOpen = false)
    {
        if (target is < 0 or >= StepCount || busy || completed || modalOpen) return false;
        if (target <= current) return true;
        if (target > Math.Max(reached, current + 1)) return false;
        if (target >= 2 && !imageReady) return false;
        return target < 3 || expertReady;
    }

    public static string Hint(int current, bool imageReady, bool expertReady) => current switch
    {
        1 when !imageReady => UiText.Get("Navigation.InspectFirst"),
        2 when !expertReady => UiText.Get("Navigation.AcceptExpertRisk"),
        _ => ""
    };
}

internal static class PresetRemovalPreview
{
    public static IReadOnlyList<CleanupOption> ForMode(BuildMode mode)
    {
        var ids = CleanupCatalog.ForMode(mode).ToHashSet(StringComparer.Ordinal);
        return CleanupCatalog.Options.Where(option => ids.Contains(option.Id)).ToArray();
    }
}

internal static class InventoryTextExport
{
    // Export the inventory itself, never a filtered ItemsSource or a set of removal selections.
    public static string Create(IReadOnlyList<ImageInventoryItem> inventory)
    {
        var text = new StringBuilder();
        text.AppendLine("# Tiny11 Builder — complete image component inventory");
        text.AppendLine($"# Components: {inventory.Count}");
        text.AppendLine("# All loaded components are included, regardless of search, type filters or removal selections.");
        text.AppendLine("# This is an inventory, not a removal plan. Identifiers and kinds are unchanged.");
        text.AppendLine("# Fields are tab-separated; backslashes, tabs and line breaks in fields are escaped.");
        text.AppendLine("Kind\tIdentity\tDetail\tProtected");
        foreach (var item in inventory)
            text.AppendLine($"{item.Kind}\t{Escape(item.Identity)}\t{Escape(item.Detail)}\t{item.Protected}");
        return text.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}

internal sealed record UpdatePackageChoice(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

internal sealed class UpdatePackageSelection
{
    public ObservableCollection<UpdatePackageChoice> Items { get; } = [];
    public string[] Paths => Items.Select(item => item.Path).ToArray();

    public void Add(string path)
    {
        var normalized = PathSafety.Full(path);
        if (!new[] { ".msu", ".cab" }.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(UiText.Get("Updates.InvalidExtension"));
        if (Items.Count >= 100) throw new InvalidDataException(UiText.Get("Updates.TooMany"));
        if (Items.Any(item => item.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(UiText.Get("Updates.Duplicate"));
        Items.Add(new(normalized));
    }

    public void Restore(IEnumerable<string> paths)
    {
        var restored = new UpdatePackageSelection();
        foreach (var path in paths) restored.Add(path);
        Items.Clear();
        foreach (var item in restored.Items) Items.Add(item);
    }

    public bool Move(int index, int offset)
    {
        var target = index + offset;
        if (index < 0 || index >= Items.Count || target < 0 || target >= Items.Count || Math.Abs(offset) != 1) return false;
        Items.Move(index, target);
        return true;
    }

    public bool Remove(int index)
    {
        if (index < 0 || index >= Items.Count) return false;
        Items.RemoveAt(index);
        return true;
    }
}
