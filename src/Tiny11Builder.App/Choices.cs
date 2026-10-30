using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Tiny11Builder.Core;

namespace Tiny11Builder.App;

// This file intentionally has no WinUI types, so the same source runs under the unit tests.
// XAML derives Visibility from the bool properties below.
public abstract class ObservableChoice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }
    protected void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class OptionChoice(CleanupOption option, bool presetRemovesEdgeFiles = false) : ObservableChoice
{
    private bool selected = option.Balanced;
    public CleanupOption Option { get; } = option;
    public string Title => UiText.GetOrEnglish($"Option.{Option.Id}.Title", Option.Title);
    public string Description => presetRemovesEdgeFiles && Option.Id == "edge" ? UiText.Get("Preset.EdgePolicyWithFileRemoval") :
        UiText.GetOrEnglish($"Option.{Option.Id}.Description", Option.Description);
    public string Category => UiText.FromEnglish(Option.Category);
    public string TechnicalDetail => $"{Category} · {Option.Id} · {Option.Kind}" +
        (Option.Targets.Length == 0 ? "" : "\n" + string.Join("\n", Option.Targets));
    public void RefreshLanguage()
    {
        Notify(nameof(Title)); Notify(nameof(Description)); Notify(nameof(Category)); Notify(nameof(TechnicalDetail));
    }
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

public sealed class ActionModeChoice(ExpertActionMode mode) : ObservableChoice
{
    public ExpertActionMode Mode { get; } = mode;
    public string Title => UiText.Get(Mode == ExpertActionMode.Disable ? "Common.Disable" : "Common.Remove");
    public void RefreshLanguage() => Notify(nameof(Title));
}

public sealed class InventoryChoice(ImageInventoryItem item) : ObservableChoice
{
    private bool selected;
    private int modeIndex = item.Kind == ImageItemKind.Service ? 0 : 1;
    public ImageInventoryItem Item { get; } = item;
    public string Title => Item.Identity;
    public string Detail => $"{UiText.GetOrEnglish("Kind." + Item.Kind, Item.Kind.ToString())} · {Item.Detail}";
    public string Risk => UiText.Get(Item.Protected ? "Inventory.Protected" : "Inventory.Application");
    public string ModeLabel => UiText.Format("Inventory.ActionFor", Title);
    public bool HasModes => Item.Kind is ImageItemKind.Service or ImageItemKind.Feature;
    public ActionModeChoice[] Modes { get; } = [new(ExpertActionMode.Disable), new(ExpertActionMode.Remove)];
    public void RefreshLanguage()
    {
        Notify(nameof(Detail)); Notify(nameof(Risk)); Notify(nameof(ModeLabel));
        foreach (var mode in Modes) mode.RefreshLanguage();
    }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public int ModeIndex { get => modeIndex; set { if (value is 0 or 1) Set(ref modeIndex, value); } }
    public ExpertAction Action => new(Item.Kind, Item.Identity, ModeIndex == 0 ? ExpertActionMode.Disable : ExpertActionMode.Remove);
}

// The seven rows of the build page. Each row is fed the engine's progress events for its phase
// (BuildProgressText.Phase) and mirrors them as grouped, counted operation rows.
public sealed class BuildStageBoard
{
    public BuildStageChoice[] Stages { get; } =
    [
        new("Prepare media", "\uE8B7"), new("Export and mount", "\uE7C5"), new("Updates and removals", "\uE74D"),
        new("Commit image", "\uE74E"), new("Compress image", "\uE8B9"), new("Create ISO", "\uE958"), new("Verify output", "\uE73E")
    ];

    // Returns the phase the event belongs to, or -1 when it is not a displayed operation.
    public int Apply(BuildProgress progress)
    {
        var phase = BuildProgressText.Phase(progress.Stage);
        if (phase >= 0 && progress.OperationId is not null) Stages[phase].Apply(progress);
        return phase;
    }

    public void RefreshLanguage() { foreach (var stage in Stages) stage.RefreshLanguage(); }
    public void Reset() { foreach (var stage in Stages) stage.Reset(); }
    public void Stop(bool cancelled) { foreach (var stage in Stages) stage.Stop(cancelled); }
    public void Complete() { foreach (var stage in Stages) stage.Complete(); }

    // A resumed run only plans the tail of the pipeline. Phases it never planned, ahead of its last planned
    // stage, were finished before the saved checkpoint, so they read as done instead of staying "Waiting".
    public void MarkEarlierPhasesDone()
    {
        var last = Array.FindLastIndex(Stages, stage => stage.Operations.Count > 0);
        for (var index = 0; index < last; index++) Stages[index].MarkDoneEarlier();
    }
}

public sealed class BuildStageChoice(string title, string glyph) : ObservableChoice
{
    private const string StagePrefix = "stage:";
    private static readonly int StateCount = Enum.GetValues<BuildOperationState>().Length;
    private string status = "Waiting";
    private bool expanded;
    private bool halted;
    private bool doneEarlier;
    private readonly Dictionary<string, BuildOperationChoice> lookup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StageTally> tallies = new(StringComparer.Ordinal);

    // One engine stage owns a "stage:" row and any number of item rows. Items are the unit of work
    // when there are any, so one failure is never counted for the item and again for its stage.
    private sealed class StageTally
    {
        public BuildOperationState? Own;
        public readonly int[] Items = new int[StateCount];
        public int ItemTotal;
    }

    public string Title => UiText.FromEnglish(title);
    public string Glyph { get; } = glyph;
    public bool IsComplete { get; private set; }
    public string Status { get => UiText.FromEnglish(status); set { if (Set(ref status, value)) Notify(nameof(AutomationName)); } }
    public string AutomationName => $"{Title}, {Status}";
    public bool Expanded { get => expanded; set => Set(ref expanded, value); }
    public ObservableCollection<BuildOperationChoice> Operations { get; } = [];

    public void Reset()
    {
        lookup.Clear();
        tallies.Clear();
        Operations.Clear();
        halted = false;
        doneEarlier = false;
        IsComplete = false;
        Status = UiText.Get("Progress.Waiting");
        Expanded = false;
    }

    public void Apply(BuildProgress progress)
    {
        if (progress.OperationId is not { } id) return;
        var isStage = id.StartsWith(StagePrefix, StringComparison.Ordinal);
        if (!lookup.TryGetValue(id, out var operation))
        {
            operation = new(id, progress.Message, progress.Stage);
            lookup.Add(id, operation);
            doneEarlier = false;
            // Item rows sit directly under their own stage row; stage rows keep the order they were planned in.
            Operations.Insert(isStage ? Operations.Count : InsertIndex(progress.Stage), operation);
            if (!tallies.TryGetValue(progress.Stage, out var created)) tallies.Add(progress.Stage, created = new());
            if (isStage) created.Own = BuildOperationState.Pending;
            else { created.Items[(int)BuildOperationState.Pending]++; created.ItemTotal++; }
        }
        var tally = tallies[operation.Stage];
        if (progress.State is { } state && state != operation.State)
        {
            var prior = operation.State;
            operation.State = state;
            if (isStage) tally.Own = state;
            else { tally.Items[(int)prior]--; tally.Items[(int)state]++; }
        }
        if (progress.Detail is not null && progress.State is BuildOperationState.Completed or BuildOperationState.Skipped or BuildOperationState.Failed or BuildOperationState.Cancelled)
            operation.SetDetail(progress.Detail);
        RefreshStatus();
    }

    public double Fraction(BuildProgress progress)
    {
        if (tallies.TryGetValue(progress.Stage, out var tally) && tally.ItemTotal > 0)
        {
            var counts = tally.Items;
            var done = counts[(int)BuildOperationState.Completed] + counts[(int)BuildOperationState.Skipped];
            var active = progress.State is null ?
                Math.Clamp(progress.Percent ?? 0, 0, 100) / 100 *
                    (progress.OperationId?.StartsWith(StagePrefix, StringComparison.Ordinal) == true ?
                        counts[(int)BuildOperationState.Running] : 1) : 0;
            return Math.Clamp((done + active) / tally.ItemTotal, 0, 1);
        }
        return progress.State == BuildOperationState.Completed ? 1 : Math.Clamp(progress.Percent ?? 0, 0, 100) / 100;
    }

    // The build ended here. Work that never started is "Not run", not "Next", and work still marked
    // as running is closed as failed or cancelled.
    public void Stop(bool cancelled)
    {
        if (Operations.Count == 0) return;
        halted = true;
        foreach (var operation in Operations.Where(o => o.State == BuildOperationState.Running).ToArray())
            Apply(new(operation.Stage, operation.Title, OperationId: operation.Id,
                State: cancelled ? BuildOperationState.Cancelled : BuildOperationState.Failed));
        foreach (var operation in Operations.Where(o => o.State == BuildOperationState.Pending)) operation.NotRun = true;
        RefreshStatus();
    }

    public void Complete()
    {
        IsComplete = true;
        Status = UiText.Get(doneEarlier ? "Progress.DoneEarlier" : "Progress.Complete");
    }

    public void RefreshLanguage()
    {
        Notify(nameof(Title));
        foreach (var operation in Operations) operation.RefreshLanguage();
        if (doneEarlier || IsComplete) Complete();
        else if (Operations.Count == 0) Status = UiText.Get("Progress.Waiting");
        else RefreshStatus();
    }

    public void MarkDoneEarlier()
    {
        if (Operations.Count > 0) return;
        doneEarlier = true;
        IsComplete = true;
        Status = UiText.Get("Progress.DoneEarlier");
    }

    private int InsertIndex(string stage)
    {
        for (var index = Operations.Count - 1; index >= 0; index--)
            if (Operations[index].Stage == stage) return index + 1;
        return Operations.Count;
    }

    private void RefreshStatus()
    {
        int failed = 0, cancelled = 0, done = 0, pending = 0;
        var running = false;
        void Add(BuildOperationState state, int count)
        {
            switch (state)
            {
                case BuildOperationState.Failed: failed += count; break;
                case BuildOperationState.Cancelled: cancelled += count; break;
                case BuildOperationState.Running: running = true; break;
                case BuildOperationState.Completed or BuildOperationState.Skipped: done += count; break;
                default: pending += count; break;
            }
        }
        foreach (var tally in tallies.Values)
        {
            if (tally.ItemTotal == 0)
            {
                if (tally.Own is { } own) Add(own, 1);
                continue;
            }
            for (var state = 0; state < tally.Items.Length; state++)
                if (tally.Items[state] > 0) Add((BuildOperationState)state, tally.Items[state]);
            if (tally.Own is BuildOperationState.Running) running = true;
            if (tally.Own is BuildOperationState.Failed && tally.Items[(int)BuildOperationState.Failed] == 0) failed++;
            if (tally.Own is BuildOperationState.Cancelled && tally.Items[(int)BuildOperationState.Cancelled] == 0) cancelled++;
        }
        var rest = pending == 0 ? "" : UiText.Format(halted ? "Progress.NotRunCount" : "Progress.NextCount", pending);
        IsComplete = failed == 0 && cancelled == 0 && !running && pending == 0;
        Status = failed > 0 ? UiText.Format("Progress.FailureSummary",
                failed == 1 ? UiText.Format("Progress.FailedItem", ErrorText.Clip(FirstFailedTitle(), 48)) : UiText.Format("Progress.FailedCount", failed), done, rest) :
            cancelled > 0 ? UiText.Format("Progress.CancelledSummary", done, rest) :
            running ? UiText.Format("Progress.RunningSummary", done, rest) :
            pending > 0 ? (halted && done == 0 ? UiText.Get("Progress.NotRun") : UiText.Format("Progress.DoneSummary", done, rest)) : UiText.Get("Progress.Complete");
    }

    private string FirstFailedTitle() =>
        (Operations.FirstOrDefault(o => o.State == BuildOperationState.Failed && !o.Id.StartsWith(StagePrefix, StringComparison.Ordinal)) ??
         Operations.First(o => o.State == BuildOperationState.Failed)).Title;
}

public sealed class BuildOperationChoice(string id, string title, string stage) : ObservableChoice
{
    // Rows show a short reason; the untruncated text stays in FullDetail and the job log.
    private const int DetailLimit = 360;
    private BuildOperationState state;
    private string detail = "";
    private bool notRun;
    public string Id { get; } = id;
    public string Title => UiText.FromEnglish(title);
    public string Stage { get; } = stage;
    public string Detail { get => detail; private set { if (Set(ref detail, value)) Notify(nameof(HasDetail)); } }
    public string FullDetail { get; private set; } = "";
    public bool HasDetail => Detail.Length > 0;
    public bool NotRun
    {
        get => notRun;
        set { if (Set(ref notRun, value)) Refresh(); }
    }
    public BuildOperationState State
    {
        get => state;
        set { if (Set(ref state, value)) Refresh(); }
    }
    public string Status => UiText.Get(State switch
    {
        BuildOperationState.Running => "Progress.InProgress", BuildOperationState.Completed => "Progress.Done",
        BuildOperationState.Skipped => "Progress.Skipped", BuildOperationState.Failed => "Progress.Failed",
        BuildOperationState.Cancelled => "Progress.Cancelled", _ => NotRun ? "Progress.NotRun" : "Common.Next"
    });
    public string Glyph => State switch
    {
        BuildOperationState.Completed => "\uE73E", BuildOperationState.Failed => "\uE711",
        BuildOperationState.Running => "\uE895", BuildOperationState.Skipped => "\uE72A",
        BuildOperationState.Cancelled => "\uE711", _ => NotRun ? "\uE738" : "\uE823"
    };
    public string AutomationName => $"{Title}, {Status}";

    public void SetDetail(string text)
    {
        FullDetail = BuildProgressText.ForDisplay(text);
        Detail = ErrorText.Summary(FullDetail, DetailLimit);
    }

    public void RefreshLanguage() { Notify(nameof(Title)); Refresh(); }

    private void Refresh()
    {
        Notify(nameof(Status));
        Notify(nameof(Glyph));
        Notify(nameof(AutomationName));
    }
}
