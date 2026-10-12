using System.Globalization;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Tiny11Builder.Core;

public static class BuildProgressText
{
    /// <summary>First stage of a resumed build: verifies the saved image, setup files and settings before anything changes.</summary>
    public const string ResumeCheckStage = "Check saved progress";

    public static double? Percent(string text)
    {
        var match = Regex.Match(text, @"(\d+(?:\.\d+)?)%");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100) : null;
    }

    public static bool IsConsoleProgress(string text) =>
        Regex.IsMatch(text.Trim(), @"^\[[\s=]*\d+(?:\.\d+)?%[\s=]*\]$");

    public static string ForDisplay(string text) => string.Join(Environment.NewLine,
        text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Where(line => !IsConsoleProgress(line)));

    public static int Phase(string stage) => stage switch
    {
        "Prepare" or "Copy media" => 0,
        "Export edition" or "Mount" => 1,
        "Inventory" or "Custom servicing" or "Remove apps" or "Optional features" or ".NET 3.5" or
            "Windows settings" or "Component cleanup" or "Advanced payloads" or "Core pruning" or "Redundant data" => 2,
        "Commit" or "Setup image" or ResumeCheckStage => 3,
        "Compress" => 4,
        "Create ISO" => 5,
        "Verify output" => 6,
        _ => -1
    };

    public static (double Start, double End) Range(string stage) => stage switch
    {
        "Prepare" or "Copy media" => (0, 8),
        "Export edition" => (8, 15), "Mount" => (15, 25), "Inventory" => (25, 28),
        "Custom servicing" => (28, 32), "Remove apps" => (32, 44), "Optional features" => (44, 53),
        ".NET 3.5" => (53, 55), "Windows settings" => (55, 57), "Component cleanup" => (57, 65),
        "Advanced payloads" => (65, 68), "Core pruning" => (68, 72), "Redundant data" => (71, 72), "Commit" => (72, 77),
        ResumeCheckStage => (72, 77),
        "Setup image" => (77, 80), "Compress" => (80, 93), "Create ISO" => (93, 98),
        "Verify output" => (98, 100), _ => (0, 0)
    };
}

internal sealed record OperationRecord(string Id, string Stage, string Title, BuildOperationState State,
    string? Detail, double? DurationMilliseconds);

internal sealed record StageTiming(string Stage, BuildOperationState State, double? Milliseconds);

internal sealed class BuildOperationTracker(IProgress<BuildProgress>? progress, Action<string> log)
{
    private static readonly (string Prefix, BuildOperationState State)[] PayloadMessages =
    [
        ("Plan payload: ", BuildOperationState.Pending), ("Removing payload: ", BuildOperationState.Running),
        ("Payload removed: ", BuildOperationState.Completed)
    ];
    private sealed record Operation(string Stage, string Title, BuildOperationState State = BuildOperationState.Pending, string? Detail = null);
    private readonly Dictionary<string, Operation> operations = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<string, long> started = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> durations = new(StringComparer.Ordinal);
    private string? stageId;
    private string? itemId;
    private double? currentPercent;
    public string Stage { get; private set; } = "Prepare";
    public IReadOnlyList<OperationRecord> Snapshot => SnapshotCompleting();
    public IReadOnlyList<StageTiming> Timings
    {
        get
        {
            lock (gate)
                return operations.Where(pair => pair.Key.StartsWith("stage:", StringComparison.Ordinal))
                    .Select(pair => new StageTiming(pair.Value.Stage, pair.Value.State,
                        durations.TryGetValue(pair.Key, out var elapsed) ? elapsed : (double?)null)).ToArray();
        }
    }

    /// <summary>
    /// Snapshot in which the named operations, if still running, are recorded as completed now. A checkpoint is written
    /// while the Commit stage is still open, yet the saved history must already describe that stage as finished.
    /// </summary>
    public IReadOnlyList<OperationRecord> SnapshotCompleting(params string[] completingIds)
    {
        lock (gate)
        {
            var now = clock.ElapsedTicks;
            return operations.Select(pair =>
            {
                var finishing = completingIds.Contains(pair.Key, StringComparer.Ordinal) &&
                    pair.Value.State == BuildOperationState.Running;
                double? duration = durations.TryGetValue(pair.Key, out var recorded) ? recorded : null;
                if (finishing && started.TryGetValue(pair.Key, out var start)) duration = (now - start) * 1000d / Stopwatch.Frequency;
                return new OperationRecord(pair.Key, pair.Value.Stage, pair.Value.Title,
                    finishing ? BuildOperationState.Completed : pair.Value.State, pair.Value.Detail, duration);
            }).ToArray();
        }
    }

    /// <summary>Seeds operations that finished in an earlier run of the same job, without raising progress events.</summary>
    public void Restore(IEnumerable<OperationRecord> history)
    {
        lock (gate)
            foreach (var item in history)
            {
                operations[item.Id] = new(item.Stage, item.Title, item.State, item.Detail);
                if (item.DurationMilliseconds is double duration) durations[item.Id] = duration;
            }
    }

    public void Plan(string id, string stage, string title)
    {
        operations.Add(id, new(stage, title));
        Publish(id, BuildOperationState.Pending);
    }

    public void BeginStage(string stage, string title)
    {
        EndStage();
        Stage = stage;
        stageId = "stage:" + stage;
        currentPercent = null;
        if (!operations.ContainsKey(stageId)) Plan(stageId, stage, title);
        Publish(stageId, BuildOperationState.Running);
        log(title);
    }

    /// <summary>Closes the stage whose body has just returned. Call before observing cancellation at the next boundary.</summary>
    public void EndStage()
    {
        var id = stageId;
        stageId = null;
        if (id is not null) CompleteStage(id);
    }

    public async Task RunAsync(string id, string stage, string title, Func<Task> action, Func<string?>? completedDetail = null)
    {
        if (!operations.ContainsKey(id)) Plan(id, stage, title);
        itemId = id;
        currentPercent = null;
        Publish(id, BuildOperationState.Running);
        log(title);
        try
        {
            await action().ConfigureAwait(false);
            Publish(id, BuildOperationState.Completed, completedDetail?.Invoke());
            log($"Completed: {title}");
        }
        catch (Exception error)
        {
            PublishFailure(id, error, error.ToString());
            throw;
        }
        finally { itemId = null; }
    }

    public void Skip(string id, string stage, string title, string reason)
    {
        if (!operations.ContainsKey(id)) Plan(id, stage, title);
        Publish(id, BuildOperationState.Skipped, reason);
        log($"{title}: {reason}");
    }

    /// <param name="detailFor">Truthful per-item outcome, evaluated after the batch succeeds (for example each feature's real state).</param>
    public async Task RunBatchAsync(IReadOnlyList<(string Id, string Title)> items, string stage, Func<Task> action,
        string noun = "optional features", Func<string, string?>? detailFor = null)
    {
        if (items.Count == 0) return;
        foreach (var (id, title) in items)
        {
            if (!operations.ContainsKey(id)) Plan(id, stage, title);
            Publish(id, BuildOperationState.Running);
        }
        itemId = null;
        currentPercent = null;
        try
        {
            await action().ConfigureAwait(false);
            // One DISM invocation served every member, so each member carries the shared batch duration.
            foreach (var (id, _) in items)
            {
                var actual = detailFor?.Invoke(id);
                Publish(id, BuildOperationState.Completed, (actual is null ? "" : actual + " ") +
                    $"Batch of {items.Count} {noun}; its duration is shared by the whole batch, not per item.");
            }
            log($"Completed batch: {items.Count} {noun}");
        }
        catch (Exception error)
        {
            foreach (var (id, _) in items)
                PublishFailure(id, error, $"{char.ToUpperInvariant(noun[0])}{noun[1..]} batch failed. No individual success is assumed. " + error);
            throw;
        }
    }

    /// <summary>Key prefix under which a failing progress observer is attached to the failure it was reporting.</summary>
    public const string ObserverFailureKey = "Tiny11Builder.ObserverFailure.";

    private void PublishFailure(string id, Exception error, string detail)
    {
        try { Publish(id, error is OperationCanceledException ? BuildOperationState.Cancelled : BuildOperationState.Failed, detail); }
        catch (Exception observer)
        {
            // The recorded state is updated before any observer runs. The original failure keeps propagating unchanged;
            // the observer's own failure travels with it instead of replacing it.
            error.Data[ObserverFailureKey + id] = observer;
        }
    }

    public void Output(string text)
    {
        lock (gate) OutputCore(text);
    }

    private void OutputCore(string text)
    {
        log(text);
        foreach (var (prefix, state) in PayloadMessages)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var title = text[prefix.Length..];
            var nativeId = "payload:" + title;
            if (!operations.ContainsKey(nativeId)) Plan(nativeId, Stage, $"Remove payload: {title}");
            if (state != BuildOperationState.Pending) Publish(nativeId, state);
            if (state == BuildOperationState.Running) currentPercent = null;
            itemId = state == BuildOperationState.Running ? nativeId : null;
            return;
        }
        var id = itemId ?? stageId;
        if (id is null || string.IsNullOrWhiteSpace(text)) return;
        var operation = operations[id];
        var copied = Regex.Match(text, @"Copied setup file (\d+) of (\d+)");
        var copyPercent = copied.Success && int.TryParse(copied.Groups[1].Value, out var count) &&
            int.TryParse(copied.Groups[2].Value, out var total) && total > 0 ? count * 100d / total : (double?)null;
        currentPercent = copyPercent ?? BuildProgressText.Percent(text) ?? currentPercent;
        progress?.Report(new(operation.Stage, operation.Title, currentPercent, id,
            Detail: BuildProgressText.IsConsoleProgress(text) ? null : text));
    }

    public void Finish() => EndStage();

    private void CompleteStage(string id)
    {
        var operation = operations[id];
        Publish(id, BuildOperationState.Completed);
        log($"Completed stage: {operation.Title}");
    }

    public void Fail(Exception error, bool cancelled)
    {
        var item = itemId;
        var stage = stageId;
        itemId = null;
        stageId = null;
        var state = cancelled ? BuildOperationState.Cancelled : BuildOperationState.Failed;
        List<Exception>? faults = null;
        // The recorded state is updated before a progress observer is called, so an observer that throws cannot leave the
        // item or its stage looking active. Observer faults are rethrown together once both operations are recorded.
        foreach (var id in new[] { item, stage })
        {
            if (id is null) continue;
            try { Publish(id, state, error.ToString()); }
            catch (Exception observer) { (faults ??= []).Add(observer); }
        }
        if (faults is not null) throw new AggregateException("The progress observer failed while recording the failure.", faults);
    }

    private void Publish(string id, BuildOperationState state, string? detail = null)
    {
        var operation = operations[id];
        if (state == BuildOperationState.Running) started[id] = clock.ElapsedTicks;
        else if (state is BuildOperationState.Completed or BuildOperationState.Failed or BuildOperationState.Cancelled &&
                 started.TryGetValue(id, out var start))
            durations[id] = (clock.ElapsedTicks - start) * 1000d / Stopwatch.Frequency;
        operations[id] = operation with { State = state, Detail = detail };
        progress?.Report(new(operation.Stage, operation.Title, OperationId: id, State: state, Detail: detail));
    }
}
