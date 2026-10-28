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

public sealed class OptionChoice(CleanupOption option) : ObservableChoice
{
    private bool selected = option.Balanced;
    public CleanupOption Option { get; } = option;
    public string Title => Option.Title;
    public string Description => Option.Description;
    public string Category => Option.Category;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}

public sealed class InventoryChoice(ImageInventoryItem item) : ObservableChoice
{
    private bool selected;
    private int modeIndex = item.Kind == ImageItemKind.Service ? 0 : 1;
    public ImageInventoryItem Item { get; } = item;
    public string Title => Item.Identity;
    public string Detail => $"{Item.Kind} · {Item.Detail}";
    public string Risk => Item.Protected ? L10n.T("RiskProtected", "Protected / potentially boot-critical") : L10n.T("RiskApp", "Application");
    public string ModeLabel => string.Format(L10n.T("ActionFor", "Action for {0}"), Title);
    public bool HasModes => Item.Kind is ImageItemKind.Service or ImageItemKind.Feature;
    public string[] Modes { get; } = [L10n.T("ModeDisable", "Disable"), L10n.T("ModeRemove", "Remove")];
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public int ModeIndex { get => modeIndex; set => Set(ref modeIndex, value); }
    public ExpertAction Action => new(Item.Kind, Item.Identity, ModeIndex == 0 ? ExpertActionMode.Disable : ExpertActionMode.Remove);
}

// The seven rows of the build page. Each row is fed the engine's progress events for its phase
// (BuildProgressText.Phase) and mirrors them as grouped, counted operation rows.
public sealed class BuildStageBoard
{
    public BuildStageChoice[] Stages { get; } =
    [
        new(L10n.T("StagePrepare", "Prepare media"), "\uE8B7"), new(L10n.T("StageExport", "Export and mount"), "\uE7C5"), new(L10n.T("StageRemove", "Remove components"), "\uE74D"),
        new(L10n.T("StageCommit", "Commit image"), "\uE74E"), new(L10n.T("StageCompress", "Compress image"), "\uE8B9"), new(L10n.T("StageIso", "Create ISO"), "\uE958"), new(L10n.T("StageVerify", "Verify output"), "\uE73E")
    ];

    // Returns the phase the event belongs to, or -1 when it is not a displayed operation.
    public int Apply(BuildProgress progress)
    {
        var phase = BuildProgressText.Phase(progress.Stage);
        if (phase >= 0 && progress.OperationId is not null) Stages[phase].Apply(progress);
        return phase;
    }

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

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Status { get => status; set { if (Set(ref status, value)) Notify(nameof(AutomationName)); } }
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
        Status = L10n.T("StatusWaiting", "Waiting");
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

    public void Complete() => Status = doneEarlier ? L10n.T("StatusDoneEarlier", "Done earlier") : L10n.T("StatusComplete", "Complete");

    public void MarkDoneEarlier()
    {
        if (Operations.Count > 0) return;
        doneEarlier = true;
        Status = L10n.T("StatusDoneEarlier", "Done earlier");
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
        var rest = pending == 0 ? "" : halted
            ? string.Format(L10n.T("StageRestNotRun", " · {0} not run"), pending)
            : string.Format(L10n.T("StageRestNext", " · {0} next"), pending);
        Status = failed > 0 ? string.Format(L10n.T("StageFailed", "{0} failed · {1} done{2}"),
                failed == 1 ? L10n.T("StageFailedOne", "Failed: ") + ErrorText.Clip(FirstFailedTitle(), 48) : failed + L10n.T("StageFailedCount", " failed"),
                done, rest) :
            cancelled > 0 ? string.Format(L10n.T("StageCancelled", "Cancelled · {0} done{1}"), done, rest) :
            running ? string.Format(L10n.T("StageInProgress", "In progress · {0} done{1}"), done, rest) :
            pending > 0 ? (halted && done == 0 ? L10n.T("StatusNotRun", "Not run") : string.Format(L10n.T("StageDone", "{0} done{1}"), done, rest)) :
            L10n.T("StatusComplete", "Complete");
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
    public string Title { get; } = title;
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
    public string Status => State switch
    {
        BuildOperationState.Running => L10n.T("StatusInProgress", "In progress"), BuildOperationState.Completed => L10n.T("StatusDone", "Done"),
        BuildOperationState.Skipped => L10n.T("StatusSkipped", "Skipped"), BuildOperationState.Failed => L10n.T("StatusFailed", "Failed"),
        BuildOperationState.Cancelled => L10n.T("StatusCancelled", "Cancelled"), _ => NotRun ? L10n.T("StatusNotRun", "Not run") : L10n.T("StatusNext", "Next")
    };
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

    private void Refresh()
    {
        Notify(nameof(Status));
        Notify(nameof(Glyph));
        Notify(nameof(AutomationName));
    }
}
