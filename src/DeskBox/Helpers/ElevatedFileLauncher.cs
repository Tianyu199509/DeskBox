using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Helpers;

public enum ElevatedFileLaunchStatus
{
    Launched,
    AlreadyRunningUnelevated,
    NoNewProcess,
    NotElevated,
    Cancelled,
    Failed
}

public readonly record struct ElevatedFileLaunchResult(
    ElevatedFileLaunchStatus Status,
    uint ProcessId = 0,
    int ErrorCode = 0);

internal readonly record struct ElevatedFileLaunchRequest(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    string OriginalTargetPath,
    bool DetectExistingProcess);

/// <summary>
/// Starts only the selected executable with the Windows <c>runas</c> verb.
/// DeskBox itself remains an ordinary-user/medium-integrity process.
/// </summary>
public static class ElevatedFileLauncher
{
    private const uint ShellExecuteMaskNoCloseProcess = 0x00000040;
    private const uint ShellExecuteMaskNoAsync = 0x00000100;
    private const uint ShellExecuteMaskFlagNoUi = 0x00000400;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Extensions Windows treats as executables; the Open With dialog
    /// refuses them and the association list is empty, so open-with UI
    /// must be hidden for these types (shared with ShellOpenWithHelper).
    /// </summary>
    internal static readonly System.Collections.Frozen.FrozenSet<string> ExecutableExtensions =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            ".exe",
            ".com",
            ".scr",
            ".msi",
            ".bat",
            ".cmd"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool CanRunAsAdministrator(WidgetItem item) =>
        item is not null && TryCreateLaunchRequest(item, out _);

    /// <summary>
    /// Path-only variant for surfaces that hold a plain file path (for
    /// example search results) instead of a <see cref="WidgetItem"/>.
    /// </summary>
    public static bool CanRunAsAdministrator(string filePath) =>
        TryCreateLaunchRequest(filePath, null, out _);

    public static ElevatedFileLaunchResult RunAsAdministrator(
        IntPtr ownerWindow,
        WidgetItem item)
    {
        if (item is null ||
            !TryCreateLaunchRequest(item, out ElevatedFileLaunchRequest request))
        {
            return new ElevatedFileLaunchResult(
                ElevatedFileLaunchStatus.Failed);
        }

        return RunAsAdministrator(ownerWindow, in request);
    }

    public static ElevatedFileLaunchResult RunAsAdministrator(
        IntPtr ownerWindow,
        string filePath)
    {
        return RunAsAdministrator(ownerWindow, filePath, targetPathHint: null);
    }

    internal static ElevatedFileLaunchResult RunAsAdministrator(
        IntPtr ownerWindow,
        string? candidatePath,
        string? targetPathHint)
    {
        if (!TryCreateLaunchRequest(
                candidatePath,
                targetPathHint,
                out ElevatedFileLaunchRequest request))
        {
            return new ElevatedFileLaunchResult(
                ElevatedFileLaunchStatus.Failed);
        }

        return RunAsAdministrator(ownerWindow, in request);
    }

    public static Task<ElevatedFileLaunchResult> RunAsAdministratorAsync(
        IntPtr ownerWindow,
        WidgetItem item,
        CancellationToken cancellationToken = default) =>
        DeskBox.Services.ShellLaunchService.RunAsAdministratorAsync(
            ownerWindow,
            item,
            cancellationToken);

    public static Task<ElevatedFileLaunchResult> RunAsAdministratorAsync(
        IntPtr ownerWindow,
        string filePath,
        CancellationToken cancellationToken = default) =>
        DeskBox.Services.ShellLaunchService.RunAsAdministratorAsync(
            ownerWindow,
            filePath,
            cancellationToken);

    private static ElevatedFileLaunchResult RunAsAdministrator(
        IntPtr ownerWindow,
        in ElevatedFileLaunchRequest request)
    {
        if (request.DetectExistingProcess &&
            TryFindRunningUnelevatedTarget(
                request.OriginalTargetPath,
                out uint existingProcessId))
        {
            App.Log(
                $"[ElevatedLaunch] blocked existing unelevated target " +
                $"pid={existingProcessId} target='{request.OriginalTargetPath}'");
            return new ElevatedFileLaunchResult(
                ElevatedFileLaunchStatus.AlreadyRunningUnelevated,
                existingProcessId);
        }

        var executeInfo = new ElevatedLaunchNativeMethods.ShellExecuteInfo
        {
            Size = (uint)Marshal.SizeOf<ElevatedLaunchNativeMethods.ShellExecuteInfo>(),
            Mask = ShellExecuteMaskNoCloseProcess |
                   ShellExecuteMaskNoAsync |
                   ShellExecuteMaskFlagNoUi,
            OwnerWindow = ownerWindow,
            Verb = "runas",
            FileName = request.FileName,
            Parameters = string.IsNullOrWhiteSpace(request.Arguments)
                ? null
                : request.Arguments,
            Directory = request.WorkingDirectory,
            Show = 1
        };

        using IDisposable? childLaunchScope =
            DeskBox.Services.ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
        _ = Win32Helper.AllowSetForegroundWindow(uint.MaxValue);

        try
        {
            if (!ElevatedLaunchNativeMethods.ShellExecuteEx(ref executeInfo))
            {
                int errorCode = Marshal.GetLastWin32Error();
                ElevatedFileLaunchStatus status = errorCode == ErrorCancelled
                    ? ElevatedFileLaunchStatus.Cancelled
                    : ElevatedFileLaunchStatus.Failed;
                App.Log(
                    $"[ElevatedLaunch] ShellExecuteEx failed status={status} " +
                    $"error={errorCode} target='{request.OriginalTargetPath}'");
                return new ElevatedFileLaunchResult(
                    status,
                    ErrorCode: errorCode);
            }

            if (executeInfo.ProcessHandle == IntPtr.Zero)
            {
                App.Log(
                    $"[ElevatedLaunch] no new process handle target=" +
                    $"'{request.OriginalTargetPath}'");
                return new ElevatedFileLaunchResult(
                    ElevatedFileLaunchStatus.NoNewProcess);
            }

            uint processId = ElevatedLaunchNativeMethods.GetProcessId(executeInfo.ProcessHandle);
            bool? elevated = TryGetTokenElevation(
                executeInfo.ProcessHandle,
                out bool tokenElevated)
                    ? tokenElevated
                    : null;
            ElevatedFileLaunchStatus resultStatus = elevated == false
                ? ElevatedFileLaunchStatus.NotElevated
                : ElevatedFileLaunchStatus.Launched;
            App.Log(
                $"[ElevatedLaunch] completed status={resultStatus} " +
                $"pid={processId} tokenElevated=" +
                $"{(elevated.HasValue ? elevated.Value.ToString() : "unknown")} " +
                $"target='{request.OriginalTargetPath}' " +
                $"launcher='{request.FileName}'");
            return new ElevatedFileLaunchResult(resultStatus, processId);
        }
        catch (Win32Exception ex)
        {
            ElevatedFileLaunchStatus status = ex.NativeErrorCode == ErrorCancelled
                ? ElevatedFileLaunchStatus.Cancelled
                : ElevatedFileLaunchStatus.Failed;
            if (status != ElevatedFileLaunchStatus.Cancelled)
            {
                App.Log($"[ElevatedLaunch] runas failed: {ex.Message}");
            }

            return new ElevatedFileLaunchResult(
                status,
                ErrorCode: ex.NativeErrorCode);
        }
        catch (Exception ex)
        {
            App.Log($"[ElevatedLaunch] runas failed: {ex.Message}");
            return new ElevatedFileLaunchResult(
                ElevatedFileLaunchStatus.Failed);
        }
        finally
        {
            if (executeInfo.ProcessHandle != IntPtr.Zero)
            {
                ElevatedLaunchNativeMethods.CloseHandle(executeInfo.ProcessHandle);
            }
        }
    }

    internal static bool TryCreateLaunchRequest(
        WidgetItem item,
        out ElevatedFileLaunchRequest request)
    {
        request = default;
        if (item is null)
        {
            return false;
        }

        return TryCreateLaunchRequest(
            item.Path,
            item.TargetPath,
            out request);
    }

    internal static bool TryCreateLaunchRequest(
        string? candidatePath,
        string? targetPathHint,
        out ElevatedFileLaunchRequest request)
    {
        request = default;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string candidate = candidatePath.Trim();
        string rawShortcutTarget = string.Empty;
        bool fromShellLink = false;
        string arguments = string.Empty;
        string workingDirectory = string.Empty;
        if (ShortcutHelper.IsShellLinkPath(candidate))
        {
            fromShellLink = true;
            ShortcutInfo? metadata = ShortcutHelper.ReadStoredMetadata(candidate);
            rawShortcutTarget = !string.IsNullOrWhiteSpace(metadata?.TargetPath)
                ? metadata.TargetPath.Trim()
                : targetPathHint?.Trim() ?? string.Empty;
            candidate = rawShortcutTarget;
            arguments = metadata?.Arguments?.Trim() ?? string.Empty;
            workingDirectory = metadata?.WorkingDirectory?.Trim() ?? string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(targetPathHint) &&
                 ShortcutHelper.IsShortcutPath(candidate))
        {
            candidate = targetPathHint.Trim();
        }

        if (!TryNormalizeExistingExecutableCandidate(
                rawShortcutTarget,
                candidate,
                out string resolvedExecutable))
        {
            if (!fromShellLink)
            {
                return false;
            }

            string? effectiveShortcutTarget =
                ShortcutHelper.TryReadEffectiveTargetPath(candidatePath.Trim());
            if (!TryNormalizeExistingExecutableCandidate(
                    effectiveShortcutTarget,
                    effectiveShortcutTarget ?? string.Empty,
                    out resolvedExecutable))
            {
                return false;
            }
        }

        workingDirectory = ResolveWorkingDirectory(
            workingDirectory,
            resolvedExecutable);
        request = CreateLaunchRequest(
            resolvedExecutable,
            arguments,
            workingDirectory);
        return true;
    }

    private static bool TryNormalizeExistingExecutableCandidate(
        string? rawTarget,
        string candidate,
        out string normalizedExecutable)
    {
        normalizedExecutable = string.Empty;
        string expanded = Environment.ExpandEnvironmentVariables(candidate);
        if (expanded.Length == 0 ||
            Uri.TryCreate(expanded, UriKind.Absolute, out Uri? uri) &&
            !uri.IsFile)
        {
            return false;
        }

        try
        {
            expanded = Path.GetFullPath(expanded);
        }
        catch
        {
            return false;
        }

        if (ExecutableExtensions.Contains(Path.GetExtension(expanded)) &&
            File.Exists(expanded))
        {
            normalizedExecutable = expanded;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(rawTarget) &&
            DeskBox.Services.ShortcutTargetProbe.TryResolveProgramFilesX86Counterpart(
                rawTarget,
                expanded,
                out string x86Counterpart))
        {
            try
            {
                string fullX86 = Path.GetFullPath(x86Counterpart);
                if (ExecutableExtensions.Contains(Path.GetExtension(fullX86)) &&
                    File.Exists(fullX86))
                {
                    normalizedExecutable = fullX86;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    internal static ElevatedFileLaunchRequest CreateLaunchRequest(
        string targetPath,
        string arguments,
        string workingDirectory)
    {
        string extension = Path.GetExtension(targetPath);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            string commandInterpreter =
                Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string scriptCommand = QuoteCommandLineArgument(targetPath);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                scriptCommand += " " + arguments.Trim();
            }

            return new ElevatedFileLaunchRequest(
                commandInterpreter,
                $"/d /s /c \"{scriptCommand}\"",
                workingDirectory,
                targetPath,
                DetectExistingProcess: false);
        }

        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            string installer = Path.Combine(
                Environment.SystemDirectory,
                "msiexec.exe");
            string installerArguments =
                "/i " + QuoteCommandLineArgument(targetPath);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                installerArguments += " " + arguments.Trim();
            }

            return new ElevatedFileLaunchRequest(
                installer,
                installerArguments,
                workingDirectory,
                targetPath,
                DetectExistingProcess: false);
        }

        return new ElevatedFileLaunchRequest(
            targetPath,
            arguments.Trim(),
            workingDirectory,
            targetPath,
            DetectExistingProcess: true);
    }

    internal static string QuoteCommandLineArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string ResolveWorkingDirectory(
        string configuredWorkingDirectory,
        string targetPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredWorkingDirectory))
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(
                    configuredWorkingDirectory);
                string normalized = Path.GetFullPath(expanded);
                if (Directory.Exists(normalized))
                {
                    return normalized;
                }
            }
            catch
            {
                // Fall back to the target's containing directory.
            }
        }

        return Path.GetDirectoryName(targetPath) ?? string.Empty;
    }

    private static bool TryFindRunningUnelevatedTarget(
        string targetPath,
        out uint processId)
    {
        processId = 0;
        string processName = Path.GetFileNameWithoutExtension(targetPath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return false;
        }

        foreach (Process process in processes)
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                IntPtr processHandle = ElevatedLaunchNativeMethods.OpenProcess(
                    ProcessQueryLimitedInformation,
                    inheritHandle: false,
                    (uint)process.Id);
                if (processHandle == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (!TryGetProcessPath(processHandle, out string runningPath) ||
                        !string.Equals(
                            Path.GetFullPath(runningPath),
                            Path.GetFullPath(targetPath),
                            StringComparison.OrdinalIgnoreCase) ||
                        !TryGetTokenElevation(
                            processHandle,
                            out bool isElevated) ||
                        isElevated)
                    {
                        continue;
                    }

                    processId = (uint)process.Id;
                    return true;
                }
                catch
                {
                    // A process can exit while it is being inspected.
                }
                finally
                {
                    ElevatedLaunchNativeMethods.CloseHandle(processHandle);
                }
            }
        }

        return false;
    }

    private static unsafe bool TryGetProcessPath(
        IntPtr processHandle,
        out string processPath)
    {
        const int ErrorInsufficientBuffer = 122;
        char* stackBuffer = stackalloc char[1024];
        uint length = 1024;
        if (ElevatedLaunchNativeMethods.QueryFullProcessImageName(
                processHandle,
                flags: 0,
                stackBuffer,
                ref length) &&
            length > 0)
        {
            processPath = new string(stackBuffer, 0, (int)length);
            return true;
        }

        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            processPath = string.Empty;
            return false;
        }

        char[] rented = System.Buffers.ArrayPool<char>.Shared.Rent(32_768);
        try
        {
            fixed (char* rentedPtr = rented)
            {
                length = (uint)rented.Length;
                if (!ElevatedLaunchNativeMethods.QueryFullProcessImageName(
                        processHandle,
                        flags: 0,
                        rentedPtr,
                        ref length) ||
                    length == 0)
                {
                    processPath = string.Empty;
                    return false;
                }

                processPath = new string(rentedPtr, 0, (int)length);
                return true;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static bool TryGetTokenElevation(
        IntPtr processHandle,
        out bool isElevated)
    {
        isElevated = false;
        if (!ElevatedLaunchNativeMethods.OpenProcessToken(
                processHandle,
                TokenQuery,
                out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            int size = Marshal.SizeOf<ElevatedLaunchNativeMethods.TokenElevation>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!ElevatedLaunchNativeMethods.GetTokenInformation(
                        tokenHandle,
                        TokenElevationInformationClass,
                        buffer,
                        size,
                        out _))
                {
                    return false;
                }

                isElevated = Marshal.PtrToStructure<ElevatedLaunchNativeMethods.TokenElevation>(buffer)
                    .TokenIsElevated != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ElevatedLaunchNativeMethods.CloseHandle(tokenHandle);
        }
    }

    // Native entry points and their marshaling structures live in
    // DeskBox.Platform.ElevatedLaunchNativeMethods.
}
