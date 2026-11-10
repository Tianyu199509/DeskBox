using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Mechanized wiring contracts for the settings shell. The facade-retirement
/// batches moved section state into feature editors and left three classes of
/// dangling wiring behind (bindings to deleted shell properties, editor
/// events the shell never subscribed, non-partial interop shapes). These
/// contracts scan live sources so a future batch that reintroduces any of
/// those fails fast in the unit-test suite instead of surviving until the
/// publish-level AOT audit.
/// </summary>
public sealed class SettingsWiringContractTests
{
    /// <summary>
    /// Bindings still referencing deleted shell facade visibility properties.
    /// The batch-44 regressions were fixed in 2b6b2e0d (rebound to editor
    /// properties); the list stays empty so any future dead binding fails
    /// immediately.
    /// </summary>
    private static readonly string[] PendingDeadBindingNames = [];

    /// <summary>
    /// Editor commit events the shell does not subscribe. The batch-47
    /// regressions were fixed in 2b6b2e0d; the list stays empty so any
    /// future unsubscribed editor event fails immediately.
    /// </summary>
    private static readonly (string EditorClassName, string EventName)[] PendingUnsubscribedEvents = [];

    [Fact]
    public void SettingsXamlClassicBindings_ResolveToLiveBindableSources()
    {
        HashSet<string> universe = CollectBindableNameUniverse();
        Assert.NotEmpty(universe);

        List<string> scannedFiles = new()
        {
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"),
        };
        scannedFiles.AddRange(Directory.EnumerateFiles(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections"),
            "*.xaml"));

        Dictionary<string, string> bindingSources = new();
        foreach (string path in scannedFiles)
        {
            string text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(
                         text,
                         @"\{Binding\s+([A-Za-z_]\w*)"))
            {
                // A token directly followed by '=' is a Binding parameter
                // (ElementName=, Path=, ...), not a property path.
                int followingIndex = match.Index + match.Length;
                if (followingIndex < text.Length && text[followingIndex] == '=')
                {
                    continue;
                }

                bindingSources[match.Groups[1].Value] = Path.GetFileName(path);
            }
        }

        Assert.NotEmpty(bindingSources);

        string[] unknown = bindingSources.Keys
            .Where(name => !universe.Contains(name))
            .Except(PendingDeadBindingNames)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unknown.Length == 0,
            "Settings XAML binds property names that no SettingsViewModel/editor " +
            "AotBindableProperties list and no [WinRT.GeneratedBindableCustomProperty] " +
            "type exposes - these are dead bindings to deleted facade names:\n" +
            string.Join('\n', unknown.Select(
                name => $"{name} ({bindingSources[name]})")));

        // Ratchet: exemption entries must still be needed. Once the pending
        // fix batch removes the dead bindings, delete the entries.
        string scannedText = string.Join(
            '\n',
            scannedFiles.Select(File.ReadAllText));
        string[] staleExemptions = PendingDeadBindingNames
            .Where(name => !bindingSources.ContainsKey(name))
            .ToArray();
        Assert.True(
            staleExemptions.Length == 0,
            "PendingDeadBindingNames entries no longer appear in the settings XAML; " +
            "delete them: " + string.Join(", ", staleExemptions));
        Assert.All(
            PendingDeadBindingNames,
            name => Assert.Contains("{Binding " + name, scannedText, StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsEditorPublicEvents_AreSubscribedByTheSettingsShell()
    {
        string shellText = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    TestPaths.FromRepository("src/DeskBox/ViewModels"),
                    "SettingsViewModel*.cs")
                .Select(File.ReadAllText));

        List<(string EditorClass, string EventName)> unsubscribed = new();

        foreach (string path in EnumerateEditorSources())
        {
            string text = File.ReadAllText(path);
            Match classMatch = Regex.Match(
                text,
                @"(?:class|record)\s+(\w*SettingsViewModel)\b");
            if (!classMatch.Success)
            {
                continue;
            }

            string editorClass = classMatch.Groups[1].Value;
            foreach (Match eventMatch in Regex.Matches(
                         text,
                         @"public\s+event\s+[\w<>,\s?.\[\]]+?\s*(\w+)\s*;"))
            {
                string eventName = eventMatch.Groups[1].Value;

                // Derive the shell-held field for this editor instance from
                // the constructor parameter and its assignment, then require
                // a field-qualified subscription so same-named events on
                // different editors cannot satisfy each other.
                Match parameter = Regex.Match(
                    shellText,
                    Regex.Escape(editorClass) + @"\s+(\w+)\s*[,\)]");
                Match field = parameter.Success
                    ? Regex.Match(
                        shellText,
                        @"(\w+)\s*=\s*" + Regex.Escape(parameter.Groups[1].Value) + @"\s*;")
                    : Match.Empty;
                bool subscribed = field.Success && Regex.IsMatch(
                    shellText,
                    Regex.Escape(field.Groups[1].Value) + @"\." +
                    Regex.Escape(eventName) + @"\s*\+=");

                if (!subscribed)
                {
                    unsubscribed.Add((editorClass, eventName));
                }
            }
        }

        string[] blocking = unsubscribed
            .Where(entry => !PendingUnsubscribedEvents.Contains(entry))
            .Select(entry => $"{entry.EditorClass}.{entry.EventName}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            blocking.Length == 0,
            "Settings editors declare public events the settings shell never " +
            "subscribes (no '<field>.<Event> +=' in SettingsViewModel*.cs):\n" +
            string.Join('\n', blocking));

        // Ratchet: exemption entries must still be needed. Once the pending
        // fix batch wires the Todo commit events, delete the entries.
        string[] staleExemptions = PendingUnsubscribedEvents
            .Where(entry => !unsubscribed.Contains(entry))
            .Select(entry => $"{entry.EditorClassName}.{entry.EventName}")
            .ToArray();
        Assert.True(
            staleExemptions.Length == 0,
            "PendingUnsubscribedEvents entries are subscribed now; delete them: " +
            string.Join(", ", staleExemptions));
    }

    /// <summary>
    /// Collects every property name the settings XAML can legitimately bind:
    /// the nameof(...) lists of [WinRT.GeneratedBindableCustomProperty]
    /// declarations (the shell and editor AotBindableProperties partials)
    /// plus the exposed surface of every attributed type (record positional
    /// parameters and public properties).
    /// </summary>
    private static HashSet<string> CollectBindableNameUniverse()
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        const string attribute = "[WinRT.GeneratedBindableCustomProperty";

        foreach (string path in EnumerateProductSources())
        {
            string text = File.ReadAllText(path);
            int search = 0;
            while ((search = text.IndexOf(attribute, search, StringComparison.Ordinal)) >= 0)
            {
                Match declaration = Regex.Match(
                    text[(search + attribute.Length)..],
                    @"(?:class|record|struct)\s+\w+");
                if (declaration.Success)
                {
                    int declarationStart = search + attribute.Length + declaration.Index;
                    foreach (Match nameOf in Regex.Matches(
                                 text[search..declarationStart],
                                 @"nameof\((\w+)\)"))
                    {
                        names.Add(nameOf.Groups[1].Value);
                    }

                    Match brace = Regex.Match(text[declarationStart..], @"[\{;]");
                    if (brace.Success)
                    {
                        string header = text[declarationStart..(declarationStart + brace.Index)];
                        foreach (Match parameter in Regex.Matches(
                                     header,
                                     @"([A-Za-z_]\w*)\s*(?:=[^,)]*)?(?:,|\))"))
                        {
                            names.Add(parameter.Groups[1].Value);
                        }

                        if (brace.Value == "{")
                        {
                            string body = ExtractBracedBody(text, declarationStart + brace.Index);
                            foreach (Match property in Regex.Matches(
                                         body,
                                         @"^[\t ]*public\s+(?:partial\s+)?[\w<>\[\],.?]+\s+([A-Za-z_]\w*)\s*(?:\{|=>)",
                                         RegexOptions.Multiline))
                            {
                                names.Add(property.Groups[1].Value);
                            }
                        }
                    }
                }

                search += attribute.Length;
            }
        }

        return names;
    }

    private static string ExtractBracedBody(string text, int openingBraceIndex)
    {
        int depth = 0;
        for (int i = openingBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[openingBraceIndex..(i + 1)];
                }
            }
        }

        return text[openingBraceIndex..];
    }

    private static IEnumerable<string> EnumerateEditorSources() =>
        Directory.EnumerateFiles(
            TestPaths.FromRepository("src/DeskBox/Features"),
            "*SettingsViewModel*.cs",
            SearchOption.AllDirectories);

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
}
