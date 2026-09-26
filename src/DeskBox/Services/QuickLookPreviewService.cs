using System.Diagnostics;
using System.Security.Principal;
using DeskBox.Platform;
using System.IO.Pipes;
using System.Text;

namespace DeskBox.Services;

/// <summary>
/// Passively forwards file previews to an already-running QuickLook instance.
/// It never discovers, starts, or configures QuickLook.
///
/// IMPORTANT: Never connect to QuickLook's named pipe unless you are actually
/// sending a message.  QuickLook runs a single-threaded pipe server; a bare
/// connect-then-disconnect (no data) crashes its server thread and renders
/// QuickLook completely inoperable until the user force-restarts it.
/// </summary>
public sealed class QuickLookPreviewService
{
    internal const string ToggleMessage = "QuickLook.App.PipeMessages.Toggle";
    internal const string SwitchMessage = "QuickLook.App.PipeMessages.Switch";
    internal const string CloseMessage = "QuickLook.App.PipeMessages.Close";
    private const string ProcessName = "QuickLook";
    private const int ConnectTimeoutMs = 600;

    // Cached pipe name — SID never changes within a session.
    private static string? s_cachedPipeName;
    private static bool s_pipeNameResolved;

    // Integrity check: run at most once per process lifetime.
    private static bool s_integrityChecked;

    /// <summary>
    /// Quick, non-intrusive availability check.
    /// Only enumerates processes — does NOT touch the named pipe.
    /// </summary>
    public bool CanPreview(string? path) =>
        IsPreviewablePath(path) && IsQuickLookRunning();

    public Task<bool> TryToggleAsync(string path) =>
        TrySendAsync(path, BuildToggleMessage(path), validatePath: true);

    /// <summary>
    /// Changes the item shown by an existing QuickLook preview. QuickLook
    /// deliberately ignores this message when its preview window is closed.
    /// </summary>
    public Task<bool> TrySwitchAsync(string path) =>
        TrySendAsync(path, BuildSwitchMessage(path), validatePath: true);

    public Task<bool> TryCloseAsync() =>
        TrySendAsync(null, BuildCloseMessage(), validatePath: false);

    private static async Task<bool> TrySendAsync(
        string? path,
        string message,
        bool validatePath)
    {
        if (validatePath && !IsPreviewablePath(path))
        {
            App.Log($"[QuickLook] Path not previewable: '{path}'.");
            return false;
        }

        string? pipeName = GetPipeName();
        if (pipeName is null)
        {
            App.Log("[QuickLook] Unable to resolve pipe name (SID unavailable).");
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(ConnectTimeoutMs);
            await client.ConnectAsync(timeout.Token);

            await using var writer = new StreamWriter(
                client,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: false);
            await writer.WriteLineAsync(message);
            await writer.FlushAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or
                                   OperationCanceledException or
                                   ObjectDisposedException)
        {
            App.Log($"[QuickLook] Pipe send failed for '{path}': {ex.GetType().Name}");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            App.Log($"[QuickLook] Pipe access denied for '{path}' (integrity mismatch?).");
            LogIntegrityHintOnce();
            return false;
        }
    }

    // ── Integrity mismatch detection & one-time hint ──

    private static void LogIntegrityHintOnce()
    {
        if (s_integrityChecked)
        {
            return;
        }

        s_integrityChecked = true;

        try
        {
            foreach (Process proc in Process.GetProcessesByName(ProcessName))
            {
                using (proc)
                {
                    if (proc.HasExited) continue;

                    if (IsProcessElevated(proc.Id) && !IsCurrentProcessElevated())
                    {
                        App.Log("[QuickLook] Integrity mismatch detected: QuickLook is running as administrator " +
                                "but DeskBox is not. Windows blocks pipe access between different integrity levels. " +
                                "To fix: run QuickLook as a normal user (not as administrator).");
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // Best-effort check — ignore failures.
        }
    }

    private static bool IsProcessElevated(int processId)
    {
        try
        {
            IntPtr hProcess = QuickLookElevationNativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                if (!QuickLookElevationNativeMethods.OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hToken)) return false;
                try
                {
                    QuickLookElevationNativeMethods.TOKEN_ELEVATION elevation;
                    bool ok = QuickLookElevationNativeMethods.GetTokenInformation(
                        hToken, TokenElevation, out elevation,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<QuickLookElevationNativeMethods.TOKEN_ELEVATION>(), out _);
                    return ok && elevation.TokenIsElevated != 0;
                }
                finally { QuickLookElevationNativeMethods.CloseHandle(hToken); }
            }
            finally { QuickLookElevationNativeMethods.CloseHandle(hProcess); }
        }
        catch { return false; }
    }

    private static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ── Availability check (process-only, NEVER touches the pipe) ──

    internal static bool IsPreviewablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (File.Exists(path) || Directory.Exists(path));

    internal static string BuildToggleMessage(string path) =>
        $"{ToggleMessage}|{path}|";

    internal static string BuildSwitchMessage(string path) =>
        $"{SwitchMessage}|{path}|";

    internal static string BuildCloseMessage() => $"{CloseMessage}||";

    private static bool IsQuickLookRunning()
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // A process can exit while the snapshot is being inspected.
        }

        return false;
    }

    private static string? GetPipeName()
    {
        if (s_pipeNameResolved)
        {
            return s_cachedPipeName;
        }

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            s_cachedPipeName = string.IsNullOrWhiteSpace(sid)
                ? null
                : $"QuickLook.App.Pipe.{sid}";
        }
        catch (SystemException)
        {
            s_cachedPipeName = null;
        }

        s_pipeNameResolved = true;
        return s_cachedPipeName;
    }

    // ── Win32 interop (integrity check only) ──

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    // kernel32/advapi32 elevation entry points live in
    // DeskBox.Platform.QuickLookElevationNativeMethods.
}
