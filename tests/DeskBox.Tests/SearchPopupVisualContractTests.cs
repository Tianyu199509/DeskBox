namespace DeskBox.Tests;

public sealed class SearchPopupVisualContractTests
{
    [Fact]
    public void BackgroundProviderRefresh_DoesNotReplayTheUserSearchEntrance()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("ProviderRefreshDebounceDelay = TimeSpan.FromSeconds(1)", viewModel, StringComparison.Ordinal);
        Assert.Contains("SearchRefreshKind.ProviderUpdate && !response.IsComplete", viewModel, StringComparison.Ordinal);
        Assert.Contains("HasSameIdentitySequence", viewModel, StringComparison.Ordinal);
        Assert.Contains("ReuseExistingInstances", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (_viewModel.IsApplyingBackgroundResultRefresh)", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultInteraction_SelectsOnClickAndOpensOnlyOnDoubleClickOrEnter()
    {
        string root = FindRepositoryRoot();
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("var item = ResolveResultItem(source);", popup, StringComparison.Ordinal);
        Assert.Contains("var item = ResolveResultItem(e.OriginalSource as DependencyObject);", popup, StringComparison.Ordinal);
        Assert.Contains("FindItemRow(element)?.Item ?? FindDataContext<SearchResultItem>(element)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceEquals(_pressedItem, releasedItem)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("[DIAG] ResultsPanel_DoubleTapped", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void InstantSearch_UsesShortDebounceWithoutBlockingLoaderAndPagesOnDemand()
    {
        string root = FindRepositoryRoot();
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));
        string popupXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));
        string everything = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/EverythingSearchService.cs"));

        Assert.Contains("TimeSpan.FromMilliseconds(35)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(150)", popup, StringComparison.Ordinal);
        Assert.Contains("SearchProgressBar.Visibility = Visibility.Collapsed", popup, StringComparison.Ordinal);
        Assert.Contains("LoadingPanel.Visibility = Visibility.Collapsed", popup, StringComparison.Ordinal);
        Assert.Contains("ViewChanged=\"ResultsPanel_ViewChanged\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("LoadMoreResultsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("LoadMoreAndAdvanceSelectionAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("_nextFileResultOffset", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetOffset", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxMaterializedFileResults", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Bind Count", popupXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSettings_ExposeEverythingWithoutAVisibleResultLimit()
    {
        string root = FindRepositoryRoot();
        string settings = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        string engine = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/SearchEngineService.cs"));

        Assert.Contains("EverythingConsentCheckBox", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchSystemNoiseToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Search.Privacy", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchSystemIndexToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchCustomIndexerToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchRustPreviewToggle", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchMaxResultsComboBox", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsIndexSearchService", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("UsnJournalIndexService", engine, StringComparison.Ordinal);
        Assert.Contains("SearchFileQueryPage", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void FileAndSearchRows_UseCompactNativeAlignedSurfaces()
    {
        string root = FindRepositoryRoot();
        string fileSurface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string resultRow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/SearchResultRowControl.xaml"));
        string searchPopup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string searchInteractions = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("Tag=\"InteractiveSurface\"", fileSurface, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"4\"", fileSurface, StringComparison.Ordinal);
        Assert.Contains(
            "Padding=\"4,5\" Margin=\"0,1\" CornerRadius=\"4\"",
            resultRow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Margin=\"12,2\" Padding=\"4,3\" CornerRadius=\"4\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"AllowFocusOnInteraction\" Value=\"False\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"UseSystemFocusVisuals\" Value=\"False\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"CornerRadius\" Value=\"2\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"InteractionSurface\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Storyboard.TargetName=\"InteractionSurface\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains("SortTypeDivider", searchPopup, StringComparison.Ordinal);
        Assert.Contains("SortSizeDivider", searchPopup, StringComparison.Ordinal);
        Assert.Contains("SortDateDivider", searchPopup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FooterAcrylicSurface\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0.5\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("SortHeaderBackground", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"-12,0,0,0\"", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPointerOnRowInteractivePart", searchInteractions, StringComparison.Ordinal);
        Assert.Contains("OnRubberBandAutoScrollTick", searchInteractions, StringComparison.Ordinal);
    }

    [Fact]
    public void Tabs_KeepIndicatorWithLabelAndDoNotStealKeyboardFocus()
    {
        string root = FindRepositoryRoot();
        string popupXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string popupCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));
        string models = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Models/SearchModels.cs"));

        Assert.Contains(
            "PreviewKeyDown=\"RootGrid_PreviewKeyDown\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllowFocusOnInteraction=\"False\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"HorizontalContentAlignment\" Value=\"Left\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"MinWidth\" Value=\"0\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Padding\" Value=\"14,4\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<RowDefinition Height=\"8\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Glyph=\"{x:Bind Glyph, Mode=OneTime}\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind SelectionIndicatorVisibility, Mode=OneWay}\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"TabSelectionIndicator\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains("RootGrid_PreviewKeyDown", popupCode, StringComparison.Ordinal);
        Assert.Contains("TryMoveResultSelection", popupCode, StringComparison.Ordinal);
        Assert.Contains("QueueTabSelectionRefreshAndFocus", popupCode, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", popupCode, StringComparison.Ordinal);
        Assert.Contains("SelectionIndicatorVisibility", models, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultNavigation_PreservesFilterKeysAndRefreshesSameIdentitySelection()
    {
        string root = FindRepositoryRoot();
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("FindVisualAncestor<TextBox>(focusedObject)", popup, StringComparison.Ordinal);
        Assert.Contains("IsVisualDescendantOf(focusedObject, ResultFilterComboBox)", popup, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(previousSelection, _viewModel.SelectedItem)", popup, StringComparison.Ordinal);
        Assert.Contains("UpdateSelectionHighlight();", popup, StringComparison.Ordinal);
        Assert.Contains("FocusSelectedResult();", popup, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
