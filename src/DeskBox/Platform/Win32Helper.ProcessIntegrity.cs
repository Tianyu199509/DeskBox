using System.IO;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

public static partial class Win32Helper
{
    public static bool IsExclusiveFullscreenOrPresentationActive()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 &&
                   state is QueryUserNotificationStateRunningD3dFullScreen or
                       QueryUserNotificationStatePresentationMode;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static string ResolveEffectiveOpenTargetAndDirectory(
        string path,
        out string directory,
        bool? isPackagedOverride = null)
    {
        string effectivePath = path;
        bool isPackaged = isPackagedOverride ?? DeskBox.Services.AppDistributionService.Current.IsPackaged;
        if (isPackaged &&
            !string.IsNullOrWhiteSpace(path) &&
            ShortcutTargetProbe.Classify(path) == ShortcutTargetKind.LocalFileSystem)
        {
            string unvirtualized = DeskBox.Services.DragDrop.StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
                path,
                isPackagedOverride: true);
            if (!string.IsNullOrWhiteSpace(unvirtualized) &&
                (File.Exists(unvirtualized) || Directory.Exists(unvirtualized)))
            {
                effectivePath = unvirtualized;
            }
        }

        directory = ResolveShellLaunchDirectory(effectivePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (isPackaged)
            {
                string unvirtualizedDir = DeskBox.Services.DragDrop.StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
                    directory,
                    isPackagedOverride: true);
                if (!string.IsNullOrWhiteSpace(unvirtualizedDir) && Directory.Exists(unvirtualizedDir))
                {
                    directory = unvirtualizedDir;
                }
            }

            if (!Directory.Exists(directory))
            {
                directory = string.Empty;
            }
        }

        return effectivePath;
    }

    internal static string ResolveEffectiveLocalLaunchPaths(
        string path,
        string? workingDirectory,
        out string? effectiveWorkingDirectory,
        bool? isPackagedOverride = null)
    {
        bool isPackaged = isPackagedOverride ?? DeskBox.Services.AppDistributionService.Current.IsPackaged;
        string effectivePath = path;
        if (isPackaged &&
            !string.IsNullOrWhiteSpace(path) &&
            ShortcutTargetProbe.Classify(path) == ShortcutTargetKind.LocalFileSystem)
        {
            string unvirtualized = DeskBox.Services.DragDrop.StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
                path,
                isPackagedOverride: true);
            if (!string.IsNullOrWhiteSpace(unvirtualized) &&
                (File.Exists(unvirtualized) || Directory.Exists(unvirtualized)))
            {
                effectivePath = unvirtualized;
            }
        }

        string? trimmedDir = workingDirectory?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(trimmedDir))
        {
            if (isPackaged)
            {
                string unvirtualizedDir = DeskBox.Services.DragDrop.StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(
                    trimmedDir,
                    isPackagedOverride: true);
                if (!string.IsNullOrWhiteSpace(unvirtualizedDir) && Directory.Exists(unvirtualizedDir))
                {
                    trimmedDir = unvirtualizedDir;
                }
            }

            effectiveWorkingDirectory = Directory.Exists(trimmedDir) ? trimmedDir : null;
        }
        else
        {
            effectiveWorkingDirectory = null;
        }

        return effectivePath;
    }

    private static int? s_cachedCurrentProcessIntegrityRid;

    /// <summary>
    /// True when the active foreground window belongs to a process at a higher
    /// mandatory integrity level (or protected/inaccessible token) than DeskBox,
    /// where UIPI legitimately blocks low-level hooks and SendInput canaries.
    /// </summary>
    public static bool IsForegroundHigherIntegrityThanCurrentProcess()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return false;
            }

            _ = DragDropPermissionNativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                return false;
            }

            int selfRid = s_cachedCurrentProcessIntegrityRid ??=
                TryReadProcessIntegrityRid(DragDropPermissionNativeMethods.GetCurrentProcess(), out int currentRid)
                    ? currentRid
                    : 0x2000; // SECURITY_MANDATORY_MEDIUM_RID

            const uint ProcessQueryLimitedInformation = 0x1000;
            IntPtr hProcess = DragDropPermissionNativeMethods.OpenProcess(
                ProcessQueryLimitedInformation,
                inheritHandle: false,
                pid);
            if (hProcess == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == 5;
            }

            try
            {
                if (!TryReadProcessIntegrityRid(hProcess, out int targetRid))
                {
                    return Marshal.GetLastWin32Error() == 5;
                }

                return targetRid > selfRid;
            }
            finally
            {
                _ = DragDropPermissionNativeMethods.CloseHandle(hProcess);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadProcessIntegrityRid(IntPtr processHandle, out int integrityRid)
    {
        integrityRid = 0;
        const uint TokenQuery = 0x0008;
        if (!DragDropPermissionNativeMethods.OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            _ = DragDropPermissionNativeMethods.GetTokenInformation(
                tokenHandle,
                DragDropPermissionNativeMethods.TokenInformationClass.TokenIntegrityLevel,
                IntPtr.Zero,
                0,
                out int length);
            if (length <= 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!DragDropPermissionNativeMethods.GetTokenInformation(
                        tokenHandle,
                        DragDropPermissionNativeMethods.TokenInformationClass.TokenIntegrityLevel,
                        buffer,
                        length,
                        out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<DragDropPermissionNativeMethods.TokenMandatoryLabel>(buffer);
                IntPtr subAuthorityCountPtr = DragDropPermissionNativeMethods.GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCountPtr == IntPtr.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(subAuthorityCountPtr);
                if (count == 0)
                {
                    return false;
                }

                IntPtr ridPtr = DragDropPermissionNativeMethods.GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                integrityRid = Marshal.ReadInt32(ridPtr);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = DragDropPermissionNativeMethods.CloseHandle(tokenHandle);
        }
    }
}
