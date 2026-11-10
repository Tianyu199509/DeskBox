using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Services;

public sealed record CustomWidgetCommandOutcome(
    bool Succeeded,
    int ExitCode,
    bool TimedOut,
    string Stdout,
    string Stderr,
    string? ErrorMessage);

/// <summary>
/// Executes bounded, windowless local CLI commands (<c>command</c> source and <c>command.run</c> action)
/// when <see cref="CustomWidgetPermissionKind.CommandExecution"/> is granted.
/// Compiled out of Microsoft Store builds via <c>DESKBOX_STORE</c>.
/// </summary>
public static class CustomWidgetCommandRunner
{
    public const int DefaultTimeoutMs = 8_000;
    public const int MinTimeoutMs = 500;
    public const int MaxTimeoutMs = 30_000;
    public const int MaxOutputChars = 128 * 1024;

    private static readonly JsonWriterOptions CompactJsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<CustomWidgetCommandOutcome> ExecuteAsync(
        string? command,
        IReadOnlyList<string>? argumentList = null,
        string? rawArguments = null,
        string? workingDirectory = null,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
#if DESKBOX_STORE
        await Task.CompletedTask;
        _ = command;
        _ = argumentList;
        _ = rawArguments;
        _ = workingDirectory;
        _ = timeoutMs;
        _ = ct;
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

        string exe = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        int clampedTimeout = Math.Clamp(timeoutMs <= 0 ? DefaultTimeoutMs : timeoutMs, MinTimeoutMs, MaxTimeoutMs);

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
                    $"Failed to start process '{exe}'.");
            }

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(clampedTimeout);

            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            Task stdoutTask = ReadBoundedReaderAsync(process.StandardOutput, stdoutBuf, MaxOutputChars, timeoutCts.Token);
            Task stderrTask = ReadBoundedReaderAsync(process.StandardError, stderrBuf, MaxOutputChars, timeoutCts.Token);

            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                TryKillProcessTree(process);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw;
            }

            await SafeAwaitOutputAsync(stdoutTask).ConfigureAwait(false);
            await SafeAwaitOutputAsync(stderrTask).ConfigureAwait(false);
            string stdout;
            string stderr;
            lock (stdoutBuf) { stdout = stdoutBuf.ToString(); }
            lock (stderrBuf) { stderr = stderrBuf.ToString(); }

            if (timedOut)
            {
                return new CustomWidgetCommandOutcome(
                    false,
                    -1,
                    true,
                    stdout,
                    stderr,
                    $"Command timed out after {clampedTimeout} ms.");
            }

            int exitCode = process.ExitCode;
            return new CustomWidgetCommandOutcome(
                Succeeded: exitCode == 0,
                ExitCode: exitCode,
                TimedOut: false,
                Stdout: stdout,
                Stderr: stderr,
                ErrorMessage: exitCode == 0 ? null : (string.IsNullOrWhiteSpace(stderr) ? $"Process exited with code {exitCode}." : stderr.Trim()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CustomWidgetCommandOutcome(
                false,
                -1,
                false,
                string.Empty,
                string.Empty,
                ex.Message);
        }
#endif
    }

    public static string BuildCallbackPayloadJson(string id, CustomWidgetCommandOutcome outcome)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id ?? string.Empty);
            writer.WriteBoolean("ok", outcome.Succeeded);
            writer.WriteNumber("exitCode", outcome.ExitCode);
            writer.WriteBoolean("timedOut", outcome.TimedOut);
            writer.WriteString("stdout", outcome.Stdout ?? string.Empty);
            writer.WriteString("stderr", outcome.Stderr ?? string.Empty);
            writer.WriteString("error", outcome.ErrorMessage ?? string.Empty);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

#if !DESKBOX_STORE
    private static async Task ReadBoundedReaderAsync(
        StreamReader reader,
        StringBuilder sb,
        int maxChars,
        CancellationToken ct)
    {
        char[] buf = new char[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                lock (sb)
                {
                    int remaining = maxChars - sb.Length;
                    if (remaining > 0)
                    {
                        sb.Append(buf, 0, Math.Min(read, remaining));
                    }
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
