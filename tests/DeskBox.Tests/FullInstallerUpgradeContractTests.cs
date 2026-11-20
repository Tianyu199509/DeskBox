using System.Diagnostics;

namespace DeskBox.Tests;

public sealed class FullInstallerUpgradeContractTests : IDisposable
{
    private static readonly Lazy<InnoCleanupProbe?> s_innoProbe = new(CreateInnoCleanupProbe);

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

    private async Task<CleanupResult> RunCleanupAsync(
        string installRoot,
        string currentManifest,
        string previousManifest)
    {
        string legacyManifest = TestPaths.FromRepository("installer/DeskBox.LegacyBundledRuntimeFiles.txt");
        if (s_innoProbe.Value is { } probe)
        {
            string logPath = Path.Combine(_tempRoot, $"cleanup-probe-{Guid.NewGuid():N}.log");
            string statusPath = Path.Combine(_tempRoot, $"cleanup-status-{Guid.NewGuid():N}.txt");
            var probePsi = new ProcessStartInfo
            {
                FileName = probe.ExePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            probePsi.ArgumentList.Add("/VERYSILENT");
            probePsi.ArgumentList.Add("/SUPPRESSMSGBOXES");
            probePsi.ArgumentList.Add($"/LOG={logPath}");
            probePsi.ArgumentList.Add($"/INSTALLROOT={installRoot}");
            probePsi.ArgumentList.Add($"/CURRENTMANIFEST={currentManifest}");
            probePsi.ArgumentList.Add($"/LEGACYMANIFEST={legacyManifest}");
            probePsi.ArgumentList.Add($"/PREVIOUSMANIFEST={previousManifest}");
            probePsi.ArgumentList.Add($"/STATUSFILE={statusPath}");

            using Process probeProcess = Process.Start(probePsi)
                ?? throw new InvalidOperationException("Unable to start Inno cleanup probe.");
            await probeProcess.WaitForExitAsync();

            string logText = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            string statusText = File.Exists(statusPath) ? (await File.ReadAllTextAsync(statusPath)).Trim() : string.Empty;
            bool succeeded = string.Equals(statusText, "OK", StringComparison.Ordinal);
            return new CleanupResult(
                succeeded ? 0 : 1,
                succeeded ? logText : string.Empty,
                succeeded ? string.Empty : (string.IsNullOrEmpty(statusText) ? logText : statusText));
        }

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
                     legacyManifest,
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

    private static InnoCleanupProbe? CreateInnoCleanupProbe()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 7", "ISCC.exe")
        ];

        string? isccPath = candidates.FirstOrDefault(File.Exists);
        if (isccPath is null)
        {
            return null;
        }

        string probeRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.InnoCleanupProbe." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeRoot);

        string installationScript = TestPaths.FromRepository("installer/DeskBox.Installation.iss");
        string migrationScript = TestPaths.FromRepository("installer/DeskBox.Migration.iss");
        string probeIssPath = Path.Combine(probeRoot, "CleanupProbe.iss");

        File.WriteAllText(
            probeIssPath,
            $$"""
            #define MyAppExeName "DeskBox.exe"
            #define DeskBoxInstallerPeMachineHex "$8664"
            #define DeskBoxBundledRuntime 1

            [Setup]
            AppName=DeskBoxCleanupProbe
            AppVersion=1.6.0
            DefaultDirName={autopf}\DeskBoxCleanupProbe
            PrivilegesRequired=lowest
            CreateUninstallRegKey=no
            UpdateUninstallLogAppName=no
            UsePreviousAppDir=no
            DirExistsWarning=no
            DisableWelcomePage=yes
            OutputDir={{probeRoot}}
            OutputBaseFilename=DeskBoxCleanupProbe
            SetupLogging=yes

            [CustomMessages]
            MultipleInstallationsTitle=Multiple
            MultipleInstallationsBody=%1
            MultipleInstallationsFooter=Footer
            UpgradeDirectoryMismatch=%1 vs %2
            PerUserUpgradeScopeMismatch=%1
            ArchitectureMismatchBody=%1

            #include "{{installationScript}}"
            #include "{{migrationScript}}"

            [Code]
            function PrepareDeskBoxDependencies(var NeedsRestart: Boolean): string;
            begin
              Result := '';
              NeedsRestart := False;
            end;

            function ReadCmdArg(Prefix: string): string;
            var
              I: Integer;
              Arg: string;
            begin
              Result := '';
              for I := 1 to ParamCount do
              begin
                Arg := ParamStr(I);
                if CompareText(Copy(Arg, 1, Length(Prefix)), Prefix) = 0 then
                begin
                  Result := Copy(Arg, Length(Prefix) + 1, MaxInt);
                  Exit;
                end;
              end;
            end;

            // Override InitializeSetup via a second [Code] probe hook is not allowed when
            // DeskBox.Migration.iss already defines InitializeSetup, so we abort in
            // InitializeWizard before any install step can run and execute the cleanup
            // probe inside DeinitializeSetup against the isolated test directory.
            procedure InitializeWizard;
            begin
              Abort;
            end;

            procedure DeinitializeSetup;
            var
              StatusFile: string;
            begin
              StatusFile := ReadCmdArg('/STATUSFILE=');
              if StatusFile = '' then
                Exit;

              try
                RunDeskBoxInstallCleanup(
                  ReadCmdArg('/INSTALLROOT='),
                  ReadCmdArg('/CURRENTMANIFEST='),
                  ReadCmdArg('/LEGACYMANIFEST='),
                  ReadCmdArg('/PREVIOUSMANIFEST='));
                SaveStringToFile(StatusFile, 'OK', False);
              except
                SaveStringToFile(StatusFile, GetExceptionMessage, False);
              end;
            end;
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo
        {
            FileName = isccPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/Qp");
        psi.ArgumentList.Add(probeIssPath);

        using Process compile = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ISCC.exe for cleanup probe.");
        string stdout = compile.StandardOutput.ReadToEnd();
        string stderr = compile.StandardError.ReadToEnd();
        if (!compile.WaitForExit(30000) || compile.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ISCC failed compiling CleanupProbe.iss (exit {compile.ExitCode}):{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        return new InnoCleanupProbe(Path.Combine(probeRoot, "DeskBoxCleanupProbe.exe"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed record InnoCleanupProbe(string ExePath);

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
