namespace DeskBox.Tests;

public sealed class RenderWindowContractTests
{
    /// <summary>
    /// Large folders must never hand their full item list to XAML: both item
    /// views bind the windowed prefix, otherwise entering a folder with
    /// thousands of items lays out every tile at once (measured 2026-09-14:
    /// ~50 s of UI-thread layout for 2165 items).
    /// </summary>
    [Fact]
    public void FileSurface_BindsTheRenderWindowInsteadOfTheFullProjection()
    {
        string xaml = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        string codeBehind = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        Assert.DoesNotContain(
            "ItemsSource=\"{Binding VisibleItems}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            CountOccurrences(xaml, "ItemsSource=\"{Binding RenderedItems}\""));
        Assert.Contains(
            "SyncActiveItemsViewSource()",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.RenderedItems",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "inactiveView.ItemsSource = null;",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AotBindableProperties_RegistersTheRenderWindow()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/ViewModels/WidgetViewModel.AotBindableProperties.cs"));

        Assert.Contains("nameof(RenderedItems)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Icons, folder counts, shortcut targets, and shell kinds must all draw
    /// from the windowed hydration universe so metadata work stays proportional
    /// to what the user can see. The fifth universe read is the
    /// HealReleasedItemIconsIfIdle predicate: release/acquire pairing needs
    /// the same universe to decide whether a heal pass has work to do.
    /// </summary>
    [Fact]
    public void ItemHydration_UsesTheRenderWindowUniverse()
    {
        string source = File.ReadAllText(GetRepoFile(
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));

        Assert.Equal(5, CountOccurrences(source, "HydrationUniverseItems"));
    }

    [Fact]
    public void Windowing_KeepsStackGroupingOnTheFullListAndResetsOnNavigation()
    {
        string windowing = File.ReadAllText(GetRepoFile(
            "src/DeskBox/ViewModels/WidgetViewModel.Windowing.cs"));
        string navigation = File.ReadAllText(GetRepoFile(
            "src/DeskBox/ViewModels/WidgetViewModel.Navigation.cs"));

        // Stack grouping reads ShellKind from every item, so the universe must
        // fall back to the full Items list while stacks are enabled.
        Assert.Contains("UsesStackProjection", windowing, StringComparison.Ordinal);
        // A window grown in one folder must not carry into the next one.
        Assert.Contains("ResetRenderWindow()", navigation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The activation threshold must be enforced where the window is computed:
    /// folders at or below it render everything. 1.5.1 shipped with the
    /// constant declared but never referenced, so every folder was capped at
    /// the initial 30-item prefix (feedback #4: "文件格子最多只能显示前30个").
    /// </summary>
    [Fact]
    public void ReconcileRenderWindow_RendersFoldersWithinTheActivationThresholdInFull()
    {
        string windowing = File.ReadAllText(GetRepoFile(
            "src/DeskBox/ViewModels/WidgetViewModel.Windowing.cs"));

        // The threshold check lives in the shared prefix mirror
        // (ReconcileRenderWindowPrefix) since the 2026-09-15 reveal fix;
        // behavior is additionally pinned by RenderWindowBehaviorTests.
        Assert.Contains(
            "visibleItemCount <= RenderWindowActivationThreshold",
            windowing,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A rendered prefix that fits inside the viewport has no overflow to
    /// scroll, so ViewChanged never fires and growth must also run after
    /// layout passes (initial load, item changes, viewport resizes) or tall
    /// widgets stay stuck at the initial window size forever (feedback #4).
    /// </summary>
    [Fact]
    public void RenderWindow_GrowsWhenTheRenderedPrefixDoesNotOverflowTheViewport()
    {
        string renderWindow = File.ReadAllText(GetRepoFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.RenderWindow.cs"));

        Assert.Contains(
            "ItemsGrid.LayoutUpdated += ItemsView_LayoutUpdatedForRenderWindow",
            renderWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsList.LayoutUpdated += ItemsView_LayoutUpdatedForRenderWindow",
            renderWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExtentHeight <= scrollViewer.ViewportHeight",
            renderWindow,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string GetRepoFile(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate repository file: {relativePath}");
    }
}
