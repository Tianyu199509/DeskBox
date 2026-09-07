namespace DeskBox.Tests;

/// <summary>
/// Spike leg 1 (roadmap stage 3.5): the declarative GitHub-Stats sample
/// package must validate against the schema v0.2 semantics, and the
/// verification chain must actually detect tampering. Runs the Node spike
/// tooling through its fully resolved executable path; Node ships on
/// GitHub-hosted Windows runners and dev machines.
/// </summary>
public sealed class DeclarativePackageSpikeTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N")))
        .FullName;

    [Fact]
    public void SamplePackage_ValidatesStructurallyAndCryptographically()
    {
        ProcessResult result = RunValidator(TestPaths.FromRepository("spikes/github-stats"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("VERIFIED", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedPayload_FailsIntegrity()
    {
        string tampered = Path.Combine(_tempRoot, "github-stats");
        CopyDirectory(TestPaths.FromRepository("spikes/github-stats"), tampered);
        File.AppendAllText(
            Path.Combine(tampered, "files", "icon.svg"),
            "<!--tamper-->");

        ProcessResult result = RunValidator(tampered);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("integrity mismatch for files/icon.svg", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void EditedManifest_FailsItsOwnIntegrityLine()
    {
        // Editing the manifest without rebuilding package.integrity must be
        // caught by step 1 (the canonicalized manifest no longer matches its
        // integrity line), even though the file itself still exists.
        string tampered = Path.Combine(_tempRoot, "github-stats");
        CopyDirectory(TestPaths.FromRepository("spikes/github-stats"), tampered);
        string manifestPath = Path.Combine(tampered, "manifest.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace("1284", "9999"));

        ProcessResult result = RunValidator(tampered);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "manifest.json integrity line does not match",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static ProcessResult RunValidator(string packageDirectory)
    {
        string nodePath = ResolveNodeExecutablePath();
        var startInfo = new System.Diagnostics.ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = TestPaths.FromRepository(".")
        };
        startInfo.ArgumentList.Add(TestPaths.FromRepository(
            "scripts/spike/validate-package.mjs"));
        startInfo.ArgumentList.Add(packageDirectory);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the Node runtime.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string ResolveNodeExecutablePath()
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                string candidate = Path.Combine(directory.Trim(), "node.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH entries are skipped.
            }
        }

        throw new InvalidOperationException(
            "node.exe was not found on PATH - required on dev machines and CI runners.");
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for files briefly held by antivirus.
        }
    }
}
