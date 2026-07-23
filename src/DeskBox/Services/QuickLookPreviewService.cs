using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
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

    public async Task<bool> TryToggleAsync(string path)
    {
        if (!IsPreviewablePath(path))
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
            await writer.WriteLineAsync(BuildToggleMessage(path));
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
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                if (!OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hToken)) return false;
                try
                {
                    TOKEN_ELEVATION elevation;
                    bool ok = GetTokenInformation(
                        hToken, TokenElevation, out elevation,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<TOKEN_ELEVATION>(), out _);
                    return ok && elevation.TokenIsElevated != 0;
                }
                finally { CloseHandle(hToken); }
            }
            finally { CloseHandle(hProcess); }
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        out TOKEN_ELEVATION TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);
}
