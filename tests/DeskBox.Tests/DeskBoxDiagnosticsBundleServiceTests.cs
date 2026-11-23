using System.IO.Compression;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DeskBoxDiagnosticsBundleServiceTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxDiagnosticsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportAsync_CreatesNarrowSanitizedSupportArchive()
    {
        Directory.CreateDirectory(_temporaryRoot);
        string logPath = Path.Combine(_temporaryRoot, "DeskBox.log");
        string userName = Environment.UserName;
        string privatePath = $@"C:\Users\{userName}\Private Folder\secret-file.txt";
        await File.WriteAllTextAsync(
            logPath,
            $"[TrayToggle] path='{privatePath}' user={userName} email=user@example.com SID=S-1-5-21-123-456-789-1001\n" +
            $"[Hotkey] executable={privatePath}\n");
        var service = new DeskBoxDiagnosticsBundleService();

        string archivePath = await service.ExportAsync(
            _temporaryRoot,
            CreateSnapshot(),
            logPath);

        Assert.True(File.Exists(archivePath));
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string[] entryNames = archive.Entries
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "DeskBox-sanitized.log", "README.txt", "diagnostics.json", "windows-events.json" }
                .Order(StringComparer.Ordinal),
            entryNames);

        string log = await ReadEntryAsync(archive, "DeskBox-sanitized.log");
        Assert.DoesNotContain(userName, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-file.txt", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user@example.com", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5-21", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<REDACTED>", log, StringComparison.Ordinal);

        string diagnostics = await ReadEntryAsync(archive, "diagnostics.json");
        Assert.Contains("\"schemaVersion\": 5", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"loadRecoveryState\": \"Primary\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"shortcutNative\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"moduleName\": \"deskbox_native.dll\"", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(AppContext.BaseDirectory, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"toggleReservedHookThreadId\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"toggleReservedHookInputFailureCount\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"widgetManager\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"fileHosts\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"strategy\": \"Unavailable\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"startupLedger\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"stormGuardActive\": true", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"assessmentReason\"", diagnostics, StringComparison.Ordinal);
        // The callback-radar section must survive source-generated
        // serialization (the dictionary value type has no other coverage).
        Assert.Contains("\"callbackRadar\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"totalWindowOperationsInCallback\": 1", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"SetWindowPos\": 1", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.json", diagnostics, StringComparison.OrdinalIgnoreCase);

        // The crash-event report ships on every export — with events, empty,
        // or explicitly unavailable — because "was there a crash at all?" is
        // the first question every loop report raises (feedback #576).
        string crashEvents = await ReadEntryAsync(archive, "windows-events.json");
        Assert.Contains("\"schemaVersion\": 1", crashEvents, StringComparison.Ordinal);
        Assert.Contains("\"status\"", crashEvents, StringComparison.Ordinal);
        Assert.Contains("\"windowStartUtc\"", crashEvents, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeDiagnosticsValue_KeepsImageNameButRedactsDirectories()
    {
        string value = $@"C:\Users\{Environment.UserName}\AppData\Local\DeskBox\DeskBox.exe";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeDiagnosticsValue(value);

        Assert.DoesNotContain(Environment.UserName, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<PATH>\\DeskBox.exe", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeDiagnosticsValue_RedactsEmailsAndSids()
    {
        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeDiagnosticsValue(
            $"user-{Environment.UserName}@example.com S-1-5-21-1-2-3-4");

        Assert.DoesNotContain(Environment.UserName, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@example.com", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5-21-1-2-3-4", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeLog_RemovesUnquotedPathsAndAccountIdentifiers()
    {
        string source = $"open C:\\Users\\{Environment.UserName}\\private.txt " +
                        "contact me@example.com S-1-5-18";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog(source);

        Assert.DoesNotContain("private.txt", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("me@example.com", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5-18", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("False", "True")]
    [InlineData("True", "False")]
    public void SanitizeLog_PreservesStartupChecksWhileRedactingActualPaths(
        string pathCheck,
        string executionLimitCheck)
    {
        string source =
            "expectedPath='C:\\Private Folder\\DeskBox.exe' " +
            "actualPath='C:\\Other Private Folder\\DeskBox.exe' " +
            $"checks=taskName=True path={pathCheck} arguments=True " +
            $"executionLimit={executionLimitCheck} restart=True";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog(source);

        Assert.DoesNotContain("Private Folder", sanitized, StringComparison.Ordinal);
        Assert.Contains("expectedPath=<REDACTED>", sanitized, StringComparison.Ordinal);
        Assert.Contains("actualPath=<REDACTED>", sanitized, StringComparison.Ordinal);
        Assert.Contains($"path={pathCheck}", sanitized, StringComparison.Ordinal);
        Assert.Contains($"executionLimit={executionLimitCheck}", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("True\\private.txt")]
    [InlineData("'False/private.txt'")]
    public void SanitizeLog_StillRedactsPathsBeginningWithBooleanText(string path)
    {
        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog($"path={path} ready=True");

        Assert.Equal("path=<REDACTED> ready=True", sanitized);
    }

    [Fact]
    public void SanitizeLog_PreservesHResultHexValuesOnKeywordCollidingKeys()
    {
        string source =
            "[OpenFile] backend=explorer-hosted implementation=IExplorerShellLaunch " +
            "path='C:\\Users\\secret\\file.txt' applicationHr=0x00000000 executeHr=0x800704CF";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog(source);

        Assert.Contains("applicationHr=0x00000000", sanitized, StringComparison.Ordinal);
        Assert.Contains("executeHr=0x800704CF", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeLog_PreservesExecuteHResultWithTrailingPeriod()
    {
        string source =
            "Native Explorer-shell launch failed: status=Failed, HRESULT=0x80004005, " +
            "execute=0x80004005.";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog(source);

        Assert.Contains("HRESULT=0x80004005", sanitized, StringComparison.Ordinal);
        Assert.Contains("execute=0x80004005.", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeLog_PreservesIntegerCountsWhileRedactingNumericSecrets()
    {
        string source =
            "[DragProtocol] stage=PackagePrepared paths=1 pathCount=4 file=2 failedItems=0 " +
            "token=123456 password=999999 pathSample='C:\\Users\\simon\\Desktop\\a.txt'";

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeLog(source);

        Assert.Contains("paths=1", sanitized, StringComparison.Ordinal);
        Assert.Contains("pathCount=4", sanitized, StringComparison.Ordinal);
        Assert.Contains("file=2", sanitized, StringComparison.Ordinal);
        Assert.Contains("failedItems=0", sanitized, StringComparison.Ordinal);
        Assert.Contains("token=<REDACTED>", sanitized, StringComparison.Ordinal);
        Assert.Contains("password=<REDACTED>", sanitized, StringComparison.Ordinal);
        Assert.Contains("pathSample=<REDACTED>", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeStudioArchiveJson_PreservesValidJsonAndJsRegexLiterals_WhileRedactingPathsAndSecrets()
    {
        const string rawJson = """
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "entryId": "1a764b590895",
                  "createdAtUtc": "2026-11-23T03:00:00Z",
                  "eventKind": "studio-commit",
                  "userPrompt": "Read C:\\Users\\alice\\Desktop\\secret.txt and call https://api.example.com/v1?api_key=abc123",
                  "summaryOrError": "Loaded /Users/alice/.config/token.json and \\\\corp-fs\\private\\plan.md",
                  "diagnostics": [
                    "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
                  ],
                  "manifestJson": "{\n  \"id\": \"deskbox.todo-agenda\",\n  \"apiKey\": \"sk-1234567890abcdef123456\",\n  \"icon\": \"C:\\\\Users\\\\alice\\\\icon.png\",\n  \"when\": \"${$computed.hasParsedTokens == 'true'}\"\n}",
                  "logicJs": "function parse(iso) {\n  const m = /^(\\d{4})-(\\d{2})-(\\d{2})$/.exec(iso);\n  const tokenOffset = m ? m.index + 1 : 0;\n  const secret = \"my-super-secret-value\";\n  const localFile = \"D:\\\\private\\\\notes.txt\";\n  return String(iso).replace(/\\s+/g, ' ') + tokenOffset;\n}"
                }
              ]
            }
            """;

        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeStudioArchiveJson(rawJson);

        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(sanitized);
        System.Text.Json.JsonElement entry = parsed.RootElement.GetProperty("entries")[0];
        string logicJs = entry.GetProperty("logicJs").GetString()!;
        string manifestJson = entry.GetProperty("manifestJson").GetString()!;
        string prompt = entry.GetProperty("userPrompt").GetString()!;
        string summary = entry.GetProperty("summaryOrError").GetString()!;
        string diag = entry.GetProperty("diagnostics")[0].GetString()!;

        Assert.Contains(@"/^(\d{4})-(\d{2})-(\d{2})$/", logicJs, StringComparison.Ordinal);
        Assert.Contains(@"/\s+/g", logicJs, StringComparison.Ordinal);
        Assert.Contains("const tokenOffset = m ? m.index + 1 : 0;", logicJs, StringComparison.Ordinal);
        Assert.Contains("secret = \"<REDACTED>\"", logicJs, StringComparison.Ordinal);
        Assert.DoesNotContain("my-super-secret-value", logicJs, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\private\\notes.txt", logicJs, StringComparison.Ordinal);

        using System.Text.Json.JsonDocument parsedManifest = System.Text.Json.JsonDocument.Parse(manifestJson);
        Assert.Equal("<REDACTED>", parsedManifest.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal("<PATH>", parsedManifest.RootElement.GetProperty("icon").GetString());
        Assert.Equal("${$computed.hasParsedTokens == 'true'}", parsedManifest.RootElement.GetProperty("when").GetString());

        Assert.Contains("<PATH>", prompt, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/v1?<QUERY_REDACTED>", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("corp-fs", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/alice", summary, StringComparison.Ordinal);
        Assert.Equal("<SECRET>", diag);
    }

    [Fact]
    public void SanitizeStudioArchiveJson_FailsClosedToValidEmptyArchiveOnCorruptJson()
    {
        string sanitized = DeskBoxDiagnosticsBundleService.SanitizeStudioArchiveJson(
            "{\"schemaVersion\":1,\"entries\":[{\"secret\":\"sk-1234567890abcdef123456\"");

        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(sanitized);
        Assert.Equal(0, parsed.RootElement.GetProperty("entries").GetArrayLength());
        Assert.DoesNotContain("sk-1234567890abcdef123456", sanitized, StringComparison.Ordinal);
    }

    private static DeskBoxDiagnosticSnapshot CreateSnapshot()
    {
        return new DeskBoxDiagnosticSnapshot(
            5,
            new DateTimeOffset(2026, 8, 5, 12, 30, 0, TimeSpan.Zero),
            "1.3.6",
            "Direct",
            false,
            "Windows",
            "X64",
            "zh-CN",
            new DeskBoxHotkeyDiagnostic(
                true,
                true,
                3,
                68,
                4,
                4,
                0,
                false,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                false,
                3,
                70),
            new DeskBoxSettingsDiagnostic(
                SettingsLoadRecoveryState.Primary,
                false,
                null,
                null),
            new DeskBoxRecoveryOfferDiagnostic(
                false,
                null,
                null,
                null,
                0,
                nameof(RecoveryOfferOutcome.NotOffered)),
            new DeskBoxShortcutNativeDiagnostic(
                "CSharp",
                "deskbox_native.dll",
                true,
                "X64",
                new string('A', 64),
                false,
                "NotProbed",
                null,
                null),
            null,
            DeskBoxWidgetManagerDiagnostic.Empty,
            [
                new DeskBoxDisplayDiagnostic(
                    1,
                    true,
                    1.5,
                    new DeskBoxDiagnosticRect(0, 0, 1920, 1080),
                    new DeskBoxDiagnosticRect(0, 0, 1920, 1040))
            ],
            StartupLedger: new DeskBoxStartupLedgerDiagnostic(
                true,
                "abnormal-exit-age=120s",
                new DateTimeOffset(2026, 10, 13, 13, 49, 49, TimeSpan.Zero)),
            CallbackRadar: new DeskBoxCallbackRadarDiagnostic(
                1,
                new Dictionary<string, long> { ["SetWindowPos"] = 1 },
                0));
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            item => string.Equals(item.FullName, name, StringComparison.Ordinal));
        await using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }
}
