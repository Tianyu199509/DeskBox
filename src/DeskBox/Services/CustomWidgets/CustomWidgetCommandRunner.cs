using System.Diagnostics;
using System.Text;

namespace DeskBox.Services;

public sealed record CustomWidgetCommandOutcome(
    bool Succeeded,
    int ExitCode,
    bool TimedOut,
    string Stdout,
    string Stderr,
    string? ErrorMessage,
    bool Cancelled = false,
    long DurationMs = 0);

/// <summary>
/// Executes windowless local CLI commands (<c>command</c> source and <c>command.run</c> action)
/// when <see cref="CustomWidgetPermissionKind.CommandExecution"/> is granted.
/// Supports short bounded commands (default 30s) and long-running background jobs
/// (<c>longRunning: true</c> / <c>timeoutMs: 0</c>) protected by an output-activity idle-stall watchdog.
/// Compiled out of Microsoft Store builds via <c>DESKBOX_STORE</c>.
/// </summary>
public static class CustomWidgetCommandRunner
{
    public const int DefaultTimeoutMs = 30_000;
    public const int MinTimeoutMs = 500;
    public const int MaxTimeoutMs = 3_600_000;
    public const int DefaultIdleStallTimeoutMs = 180_000;
    public const int MaxOutputChars = 256 * 1024;

    public static async Task<CustomWidgetCommandOutcome> ExecuteAsync(
        string? command,
        IReadOnlyList<string>? argumentList = null,
        string? rawArguments = null,
        string? workingDirectory = null,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default,
        bool longRunning = false,
        Action<string, string, long>? onOutputLine = null,
        int idleStallTimeoutMs = DefaultIdleStallTimeoutMs)
    {
#if DESKBOX_STORE
        await Task.CompletedTask;
        _ = command;
        _ = argumentList;
        _ = rawArguments;
        _ = workingDirectory;
        _ = timeoutMs;
        _ = ct;
        _ = longRunning;
        _ = onOutputLine;
        _ = idleStallTimeoutMs;
        return new CustomWidgetCommandOutcome(
            false,
            -1,
            false,
            string.Empty,
            string.Empty,
            "Command execution is disabled in the Microsoft Store edition.");
#else
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CustomWidgetCommandOutcome(
                false,
                -1,
                false,
                string.Empty,
                string.Empty,
                "Command executable cannot be empty.");
        }

        if (ct.IsCancellationRequested)
        {
            return new CustomWidgetCommandOutcome(
                false,
                -1,
                false,
                string.Empty,
                string.Empty,
                "Command cancelled before start.",
                Cancelled: true,
                DurationMs: 0);
        }

        string exe = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        bool useIdleWatchdog = longRunning || timeoutMs == 0;
        int clampedTimeout = useIdleWatchdog
            ? 0
            : Math.Clamp(timeoutMs < 0 ? DefaultTimeoutMs : timeoutMs, MinTimeoutMs, MaxTimeoutMs);
        int effectiveIdleStallMs = Math.Clamp(idleStallTimeoutMs <= 0 ? DefaultIdleStallTimeoutMs : idleStallTimeoutMs, 200, 3_600_000);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            string expandedDir = Environment.ExpandEnvironmentVariables(workingDirectory.Trim().Trim('"'));
            if (expandedDir.StartsWith("~/", StringComparison.Ordinal) ||
                expandedDir.StartsWith("~\\", StringComparison.Ordinal))
            {
                expandedDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    expandedDir[2..]);
            }

            if (!Directory.Exists(expandedDir))
            {
                return new CustomWidgetCommandOutcome(
                    false,
                    -1,
                    false,
                    string.Empty,
                    string.Empty,
                    $"Working directory does not exist: '{expandedDir}'.");
            }

            psi.WorkingDirectory = expandedDir;
        }

        if (argumentList is { Count: > 0 })
        {
            foreach (string arg in argumentList)
            {
                psi.ArgumentList.Add(arg ?? string.Empty);
            }
        }
        else if (!string.IsNullOrWhiteSpace(rawArguments))
        {
            psi.Arguments = rawArguments;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new CustomWidgetCommandOutcome(
                    false,
                    -1,
                    false,
                    string.Empty,
                    string.Empty,
                    $"Failed to start process '{exe}'.",
                    Cancelled: false,
                    DurationMs: sw.ElapsedMilliseconds);
            }

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!useIdleWatchdog)
            {
                readerCts.CancelAfter(clampedTimeout);
            }

            long lastActivityTick = Environment.TickCount64;
            void RecordActivity() => Interlocked.Exchange(ref lastActivityTick, Environment.TickCount64);

            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            Task stdoutTask = ReadBoundedReaderAsync(
                process.StandardOutput,
                stdoutBuf,
                MaxOutputChars,
                "stdout",
                sw,
                RecordActivity,
                onOutputLine,
                readerCts.Token);
            Task stderrTask = ReadBoundedReaderAsync(
                process.StandardError,
                stderrBuf,
                MaxOutputChars,
                "stderr",
                sw,
                RecordActivity,
                onOutputLine,
                readerCts.Token);

            bool timedOut = false;
            bool cancelled = false;

            if (!useIdleWatchdog)
            {
                try
                {
                    await process.WaitForExitAsync(readerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    TryKillProcessTree(process);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    TryKillProcessTree(process);
                }
            }
            else
            {
                Task exitTask = process.WaitForExitAsync(ct);
                int checkStepMs = Math.Clamp(effectiveIdleStallMs / 6, 40, 1_000);
                while (!exitTask.IsCompleted)
                {
                    Task finished = await Task.WhenAny(exitTask, Task.Delay(checkStepMs, ct)).ConfigureAwait(false);
                    if (ReferenceEquals(finished, exitTask))
                    {
                        try
                        {
                            await exitTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            cancelled = true;
                            TryKillProcessTree(process);
                        }
                        break;
                    }

                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        TryKillProcessTree(process);
                        break;
                    }

                    long silentMs = Environment.TickCount64 - Interlocked.Read(ref lastActivityTick);
                    if (silentMs >= effectiveIdleStallMs)
                    {
                        timedOut = true;
                        try { readerCts.Cancel(); } catch { }
                        TryKillProcessTree(process);
                        break;
                    }
                }
            }

            await SafeAwaitOutputAsync(stdoutTask).ConfigureAwait(false);
            await SafeAwaitOutputAsync(stderrTask).ConfigureAwait(false);
            sw.Stop();

            string stdout;
            string stderr;
            lock (stdoutBuf) { stdout = stdoutBuf.ToString(); }
            lock (stderrBuf) { stderr = stderrBuf.ToString(); }

            if (cancelled)
            {
                return new CustomWidgetCommandOutcome(
                    Succeeded: false,
                    ExitCode: -1,
                    TimedOut: false,
                    Stdout: stdout,
                    Stderr: stderr,
                    ErrorMessage: "Command cancelled.",
                    Cancelled: true,
                    DurationMs: sw.ElapsedMilliseconds);
            }

            if (timedOut)
            {
                string timeoutReason = useIdleWatchdog
                    ? $"Command stalled with no output for {effectiveIdleStallMs} ms."
                    : $"Command timed out after {clampedTimeout} ms.";
                return new CustomWidgetCommandOutcome(
                    Succeeded: false,
                    ExitCode: -1,
                    TimedOut: true,
                    Stdout: stdout,
                    Stderr: stderr,
                    ErrorMessage: timeoutReason,
                    Cancelled: false,
                    DurationMs: sw.ElapsedMilliseconds);
            }

            int exitCode = process.ExitCode;
            return new CustomWidgetCommandOutcome(
                Succeeded: exitCode == 0,
                ExitCode: exitCode,
                TimedOut: false,
                Stdout: stdout,
                Stderr: stderr,
                ErrorMessage: exitCode == 0
                    ? null
                    : (string.IsNullOrWhiteSpace(stderr) ? $"Process exited with code {exitCode}." : stderr.Trim()),
                Cancelled: false,
                DurationMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            TryKillProcessTree(process);
            return new CustomWidgetCommandOutcome(
                Succeeded: false,
                ExitCode: -1,
                TimedOut: false,
                Stdout: string.Empty,
                Stderr: string.Empty,
                ErrorMessage: "Command cancelled.",
                Cancelled: true,
                DurationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CustomWidgetCommandOutcome(
                false,
                -1,
                false,
                string.Empty,
                string.Empty,
                ex.Message,
                Cancelled: false,
                DurationMs: sw.ElapsedMilliseconds);
        }
#endif
    }

#if !DESKBOX_STORE
    private static async Task ReadBoundedReaderAsync(
        StreamReader reader,
        StringBuilder sb,
        int maxChars,
        string streamName,
        Stopwatch sw,
        Action onActivity,
        Action<string, string, long>? onOutputLine,
        CancellationToken ct)
    {
        char[] buf = new char[2048];
        var lineAcc = onOutputLine is not null ? new StringBuilder(256) : null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                onActivity();

                lock (sb)
                {
                    int remaining = maxChars - sb.Length;
                    if (remaining > 0)
                    {
                        sb.Append(buf, 0, Math.Min(read, remaining));
                    }
                    else if (maxChars > 4096 && read < maxChars)
                    {
                        // Rolling tail preservation for multi-hour CLI logs (e.g., ffmpeg verbose stderr)
                        int drop = Math.Min(sb.Length, Math.Max(read, 4096));
                        sb.Remove(0, drop);
                        sb.Append(buf, 0, Math.Min(read, maxChars - sb.Length));
                    }
                }

                if (lineAcc is not null && onOutputLine is not null)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char ch = buf[i];
                        if (ch is '\r' or '\n')
                        {
                            if (lineAcc.Length > 0)
                            {
                                string line = lineAcc.ToString().Trim();
                                lineAcc.Clear();
                                if (line.Length > 0)
                                {
                                    try { onOutputLine(line, streamName, sw.ElapsedMilliseconds); } catch { }
                                }
                            }
                        }
                        else if (lineAcc.Length < 2048)
                        {
                            lineAcc.Append(ch);
                        }
                    }
                }
            }

            if (lineAcc is { Length: > 0 } && onOutputLine is not null)
            {
                string trailing = lineAcc.ToString().Trim();
                lineAcc.Clear();
                if (trailing.Length > 0)
                {
                    try { onOutputLine(trailing, streamName, sw.ElapsedMilliseconds); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task SafeAwaitOutputAsync(Task task)
    {
        try
        {
            Task completed = await Task.WhenAny(task, Task.Delay(350)).ConfigureAwait(false);
            if (ReferenceEquals(completed, task))
            {
                await task.ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try { process.StandardOutput.Close(); } catch { }
        try { process.StandardError.Close(); } catch { }
    }
#endif
}
