using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiny11Builder.Core;

public enum Compression { Recovery, Maximum, Fast }
public enum CleanupPreset { Balanced, Minimal, Original, Custom }
public enum BuildFlavor { Regular, Core }
public enum BuildMode { Tiny11, Tiny11Core, Custom }
public enum OptionKind { App, Feature, Capability, Policy, File }
public enum PolicyHive { Software, DefaultUser, System }
public enum ImageItemKind { ProvisionedApp, SystemApp, Feature, Capability, Package, Service, ComponentPayload }
public enum ExpertActionMode { Disable, Remove }
public sealed record ExpertAction(ImageItemKind Kind, string Identity, ExpertActionMode Mode);
public sealed record ImageInventoryItem(ImageItemKind Kind, string Identity, string Detail, bool Protected)
{
    public string Key => $"{Kind}:{Identity}";
}

public sealed record CleanupOption(
    string Id, string Title, string Description, string Category, OptionKind Kind,
    bool Balanced, bool Minimal, string[] Targets);

public sealed record WindowsImage(
    int Index, string Name, string Description, string Architecture,
    Version Version, string Edition, string Installation, long Size)
{
    public string Summary => $"{Name}  |  {Architecture}  |  build {Version.Build}.{Version.Revision}";
    public bool IsSupported => Index > 0 && Size > 0 && Version.Major == 10 && Version.Build >= 22621 &&
        Installation.Equals("Client", StringComparison.OrdinalIgnoreCase) && Architecture is "x64" or "arm64";

    public void Validate()
    {
        if (!IsSupported)
            throw new InvalidDataException(
                L10n.T("ImageUnsupported", "Choose a Windows 11 client image, build 22621 (22H2) or newer, in x64 or ARM64 format."));
    }
}

public sealed record ProvisionedApp(string DisplayName, string PackageName);
public enum BuildOperationState { Pending, Running, Completed, Skipped, Failed, Cancelled }
public sealed record BuildProgress(string Stage, string Message, double? Percent = null,
    string? OperationId = null, BuildOperationState? State = null, string? Detail = null);
public sealed record BuildResult(string IsoPath, string Sha256, string ReportPath, string WorkDirectory,
    TimeSpan Duration = default, long RemovedLogicalBytes = 0, long OmittedSetupBytes = 0)
{
    // Problems after the ISO was published (report, cleanup) or in advisory data. They never turn a published ISO into a failure.
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
public sealed record PolicyValue(PolicyHive Hive, string Key, string Name, object Value);

public sealed record BuildRecipe
{
    public int SchemaVersion { get; init; } = 1;
    public string SourcePath { get; init; } = "";
    public int ImageIndex { get; init; } = 1;
    public string WorkspacePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string DismPath { get; init; } = "";
    public string OscdimgPath { get; init; } = "";
    public Compression Compression { get; init; } = Compression.Recovery;
    public BuildFlavor Flavor { get; init; }
    public BuildMode? Mode { get; init; }
    public bool CoreAcknowledged { get; init; }
    public bool ExpertAcknowledged { get; init; }
    public ExpertAction[] ExpertActions { get; init; } = [];
    public string[] SelectedOptions { get; init; } = [];
    public bool EnableNetFx3 { get; init; }
    public bool CompactInstallation { get; init; } = true;
    public bool PreferLocalAccount { get; init; }
    public bool KeepWorkingFiles { get; init; }
    public bool TurboMode { get; init; }
    public bool RemoveRedundantData { get; init; }
    public bool OmitNetFx3SetupPayload { get; init; }

    public void ValidateOptions()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(Compression) || !Enum.IsDefined(Flavor) ||
            (Mode is { } mode && !Enum.IsDefined(mode)))
            throw new InvalidDataException(L10n.T("RecipeUnsupported", "This recipe version or compression mode is not supported."));
        if (Flavor == BuildFlavor.Core && !CoreAcknowledged)
            throw new InvalidDataException(L10n.T("CoreAckRequired", "Core requires acknowledgement of the loss of updates, recovery and antivirus protection."));
        if (ExpertActions is null || ExpertActions.Any(action => action is null) ||
            (ExpertActions.Length > 0 && !ExpertAcknowledged))
            throw new InvalidDataException(L10n.T("ExpertAckRequired", "Custom removals require acknowledgement of the risk of an unbootable image."));
        if (ExpertActions.Select(a => (a.Kind, a.Identity)).Distinct().Count() != ExpertActions.Length)
            throw new InvalidDataException(L10n.T("DuplicateActions", "Duplicate custom image actions."));
        foreach (var action in ExpertActions)
            if (!Enum.IsDefined(action.Kind) || !Enum.IsDefined(action.Mode) || string.IsNullOrWhiteSpace(action.Identity) ||
                action.Identity.Contains('\\') || action.Identity.Contains('/') ||
                (action.Mode == ExpertActionMode.Disable && action.Kind is not (ImageItemKind.Feature or ImageItemKind.Service)))
                throw new InvalidDataException(L10n.T("InvalidAction", "Invalid custom image action."));
        if (ImageIndex < 1 || SelectedOptions is null ||
            SelectedOptions.Length != SelectedOptions.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException(L10n.T("InvalidOptions", "Invalid image index or duplicate cleanup options."));
        foreach (var id in SelectedOptions)
            if (!CleanupCatalog.Options.Any(o => o.Id == id))
                throw new InvalidDataException(string.Format(L10n.T("UnknownOption", "Unknown cleanup option: {0}"), id));
        if (Mode is BuildMode.Tiny11 or BuildMode.Tiny11Core &&
            (Flavor != (Mode == BuildMode.Tiny11Core ? BuildFlavor.Core : BuildFlavor.Regular) ||
             ExpertActions.Length > 0 ||
             !SelectedOptions.ToHashSet(StringComparer.Ordinal).SetEquals(CleanupCatalog.ForMode(Mode.Value))))
            throw new InvalidDataException(L10n.T("UseCustomMode", "Use Custom for changes to the Tiny11 or Tiny11 Core removal sets."));
    }

    public static BuildRecipe Read(string path)
    {
        var recipe = JsonSerializer.Deserialize<BuildRecipe>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidDataException(L10n.T("RecipeEmpty", "The recipe is empty."));
        recipe.ValidateOptions();
        return recipe;
    }

    public void Write(string path)
    {
        ValidateOptions();
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonDefaults.Options));
    }
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
