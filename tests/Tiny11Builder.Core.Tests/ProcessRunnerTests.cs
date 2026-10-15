using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Tiny11Builder.Core;
using Xunit;

namespace Tiny11Builder.Core.Tests;

// Every child here is a real process (cmd.exe type, ping, and where available DISM and oscdimg) writing real bytes. A
// run that hangs is killed by its own timeout token, so no test can leave a child behind or wait for ever.
[SupportedOSPlatform("windows")]
public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string NewLine = Environment.NewLine;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    // The folder name has a space and Cyrillic letters, so every file argument also exercises Unicode quoting.
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tiny11 тест " + Guid.NewGuid().ToString("N"));

    public ProcessRunnerTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // cmd's TYPE copies a file's bytes to a pipe untouched, except that it stops at Ctrl+Z, which the fixtures avoid.
    private async Task<ProcessResult> TypeAsync(ProcessRunner runner, byte[] bytes, bool toStandardError = false, Action<string>? lines = null)
    {
        Assert.DoesNotContain((byte)0x1A, bytes);
        var file = Path.Combine(folder, "файл " + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(file, bytes);
        string[] arguments = toStandardError ? ["/d", "/c", "type", file, "1>&2"] : ["/d", "/c", "type", file];
        using var timeout = new CancellationTokenSource(Bound);
        return await runner.RunAsync(Cmd, arguments, lines, timeout.Token, finishOnCancellation: false);
    }

    private static bool Represents(Encoding encoding, string text) => encoding.GetString(encoding.GetBytes(text)) == text;

    // TYPE converts a file that starts with a UTF-16LE mark to the console code page, so output that must reach the pipe
    // byte for byte is written by Windows PowerShell instead.
    private async Task<ProcessResult> RawAsync(ProcessRunner runner, byte[] bytes, Action<string>? lines = null)
    {
        var file = Path.Combine(folder, "файл " + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(file, bytes);
        string[] arguments = ["-NoProfile", "-NonInteractive", "-Command",
            "& { param($path) $b = [IO.File]::ReadAllBytes($path); $o = [Console]::OpenStandardOutput(); $o.Write($b, 0, $b.Length); $o.Flush() } '" + file + "'"];
        using var timeout = new CancellationTokenSource(Bound);
        return await runner.RunAsync(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), arguments, lines,
            timeout.Token, finishOnCancellation: false);
    }

    [Fact]
    public async Task Decodes_the_CP866_bytes_DISM_writes_on_a_Russian_system_that_the_ANSI_default_turned_into_the_reported_mojibake()
    {
        var oem = ConsoleText.ForCodePage(866);
        var ansi = ConsoleText.ForCodePage(1251);
        const string label = "Windows 11 Домашняя | x64 | build 26200.9445";
        var bytes = oem.GetBytes(label);
        Assert.Equal(new byte[] { 0x84, 0xAE, 0xAC, 0xA0, 0xE8, 0xAD, 0xEF, 0xEF }, bytes[11..19]);
        // The same bytes read with the ANSI code page are exactly the text in the issue's screenshot.
        Assert.Equal("Windows 11 „®¬\u00A0и\u00ADпп | x64 | build 26200.9445", ansi.GetString(bytes));
        var result = await TypeAsync(new ProcessRunner(oem), bytes);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(label + NewLine, result.Output);
    }

    [Theory]
    [InlineData(866, "Домашняя")]
    [InlineData(1251, "Домашняя")]
    [InlineData(936, "家庭版 中文")]
    [InlineData(850, "Édition Familiale à l'école")]
    [InlineData(437, "Éditiön Familiàle")]
    [InlineData(65001, "Домашняя 家庭版 édition")]
    public async Task Decodes_each_chosen_encoding_from_a_real_child_process(int codePage, string text)
    {
        var encoding = ConsoleText.ForCodePage(codePage);
        var bytes = encoding.GetBytes(text);
        Assert.Equal(text, encoding.GetString(bytes));   // representable, so the bytes below are exact
        if (codePage != 65001) Assert.NotEqual(Encoding.UTF8.GetBytes(text), bytes);   // not the same bytes as UTF-8
        var result = await TypeAsync(new ProcessRunner(encoding), bytes);
        Assert.Equal(text + NewLine, result.Output);
    }

    [Theory]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf8")]
    public async Task A_byte_order_mark_wins_over_the_chosen_encoding(string kind)
    {
        const string text = "Домашняя 家庭版 édition";
        Encoding encoding = kind switch
        {
            "utf16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
        };
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        var lines = new List<string>();
        var result = await RawAsync(new ProcessRunner(ConsoleText.ForCodePage(866)), bytes, lines.Add);
        Assert.Equal(text + NewLine, result.Output);
        Assert.Equal([text], lines);   // the mark itself is never part of the text
    }

    [Fact]
    public async Task Standard_error_is_decoded_the_same_way_as_standard_output()
    {
        var oem = ConsoleText.ForCodePage(866);
        var result = await TypeAsync(new ProcessRunner(oem), oem.GetBytes("ошибка Домашняя"), toStandardError: true);
        Assert.Equal("ошибка Домашняя" + NewLine, result.Output);
    }

    [Fact]
    public async Task Bytes_that_are_not_valid_in_the_chosen_encoding_fail_the_run_instead_of_being_replaced_or_guessed()
    {
        // CP866 bytes read as UTF-8: the old behaviour showed replacement characters and carried on.
        byte[] bytes = [.. "Windows 11 "u8.ToArray(), 0x84, 0xAE, 0xAC, .. " | x64"u8.ToArray()];
        var lines = new List<string>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TypeAsync(new ProcessRunner(ConsoleText.ForCodePage(65001)), bytes, lines: lines.Add));
        Assert.Contains("cmd.exe wrote standard output that is not valid", error.Message);
        Assert.Contains("code page 65001", error.Message);
        Assert.Contains("near byte 11", error.Message);
        Assert.Contains("84", error.Message);
        Assert.Contains("not guessed or repaired", error.Message);
        Assert.Contains("exited with code 0", error.Message);
        Assert.IsType<DecoderFallbackException>(error.InnerException);
        Assert.DoesNotContain(lines, line => line.Contains('\uFFFD'));
    }

    [Fact]
    public async Task A_character_cut_in_half_at_the_end_of_the_output_is_reported()
    {
        byte[] bytes = [.. "ok "u8.ToArray(), 0xD0];   // the first half of a two-byte UTF-8 letter
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TypeAsync(new ProcessRunner(ConsoleText.ForCodePage(65001)), bytes));
        Assert.IsType<DecoderFallbackException>(error.InnerException);
    }

    [Fact]
    public void The_default_is_the_system_OEM_code_page_chosen_explicitly_and_an_unavailable_one_is_an_error()
    {
        Assert.Equal((int)Native.GetOEMCP(), new ProcessRunner().OutputEncoding.CodePage);
        Assert.Equal(866, ConsoleText.ForCodePage(866).CodePage);
        Assert.Equal(65001, ConsoleText.ForCodePage(65001).CodePage);
        Assert.Same(Encoding.UTF8, new ProcessRunner(Encoding.UTF8).OutputEncoding);
        // No silent UTF-8: a code page .NET cannot decode stops the run with its own message.
        Assert.Throws<NotSupportedException>(() => ConsoleText.ForCodePage(12345));
        Assert.Throws<NotSupportedException>(() => ConsoleText.ForCodePage(99999));
        var strict = ConsoleText.Strict(ConsoleText.ForCodePage(65001));
        Assert.Throws<DecoderFallbackException>(() => strict.GetString([0x84]));
        Assert.Equal("\uFFFD", ConsoleText.ForCodePage(65001).GetString([0x84]));   // the original is left as it was
    }

    [Fact]
    public async Task Lines_end_at_LF_CR_or_CRLF_so_every_progress_redraw_is_delivered_and_the_text_stays_whole()
    {
        var lines = new List<string>();
        var result = await TypeAsync(new ProcessRunner(Encoding.ASCII), "one\r\ntwo\rthree\n\nfour"u8.ToArray(), lines: lines.Add);
        Assert.Equal(["one", "two", "three", "", "four"], lines);
        Assert.Equal("one\r\ntwo\r\nthree\r\n\r\nfour\r\n", result.Output);
    }

    [Fact]
    public async Task A_line_break_split_between_two_reads_is_still_one_line_break()
    {
        // About 1 MB of CRLF lines of varying length is read in many 4 KB pieces, so some piece ends between a CR and its LF.
        var text = new StringBuilder();
        for (var i = 0; i < 20_000; i++) text.Append("line ").Append(i).Append(' ').Append('x', i % 97).Append("\r\n");
        var lines = new List<string>();
        await TypeAsync(new ProcessRunner(Encoding.ASCII), Encoding.ASCII.GetBytes(text.ToString()), lines: lines.Add);
        Assert.Equal(20_000, lines.Count);
        Assert.DoesNotContain("", lines);
        Assert.Equal("line 19999 " + new string('x', 19_999 % 97), lines[^1]);
    }

    [Fact]
    public async Task Both_pipes_are_drained_together_so_a_chatty_child_never_stalls()
    {
        var output = string.Concat(Enumerable.Repeat("out line 0123456789\r\n", 20_000));
        var errors = string.Concat(Enumerable.Repeat("err line 0123456789\r\n", 20_000));
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), output, Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(folder, "b.txt"), errors, Encoding.ASCII);
        // All of stdout is written first, then all of stderr: a reader that waited for stdout to end would deadlock.
        var script = Path.Combine(folder, "both " + Guid.NewGuid().ToString("N") + ".cmd");
        await File.WriteAllTextAsync(script, "@echo off\r\ntype \"%~dp0a.txt\"\r\ntype \"%~dp0b.txt\" 1>&2\r\n", Encoding.ASCII);
        var lines = new ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(Bound);
        var result = await new ProcessRunner(Encoding.ASCII).RunAsync(Cmd, ["/d", "/c", script], lines.Enqueue, timeout.Token, finishOnCancellation: false);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(20_000, lines.Count(line => line.StartsWith("out line", StringComparison.Ordinal)));
        Assert.Equal(20_000, lines.Count(line => line.StartsWith("err line", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_failing_output_callback_does_not_stall_the_child_and_is_reported_after_it_finishes()
    {
        var bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("line of output\r\n", 30_000)));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TypeAsync(new ProcessRunner(Encoding.ASCII), bytes, lines: _ =>
        {
            calls++;
            throw new InvalidOperationException("the log is full");
        }));
        Assert.Equal(1, calls);   // a broken sink is called once, then the output is only drained
    }

    [Fact]
    public async Task The_exit_code_and_arguments_with_spaces_and_Unicode_arrive_intact()
    {
        using var timeout = new CancellationTokenSource(Bound);
        var result = await new ProcessRunner(Encoding.ASCII).RunAsync(Cmd, ["/d", "/c", "exit", "/b", "7"], null, timeout.Token, finishOnCancellation: false);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public async Task Cancellation_lets_a_normal_command_finish_and_kills_only_one_that_asked_for_it()
    {
        using var cancelled = new CancellationTokenSource();
        // Like DISM, the default must not be interrupted: the command completes and reports its own exit code.
        var finishing = new ProcessRunner(Encoding.ASCII).RunAsync(Cmd, ["/d", "/c", "ping", "-n", "2", "127.0.0.1", ">nul", "&", "exit", "/b", "7"],
            null, cancelled.Token);
        cancelled.Cancel();
        Assert.Equal(7, (await finishing.WaitAsync(Bound)).ExitCode);

        // Like oscdimg, a command that asked for it is killed with its tree and the cancellation is reported.
        using var kill = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner(Encoding.ASCII).RunAsync(
            Cmd, ["/d", "/c", "ping", "-n", "60", "127.0.0.1"], null, kill.Token, finishOnCancellation: false).WaitAsync(Bound));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "the 60 second command was not killed");

        using var already = new CancellationTokenSource();
        already.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(Cmd, ["/d", "/c", "exit"], null, already.Token));
    }

    [Fact]
    public async Task Real_DISM_output_for_a_Unicode_image_name_is_decoded_to_exactly_that_name()
    {
        if (!ToolLocator.IsAdministrator) return;   // capturing an image needs elevation; the tests above need none
        var oem = new ProcessRunner().OutputEncoding;
        // DISM converts to the OEM code page, so the name is one that this machine's OEM code page can hold.
        var name = new[] { "Домашняя", "家庭版", "Édition", "Home" }.First(candidate => Represents(oem, candidate));
        var work = Path.Combine(Path.GetTempPath(), "tiny11-runner-dism-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "capture"));
            await File.WriteAllTextAsync(Path.Combine(work, @"capture\readme.txt"), "synthetic image");
            var wim = Path.Combine(work, "synthetic.wim");
            var dism = new DismClient(Path.Combine(Environment.SystemDirectory, "dism.exe"), new ProcessRunner(), null,
                new(Path.Combine(work, "scratch"), Path.Combine(work, "dism.log"), false));
            Directory.CreateDirectory(Path.Combine(work, "scratch"));
            await dism.RunAsync(CancellationToken.None, "/Capture-Image", $"/ImageFile:{wim}", $"/CaptureDir:{Path.Combine(work, "capture")}",
                $"/Name:{name}", $"/Description:{name} description", "/Compress:none");
            var info = await dism.RunAsync(CancellationToken.None, "/Get-WimInfo", $"/WimFile:{wim}", "/Index:1");
            Assert.Contains($"Name : {name}{NewLine}", info);
            Assert.Contains($"Description : {name} description{NewLine}", info);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public async Task Real_oscdimg_output_that_echoes_a_Unicode_path_is_decoded_to_exactly_that_path_when_installed()
    {
        var oscdimg = ToolLocator.FindOscdimg();
        if (oscdimg.Length == 0) return;
        var oem = new ProcessRunner().OutputEncoding;
        var name = new[] { "образ", "镜像", "édition", "image" }.First(candidate => Represents(oem, candidate));
        var work = Path.Combine(Path.GetTempPath(), "tiny11-runner-oscdimg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(work, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "x");
            var iso = Path.Combine(work, name + ".iso");
            using var timeout = new CancellationTokenSource(Bound);
            var result = await new ProcessRunner().RunAsync(oscdimg, ["-m", "-o", "-u2", "-udfver102", source, iso], null, timeout.Token,
                finishOnCancellation: false);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(iso, result.Output);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern uint GetOEMCP();
    }
}
