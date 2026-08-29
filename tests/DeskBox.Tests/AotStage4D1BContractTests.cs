using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class AotStage4D1BContractTests
{
    [Fact]
    public void QuickCaptureXamlFailure_UsesOnlyFixedExceptionDiagnostics()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");

        Assert.Contains("ex.GetType().FullName", source, StringComparison.Ordinal);
        Assert.Contains("HResult=0x{ex.HResult:X8}", source, StringComparison.Ordinal);
        Assert.Contains("Message={ex.Message}", source, StringComparison.Ordinal);
        Assert.Contains("InnerException={ex.InnerException}", source, StringComparison.Ordinal);
        Assert.Contains("StackTrace={ex.StackTrace}", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetProperties()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("property.GetValue(ex)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizedHeaderAndDescription_UseTheFrozenTypedControlSet()
    {
        string source = ReadRepositoryFile("src/DeskBox/Services/Localized.cs");

        Assert.Contains("using CommunityToolkit.WinUI.Controls;", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"case SettingsCard (?<name>\w+):\s*\k<name>\.Header = value;",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                @"case SettingsExpander (?<name>\w+):\s*\k<name>\.Header = value;",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                @"case TextBox (?<name>\w+):\s*\k<name>\.Header = value;",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                @"case SettingsCard (?<name>\w+):\s*\k<name>\.Description = value;",
                RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(
                @"case SettingsExpander (?<name>\w+):\s*\k<name>\.Description = value;",
                RegexOptions.CultureInvariant),
            source);
        Assert.DoesNotContain("SetObjectProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetType().GetProperty", source, StringComparison.Ordinal);

        IReadOnlyDictionary<string, int> usages = ReadLocalizedXamlUsages();
        Assert.Equal(5, usages.Count);
        Assert.Equal(163, usages["toolkit:SettingsCard|HeaderKey"]);
        Assert.Equal(134, usages["toolkit:SettingsCard|DescriptionKey"]);
        Assert.Equal(20, usages["toolkit:SettingsExpander|HeaderKey"]);
        Assert.Equal(7, usages["toolkit:SettingsExpander|DescriptionKey"]);
        Assert.Equal(2, usages["TextBox|HeaderKey"]);
        Assert.Equal(326, usages.Values.Sum());
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D1BTargetFilesToRemainWarningFree()
    {
        string script = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 58", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("stage4D1BWarningMessages", script, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureSurfaceContent.xaml.cs", script, StringComparison.Ordinal);
        Assert.Contains("Localized.cs", script, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-1B target files still produce AOT analysis warnings",
            script,
            StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, int> ReadLocalizedXamlUsages()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        string separator = Path.DirectorySeparatorChar.ToString();
        var tagRegex = new Regex(
            @"<(?<tag>[A-Za-z_][A-Za-z0-9_.:-]*)\b(?:(?!>).)*svc:Localized\.(?:HeaderKey|DescriptionKey)(?:(?!>).)*>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        var propertyRegex = new Regex(
            @"svc:Localized\.(?<property>HeaderKey|DescriptionKey)=",
            RegexOptions.CultureInvariant);
        var usages = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(
                     projectDirectory,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string xaml = File.ReadAllText(path);
            foreach (Match tagMatch in tagRegex.Matches(xaml))
            {
                string tag = tagMatch.Groups["tag"].Value;
                foreach (Match propertyMatch in propertyRegex.Matches(tagMatch.Value))
                {
                    string key = $"{tag}|{propertyMatch.Groups["property"].Value}";
                    usages[key] = usages.GetValueOrDefault(key) + 1;
                }
            }
        }

        return usages;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
