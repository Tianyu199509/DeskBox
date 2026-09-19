using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskBox.Platform;
using Microsoft.Win32;

namespace DeskBox.Services;

internal sealed record EverythingInstallationSnapshot(
    string? ExecutablePath,
    string? Version,
    bool IsRunning,
    bool HasElevatedProcess,
    bool HasUnelevatedProcess,
    bool IsCurrentProcessElevated,
    bool ConfiguredToRunAsAdministrator,
    bool ServiceInstalled,
    bool UsesManualPath);

/// <summary>Finds installed, portable, and already-running Everything copies without IPC.</summary>
internal static class EverythingInstallationDetector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;

    internal static EverythingInstallationSnapshot Detect(string? configuredPath)
    {
        var candidates = new List<(string Path, bool Manual)>();
        AddCandidate(candidates, configuredPath, manual: true);

        bool configuredRunAsAdministrator = false;
        bool serviceInstalled = false;
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadRegistryCandidates(
                    hive,
                    view,
                    candidates,
                    ref configuredRunAsAdministrator,
                    ref serviceInstalled);
            }
        }

        AddCandidate(
            candidates,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Everything",
                "Everything.exe"),
            manual: false);
        AddCandidate(
            candidates,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Everything",
                "Everything.exe"),
            manual: false);
        AddCandidate(
            candidates,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Everything",
                "Everything.exe"),
            manual: false);

        bool hasElevatedProcess = false;
        bool hasUnelevatedProcess = false;
        bool isRunning = false;
        foreach (Process process in Process.GetProcessesByName("Everything"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    isRunning = true;
                    string? processPath = TryGetProcessPath(process);
                    AddCandidate(candidates, processPath, manual: false);
                    if (TryGetProcessElevation(process.Id, out bool elevated))
                    {
                        hasElevatedProcess |= elevated;
                        hasUnelevatedProcess |= !elevated;
                    }
                }
                catch
                {
                    // A protected/elevated Everything process still counts as running.
                    isRunning = true;
                }
            }
        }

        (string Path, bool Manual)? selected = null;
        foreach ((string path, bool manual) in candidates)
        {
            if (File.Exists(path) &&
                string.Equals(
                    Path.GetFileName(path),
                    "Everything.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                selected = (path, manual);
                break;
            }
        }
        string? executablePath = selected?.Path;
        string? version = null;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                version = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
            }
            catch
            {
                // Version is helpful but not required for a valid executable path.
            }
        }

        bool currentElevated = TryGetProcessElevation(Environment.ProcessId, out bool elevatedCurrent) &&
                               elevatedCurrent;
        return new EverythingInstallationSnapshot(
            executablePath,
            version,
            isRunning,
            hasElevatedProcess,
            hasUnelevatedProcess,
            currentElevated,
            configuredRunAsAdministrator,
            serviceInstalled,
            selected?.Manual == true);
    }

    internal static bool IsValidExecutablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        string.Equals(Path.GetFileName(path), "Everything.exe", StringComparison.OrdinalIgnoreCase);

    private static void ReadRegistryCandidates(
        RegistryHive hive,
        RegistryView view,
        List<(string Path, bool Manual)> candidates,
        ref bool configuredRunAsAdministrator,
        ref bool serviceInstalled)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using (RegistryKey? uninstall = baseKey.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Everything"))
            {
                AddCandidate(
                    candidates,
                    ParseDisplayIcon(uninstall?.GetValue("DisplayIcon") as string),
                    manual: false);
                if (uninstall?.GetValue("InstallLocation") is string installLocation)
                {
                    AddCandidate(
                        candidates,
                        Path.Combine(installLocation, "Everything.exe"),
                        manual: false);
                }
            }

            using RegistryKey? product = baseKey.OpenSubKey(@"SOFTWARE\voidtools\Everything");
            if (product?.GetValue("InstallLocation") is string productLocation)
            {
                AddCandidate(
                    candidates,
                    Path.Combine(productLocation, "Everything.exe"),
                    manual: false);
            }

            configuredRunAsAdministrator |= ReadRegistryBoolean(product, "InstallRunAsAdmin");
            serviceInstalled |= ReadRegistryBoolean(product, "InstallService");
        }
        catch
        {
            // Missing or inaccessible registry views are normal for portable copies.
        }
    }

    private static bool ReadRegistryBoolean(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        return value switch
        {
            int number => number != 0,
            long number => number != 0,
            string text => text.Trim() is "1" or "true" or "True" or "TRUE",
            _ => false
        };
    }

    private static string? ParseDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        int comma = trimmed.LastIndexOf(',');
        if (comma > 0 && int.TryParse(trimmed[(comma + 1)..], out _))
        {
            trimmed = trimmed[..comma];
        }

        return trimmed.Trim().Trim('"');
    }

    private static void AddCandidate(
        List<(string Path, bool Manual)> candidates,
        string? path,
        bool manual)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch
        {
            return;
        }

        if (candidates.Any(candidate =>
                string.Equals(candidate.Path, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add((normalized, manual));
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetProcessElevation(int processId, out bool elevated)
    {
        elevated = false;
        nint processHandle = Kernel32NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == 0)
        {
            return false;
        }

        try
        {
            if (!AdvApi32NativeMethods.OpenProcessToken(
                    processHandle,
                    TokenQuery,
                    out nint tokenHandle))
            {
                return false;
            }

            try
            {
                if (!AdvApi32NativeMethods.GetTokenInformation(
                        tokenHandle,
                        TokenElevationInformationClass,
                        out AdvApi32NativeMethods.TokenElevation elevation,
                        Marshal.SizeOf<AdvApi32NativeMethods.TokenElevation>(),
                        out _))
                {
                    return false;
                }

                elevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                _ = Kernel32NativeMethods.CloseHandle(tokenHandle);
            }
        }
        finally
        {
            _ = Kernel32NativeMethods.CloseHandle(processHandle);
        }
    }
}
