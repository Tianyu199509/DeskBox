using System.Diagnostics;

namespace DeskBox.Tests;

public sealed class FullInstallerUpgradeContractTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.FullInstallerUpgrade.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ManifestCleanup_RemovesOnlyFilesOwnedByThePreviousPayload()
    {
        string installRoot = CreateInstallRoot("manifest-upgrade");
        string currentManifest = Path.Combine(_tempRoot, "current-manifest.txt");
        string previousManifest = Path.Combine(installRoot, "DeskBox.InstallManifest.txt");
        string outsideFile = WriteFile(Path.Combine(_tempRoot, "outside.txt"));
        string keepFile = WriteFile(Path.Combine(installRoot, "keep.dll"));
        string staleFile = WriteFile(Path.Combine(installRoot, "stale.dll"));
        string userFile = WriteFile(Path.Combine(installRoot, "user-note.txt"));

        File.WriteAllLines(
            currentManifest,
            ["DeskBox.exe", "DeskBox.InstallManifest.txt", "KEEP.dll"]);
        File.WriteAllLines(
            previousManifest,
            [
                "DeskBox.exe",
                "DeskBox.InstallManifest.txt",
                "keep.dll",
                "stale.dll",
                "../outside.txt"
            ]);

        CleanupResult result = await RunCleanupAsync(
            installRoot,
            currentManifest,
            previousManifest);

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(File.Exists(keepFile));
        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(outsideFile));
        Assert.Contains(
            "using the previous manifest",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyCleanup_UsesExactAllowlistAndPreservesCurrentOrUnknownFiles()
    {
        string installRoot = CreateInstallRoot("legacy-upgrade");
        string currentManifest = Path.Combine(_tempRoot, "legacy-current-manifest.txt");
        string currentRuntimeFile = WriteFile(Path.Combine(installRoot, "Microsoft.UI.Input.dll"));
        string staleDotNetRuntimeFile = WriteFile(Path.Combine(installRoot, "coreclr.dll"));
        string staleLegacyDependencyFile = WriteFile(Path.Combine(installRoot, "Microsoft.WinUI.dll"));
        string unknownMicrosoftFile = WriteFile(Path.Combine(installRoot, "Microsoft.PersonalPlugin.dll"));
        string userFile = WriteFile(Path.Combine(installRoot, "user-note.txt"));

        File.WriteAllLines(
            currentManifest,
            [
                "DeskBox.exe",
                "DeskBox.InstallManifest.txt",
                "Microsoft.UI.Input.dll"
            ]);

        CleanupResult result = await RunCleanupAsync(
            installRoot,
            currentManifest,
            Path.Combine(installRoot, "missing-previous-manifest.txt"));

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.True(File.Exists(currentRuntimeFile));
        Assert.False(File.Exists(staleDotNetRuntimeFile));
        Assert.False(File.Exists(staleLegacyDependencyFile));
        Assert.True(File.Exists(unknownMicrosoftFile));
        Assert.True(File.Exists(userFile));
        Assert.Contains(
            "exact compatibility manifest",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCurrentManifest_FailsBeforeDeletingLegacyFiles()
    {
        string installRoot = CreateInstallRoot("invalid-current");
        string currentManifest = Path.Combine(_tempRoot, "invalid-current-manifest.txt");
        string staleRuntimeFile = WriteFile(Path.Combine(installRoot, "coreclr.dll"));

        File.WriteAllLines(currentManifest, ["DeskBox.exe"]);

        CleanupResult result = await RunCleanupAsync(
            installRoot,
            currentManifest,
            Path.Combine(installRoot, "missing-previous-manifest.txt"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(staleRuntimeFile));
        Assert.Contains(
            "DeskBox.InstallManifest.txt",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private string CreateInstallRoot(string name)
    {
        string installRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, name)).FullName;
        WriteFile(Path.Combine(installRoot, "DeskBox.exe"));
        return installRoot;
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    private static async Task<CleanupResult> RunCleanupAsync(
        string installRoot,
        string currentManifest,
        string previousManifest)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
                 {
                     "-NoLogo",
                     "-NoProfile",
                     "-NonInteractive",
                     "-ExecutionPolicy",
                     "Bypass",
                     "-File",
                     TestPaths.FromRepository("scripts/cleanup-deskbox-install.ps1"),
                     "-InstallRoot",
                     installRoot,
                     "-CurrentManifestPath",
                     currentManifest,
                     "-LegacyManifestPath",
                     TestPaths.FromRepository("installer/DeskBox.LegacyBundledRuntimeFiles.txt"),
                     "-PreviousManifestPath",
                     previousManifest
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CleanupResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed record CleanupResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public override string ToString() =>
            $"ExitCode={ExitCode}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{StandardError}";
    }
}
