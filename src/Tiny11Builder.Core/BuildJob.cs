using System.Text.Json;

namespace Tiny11Builder.Core;

public sealed class BuildFailureException : Exception
{
    public BuildFailureException(string message, string workDirectory, Exception inner,
        bool? isCancellation = null, bool recoveryRequired = false) : base(message, inner)
    {
        WorkDirectory = workDirectory;
        IsCancellation = isCancellation ?? IsCancellationCause(inner);
        RecoveryRequired = recoveryRequired;
    }

    public string WorkDirectory { get; }

    /// <summary>True when the primary cause was cancellation, even if secondary cleanup problems were aggregated with it.</summary>
    public bool IsCancellation { get; }

    /// <summary>True when an owned image mount could not be discarded; the job must be recovered before anything else.</summary>
    public bool RecoveryRequired { get; }

    // Aggregated failures list the primary cause first.
    internal static bool IsCancellationCause(Exception error) => error switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && IsCancellationCause(aggregate.InnerExceptions[0]),
        _ => false
    };
}

public sealed record JobState(string Id, string Purpose, string Status, string Stage, DateTimeOffset StartedUtc);

internal sealed class BuildJob : IDisposable
{
    private readonly StreamWriter log;
    private readonly FileStream jobLock;
    private JobState state;
    private readonly object gate = new();
    public string Directory { get; }

    private BuildJob(string directory, FileStream jobLock, StreamWriter log, JobState state)
    {
        Directory = directory;
        this.jobLock = jobLock;
        this.log = log;
        this.state = state;
    }

    public static BuildJob Create(string workspace, string purpose, bool bufferedLog = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(PathSafety.Full(workspace), "tiny11-" + id);
        System.IO.Directory.CreateDirectory(directory);
        var jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        StreamWriter? log = null;
        try
        {
            log = new StreamWriter(Path.Combine(directory, "build.log"), append: false) { AutoFlush = !bufferedLog };
            var job = new BuildJob(directory, jobLock, log, new(id, purpose, "running", "Prepare", DateTimeOffset.UtcNow));
            job.State("running");
            return job;
        }
        catch
        {
            log?.Dispose();
            jobLock.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes the exclusive lock of an existing job and appends to its original log. Nothing else is changed: the job marker
    /// is only rewritten by <see cref="State"/> once a resume has passed every check.
    /// </summary>
    public static BuildJob Open(string directory, bool bufferedLog)
    {
        FileStream jobLock;
        try { jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("This folder is not a complete tiny11 job (job.lock is missing).", error);
        }
        catch (IOException error)
        {
            throw new InvalidDataException("This job is in use by a running build or another copy of the app, " +
                "so it cannot be resumed right now.", error);
        }
        StreamWriter? log = null;
        try
        {
            var current = JsonSerializer.Deserialize<JobState>(File.ReadAllText(Path.Combine(directory, "job.json")), JsonDefaults.Options)
                ?? throw new InvalidDataException("Invalid job marker.");
            log = new StreamWriter(Path.Combine(directory, "build.log"), append: true) { AutoFlush = !bufferedLog };
            return new BuildJob(directory, jobLock, log, current);
        }
        catch (JsonException error)
        {
            log?.Dispose();
            jobLock.Dispose();
            throw new InvalidDataException("The job marker (job.json) is damaged.", error);
        }
        catch
        {
            log?.Dispose();
            jobLock.Dispose();
            throw;
        }
    }

    public JobState Current => state;
    public void Log(string message) { lock (gate) log.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}"); }
    public void Flush() { lock (gate) log.Flush(); }
    public void State(string status, string? stage = null)
    {
        state = state with { Status = status, Stage = stage ?? state.Stage };
        var file = Path.Combine(Directory, "job.json");
        File.WriteAllText(file + ".new", JsonSerializer.Serialize(state, JsonDefaults.Options));
        File.Move(file + ".new", file, overwrite: true);
    }
    public void Dispose() { log.Dispose(); jobLock.Dispose(); }
}
