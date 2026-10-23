using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("DeskBox.Tests")]

namespace DeskBox.Updater;

internal enum DirectInstallScope
{
    CurrentUser,
    AllUsers
}

internal static class Program
{
    private const int InstallRegistrationMismatchExitCode = 20;
    private const string UpdateInstallResultArgument = "--update-install-result";

    // Canonical names from the [Languages] sections of installer/DeskBox.iss
    // and DeskBox.arm64.iss (both list English first). The app maps its
    // effective UI culture to one of these; anything else is dropped here so
    // a stale or malformed --installer-lang value can never reach the
    // installer command line.
    internal static readonly FrozenSet<string> KnownInstallerLanguages = new[]
    {
        "english",
        "chinesesimplified",
        "chinesetraditional",
        "japanese",
        "german",
        "brazilianportuguese",
        "hindi",
        "spanish",
        "french",
        "arabic",
        "bengali",
        "russian",
        "korean",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private const string InstallStateRegistryPath = @"Software\DeskBox\DirectInstall";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1";
    private const string WowUninstallRegistryPath =
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1";
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "DeskBox.Updater.log");

    private static int Main(string[] args)
    {
        UpdateOptions? options = null;
        try
        {
            options = UpdateOptions.Parse(args);
            if (options.RestartOnly)
            {
                if (string.IsNullOrWhiteSpace(options.AppPath) || !File.Exists(options.AppPath))
                {
                    Log("App executable not found for restart.");
                    return 2;
                }

                WaitForParentExit(options.ParentProcessId);
                return RestartApp(options.AppPath) ? 0 : 3;
            }

            // Wait for the parent to exit BEFORE touching the installer or
            // deciding anything: restarting the app while the old instance
            // still holds the single-instance mutex makes the new process a
            // bare secondary that drops this result argument and exits,
            // leaving the desktop without an app until the next manual start.
            WaitForParentExit(options.ParentProcessId);

            if (string.IsNullOrWhiteSpace(options.InstallerPath) || !File.Exists(options.InstallerPath))
            {
                Log("Installer not found.");
                RestartAfterIncompleteUpdate(options, "failed");
                return 2;
            }

            int exitCode = RunInstaller(options);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(options.AppPath) && File.Exists(options.AppPath))
            {
                _ = RestartApp(options.AppPath);
            }
            else if (exitCode != 0)
            {
                RestartAfterIncompleteUpdate(options, GetIncompleteUpdateOutcome(exitCode));
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
            if (options is not null)
            {
                RestartAfterIncompleteUpdate(options, "failed");
            }

            return 1;
        }
    }

    private static void WaitForParentExit(int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            return;
        }

        try
        {
            using var parentProcess = Process.GetProcessById(parentProcessId);
            Log($"Waiting for DeskBox process {parentProcessId} to exit.");
            parentProcess.WaitForExit(milliseconds: 90_000);
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            Log($"Wait parent failed: {ex.Message}");
        }
    }

    private static int RunInstaller(UpdateOptions options)
    {
        if (!TryGetInstallDirectory(options, out string installDirectory))
        {
            Log("Current installation directory is missing or invalid; refusing to run a silent update without /DIR.");
            return 4;
        }

        DirectInstallScope installScope = ResolveInstallScope(installDirectory);
        string installerLanguage = ResolveInstallerLanguage(options.InstallerLanguage);
        if (!string.IsNullOrWhiteSpace(options.InstallerLanguage) && installerLanguage.Length == 0)
        {
            Log($"Ignoring unrecognized installer language '{options.InstallerLanguage}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.InstallerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(options.InstallerPath) ?? Environment.CurrentDirectory
        };

        // Inno Setup otherwise falls back to DefaultDirName during a silent
        // install. Always pin an in-app update to the directory containing
        // the currently running DeskBox.exe.
        foreach (string argument in BuildInstallerArguments(
                     installDirectory,
                     options.Silent,
                     installScope,
                     installerLanguage))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Inno's own log is the only place a failed silent setup explains
        // itself; keep one per version under the updates root.
        string? installerDirectory = Path.GetDirectoryName(options.InstallerPath);
        string? updatesRoot = Path.GetDirectoryName(installerDirectory);
        if (options.Silent &&
            !string.IsNullOrWhiteSpace(updatesRoot) &&
            !string.IsNullOrWhiteSpace(installerDirectory))
        {
            startInfo.ArgumentList.Add(
                $"/LOG={Path.Combine(updatesRoot, $"installer-{Path.GetFileName(installerDirectory)}.log")}");
        }

        Log(
            $"Starting installer: {options.InstallerPath}; " +
            $"target directory: {installDirectory}; scope: {installScope}; " +
            $"language: {(installerLanguage.Length == 0 ? "<installer default>" : installerLanguage)}");
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Log("Installer process did not start.");
            return 3;
        }

        // A wedged installer (stuck custom setup code, full disk) must not
        // strand the updater forever: the app is already gone, so past the
        // generous ceiling restart with the failure outcome instead.
        const int InstallerTimeoutMilliseconds = 30 * 60 * 1000;
        if (!process.WaitForExit(InstallerTimeoutMilliseconds))
        {
            try
            {
                process.Kill();
            }
            catch (Exception killException)
            {
                Log($"Failed to kill the timed-out installer: {killException.Message}");
            }

            Log($"Installer timed out after {InstallerTimeoutMilliseconds / 60_000} minutes.");
            return 5;
        }
        Log($"Installer exited with code {process.ExitCode}.");
        if (process.ExitCode != 0)
        {
            return process.ExitCode;
        }

        if (!IsInstallRegistrationConsistent(installDirectory, installScope))
        {
            Log($"Installer reported success, but the registered installation path does not match {installDirectory}.");
            return InstallRegistrationMismatchExitCode;
        }

        return 0;
    }

    internal static IReadOnlyList<string> BuildInstallerArguments(
        string installDirectory,
        bool silent,
        DirectInstallScope installScope,
        string installerLanguage = "")
    {
        var arguments = new List<string>
        {
            $"/DIR={installDirectory}",
            installScope == DirectInstallScope.AllUsers
                ? "/ALLUSERS"
                : "/CURRENTUSER"
        };

        if (silent)
        {
            // Keep Inno's progress window visible while hiding the wizard.
            // Error messages remain enabled so a blocked update is actionable.
            arguments.Add("/SILENT");
            arguments.Add("/SP-");
            arguments.Add("/NORESTART");
            arguments.Add("/FORCECLOSEAPPLICATIONS");
            // /LANG is deliberately silent-only: a matching /LANG suppresses
            // Inno's Select Language dialog even in interactive runs, which
            // would take the language choice away from a user running Setup
            // manually. In a silent run it pins the language to what the app
            // is currently showing and overrides any stale
            // "Inno Setup: Language" value left by an old install.
            if (!string.IsNullOrWhiteSpace(installerLanguage))
            {
                arguments.Add($"/LANG={installerLanguage}");
            }
        }

        return arguments;
    }

    internal static string ResolveInstallerLanguage(string? value) =>
        !string.IsNullOrWhiteSpace(value) && KnownInstallerLanguages.Contains(value)
            ? value.ToLowerInvariant()
            : string.Empty;

    internal static DirectInstallScope ResolveInstallScope(string installDirectory)
    {
        if (RegisteredLocationMatches(Registry.LocalMachine, InstallStateRegistryPath, installDirectory) ||
            RegisteredLocationMatches(Registry.LocalMachine, UninstallRegistryPath, installDirectory) ||
            RegisteredLocationMatches(Registry.LocalMachine, WowUninstallRegistryPath, installDirectory))
        {
            return DirectInstallScope.AllUsers;
        }

        if (RegisteredLocationMatches(Registry.CurrentUser, InstallStateRegistryPath, installDirectory) ||
            RegisteredLocationMatches(Registry.CurrentUser, UninstallRegistryPath, installDirectory) ||
            RegisteredLocationMatches(Registry.CurrentUser, WowUninstallRegistryPath, installDirectory))
        {
            return DirectInstallScope.CurrentUser;
        }

        // Compatibility fallback for older machine-wide builds that predate the
        // explicit InstallScope registration.
        if (IsDirectoryWithinKnownFolder(
                installDirectory,
                Environment.SpecialFolder.ProgramFiles) ||
            IsDirectoryWithinKnownFolder(
                installDirectory,
                Environment.SpecialFolder.ProgramFilesX86))
        {
            return DirectInstallScope.AllUsers;
        }

        return DirectInstallScope.CurrentUser;
    }

    private static bool IsInstallRegistrationConsistent(
        string expectedInstallDirectory,
        DirectInstallScope installScope)
    {
        try
        {
            RegistryKey root = installScope == DirectInstallScope.AllUsers
                ? Registry.LocalMachine
                : Registry.CurrentUser;
            using RegistryKey? key = root.OpenSubKey(InstallStateRegistryPath, writable: false);
            string? registeredPath = key?.GetValue("InstallLocation") as string;
            string? registeredScope = key?.GetValue("InstallScope") as string;
            if (string.IsNullOrWhiteSpace(registeredPath))
            {
                return false;
            }

            string expectedScope = installScope == DirectInstallScope.AllUsers
                ? "all-users"
                : "current-user";
            return PathsEqual(registeredPath, expectedInstallDirectory) &&
                   string.Equals(
                       registeredScope?.Trim(),
                       expectedScope,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log($"Install registration verification failed: {ex.Message}");
            return false;
        }
    }

    private static bool RegisteredLocationMatches(
        RegistryKey root,
        string registryPath,
        string expectedInstallDirectory)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(registryPath, writable: false);
            return key?.GetValue("InstallLocation") is string registeredPath &&
                   PathsEqual(registeredPath, expectedInstallDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsDirectoryWithinKnownFolder(
        string directory,
        Environment.SpecialFolder specialFolder)
    {
        string knownFolder = Environment.GetFolderPath(specialFolder);
        if (string.IsNullOrWhiteSpace(knownFolder))
        {
            return false;
        }

        try
        {
            string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string normalizedKnownFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(knownFolder));
            return string.Equals(
                       normalizedDirectory,
                       normalizedKnownFolder,
                       StringComparison.OrdinalIgnoreCase) ||
                   normalizedDirectory.StartsWith(
                       normalizedKnownFolder + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryGetInstallDirectory(UpdateOptions options, out string installDirectory)
    {
        installDirectory = string.Empty;
        string candidate = options.InstallDirectory;
        if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(options.AppPath))
        {
            candidate = Path.GetDirectoryName(options.AppPath) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            string normalizedDirectory = Path.GetFullPath(candidate);
            string expectedAppPath = Path.Combine(normalizedDirectory, "DeskBox.exe");
            if (!Directory.Exists(normalizedDirectory) || !File.Exists(expectedAppPath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(options.AppPath) &&
                !string.Equals(
                    Path.GetFullPath(options.AppPath),
                    expectedAppPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            installDirectory = normalizedDirectory;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void RestartAfterIncompleteUpdate(UpdateOptions options, string outcome)
    {
        if (string.IsNullOrWhiteSpace(options.AppPath) || !File.Exists(options.AppPath))
        {
            Log($"Cannot restart DeskBox after incomplete update ({outcome}); app executable is missing.");
            return;
        }

        _ = RestartApp(options.AppPath, outcome);
    }

    internal static string GetIncompleteUpdateOutcome(int exitCode)
    {
        if (exitCode is 2 or 5)
        {
            return "cancelled";
        }

        return exitCode == InstallRegistrationMismatchExitCode
            ? "path-mismatch"
            : "failed";
    }

    private static bool RestartApp(string appPath, string? updateInstallOutcome = null)
    {
        // An elevated updater (it inherits the app's token, and app-update
        // flows on fully elevated desktops run elevated) must not hand that
        // elevation to the restarted app: a post-update DeskBox above the
        // shell loses drag/drop and triggers the elevation notice on every
        // update. But the hand-down is only safe when the shell itself
        // offers a standard-user context — on a fully elevated desktop
        // matching the shell is what keeps tray and drag working. That
        // guard, and the failure fallback to the historical inherited
        // restart, live in ShellUnelevatedLaunch.
        if (DeskBox.UpdaterPlatform.ShellUnelevatedLaunch.ShouldRestartAtShellIntegrity())
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (DeskBox.UpdaterPlatform.ShellUnelevatedLaunch.TryStartAtShellIntegrity(
                        appPath,
                        BuildRestartArguments(updateInstallOutcome),
                        Log))
                {
                    Log($"Restarting app at shell integrity: {appPath} (attempt {attempt})");
                    return true;
                }

                if (attempt < 3)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }
            }

            Log("Shell-integrity restart attempts failed; falling back to inherited restart.");
            // If the fallback lands elevated under a Medium shell, the
            // app-side launch gate surfaces its one-click fix notice.
        }

        // A direct start can fail transiently (the installer tail still
        // holding a handle, an on-access scanner touching the fresh
        // executable), so retry before falling back to the Start Menu
        // shortcut, which resolves through a different shell path. If
        // everything fails the user must launch manually; the ERROR tag
        // keeps that outcome searchable in the log.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (TryStartApp(appPath, Path.GetDirectoryName(appPath), updateInstallOutcome))
            {
                Log($"Restarting app: {appPath} (attempt {attempt})");
                return true;
            }

            if (attempt < 3)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }

        return TryRestartViaStartMenuShortcut(appPath, updateInstallOutcome);
    }

    private static string? BuildRestartArguments(string? updateInstallOutcome)
    {
        return string.IsNullOrWhiteSpace(updateInstallOutcome)
            ? null
            : $"{UpdateInstallResultArgument} {updateInstallOutcome.Trim()}";
    }

    private static bool TryStartApp(
        string targetPath,
        string? workingDirectory,
        string? updateInstallOutcome)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };
            if (!string.IsNullOrWhiteSpace(updateInstallOutcome))
            {
                startInfo.ArgumentList.Add(UpdateInstallResultArgument);
                startInfo.ArgumentList.Add(updateInstallOutcome);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Starting '{targetPath}' failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryRestartViaStartMenuShortcut(string appPath, string? updateInstallOutcome)
    {
        // {autoprograms} resolves per install scope: the user's Programs
        // folder for current-user installs, the common one for all-users
        // installs (see the [Icons] section in installer/DeskBox.iss). Try
        // both locations; the one Setup wrote is the one that exists.
        string? userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        string? commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        foreach (string menuRoot in new[] { userPrograms, commonPrograms })
        {
            if (string.IsNullOrWhiteSpace(menuRoot))
            {
                continue;
            }

            string shortcutPath = Path.Combine(menuRoot, "Programs", "DeskBox.lnk");
            if (!File.Exists(shortcutPath))
            {
                continue;
            }

            if (TryStartApp(shortcutPath, workingDirectory: null, updateInstallOutcome))
            {
                Log($"Restarted app via Start Menu shortcut: {shortcutPath}");
                return true;
            }
        }

        Log($"[ERROR] Could not restart DeskBox after update; user launch required. Path: {appPath}");
        return false;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed class UpdateOptions
    {
        public int ParentProcessId { get; private init; }
        public string InstallerPath { get; private init; } = string.Empty;
        public string AppPath { get; private init; } = string.Empty;
        public string InstallDirectory { get; private init; } = string.Empty;
        public string InstallerLanguage { get; private init; } = string.Empty;
        public bool Silent { get; private init; }
        public bool RestartOnly { get; private init; }

        public static UpdateOptions Parse(IReadOnlyList<string> args)
        {
            int parentProcessId = 0;
            string installerPath = string.Empty;
            string appPath = string.Empty;
            string installDirectory = string.Empty;
            string installerLanguage = string.Empty;
            bool silent = false;
            bool restartOnly = false;

            for (int index = 0; index < args.Count; index++)
            {
                string arg = args[index];
                if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase))
                {
                    silent = true;
                    continue;
                }

                if (string.Equals(arg, "--restart-only", StringComparison.OrdinalIgnoreCase))
                {
                    restartOnly = true;
                    continue;
                }

                if (index + 1 >= args.Count)
                {
                    continue;
                }

                string value = args[++index];
                if (string.Equals(arg, "--pid", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid))
                {
                    parentProcessId = parsedPid;
                }
                else if (string.Equals(arg, "--installer", StringComparison.OrdinalIgnoreCase))
                {
                    installerPath = value;
                }
                else if (string.Equals(arg, "--app", StringComparison.OrdinalIgnoreCase))
                {
                    appPath = value;
                }
                else if (string.Equals(arg, "--install-dir", StringComparison.OrdinalIgnoreCase))
                {
                    installDirectory = value;
                }
                else if (string.Equals(arg, "--installer-lang", StringComparison.OrdinalIgnoreCase))
                {
                    installerLanguage = value;
                }
            }

            return new UpdateOptions
            {
                ParentProcessId = parentProcessId,
                InstallerPath = installerPath,
                AppPath = appPath,
                InstallDirectory = installDirectory,
                InstallerLanguage = installerLanguage,
                Silent = silent,
                RestartOnly = restartOnly
            };
        }
    }
}
