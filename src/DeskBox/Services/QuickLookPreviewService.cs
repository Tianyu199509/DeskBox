using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace DeskBox.Services;

/// <summary>
/// Passively forwards file previews to an already-running QuickLook instance.
/// It never discovers, starts, or configures QuickLook.
/// </summary>
public sealed class QuickLookPreviewService
{
    internal const string ToggleMessage = "QuickLook.App.PipeMessages.Toggle";
    private const string ProcessName = "QuickLook";
    private const int ConnectTimeoutMs = 160;

    public bool CanPreview(string? path) =>
        IsPreviewablePath(path) && IsQuickLookRunning();

    public async Task<bool> TryToggleAsync(string path)
    {
        if (!IsPreviewablePath(path))
        {
            return false;
        }

        string? pipeName = GetPipeName();
        if (pipeName is null)
        {
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
                                   UnauthorizedAccessException or
                                   ObjectDisposedException)
        {
            return false;
        }
    }

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
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            return string.IsNullOrWhiteSpace(sid)
                ? null
                : $"QuickLook.App.Pipe.{sid}";
        }
        catch (SystemException)
        {
            return null;
        }
    }
}
