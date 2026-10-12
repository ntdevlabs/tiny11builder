using System.Diagnostics;
using System.Text;

namespace Tiny11Builder.Core;

public sealed record ProcessResult(int ExitCode, string Output);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        Action<string>? output, CancellationToken cancellationToken, bool finishOnCancellation = true);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        Action<string>? output, CancellationToken cancellationToken, bool finishOnCancellation = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        var text = new StringBuilder();
        void Receive(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (text) text.AppendLine(e.Data);
            output?.Invoke(e.Data);
        }
        process.OutputDataReceived += Receive;
        process.ErrorDataReceived += Receive;
        if (!process.Start()) throw new IOException($"Unable to start {Path.GetFileName(executable)}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            if (finishOnCancellation) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process exited between the check and the kill. */ }
        });
        // Killing DISM during mount/commit can orphan an image. Cancellation is checked at the next boundary.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        process.WaitForExit();
        if (!finishOnCancellation) cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, text.ToString());
    }
}
