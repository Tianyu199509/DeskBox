using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class Windows10InstallerCompatibilityContractTests
{
    private static readonly string[] InstallerLanguages =
    [
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
        "korean"
    ];

    [Theory]
    [InlineData("installer/DeskBox.iss")]
    [InlineData("installer/DeskBox.arm64.iss")]
    public void DirectInstaller_MatchesPackagedWindows10Minimum(string scriptPath)
    {
        string installer = File.ReadAllText(TestPaths.FromRepository(scriptPath));
        string manifest = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Package.appxmanifest"));

        Assert.Contains("MinVersion=10.0.19041", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnlyBelowVersion=", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#include \"DeskBox.StartupErrorMessages.iss\"", installer, StringComparison.Ordinal);
        Assert.Contains("MinVersion=\"10.0.19041.0\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupErrorMessages_AreLocalizedAndPlaceholderAlignedForBothArchitectures()
    {
        string raw = File.ReadAllText(TestPaths.FromRepository("installer/DeskBox.StartupErrorMessages.iss"));
        int arm64Marker = raw.IndexOf("#if MyAppRuntimeArchitecture == \"ARM64\"", StringComparison.Ordinal);
        int elseMarker = raw.IndexOf("#else", arm64Marker + 1, StringComparison.Ordinal);
        int endIfMarker = raw.IndexOf("#endif", elseMarker + 1, StringComparison.Ordinal);

        Assert.True(arm64Marker >= 0 && elseMarker > arm64Marker && endIfMarker > elseMarker);

        string arm64Section = raw[arm64Marker..elseMarker] + Environment.NewLine + raw[(endIfMarker + "#endif".Length)..];
        string x64Section = raw[(elseMarker + "#else".Length)..endIfMarker] + Environment.NewLine + raw[(endIfMarker + "#endif".Length)..];

        AssertStartupBranchLocalized(arm64Section, expectedRedirectSuffix: "_x64.exe");
        AssertStartupBranchLocalized(x64Section, expectedRedirectSuffix: "_arm64.exe");
    }

    [Fact]
    public void InstallerScripts_DoNotReferenceStaleWinAppRuntime25Text()
    {
        foreach (string scriptPath in Directory.GetFiles(
                     TestPaths.FromRepository("installer"),
                     "*.iss",
                     SearchOption.TopDirectoryOnly))
        {
            string content = File.ReadAllText(scriptPath);
            Assert.DoesNotContain("Windows App Runtime 2.5", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Windows10Floor_IsDeclaredConsistentlyAcrossProjectsAndProbes()
    {
        // 19041 (Windows 10 2004) is the base build of the 19041-19045 servicing family;
        // every declared floor must move together when the compatibility floor changes.
        foreach (string projectPath in new[]
                 {
                     "src/DeskBox/DeskBox.csproj",
                     "src/DeskBox.Settings/DeskBox.Settings.csproj",
                     "src/DeskBox.Updater/DeskBox.Updater.csproj",
                     "tests/DeskBox.Tests/DeskBox.Tests.csproj",
                     "tests/DeskBox.NotificationActivationProbe/DeskBox.NotificationActivationProbe.csproj"
                 })
        {
            string project = File.ReadAllText(TestPaths.FromRepository(projectPath));
            Assert.Contains(
                "<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>",
                project,
                StringComparison.Ordinal);
        }

        string mainProject = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        Assert.Contains(
            "<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>",
            mainProject,
            StringComparison.Ordinal);

        string startupProbe = File.ReadAllText(TestPaths.FromRepository(
            "scripts/Test-StoreStartupTaskLifecycle.ps1"));
        Assert.Contains("MinVersion=\"10.0.19041.0\"", startupProbe, StringComparison.Ordinal);

        string notificationProbe = File.ReadAllText(TestPaths.FromRepository(
            "tests/DeskBox.NotificationActivationProbe/Run-MSIX.ps1"));
        Assert.Contains("MinVersion=\"10.0.19041.0\"", notificationProbe, StringComparison.Ordinal);
    }

    private static void AssertStartupBranchLocalized(string branchContent, string expectedRedirectSuffix)
    {
        string[] requiredKeys =
        [
            "WindowsVersionNotSupported",
            "OnlyOnTheseArchitectures",
            "WinVersionTooLowError"
        ];

        var tables = InstallerLanguages.ToDictionary(
            language => language,
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (string line in branchContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            int dotIndex = line.IndexOf('.');
            int equalsIndex = line.IndexOf('=');
            if (dotIndex <= 0 || equalsIndex <= dotIndex)
            {
                continue;
            }

            string language = line[..dotIndex].Trim();
            Assert.True(tables.TryGetValue(language, out Dictionary<string, string>? table), $"Unexpected language '{language}'.");

            string key = line[(dotIndex + 1)..equalsIndex].Trim();
            Assert.True(table.TryAdd(key, line[(equalsIndex + 1)..]), $"Duplicate {language}.{key}");
        }

        Dictionary<string, string> english = tables["english"];
        Assert.Equal(
            requiredKeys.OrderBy(key => key, StringComparer.Ordinal),
            english.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach ((string language, Dictionary<string, string> table) in tables)
        {
            Assert.Equal(
                english.Keys.OrderBy(key => key, StringComparer.Ordinal),
                table.Keys.OrderBy(key => key, StringComparer.Ordinal));

            Assert.Contains(
                expectedRedirectSuffix,
                table["WindowsVersionNotSupported"],
                StringComparison.Ordinal);
            Assert.Contains(
                expectedRedirectSuffix,
                table["OnlyOnTheseArchitectures"],
                StringComparison.Ordinal);

            foreach ((string key, string englishValue) in english)
            {
                Assert.Equal(
                    GetInnoPlaceholders(englishValue),
                    GetInnoPlaceholders(table[key]));
            }
        }
    }

    private static string[] GetInnoPlaceholders(string value)
    {
        return Regex.Matches(value, @"%(?:n|\d+)")
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
            .ToArray();
    }
}
