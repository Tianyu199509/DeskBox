using System.Diagnostics;
using System.Globalization;

namespace DeskBox.Updater;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "DeskBox.Updater.log");

    private static int Main(string[] args)
    {
        try
        {
            var options = UpdateOptions.Parse(args);
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

            if (string.IsNullOrWhiteSpace(options.InstallerPath) || !File.Exists(options.InstallerPath))
            {
                Log("Installer not found.");
                return 2;
            }

            WaitForParentExit(options.ParentProcessId);

            int exitCode = RunInstaller(options);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(options.AppPath) && File.Exists(options.AppPath))
            {
                _ = RestartApp(options.AppPath);
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex}");
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
        var startInfo = new ProcessStartInfo
        {
            FileName = options.InstallerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(options.InstallerPath) ?? Environment.CurrentDirectory
        };

        if (options.Silent)
        {
            startInfo.ArgumentList.Add("/VERYSILENT");
            startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            startInfo.ArgumentList.Add("/NORESTART");
            startInfo.ArgumentList.Add("/FORCECLOSEAPPLICATIONS");
        }

        Log($"Starting installer: {options.InstallerPath}");
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Log("Installer process did not start.");
            return 3;
        }

        process.WaitForExit();
        Log($"Installer exited with code {process.ExitCode}.");
        return process.ExitCode;
    }

    private static bool RestartApp(string appPath)
    {
        try
        {
            Log($"Restarting app: {appPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            Log($"Restart failed: {ex.Message}");
            return false;
        }
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
        public bool Silent { get; private init; }
        public bool RestartOnly { get; private init; }

        public static UpdateOptions Parse(IReadOnlyList<string> args)
        {
            int parentProcessId = 0;
            string installerPath = string.Empty;
            string appPath = string.Empty;
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
            }

            return new UpdateOptions
            {
                ParentProcessId = parentProcessId,
                InstallerPath = installerPath,
                AppPath = appPath,
                Silent = silent,
                RestartOnly = restartOnly
            };
        }
    }
}
