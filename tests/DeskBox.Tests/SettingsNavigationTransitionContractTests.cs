namespace DeskBox.Tests;

/// <summary>
/// Pins the settings-window navigation transition wiring: the horizontal
/// enter animation, the synchronous scroll-to-top that must precede it,
/// the same-section re-entry guard, and the direction derivation from
/// route depth. These are behavioral contracts of the settings navigation
/// flow; regressions here have previously shipped as visible UI bugs.
/// </summary>
public sealed class SettingsNavigationTransitionContractTests
{
    [Fact]
    public void DetailPageTransitionHelper_ExposesHorizontalNavigationEnter()
    {
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/DetailPageTransitionHelper.cs");

        Assert.Contains(
            "public static void PlayNavigationEnter(UIElement element, float fromOffsetX)",
            helper,
            StringComparison.Ordinal);
        // The navigation variant must keep honoring the system animation
        // toggle the same way the vertical detail transitions do.
        int gateCount = CountOccurrences(
            helper,
            "if (!WindowsCompatibilityService.AreAnimationsEnabled)");
        Assert.True(gateCount >= 2, "PlayNavigationEnter must gate on AreAnimationsEnabled");
    }

    [Fact]
    public void ShowSettingsSection_PlaysEnterTransitionAfterVisibilityAndScrollReset()
    {
        string navigation = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains(
            "PlaySettingsSectionEnterTransition(visibleSectionTag, previousSectionTag, sectionTag, inlineSectionTag);",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "DetailPageTransitionHelper.PlayNavigationEnter(enteringSection, enterOffsetX);",
            navigation,
            StringComparison.Ordinal);

        // Scroll reset must run synchronously inside ShowSettingsSection
        // (before the animation), not only in the low-priority dispatch —
        // otherwise the sliding page renders at a stale offset and jumps.
        string scrollReset = "PageScroller.ChangeView(null, 0, null, disableAnimation: true);";
        Assert.Contains(scrollReset, navigation, StringComparison.Ordinal);
        Assert.Single(ExtractLines(navigation, scrollReset));
        int resetIndex = navigation.IndexOf(scrollReset, StringComparison.Ordinal);
        int transitionIndex = navigation.IndexOf(
            "PlaySettingsSectionEnterTransition(visibleSectionTag",
            StringComparison.Ordinal);
        Assert.True(
            resetIndex >= 0 && transitionIndex > resetIndex,
            "scroll reset must precede the enter transition");
    }

    [Fact]
    public void ShowSettingsSection_SkipsSameSectionReentryOnceLoaded()
    {
        string navigation = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains("if (SettingsRoot.IsLoaded &&", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(sectionTag, _currentSettingsSection, StringComparison.Ordinal))",
            navigation,
            StringComparison.Ordinal);
        // The guard must run before the section switch bookkeeping starts
        // (before the current-section field is overwritten), i.e. the guard
        // line precedes the assignment.
        int guardIndex = navigation.IndexOf(
            "string.Equals(sectionTag, _currentSettingsSection, StringComparison.Ordinal))",
            StringComparison.Ordinal);
        int assignIndex = navigation.IndexOf(
            "_currentSettingsSection = sectionTag;",
            StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && assignIndex > guardIndex);
    }

    [Fact]
    public void TransitionDirection_FollowsRouteDepthThenNavOrder()
    {
        string navigation = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains(
            "return nextDepth > previousDepth ? NavigationEnterOffsetPx : -NavigationEnterOffsetPx;",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains("private static int GetSectionRouteDepth(string sectionTag)", navigation, StringComparison.Ordinal);
        // Lateral moves resolve through the flattened nav menu order, and
        // sibling sub-pages sharing one nav entry fall through to their
        // drill-down order in SubSectionTagOrder.
        Assert.Contains(
            "navComparison > 0 ? NavigationEnterOffsetPx : -NavigationEnterOffsetPx;",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "nextKey.Value.SubIndex > previousKey.Value.SubIndex",
            navigation,
            StringComparison.Ordinal);
        // CsWinRT foreach over projected IVector collections has thrown
        // InvalidCast before; the tag flattening must keep indexer loops.
        Assert.DoesNotContain(
            "foreach (object item in menuItems)",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains("for (int index = 0; index < menuItems.Count; index++)", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void DetermineNavigationEnterOffset_DepthThenNavOrderThenSubOrder()
    {
        string[] navOrder = new[] { "General", "AppearanceDetail" };
        string[] subOrder = new[]
        {
            "FileDisplaySettings", "ManagedStorage", "FileStackSettings", "DesktopOrganizationSettings"
        };

        float Forward(string previous, string next) =>
            DeskBox.Views.SettingsWindow.DetermineNavigationEnterOffset(previous, next, navOrder, subOrder);

        // Drilling into a sub-page enters from the right; backing out left.
        Assert.Equal(40f, Forward("General", "FileDisplaySettings"));
        Assert.Equal(-40f, Forward("FileDisplaySettings", "General"));

        // Same-depth top-level pages follow the nav menu order.
        Assert.Equal(40f, Forward("General", "AppearanceDetail"));
        Assert.Equal(-40f, Forward("AppearanceDetail", "General"));

        // Sibling sub-pages under one nav entry follow their drill-down
        // order instead of always sliding in from the left.
        Assert.Equal(40f, Forward("FileDisplaySettings", "FileStackSettings"));
        Assert.Equal(-40f, Forward("FileStackSettings", "FileDisplaySettings"));

        // Unknown tags keep the forward default rather than guessing.
        Assert.Equal(40f, Forward("General", "NoSuchSection"));
    }

    private static string[] ExtractLines(string content, string needle)
    {
        return content
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(line => line.Contains(needle, StringComparison.Ordinal))
            .ToArray();
    }

    private static int CountOccurrences(string content, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
