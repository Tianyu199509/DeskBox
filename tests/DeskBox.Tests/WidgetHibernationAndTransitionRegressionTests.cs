using System.Text.RegularExpressions;
using Xunit;

namespace DeskBox.Tests;

public sealed class WidgetHibernationAndTransitionRegressionTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskBox.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string StripXmlCommentsAndDataTemplates(string xaml)
    {
        string withoutComments = Regex.Replace(xaml, @"<!--[\s\S]*?-->", string.Empty);

        // Iteratively strip innermost <DataTemplate ...>...</DataTemplate> blocks
        // so nested DataTemplates cannot leak inner {x:Bind} into root scope.
        var leafDataTemplatePattern = new Regex(
            @"<DataTemplate\b[^>]*>(?:(?!</?DataTemplate\b)[\s\S])*?</DataTemplate>",
            RegexOptions.CultureInvariant);

        string current = withoutComments;
        while (true)
        {
            string next = leafDataTemplatePattern.Replace(current, string.Empty);
            if (string.Equals(next, current, StringComparison.Ordinal))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    [Fact]
    public void XamlTypesWithoutRootXBind_NeverCallUncheckedRootBindingsMember()
    {
        var uncheckedBindingsCall = new Regex(@"(?<![\w?.])Bindings\s*\.", RegexOptions.CultureInvariant);
        var xamlFiles = Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src"),
                "*.xaml",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var dataTemplateOnlyControls = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (string xamlPath in xamlFiles)
        {
            string rawXaml = File.ReadAllText(xamlPath);
            if (!rawXaml.Contains("{x:Bind", StringComparison.Ordinal))
            {
                continue;
            }

            string rootScopeXaml = StripXmlCommentsAndDataTemplates(rawXaml);
            if (rootScopeXaml.Contains("{x:Bind", StringComparison.Ordinal))
            {
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(xamlPath);
            dataTemplateOnlyControls.Add(baseName);

            string directory = Path.GetDirectoryName(xamlPath)!;
            IEnumerable<string> codeBehindFiles = Directory.EnumerateFiles(
                directory,
                $"{baseName}*.cs",
                SearchOption.TopDirectoryOnly);

            foreach (string csPath in codeBehindFiles)
            {
                string fileName = Path.GetFileName(csPath);
                if (!string.Equals(fileName, $"{baseName}.xaml.cs", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.StartsWith($"{baseName}.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string source = File.ReadAllText(csPath);
                string withoutComments = Regex.Replace(
                    source,
                    @"//.*?$|/\*[\s\S]*?\*/",
                    string.Empty,
                    RegexOptions.Multiline);

                if (uncheckedBindingsCall.IsMatch(withoutComments))
                {
                    string rel = Path.GetRelativePath(RepoRoot, csPath).Replace('\\', '/');
                    offenders.Add(rel);
                }
            }
        }

        // Guard the test's own sensitivity: both crash-dump culprits use {x:Bind}
        // exclusively inside DataTemplates and must stay covered by this scan.
        Assert.Contains("SearchWidgetContent", dataTemplateOnlyControls);
        Assert.Contains("QuickCaptureSurfaceContent", dataTemplateOnlyControls);
        Assert.Empty(offenders);
    }

    [Fact]
    public void GlanceWidgetContent_RunTransition_StopsStoryboardBeforeClearingChildren()
    {
        string source = ReadRepoFile("src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs");
        int start = source.IndexOf("private void RunTransition(", StringComparison.Ordinal);
        int end = source.IndexOf("private static void AddAnimation(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        string method = source[start..end];
        int completedIndex = method.IndexOf("completedHandler = (_, _) =>", StringComparison.Ordinal);
        int detachIndex = method.IndexOf("storyboard.Completed -= completedHandler;", StringComparison.Ordinal);
        int stopIndex = method.IndexOf("storyboard.Stop();", StringComparison.Ordinal);
        int clearIndex = method.IndexOf("storyboard.Children.Clear();", StringComparison.Ordinal);

        Assert.True(completedIndex >= 0, "RunTransition must attach a named Completed handler so it can detach on completion.");
        Assert.True(detachIndex > completedIndex, "Completed handler must detach itself.");
        Assert.True(stopIndex > detachIndex, "Completed handler must stop the HoldEnd storyboard.");
        Assert.True(clearIndex > stopIndex, "storyboard.Stop() must precede storyboard.Children.Clear() to avoid COMException 0x800F1000.");
    }

    [Fact]
    public void AllStoryboardChildrenClearSites_StopStoryboardFirst()
    {
        var clearPattern = new Regex(
            @"(?<target>[A-Za-z0-9_]+)\??\.Children\.Clear\(\)",
            RegexOptions.CultureInvariant);

        var csFiles = Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var offenders = new List<string>();
        foreach (string csPath in csFiles)
        {
            string[] lines = File.ReadAllLines(csPath);
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = clearPattern.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                string target = match.Groups["target"].Value;
                if (!target.Contains("storyboard", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(target, "active", StringComparison.Ordinal))
                {
                    continue;
                }

                int windowStart = Math.Max(0, i - 8);
                string precedingWindow = string.Join('\n', lines[windowStart..i]);
                if (!precedingWindow.Contains($"{target}.Stop()", StringComparison.Ordinal) &&
                    !precedingWindow.Contains($"{target}?.Stop()", StringComparison.Ordinal))
                {
                    string rel = Path.GetRelativePath(RepoRoot, csPath).Replace('\\', '/');
                    offenders.Add($"{rel}:{i + 1}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void WidgetWindowOneShotTimer_RoutesUnhandledTickExceptionsThroughAppLifelineReporter()
    {
        string collapseSource = ReadRepoFile("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        int timerStart = collapseSource.IndexOf("private sealed class OwnedOneShotDispatcherTimer", StringComparison.Ordinal);
        Assert.True(timerStart >= 0);

        string timerSlice = collapseSource[timerStart..];
        Assert.Contains("ReportUnhandledUiCallbackException(", timerSlice, StringComparison.Ordinal);
        Assert.Contains("throw;", timerSlice, StringComparison.Ordinal);

        string appSource = ReadRepoFile("src/DeskBox/App.xaml.cs");
        int reportStart = appSource.IndexOf("internal void ReportUnhandledUiCallbackException(", StringComparison.Ordinal);
        int searchMarker = appSource.IndexOf("// ─── Search Services ─", reportStart, StringComparison.Ordinal);
        Assert.True(reportStart >= 0 && searchMarker > reportStart);

        string reportSlice = appSource[reportStart..searchMarker];
        Assert.Contains("Log($\"Unhandled exception ({source}): {exception}\");", reportSlice, StringComparison.Ordinal);
        Assert.Contains("if (!IsStartupLifelineEstablished)", reportSlice, StringComparison.Ordinal);
        Assert.Contains("FailStartup(", reportSlice, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetContentAdapters_NeverHibernateLiveViewsFromOnWindowLongHidden()
    {
        var hibernateCallPattern = new Regex(@"\bHibernateCollapsedView\s*\(", RegexOptions.CultureInvariant);
        var csFiles = Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var callSites = new List<string>();
        foreach (string csPath in csFiles)
        {
            string rel = Path.GetRelativePath(RepoRoot, csPath).Replace('\\', '/');
            if (string.Equals(rel, "src/DeskBox/Controls/WidgetContents/WidgetContentAdapterBase.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string withoutComments = Regex.Replace(
                File.ReadAllText(csPath),
                @"//.*?$|/\*[\s\S]*?\*/",
                string.Empty,
                RegexOptions.Multiline);

            if (hibernateCallPattern.IsMatch(withoutComments))
            {
                callSites.Add(rel);
            }
        }

        // Only WidgetShell.HibernateHostedContentView() may call HibernateCollapsedView(),
        // because it detaches ShellContent = null first while collapsed. Calling it from
        // Adapter.OnWindowLongHidden() strands a disposed view inside expanded ShellContent.
        Assert.Equal(["src/DeskBox/Controls/WidgetShell.xaml.cs"], callSites);

        string shellSource = ReadRepoFile("src/DeskBox/Controls/WidgetShell.xaml.cs");
        int methodStart = shellSource.IndexOf("internal bool HibernateHostedContentView()", StringComparison.Ordinal);
        int methodEnd = shellSource.IndexOf("internal bool EnsureHostedContentViewAttached()", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string methodSlice = shellSource[methodStart..methodEnd];
        int detachIndex = methodSlice.IndexOf("ShellContent = null;", StringComparison.Ordinal);
        int hibernateIndex = methodSlice.IndexOf("adapter.HibernateCollapsedView()", StringComparison.Ordinal);
        Assert.True(detachIndex >= 0 && hibernateIndex > detachIndex);

        string clipboardAdapter = ReadRepoFile("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContentAdapter.cs");
        Assert.Contains("public override Task InitializeAsync()", clipboardAdapter, StringComparison.Ordinal);
        int longHiddenStart = clipboardAdapter.IndexOf("public override void OnWindowLongHidden()", StringComparison.Ordinal);
        int nextMember = clipboardAdapter.IndexOf("object? IWidgetTransientStateContent.CaptureTransientState()", longHiddenStart, StringComparison.Ordinal);
        Assert.True(longHiddenStart >= 0 && nextMember > longHiddenStart);
        string longHiddenSlice = clipboardAdapter[longHiddenStart..nextMember];
        Assert.DoesNotContain("SetViewAttached(false)", longHiddenSlice, StringComparison.Ordinal);
    }
}
