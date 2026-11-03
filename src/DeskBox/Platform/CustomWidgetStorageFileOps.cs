using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskBox.Platform;

/// <summary>
/// App-data file and console primitives for isolated Custom Widget directories
/// and the headless Agent CLI/MCP entry point.
/// </summary>
internal static partial class CustomWidgetStorageFileOps
{
    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    private const int StdOutputHandle = -11;

    internal static void CopyFileWithoutOverwrite(string sourcePath, string destinationPath) =>
        File.Copy(sourcePath, destinationPath, overwrite: false);

    internal static void CopyFileOverwrite(string sourcePath, string destinationPath) =>
        File.Copy(sourcePath, destinationPath, overwrite: true);

    internal static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    internal static void DeleteDirectoryRecursive(string directoryPath) =>
        Directory.Delete(directoryPath, recursive: true);

    internal static void DeleteEmptyDirectoryIfExists(string directoryPath)
    {
        if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            Directory.Delete(directoryPath, recursive: false);
        }
    }

    internal static void MoveDirectory(string sourceDirectoryPath, string destinationDirectoryPath) =>
        Directory.Move(sourceDirectoryPath, destinationDirectoryPath);

    internal static void MoveFileOverwrite(string sourceFilePath, string destinationFilePath) =>
        File.Move(sourceFilePath, destinationFilePath, overwrite: true);

    internal static bool ShouldSkipPerUserProtocolRegistration(
        string executablePath,
        string? machineRegisteredCommand)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return true;
        }

        // Never allow repository Debug/Release build outputs to shadow an installed
        // DeskBox build under HKCU\Software\Classes\deskbox.
        string normalized = executablePath.Replace('/', '\\');
        if (normalized.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // When HKLM\Software\Classes\deskbox\shell\open\command is already registered by the
        // installer, skip writing HKCU so per-user overrides do not outlive or shadow upgrades.
        if (!string.IsNullOrWhiteSpace(machineRegisteredCommand))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Idempotently registers the per-user <c>deskbox://</c> URI protocol handler under
    /// <c>HKCU\Software\Classes\deskbox</c> for unpackaged installations when not already
    /// registered at machine scope or pointing to the current executable.
    /// </summary>
    internal static void TryEnsureDeskBoxProtocolRegistered(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        try
        {
            string? machineCmd = null;
            try
            {
                using RegistryKey? machineKey = Registry.LocalMachine.OpenSubKey(
                    @"Software\Classes\deskbox\shell\open\command",
                    writable: false);
                machineCmd = machineKey?.GetValue(null) as string;
            }
            catch
            {
            }

            if (ShouldSkipPerUserProtocolRegistration(executablePath, machineCmd))
            {
                return;
            }

            string expectedCommand = $"\"{executablePath}\" \"%1\"";
            using (RegistryKey? existingCmd = Registry.CurrentUser.OpenSubKey(
                       @"Software\Classes\deskbox\shell\open\command",
                       writable: false))
            {
                if (existingCmd?.GetValue(null) is string currentCmd &&
                    string.Equals(currentCmd, expectedCommand, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using RegistryKey rootKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\deskbox");
            rootKey.SetValue(null, "URL:DeskBox Protocol");
            rootKey.SetValue("URL Protocol", string.Empty);

            using (RegistryKey iconKey = rootKey.CreateSubKey("DefaultIcon"))
            {
                iconKey.SetValue(null, $"\"{executablePath}\",0");
            }

            using RegistryKey commandKey = rootKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(null, expectedCommand);
        }
        catch
        {
            // Restricted environments proceed cleanly without per-user protocol registration.
        }
    }

    internal static void ReplaceJsonFileAtomically(string tempFilePath, string destinationFilePath)
    {
        if (!destinationFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to replace a non-JSON custom widget data path.");
        }

        File.Move(tempFilePath, destinationFilePath, overwrite: true);
    }

    internal static void ReplaceTextFileAtomically(string destinationFilePath, string content)
    {
        string? directory = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempFilePath = $"{destinationFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempFilePath, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempFilePath, destinationFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Attaches to the parent console when invoked from an interactive terminal
    /// without redirected stdout so CLI output is visible while keeping
    /// <c>WinExe</c> subsystem behavior (zero console window flash on GUI/MCP pipe runs).
    /// </summary>
    internal static void TryEnsureCliConsoleAttached()
    {
        try
        {
            IntPtr stdOut = GetStdHandle(StdOutputHandle);
            if (stdOut == IntPtr.Zero || stdOut == new IntPtr(-1))
            {
                _ = AttachConsole(AttachParentProcess);
            }
        }
        catch
        {
            // Redirected pipes or headless hosts proceed without a console attachment.
        }
    }
}
