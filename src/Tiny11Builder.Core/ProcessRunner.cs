using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
    private readonly Encoding? configured;

    public ProcessRunner() : this(null) { }

    /// <param name="outputEncoding">
    /// Decodes child output that has no byte-order mark. Null selects the system OEM code page, which is what DISM and
    /// oscdimg write when their output is redirected. A byte-order mark always overrides it, and bytes that are not valid
    /// in the chosen encoding fail the run instead of being replaced.
    /// </param>
    public ProcessRunner(Encoding? outputEncoding) => configured = outputEncoding;

    internal Encoding OutputEncoding => configured ?? ConsoleText.SystemOem();

    private sealed record DecodeFailure(string Stream, Encoding Encoding, long Offset, DecoderFallbackException Error);

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        Action<string>? output, CancellationToken cancellationToken, bool finishOnCancellation = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoding = ConsoleText.Strict(OutputEncoding);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        var text = new StringBuilder();
        ExceptionDispatchInfo? sinkFailure = null;
        void Receive(string line)
        {
            lock (text) text.AppendLine(line);
            if (output is null || Volatile.Read(ref sinkFailure) is not null) return;
            try { output(line); }
            catch (Exception error) { Interlocked.CompareExchange(ref sinkFailure, ExceptionDispatchInfo.Capture(error), null); }
        }
        if (!process.Start()) throw new IOException($"Unable to start {Path.GetFileName(executable)}.");
        // Both pipes are read at once: a child that fills one while the other is unread would otherwise stall.
        var readers = Task.WhenAll(
            ReadLinesAsync(process.StandardOutput.BaseStream, "standard output", encoding, Receive),
            ReadLinesAsync(process.StandardError.BaseStream, "standard error", encoding, Receive));
        using var registration = cancellationToken.Register(() =>
        {
            if (finishOnCancellation) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The process exited between the check and the kill. */ }
        });
        // Killing DISM during mount/commit can orphan an image. Cancellation is checked at the next boundary.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var failures = await readers.ConfigureAwait(false);
        if (!finishOnCancellation) cancellationToken.ThrowIfCancellationRequested();
        if (failures.FirstOrDefault(failure => failure is not null) is { } undecodable)
            throw new InvalidDataException($"{Path.GetFileName(executable)} wrote {undecodable.Stream} that is not valid " +
                $"{undecodable.Encoding.EncodingName} text (code page {undecodable.Encoding.CodePage}) near byte {undecodable.Offset}" +
                $" (bytes {Convert.ToHexString(undecodable.Error.BytesUnknown ?? [])}). The text was not guessed or repaired. " +
                $"The process exited with code {process.ExitCode}.", undecodable.Error);
        sinkFailure?.Throw();
        return new(process.ExitCode, text.ToString());
    }

    // Reads one redirected stream as bytes. A byte-order mark selects UTF-8 or UTF-16; otherwise the chosen encoding decodes.
    // A line ends at "\n", "\r" or "\r\n": DISM redraws its progress bar with a bare "\r", and each redraw is a line.
    // After bytes that cannot be decoded the stream is still drained, so the child never stalls on a full pipe.
    private static async Task<DecodeFailure?> ReadLinesAsync(Stream stream, string name, Encoding chosen, Action<string> line)
    {
        var bytes = new byte[4096];
        var filled = 0;
        while (filled < 3)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(filled)).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
            if (bytes[0] is not (0xEF or 0xFE or 0xFF)) break;   // only these can start a byte-order mark
        }
        var encoding = Sniff(bytes.AsSpan(0, filled), chosen, out var skip);
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var current = new StringBuilder();
        var skipLineFeed = false;
        long start = 0;
        DecodeFailure? failure = null;

        void Decode(int from, int count, bool flush)
        {
            if (failure is not null) return;
            int produced;
            try { produced = decoder.GetChars(bytes, from, count, chars, 0, flush); }
            catch (DecoderFallbackException error)
            {
                failure = new(name, encoding, start + from + Math.Max(error.Index, 0), error);
                return;
            }
            for (var i = 0; i < produced; i++)
            {
                var c = chars[i];
                if (skipLineFeed) { skipLineFeed = false; if (c == '\n') continue; }
                if (c is '\r' or '\n') { line(current.ToString()); current.Clear(); skipLineFeed = c == '\r'; }
                else current.Append(c);
            }
        }

        Decode(skip, filled - skip, flush: false);
        start = filled;
        int received;
        while ((received = await stream.ReadAsync(bytes.AsMemory()).ConfigureAwait(false)) > 0)
        {
            Decode(0, received, flush: false);
            start += received;
        }
        Decode(0, 0, flush: true);
        if (failure is null && current.Length > 0) line(current.ToString());
        return failure;
    }

    private static Encoding Sniff(ReadOnlySpan<byte> head, Encoding chosen, out int skip)
    {
        skip = 0;
        if (head is [0xEF, 0xBB, 0xBF, ..]) { skip = 3; return new UTF8Encoding(false, throwOnInvalidBytes: true); }
        if (head is [0xFF, 0xFE, ..]) { skip = 2; return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true); }
        if (head is [0xFE, 0xFF, ..]) { skip = 2; return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true); }
        return chosen;
    }
}

internal static class ConsoleText
{
    /// <summary>
    /// DISM and oscdimg convert their Unicode text to the system OEM code page when redirected, whatever the console code page
    /// is. A GUI process has no console, so .NET would decode their output with the ANSI code page, which turns a Russian
    /// edition name written as CP866 into CP1251 mojibake. Windows tools are the only ones run here; elsewhere text is UTF-8.
    /// </summary>
    public static Encoding SystemOem() => OperatingSystem.IsWindows() ? ForCodePage((int)GetOEMCP()) : new UTF8Encoding(false);

    // Asked of the framework's provider directly: this needs no package and registers nothing for the whole process.
    public static Encoding ForCodePage(int codePage)
    {
        if (codePage == 65001) return new UTF8Encoding(false);
        try { return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new NotSupportedException($"Code page {codePage} is not available, so the output of Windows tools cannot be decoded reliably.", error);
        }
    }

    public static Encoding Strict(Encoding encoding)
    {
        var strict = (Encoding)encoding.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        return strict;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetOEMCP();
}
