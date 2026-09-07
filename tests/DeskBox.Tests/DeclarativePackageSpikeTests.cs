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

    [Fact]
    public void TraversalPathInIntegrityList_ViolatesThePackagePathGrammar()
    {
        // The integrity path grammar is platform protocol (zip-slip
        // defense): relative forward-slash paths only, no .. segments, no
        // backslashes, no drive prefixes/colons, no case collisions.
        string tampered = Path.Combine(_tempRoot, "github-stats");
        CopyDirectory(TestPaths.FromRepository("spikes/github-stats"), tampered);
        File.AppendAllText(
            Path.Combine(tampered, "package.integrity"),
            $"{new string('a', 64)}  ../../evil.txt\n");

        ProcessResult result = RunValidator(tampered);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "path violates the package path grammar: ../../evil.txt",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LivePackage_ValidatesAndExecutesTheDeclarativeLoop()
    {
        // Leg 1B: validation -> requested/granted permission gate -> HOST-
        // side fetch (mock) -> JSON-path binding -> widget primaryActionId
        // -> open-url resolution, all in one harness run.
        ProcessResult result = RunHarness(
            TestPaths.FromRepository("spikes/github-stats-live"),
            "--self-test=ok",
            "--grant=network.fetch=127.0.0.1",
            "--grant=shell.open=github.com",
            "--invoke-widget=live-stars");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": 1284", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"primaryActionId\": \"open-repo\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"open-url\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("https://github.com/Tianyu199509/DeskBox", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePackage_FetchOutsideDeclaredScopeIsRefused()
    {
        // The host policy gate must refuse BEFORE any bytes move when the
        // data source URL host is not inside the declared network.fetch scope.
        ProcessResult result = RunHarness(
            TestPaths.FromRepository("spikes/github-stats-live"),
            "--self-test=out-of-scope",
            "--grant=network.fetch=127.0.0.1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "outside the declared network.fetch scope",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LivePackage_RequestedPermissionIsNotGrantedByDefault()
    {
        // Round 7: manifest permissions are REQUESTS. Without a host-side
        // grant nothing runs (fail closed) - legs 2/3 must copy this split.
        ProcessResult result = RunHarness(
            TestPaths.FromRepository("spikes/github-stats-live"),
            "--self-test=ok",
            "--invoke-widget=live-stars");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "has no granted network.fetch capability (requested != granted)",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LivePackage_RedirectAttemptIsRefusedNotFollowed()
    {
        // A 3xx would move the request to a host that never passed the
        // gate; v0.3 refuses instead of following (the redirect target
        // receives nothing because redirects are never followed).
        ProcessResult result = RunHarness(
            TestPaths.FromRepository("spikes/github-stats-live"),
            "--self-test=redirect",
            "--grant=network.fetch=127.0.0.1",
            "--grant=shell.open=github.com");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "attempted a redirect; redirects are refused in v0.3",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LivePackage_FetchFailureUsesPayloadFallback()
    {
        // Data failures (HTTP 5xx) are not fatal: the source is marked
        // failed, its bindings keep the payload fallback, widget state is
        // still produced.
        ProcessResult result = RunHarness(
            TestPaths.FromRepository("spikes/github-stats-live"),
            "--self-test=server-error",
            "--grant=network.fetch=127.0.0.1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": \"…\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("fallback (HTTP 500)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"github-repo\": \"HTTP 500\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePackage_OversizedResponseIsCappedAndFallsBack()
    {
        // Host hard limit: a 3MB "JSON" body exceeds the 2MB cap; the
        // source fails, bindings fall back, the run still succeeds.
        ProcessResult result = RunHarness(
            TestPaths.FromRepository("spikes/github-stats-live"),
            "--self-test=huge",
            "--grant=network.fetch=127.0.0.1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "response exceeds the 2097152-byte host limit",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("\"value\": \"…\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsDuplicatePermissionIds()
    {
        // One entry per permission id: duplicate ids with different scopes
        // would make grant/scope lookup implementation-defined across the
        // three future runtimes. (The manifest edit also breaks integrity,
        // which is expected - both failures are listed.)
        string tampered = Path.Combine(_tempRoot, "github-stats-live");
        CopyDirectory(TestPaths.FromRepository("spikes/github-stats-live"), tampered);
        string manifestPath = Path.Combine(tampered, "manifest.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath).Replace(
                "\"id\": \"shell.open\"",
                "\"id\": \"network.fetch\""));

        ProcessResult result = RunValidator(tampered);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "duplicate id 'network.fetch'",
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
        return RunNode(
            TestPaths.FromRepository("scripts/spike/validate-package.mjs"),
            packageDirectory);
    }

    private static ProcessResult RunHarness(string packageDirectory, params string[] harnessArgs)
    {
        return RunNode(
            TestPaths.FromRepository("scripts/spike/run-declarative.mjs"),
            [packageDirectory, .. harnessArgs]);
    }

    private static ProcessResult RunNode(string scriptPath, params string[] scriptArgs)
    {
        string nodePath = ResolveNodeExecutablePath();
        var startInfo = new System.Diagnostics.ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = TestPaths.FromRepository("."),
            // Node always writes UTF-8; without this the redirected stream
            // decodes with the system code page and non-ASCII output breaks.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in scriptArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

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
