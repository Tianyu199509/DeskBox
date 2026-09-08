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

    // ----- spike leg 2: external TS-process runtime (same behavior and the
    // same permission semantics as the declarative leg, but the logic is
    // third-party code in its own process calling host capabilities) -----

    [Fact]
    public void ProcessPackage_ExecutesTheSameBehaviorThroughCapabilityCalls()
    {
        ProcessResult result = RunProcessHarness(
            TestPaths.FromRepository("spikes/github-stats-process"),
            "--self-test=ok",
            "--grant=network.fetch=127.0.0.1",
            "--grant=shell.open=github.com",
            "--invoke-widget=live-stars");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": 1284", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"method\": \"network.fetch\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"open-url\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("https://github.com/Tianyu199509/DeskBox", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessPackage_UngrantedCapabilityCallIsRefusedAndPluginDegrades()
    {
        // Without a host-side grant the capability CALL is refused (the
        // plugin asked properly, so it is not killed); the plugin degrades
        // to the fallback payload and keeps rendering.
        ProcessResult result = RunProcessHarness(
            TestPaths.FromRepository("spikes/github-stats-process"),
            "--self-test=ok");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": \"…\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "has no granted network.fetch capability (requested != granted)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessPackage_OutOfScopeAskFromHostileCodeIsRefused()
    {
        // The ask itself is out of scope: the gate must refuse the call
        // host-side - process isolation does not mean trust.
        ProcessResult result = RunProcessHarness(
            TestPaths.FromRepository("spikes/github-stats-process"),
            "--self-test=evil-ask",
            "--grant=network.fetch=api.github.com");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": \"…\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "outside the declared network.fetch scope",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessPackage_RedirectAttemptIsRefusedAndPluginDegrades()
    {
        ProcessResult result = RunProcessHarness(
            TestPaths.FromRepository("spikes/github-stats-process"),
            "--self-test=redirect",
            "--grant=network.fetch=127.0.0.1");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": \"…\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "attempted a redirect; redirects are refused in v0.3",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessPackage_DowngradedHttpSchemeIsRefused()
    {
        // Round 8: the gate must enforce https even when the host is in
        // scope AND granted - http://<granted-host> is a scheme downgrade,
        // not an allowed request.
        ProcessResult result = RunProcessHarness(
            TestPaths.FromRepository("spikes/github-stats-process"),
            "--self-test=downgrade",
            "--grant=network.fetch=api.github.com");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"value\": \"…\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "must use https (scheme enforcement; http requests are refused)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessPackage_PluginCrashIsSurfacedByProcessGovernance()
    {
        // A crashed plugin must never be swallowed as success.
        ProcessResult result = RunProcessHarness(
            TestPaths.FromRepository("spikes/github-stats-process"),
            "--self-test=crash",
            "--grant=network.fetch=api.github.com");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "plugin process exited with code 3",
            result.StandardError,
            StringComparison.Ordinal);
    }

    // ----- spike leg 3: WASM component runtime (source-scan pins only -
    // building the Rust crate is NOT a CI prerequisite; the red line is
    // that the app build never references the spike crate) -----

    [Fact]
    public void WasmSpike_PackageValidatesAndCarriesTheComponent()
    {
        // The committed artifact's integrity chain must verify; this also
        // fails loudly if someone rebuilds the .wasm without refreshing the
        // package (wasm builds are not byte-reproducible).
        string packageDir = TestPaths.FromRepository("spikes/github-stats-wasm");
        Assert.True(File.Exists(Path.Combine(packageDir, "plugin", "plugin.wasm")));
        byte[] header = File.ReadAllBytes(Path.Combine(packageDir, "plugin", "plugin.wasm"))[0..8];
        Assert.True(
            header[4] == 0x0d && header[6] == 0x01 && header[7] == 0x00,
            "artifact must be a WASM component (version 0x0d header)");

        ProcessResult result = RunValidator(packageDir);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("VERIFIED", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void WasmSpike_WitWorldPinsTheCapabilityBoundary()
    {
        string wit = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-wasm-spike/wit/deskbox-plugin.wit"));

        Assert.Contains("package deskbox:plugin@0.1.0", wit, StringComparison.Ordinal);
        Assert.Contains("world plugin-world", wit, StringComparison.Ordinal);
        // The guest gets capabilities ONLY as host imports - the boundary
        // every leg shares.
        Assert.Contains("import capabilities;", wit, StringComparison.Ordinal);
        Assert.Contains("network-fetch: func", wit, StringComparison.Ordinal);
        Assert.Contains("shell-open: func", wit, StringComparison.Ordinal);
        Assert.Contains("widget-update: func", wit, StringComparison.Ordinal);
        Assert.Contains("export activate: func", wit, StringComparison.Ordinal);
        Assert.Contains("export invoke-action: func", wit, StringComparison.Ordinal);
    }

    [Fact]
    public void WasmSpike_HostEnforcesGateSemanticsAndGovernance()
    {
        string host = File.ReadAllText(TestPaths.FromRepository(
            "native/deskbox-wasm-spike/host/src/main.rs"));

        // Same permission semantics as the other legs.
        Assert.Contains("requested != granted", host, StringComparison.Ordinal);
        Assert.Contains("outside the declared", host, StringComparison.Ordinal);
        Assert.Contains("redirects are refused in v0.3", host, StringComparison.Ordinal);
        Assert.Contains("MAX_RESPONSE_BYTES", host, StringComparison.Ordinal);

        // Round 8: https scheme enforcement and a real URL parser - never a
        // hand-rolled split in a security boundary.
        Assert.Contains("must use https (scheme enforcement", host, StringComparison.Ordinal);
        Assert.Contains("url::Url::parse", host, StringComparison.Ordinal);
        Assert.DoesNotContain("split(\"://\")", host, StringComparison.Ordinal);

        // Governance: deterministic fuel budget, epoch deadline, memory cap.
        Assert.Contains("consume_fuel(true)", host, StringComparison.Ordinal);
        Assert.Contains("epoch_interruption(true)", host, StringComparison.Ordinal);
        Assert.Contains("ResourceLimiter", host, StringComparison.Ordinal);
        Assert.Contains("FUEL_LIMIT", host, StringComparison.Ordinal);
        Assert.Contains("exhausted its fuel budget", host, StringComparison.Ordinal);
    }

    [Fact]
    public void WasmSpike_RedLine_SpikeCrateStaysOutOfTheAppBuild()
    {
        // Roadmap red line: the spike crate must never leak into the app
        // build, the AOT audit, or the retail pipeline (it would drag
        // wasmtime into the shipped app).
        string[] guardedFiles =
        [
            "src/DeskBox/DeskBox.csproj",
            "scripts/publish-aot-audit.ps1",
            "scripts/publish-aot-retail.ps1",
            "scripts/publish-arm64-aot-static-audit.ps1",
            "src/DeskBox.Updater/DeskBox.Updater.csproj"
        ];
        foreach (string file in guardedFiles)
        {
            string fullPath = TestPaths.FromRepository(file);
            if (!File.Exists(fullPath))
            {
                continue;
            }
            Assert.DoesNotContain(
                "deskbox-wasm-spike",
                File.ReadAllText(fullPath),
                StringComparison.OrdinalIgnoreCase);
        }

        // And the native crate stays standalone: no reference FROM the app
        // project files anywhere under src/.
        foreach (string project in Directory.GetFiles(
                     TestPaths.FromRepository("src"),
                     "*.csproj",
                     SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(
                "deskbox-wasm-spike",
                File.ReadAllText(project),
                StringComparison.OrdinalIgnoreCase);
        }
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

    private static ProcessResult RunProcessHarness(string packageDirectory, params string[] harnessArgs)
    {
        return RunNode(
            TestPaths.FromRepository("scripts/spike/run-process.mjs"),
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
