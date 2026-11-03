namespace DeskBox.Services;

/// <summary>
/// Resolves DeskBox's canonical main host executable (<c>DeskBox.exe</c>) even when
/// code runs inside a companion executable such as <c>DeskBox.Settings.exe</c> or
/// <c>DeskBox.Updater.exe</c>.
/// </summary>
public static class AppExecutablePath
{
    public const string MainExecutableFileName = "DeskBox.exe";
    public const string SettingsCompanionFileName = "DeskBox.Settings.exe";
    public const string UpdaterCompanionFileName = "DeskBox.Updater.exe";

    public static bool IsCompanionExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string fileName = Path.GetFileName(executablePath.Trim().Trim('"'));
        return string.Equals(fileName, SettingsCompanionFileName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, UpdaterCompanionFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveMainExecutablePath(
        string? processPathOverride = null,
        string? baseDirectoryOverride = null,
        Func<string, bool>? fileExists = null)
    {
        Func<string, bool> exists = fileExists ?? File.Exists;
        string? currentPath = !string.IsNullOrWhiteSpace(processPathOverride)
            ? processPathOverride.Trim()
            : Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (!IsCompanionExecutablePath(currentPath))
            {
                return currentPath;
            }

            string? directory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                string siblingMain = Path.Combine(directory, MainExecutableFileName);
                if (exists(siblingMain))
                {
                    return siblingMain;
                }
            }
        }

        string baseDirectory = !string.IsNullOrWhiteSpace(baseDirectoryOverride)
            ? baseDirectoryOverride.Trim()
            : AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            string baseMain = Path.Combine(baseDirectory, MainExecutableFileName);
            if (exists(baseMain))
            {
                return baseMain;
            }
        }

        return currentPath;
    }

    internal static bool IsSameInstallCompanionExecutable(
        string? candidateExecutablePath,
        string mainExecutablePath)
    {
        if (!IsCompanionExecutablePath(candidateExecutablePath) ||
            string.IsNullOrWhiteSpace(mainExecutablePath))
        {
            return false;
        }

        try
        {
            string? candidateDir = Path.GetDirectoryName(Path.GetFullPath(candidateExecutablePath!.Trim().Trim('"')));
            string? mainDir = Path.GetDirectoryName(Path.GetFullPath(mainExecutablePath.Trim().Trim('"')));
            return DirectStartupTaskBackend.PathsEqual(candidateDir, mainDir);
        }
        catch
        {
            return false;
        }
    }
}
