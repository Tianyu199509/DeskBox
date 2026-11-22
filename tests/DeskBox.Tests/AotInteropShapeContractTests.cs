using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Source-shape contracts for the Native AOT interop surface. CsWinRT's
/// generated bindable-property and vtable support requires partial type
/// declarations; a non-partial converter or bindable class compiles fine in
/// JIT test runs and only turns red at publish time (CsWinRT1028), which the
/// audit previously surfaced as a single fail-fast error that masked every
/// later finding. These contracts move that guard to the unit-test layer so
/// the regression cannot even reach the publish audit.
/// </summary>
public sealed class AotInteropShapeContractTests
{
    [Fact]
    public void EveryValueConverter_IsDeclaredPartial()
    {
        List<string> violations = new();

        foreach (string path in EnumerateProductSources())
        {
            string text = File.ReadAllText(path);
            foreach (Match declaration in ConverterDeclarations().Matches(text))
            {
                if (!declaration.Value.Contains("partial", StringComparison.Ordinal))
                {
                    violations.Add($"{RelativeToRepository(path)}: {declaration.Value.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "IValueConverter implementations must be partial so CsWinRT can generate " +
            "AOT vtable support. Violations:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void EveryGeneratedBindableCustomPropertyType_IsDeclaredPartial()
    {
        List<string> violations = new();

        foreach (string path in EnumerateProductSources())
        {
            string text = File.ReadAllText(path);
            foreach (string declaration in GeneratedBindableDeclarations(text))
            {
                if (!declaration.Contains("partial", StringComparison.Ordinal))
                {
                    violations.Add($"{RelativeToRepository(path)}: {declaration.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Types carrying [WinRT.GeneratedBindableCustomProperty] must be partial; " +
            "the source generator emits additional members into them. Violations:\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void SettingsBindingConverters_PinsBothConverterDeclarationsAsSealedPartial()
    {
        string text = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/SettingsBindingConverters.cs"));

        Assert.Contains(
            "public sealed partial class SettingsBoolToVisibilityConverter : IValueConverter",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial class SettingsColorStringConverter : IValueConverter",
            text,
            StringComparison.Ordinal);
    }

    private static readonly Regex ConverterDeclarationsRegex = new(
        @"^[\t ]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*(?:class|record|struct)\s+\w+[^{;]*?IValueConverter[^{;]*",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled);

    private static Regex ConverterDeclarations() => ConverterDeclarationsRegex;

    /// <summary>
    /// Yields the full declaration header (modifiers through the base list)
    /// of every type carrying [WinRT.GeneratedBindableCustomProperty].
    /// </summary>
    private static IEnumerable<string> GeneratedBindableDeclarations(string text)
    {
        const string Attribute = "[WinRT.GeneratedBindableCustomProperty";
        int search = 0;
        while ((search = text.IndexOf(Attribute, search, StringComparison.Ordinal)) >= 0)
        {
            Match declaration = Regex.Match(
                text[(search + Attribute.Length)..],
                @"(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*(?:class|record|struct)\s+\w+[^{;]*",
                RegexOptions.Singleline);
            if (declaration.Success)
            {
                yield return declaration.Value;
            }

            search += Attribute.Length;
        }
    }

    [Fact]
    public void WinrtPropertyLookups_AreRootedOrDirect()
    {
        // Two shipped regressions of this shape: UISettings.AdvancedEffectsEnabled
        // was looked up by name only, which Native AOT trims away (retail silently
        // degraded to "always enabled" — now a direct reference inside the 19041
        // floor contract), and UIElement.ProtectedCursor is protected in the WinUI
        // projection, so it must stay a reflection lookup paired with a
        // DynamicDependency root (the PasswordBox incident's pattern) in exactly
        // one shared helper.
        List<string> violations = new();
        foreach (string path in EnumerateProductSources())
        {
            string text = File.ReadAllText(path);
            if (text.Contains(
                    "GetProperty(\"AdvancedEffectsEnabled\"",
                    StringComparison.Ordinal))
            {
                violations.Add($"{RelativeToRepository(path)}: GetProperty(\"AdvancedEffectsEnabled\")");
            }

            if (!text.Contains("GetProperty(\"ProtectedCursor\"", StringComparison.Ordinal))
            {
                continue;
            }

            bool isTheRootedHelper = text.Contains(
                    "[DynamicDependency(\"set_ProtectedCursor\"",
                    StringComparison.Ordinal) &&
                text.Contains("UiElementCursorHelper");
            if (!isTheRootedHelper)
            {
                violations.Add(
                    $"{RelativeToRepository(path)}: unrooted ProtectedCursor lookup — " +
                    "use UiElementCursorHelper.SetCursor");
            }
        }

        Assert.True(
            violations.Count == 0,
            "WinRT property lookups must be direct references or reflection paired " +
            "with a DynamicDependency root — bare name lookups get trimmed from " +
            "Native AOT retail builds. Violations:\n" + string.Join('\n', violations));
    }

    private static readonly Regex WinUiSubclassDeclarationsRegex = new(
        @"^[\t ]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*class\s+\w+\s*:\s*(?:global::)?(?:(?:Microsoft\.UI\.Xaml|CommunityToolkit\.Mvvm\.ComponentModel|System\.ComponentModel|System\.Collections\.Specialized)(?:\.\w+)*\.)?(?:UIElement|FrameworkElement|Panel|Grid|Border|StackPanel|Canvas|RelativePanel|Control|ContentControl|UserControl|Button|ButtonBase|Window|DependencyObject|SystemBackdrop|DataTemplateSelector|StyleSelector|ObservableObject|INotifyPropertyChanged|INotifyCollectionChanged)\b[^{;]*",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void EveryWinUiElementSubclass_AndEnclosingTypes_AreDeclaredPartial()
    {
        List<string> violations = new();

        foreach (string path in EnumerateProductSources())
        {
            string text = File.ReadAllText(path);
            foreach (Match match in WinUiSubclassDeclarationsRegex.Matches(text))
            {
                if (!match.Value.Contains("partial", StringComparison.Ordinal))
                {
                    violations.Add($"{RelativeToRepository(path)}: {match.Value.Trim()}");
                    continue;
                }

                // If nested inside an outer class, the outer class must also be partial
                // so CsWinRT's source generator can emit nested vtable members (CsWinRT1028).
                string prefix = text[..match.Index];
                foreach (Match outer in Regex.Matches(
                             prefix,
                             @"^[\t ]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*class\s+\w+[^{;]*\{",
                             RegexOptions.Multiline))
                {
                    // Check if this outer class is still open at match.Index by counting braces.
                    string between = text[outer.Index..match.Index];
                    int depth = between.Count(static c => c == '{') - between.Count(static c => c == '}');
                    if (depth > 0 && !outer.Value.Contains("partial", StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{RelativeToRepository(path)}: enclosing type not partial ({outer.Value.Trim()}) for {match.Value.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Every WinUI/DependencyObject subclass (and any enclosing class) must be declared partial " +
            "so CsWinRT generates Native AOT vtables instead of crashing with 0x80004002 (E_NOINTERFACE). Violations:\n" +
            string.Join('\n', violations));
    }

    private static IEnumerable<string> EnumerateProductSources()
    {
        foreach (string path in Directory.EnumerateFiles(
                     TestPaths.FromRepository("src/DeskBox"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string[] segments = path.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (segments.Contains("obj") || segments.Contains("bin"))
            {
                continue;
            }

            yield return path;
        }
    }

    private static string RelativeToRepository(string path)
    {
        string root = TestPaths.FromRepository("src/DeskBox")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..]
            : path;
    }
}
