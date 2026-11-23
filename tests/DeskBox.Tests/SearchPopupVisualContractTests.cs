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
            "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs"));

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
            "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs"));

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
        Assert.Contains("offset: checked((uint)resultOffset)", everything, StringComparison.Ordinal);
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
            "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs"));

        Assert.Contains("Tag=\"InteractiveSurface\"", fileSurface, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"2\"", fileSurface, StringComparison.Ordinal);
        Assert.Contains(
            "Padding=\"10,0\" Margin=\"0,1\" CornerRadius=\"10\" Height=\"44\"",
            resultRow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"MultiSelectedIcon\"",
            resultRow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Height=\"28\" Margin=\"8,0,8,2\" Padding=\"9,0,9,1\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Name=\"ResultFilterBar\"",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"AllowFocusOnInteraction\" Value=\"False\"/>",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(searchPopup, "<Setter Property=\"UseSystemFocusVisuals\" Value=\"True\"/>"));
        Assert.Contains(
            "<Setter Property=\"CornerRadius\" Value=\"5\"/>",
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
        // Footer material is the Win11 command-layer fill on the Mica base;
        // large in-app acrylic (SystemControlAcrylicElementBrush @ 0.5) was
        // retired because it read as a washed-out hole in light theme.
        Assert.Contains("Background=\"{ThemeResource LayerFillColorDefaultBrush}\"", searchPopup, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", searchPopup, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemControlAcrylicElementBrush", searchPopup, StringComparison.Ordinal);
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
        string popupCode =
            File.ReadAllText(Path.Combine(
                root,
                "src/DeskBox/Views/SearchPopupWindow.xaml.cs")) +
            "\n" +
            File.ReadAllText(Path.Combine(
                root,
                "src/DeskBox/Views/SearchPopupWindow.ResultsAndPreview.cs"));
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
            "<Setter Property=\"Padding\" Value=\"12,0\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "CornerRadius=\"14\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"TabsScrollViewer\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "PointerWheelChanged=\"TabsScrollViewer_PointerWheelChanged\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<StackPanel Orientation=\"Horizontal\" Spacing=\"6\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ManageTabsButton\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ManageTabsFlyout\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaxHeight=\"264\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Margin=\"8,8,8,0\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Target=\"TabContent.FontWeight\" Value=\"SemiBold\"/>",
            popupXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Glyph=\"{x:Bind Glyph, Mode=OneTime}\"",
            popupXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
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
    public void SearchInputAndFocus_NeverCallParameterlessGetFocusedElementOrRedirectInKeyDown()
    {
        string root = FindRepositoryRoot();
        string popupXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string popupCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        // WinUI 3 desktop FocusManager.GetFocusedElement() without XamlRoot always returns null,
        // which previously caused RootGrid_KeyDown to duplicate every typed digit/letter.
        Assert.DoesNotContain("FocusManager.GetFocusedElement()", popupCode, StringComparison.Ordinal);
        Assert.Contains("FocusManager.GetFocusedElement(xamlRoot)", popupCode, StringComparison.Ordinal);
        Assert.Contains("CharacterReceived=\"RootGrid_CharacterReceived\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("private void RootGrid_CharacterReceived(", popupCode, StringComparison.Ordinal);
        Assert.Contains("SearchTextBox.FocusState != FocusState.Unfocused", popupCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TryRedirectCharToSearchBox(", popupCode, StringComparison.Ordinal);

        // First-open IME focus + Escape routing contract:
        // 1) RootGrid keeps IsTabStop="True" as fallback focus sink so Escape always routes even if rows recycle.
        // 2) Show/Loaded/DispatcherQueue complete SearchTextBox.Focus(FocusState.Programmatic) before first keystroke.
        // 3) GotFocus and post-Focus() paths MUST NEVER call FocusContentIsland/FocusContentIslandIfForeground,
        //    which would overwrite XAML InputSite focus with DesktopWindowContentBridge and lock out input/Esc.
        Assert.Contains("<Grid x:Name=\"RootGrid\" IsTabStop=\"True\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("PointerPressed=\"SearchBoxBorder_PointerPressed\"", popupXaml, StringComparison.Ordinal);
        int textBoxStart = popupXaml.IndexOf("x:Name=\"SearchTextBox\"", StringComparison.Ordinal);
        Assert.True(textBoxStart >= 0);
        int textBoxTagEnd = popupXaml.IndexOf('>', textBoxStart);
        Assert.True(textBoxTagEnd > textBoxStart);
        string textBoxHeader = popupXaml.Substring(textBoxStart, textBoxTagEnd - textBoxStart + 1);
        Assert.Contains("VerticalAlignment=\"Center\"", textBoxHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("VerticalAlignment=\"Stretch\"", textBoxHeader, StringComparison.Ordinal);
        Assert.Contains("private bool TryFocusSearchTextBoxForInput()", popupCode, StringComparison.Ordinal);
        Assert.Contains("_pendingInitialSearchBoxFocus", popupCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusContentIslandIfForeground(_hwnd)", popupCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchPopup_Fluent2DetailsFooterAndHorizontalResizeContractsHold()
    {
        string root = FindRepositoryRoot();
        string popupXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string cmdPaletteCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.CommandPalette.cs"));
        string interactionCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.WindowInteraction.cs"));

        // Horizontal-only resize strips (no top/bottom resize edges) and responsive recommendations.
        Assert.Contains("x:Name=\"ResizeGrid\" Grid.RowSpan=\"5\" Canvas.ZIndex=\"20\" Visibility=\"Visible\" IsHitTestVisible=\"True\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Left\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Right\"", popupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Top\"", popupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Bottom\"", popupXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth=\"660\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("SearchPopupCustomWidth = SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(", interactionCode, StringComparison.Ordinal);
        Assert.Contains("SearchPopupCustomHeight = null;", interactionCode, StringComparison.Ordinal);

        // Details pane: responsive width (no hardcoded 266px), tightened radii, auto-hide redundant hero icon.
        Assert.DoesNotContain("Width=\"266\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsImagePreviewBorder\" Visibility=\"Collapsed\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"6\" Padding=\"4\" MaxHeight=\"192\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsImagePreview\" MaxHeight=\"184\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("ExpectsVisualImagePreview(item)", cmdPaletteCode, StringComparison.Ordinal);
        Assert.Contains("DetailsHeroIconHost.Visibility = Visibility.Collapsed;", cmdPaletteCode, StringComparison.Ordinal);

        // Footer command bar uses subtle Fluent 2 ghost buttons instead of nested bordered boxes.
        Assert.Contains("x:Key=\"CmdBarGhostButtonStyle\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CmdBarPrimaryButton\"", popupXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CmdBarGhostButtonStyle}\"", popupXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultNavigation_PreservesFilterKeysAndRefreshesSameIdentitySelection()
    {
        string root = FindRepositoryRoot();
        string popup = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Contains("FindVisualAncestor<TextBox>(focusedObject)", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultFilterComboBox", popup, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(previousSelection, _viewModel.SelectedItem)", popup, StringComparison.Ordinal);
        Assert.Contains("UpdateSelectionHighlight();", popup, StringComparison.Ordinal);
        Assert.Contains("FocusSelectedResult();", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void SortHeaderBar_IsExposedInSettingsAndCtrlKAndAlignedInsideLeftSplitColumn()
    {
        string root = FindRepositoryRoot();
        string popupXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string cmdPaletteCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SearchPopupWindow.CommandPalette.cs"));
        string settingsXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        string settingsCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs"));

        var doc = System.Xml.Linq.XDocument.Parse(popupXaml);
        System.Xml.Linq.XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        var splitGrid = doc.Descendants()
            .Single(el => (string?)el.Attribute(xNs + "Name") == "ResultsSplitGrid");
        var sortHeader = splitGrid.Descendants()
            .SingleOrDefault(el => (string?)el.Attribute(xNs + "Name") == "SortHeaderRow");
        var resultsPanel = splitGrid.Descendants()
            .SingleOrDefault(el => (string?)el.Attribute(xNs + "Name") == "ResultsPanel");

        Assert.NotNull(sortHeader);
        Assert.NotNull(resultsPanel);
        Assert.Same(sortHeader!.Parent, resultsPanel!.Parent);
        Assert.Equal("0", (string?)sortHeader.Parent?.Attribute("Grid.Column") ?? "0");

        Assert.Contains("x:Name=\"SearchShowSortHeaderToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Settings.Search.ShowSortHeader.Title\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("SearchShowSortHeaderToggle.IsOn = settings.ShowSortHeader;", settingsCode, StringComparison.Ordinal);
        Assert.Contains("_viewModel?.UpdatePreferences(new(ShowSortHeader: SearchShowSortHeaderToggle.IsOn));", settingsCode, StringComparison.Ordinal);

        Assert.Contains("Id: \"toggle-sort-header\"", cmdPaletteCode, StringComparison.Ordinal);
        Assert.Contains("ToggleSortHeaderPreference()", cmdPaletteCode, StringComparison.Ordinal);
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
