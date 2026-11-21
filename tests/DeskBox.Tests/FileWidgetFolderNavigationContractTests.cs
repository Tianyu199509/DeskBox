namespace DeskBox.Tests;

public sealed class FileWidgetFolderNavigationContractTests
{
    [Fact]
    public void FileSurface_UsesUnifiedPinnedNavigationOutsideItemData()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));

        Assert.Contains(
            "x:Name=\"FolderNavigationBar\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding FolderNavigationVisibility}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FolderNavigationRootButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0.24\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,0,4,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"7\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"*,Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<PathIcon", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Data=\"M6 15.5c0 .28.22.5.5.5H11",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Symbol=\"GoToStart\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderOpenInExplorerButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness=\"0,0,0,1\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IconFolderNavigationBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ListFolderNavigationBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding FolderNavigation",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"FolderNavigationLoadingOverlay\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_PreparesVerticalTransitionBeforeReplacingItems()
    {
        string root = FindRepositoryRoot();
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));
        string hydration = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));

        Assert.Contains(
            "new Vector3(0, navigatingUp ? -16 : 16, 0)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("contentVisual.Opacity = 0", surface, StringComparison.Ordinal);
        Assert.Contains("beforeItemsReplaced?.Invoke()", hydration, StringComparison.Ordinal);
        Assert.True(
            hydration.IndexOf("beforeItemsReplaced?.Invoke()", StringComparison.Ordinal) <
            hydration.IndexOf("SyncFolderItems(items)", StringComparison.Ordinal));
        Assert.Contains(
            "TimeSpan.FromSeconds(1)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowDelayedFolderNavigationLoadingAsync",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "allowFolderPathTransition: true",
            File.ReadAllText(Path.Combine(
                root,
                "src/DeskBox/ViewModels/WidgetViewModel.Navigation.cs")),
            StringComparison.Ordinal);
        Assert.Contains("Task.Run(", hydration, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_DefersShortcutResolutionUntilAfterFirstFrame()
    {
        string root = FindRepositoryRoot();
        string fileService = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/FileService.cs"));
        string shortcutHelper = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Helpers/ShortcutHelper.cs"));
        string hydration = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));

        Assert.Contains("loadShortcutTarget: false", fileService, StringComparison.Ordinal);
        Assert.Contains(
            "HydrateShortcutTargetsThenShellKindsAsync",
            hydration,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetStoredShortcutTargetAsync",
            hydration,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanApplyHydrationResult(item, expectedPath, generation)",
            hydration,
            StringComparison.Ordinal);

        int fastReadStart = shortcutHelper.IndexOf(
            "private static ShortcutInfo? ReadStoredMetadataUncached",
            StringComparison.Ordinal);
        int fastReadEnd = shortcutHelper.IndexOf(
            "internal static string? TryReadEffectiveTargetPath",
            fastReadStart,
            StringComparison.Ordinal);
        Assert.True(fastReadStart >= 0 && fastReadEnd > fastReadStart);
        string fastReadBody = shortcutHelper[fastReadStart..fastReadEnd];
        Assert.Contains("ShortcutNativeBackend.ReadStoredRaw(", fastReadBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveNoUi(", fastReadBody, StringComparison.Ordinal);
        Assert.DoesNotContain("link.Resolve(", fastReadBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_SeparatesMappedRootFromCurrentFolderOperations()
    {
        string root = FindRepositoryRoot();
        string navigation = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Navigation.cs"));
        string operations = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs"));
        string watchers = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.SortingAndWatchers.cs"));

        Assert.Contains("CurrentFolderPath", navigation, StringComparison.Ordinal);
        Assert.Contains("MappedFolderPath", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "destinationFolderPath = CurrentFolderPath",
            operations,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsCurrentWatcherBatch(changeBatch, CurrentFolderPath)",
            watchers,
            StringComparison.Ordinal);
        Assert.Contains("!IsAtMappedRoot", watchers, StringComparison.Ordinal);
        Assert.Contains(
            "IsEmbeddedFolderNavigationEnabled && !IsAtMappedRoot",
            navigation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_KeepsFolderShortcutsAsFilesWhileRoutingActivation()
    {
        string root = FindRepositoryRoot();
        string navigation = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Navigation.cs"));
        string surface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));
        string dragPackage = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemDragPackage.cs"));

        Assert.Contains(
            "NavigateIntoFolderShortcutAsync",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "FolderNavigationPathPolicy.TryNormalizeShortcutTargetPath",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "FolderNavigationPathPolicy.TryResolve",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains("await Task.Run(", navigation, StringComparison.Ordinal);
        Assert.Contains(
            "FolderNavigationPathPolicy.IsFolderShortcutCandidate(item)",
            surface,
            StringComparison.Ordinal);
        int noOpNavigationIndex = surface.IndexOf(
            "if (FolderNavigationPathPolicy.ArePathsEqual(",
            StringComparison.Ordinal);
        Assert.True(noOpNavigationIndex >= 0);
        Assert.True(
            surface.IndexOf(
                "await OpenFileItemAsync(item)",
                noOpNavigationIndex,
                StringComparison.Ordinal) > noOpNavigationIndex);
        Assert.Contains(
            ".Select(item => item.Path)",
            dragPackage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Select(item => item.TargetPath)",
            dragPackage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_ReturnKeepsFolderVisibleWithoutRestoringSelection()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));
        int completeStart = source.IndexOf(
            "private void CompleteFolderNavigationVisuals(", StringComparison.Ordinal);
        int scrollStart = source.IndexOf(
            "private void ScrollExitedFolderIntoView(", StringComparison.Ordinal);
        int scrollEnd = source.IndexOf(
            "private void AnimateFolderNavigation(", StringComparison.Ordinal);

        Assert.True(completeStart >= 0 && scrollStart > completeStart && scrollEnd > scrollStart);
        Assert.Contains("ClearSelection()", source[completeStart..scrollStart], StringComparison.Ordinal);
        Assert.Contains("ScrollExitedFolderIntoView(exitedFolderPath)", source[..completeStart], StringComparison.Ordinal);
        string scroll = source[scrollStart..scrollEnd];
        Assert.Contains("_isDisposed || _isFolderNavigationOperationActive", scroll, StringComparison.Ordinal);
        Assert.Contains("activeView.Items.Contains(folder)", scroll, StringComparison.Ordinal);
        Assert.Contains("activeView.ScrollIntoView(folder)", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItems", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedItem =", scroll, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreExitedFolderSelection", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DeskBox.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
