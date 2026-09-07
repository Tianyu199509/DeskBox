using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class MarkdownAndSplitterContractTests
{
    [Fact]
    public void Foundation_UsesStableToolkitSplitterWithCompactGutterAndWideHitTarget()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        string appXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));

        Assert.Contains("CommunityToolkit.WinUI.Controls.Sizers", project, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", appXaml, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"Control\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SplitterHoverTrack\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"SplitterThumb.Width\" Value=\"64\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"SplitterThumb.Height\" Value=\"2\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"SplitterThumb.RadiusX\" Value=\"1\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"2\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"24\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("SplitterWidth = 8", File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MasterDetailLayoutPolicy.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_PreservesUndoSelectionAndViewportAcrossFormattingCommands()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs"));

        Assert.Contains("x:Name=\"FormattingToolbar\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<CommandBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppBarButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"30\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Spacing=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"EditorLayoutRoot_SizeChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FormattingMoreButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FormattingMoreFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownFormattingMenuButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("Width\" Value=\"132\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height\" Value=\"30\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"18\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnSpacing=\"7\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StrikeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TableButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"S&#x0336;\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Symbol=\"Clear\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"“\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Symbol=\"Comment\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Cambria\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureFormattingButtonIcons", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AppBarButton.IsInOverflowProperty", code, StringComparison.Ordinal);
        Assert.Contains("FormattingMoreFlyout.Hide()", code, StringComparison.Ordinal);
        Assert.Contains("FormattingMenuPanel.AddHandler", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ListMenuButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TableMenuButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CalculateVisibleToolbarCommandCount", code, StringComparison.Ordinal);
        Assert.Contains("menuButtons[index].Visibility", code, StringComparison.Ordinal);
        Assert.Contains("SetButtonText(ListMenuButton, ListMenuButtonText", code, StringComparison.Ordinal);
        Assert.Contains("PrepareEditorCommandViewport", code, StringComparison.Ordinal);
        Assert.Contains("RestoreEditorViewport", code, StringComparison.Ordinal);
        Assert.Contains("previous with", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorTextBox.LostFocus +=", code, StringComparison.Ordinal);
        Assert.Contains("PointerPressedEvent", code, StringComparison.Ordinal);
        Assert.Contains("PointerReleasedEvent", code, StringComparison.Ordinal);
        Assert.Contains("TappedEvent", code, StringComparison.Ordinal);
        Assert.Contains("KeyUpEvent", code, StringComparison.Ordinal);
        Assert.Contains("RememberEditorViewport", code, StringComparison.Ordinal);
        Assert.Contains("_isEditorPointerActive", code, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"EditorTextBox_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EditorTextBox.SelectedText = replacement", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", code, StringComparison.Ordinal);
        Assert.Contains("TryContinueMarkdownList", code, StringComparison.Ordinal);
        Assert.Contains("MarkdownEditCommandEngine.TryCreateEdit", code, StringComparison.Ordinal);
        Assert.Contains(
            "LargeDocumentThreshold = MarkdownDocumentService.MaxCharacters",
            code,
            StringComparison.Ordinal);
        Assert.Contains("UpdateLargeDocumentBehavior", code, StringComparison.Ordinal);
        Assert.Contains("BeforeTextChanging=\"EditorTextBox_BeforeTextChanging\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ContainsInlineDataImage(args.NewText)", code, StringComparison.Ordinal);
        Assert.Contains("TextTruncated?.Invoke", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(28, 0)]
    [InlineData(57, 1)]
    [InlineData(144, 4)]
    [InlineData(260, 8)]
    [InlineData(288, 8)]
    [InlineData(289, 10)]
    [InlineData(800, 10)]
    public void Editor_ResponsiveToolbarUsesAllAvailableWidth(
        double availableWidth,
        int expectedVisibleCommands) =>
        Assert.Equal(
            expectedVisibleCommands,
            DeskBox.Controls.MarkdownSourceEditor.CalculateVisibleToolbarCommandCount(
                availableWidth,
                commandCount: 10));

    [Fact]
    public void Reader_DisablesHtmlAndBlocksRemoteImagesByDefault()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/MarkdownDocumentService.cs"));
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains(".DisableHtml()", service, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(false, OnDocumentPropertyChanged)", reader, StringComparison.Ordinal);
        Assert.Contains("IsAllowedLink", reader, StringComparison.Ordinal);
        Assert.Contains("AttachmentResolver", reader, StringComparison.Ordinal);
        Assert.Contains("UseInternalScrollViewer", reader, StringComparison.Ordinal);
        Assert.Contains("private readonly RichTextBlock _documentText", reader, StringComparison.Ordinal);
        Assert.Contains("_documentText.Blocks.Add", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly StackPanel _documentPanel", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RendersTablesCodeBlocksAndLinksAsDistinctVisualElements()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains("TableColumnMinimumWidth", reader, StringComparison.Ordinal);
        Assert.Contains("Content = tableGrid", reader, StringComparison.Ordinal);
        Assert.Contains("Child = codeText", reader, StringComparison.Ordinal);
        Assert.Contains("ControlFillColorSecondaryBrush", reader, StringComparison.Ordinal);
        Assert.Contains("CardStrokeColorDefaultBrush", reader, StringComparison.Ordinal);
        Assert.Contains("CreateHyperlink()", reader, StringComparison.Ordinal);
        Assert.Contains("TextDecorations.Underline", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("new Run { Text = \"| \" }", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_UsesHostingWidgetForegroundAndHighContrastSemanticText()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains("UpdateForegrounds();", reader, StringComparison.Ordinal);
        Assert.Contains("_documentText.Foreground = _contentForeground", reader, StringComparison.Ordinal);
        Assert.Contains("ForegroundProperty,", reader, StringComparison.Ordinal);
        Assert.Contains(
            "InvalidateRenderForAppearanceChange(\"foreground\")",
            reader,
            StringComparison.Ordinal);
        Assert.Contains(
            "_contentForeground = Foreground ?? BrushResource(\"TextFillColorPrimaryBrush\")",
            reader,
            StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetParent(current)", reader, StringComparison.Ordinal);
        Assert.Contains("_semanticForeground = UsesDarkTheme", reader, StringComparison.Ordinal);
        Assert.Contains("AccentTextFillColorPrimaryBrush", reader, StringComparison.Ordinal);
        Assert.Contains("CreateLightThemeSemanticForeground", reader, StringComparison.Ordinal);
        Assert.Contains("EnsureMinimumContrast", reader, StringComparison.Ordinal);
        Assert.Contains("LightThemeSemanticMinimumContrast = 4.5", reader, StringComparison.Ordinal);
        Assert.Contains("Foreground = _semanticForeground", reader, StringComparison.Ordinal);
        Assert.Contains("Foreground = _contentForeground", reader, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "UsesDarkTheme ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black",
            reader,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_OnlyLoadsTheFullBodyIntoTheEditorWhenEditing()
    {
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains(
            "SetDetailEditorText(_isDetailEditing ? item.Body : string.Empty)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("GetDetailPresentationBody()", surface, StringComparison.Ordinal);
        Assert.Contains(
            "SetDetailEditorText(_detailItem?.Body ?? string.Empty)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("_deferDetailReaderUntilTransitionCompletes", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_UsesComfortableTypographyThatScalesWithTheSystemFontSize()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string surfaceCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("BodyLineHeightRatio = 1.72", reader, StringComparison.Ordinal);
        Assert.Contains("TaskLineHeightRatio = 2.16", reader, StringComparison.Ordinal);
        Assert.Contains("HeadingLineHeightRatio = 1.42", reader, StringComparison.Ordinal);
        Assert.Contains("CodeLineHeightRatio = 1.60", reader, StringComparison.Ordinal);
        Assert.Contains("LineStackingStrategy.BlockLineHeight", reader, StringComparison.Ordinal);
        Assert.Contains("ListItemSpacing = 3", reader, StringComparison.Ordinal);
        Assert.Contains("Margin = new Thickness(0, 0, 2, 0)", reader, StringComparison.Ordinal);
        Assert.Contains("RenderTransform = new TranslateTransform { Y = 6 }", reader, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBodyReaderSurface\"", surface, StringComparison.Ordinal);
        Assert.Contains("<Grid\n                            x:Name=\"DetailBodyReaderSurface\"", surface.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("DetailBodyReaderSurface.AddHandler", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBackColumn\"", surface, StringComparison.Ordinal);
        Assert.Contains("DetailBackColumn.Width = new GridLength(8)", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("DetailBackColumn.Width = new GridLength(28)", surfaceCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_KeepsScrollbarNearSurfaceEdgeAndClearOfText()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string standalone = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));

        Assert.Contains("InternalScrollBarContentClearance = 12", reader, StringComparison.Ordinal);
        Assert.Contains("_documentText.Margin = UseInternalScrollViewer", reader, StringComparison.Ordinal);
        Assert.Contains("Margin=\"8,4,0,6\"", surface, StringComparison.Ordinal);
        Assert.Contains("Margin=\"8,6,0,6\"", standalone, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ReservesLayoutHeightForAsynchronouslyDecodedImages()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains("UseInlineContentLineHeightWhenNeeded", reader, StringComparison.Ordinal);
        Assert.Contains("LineStackingStrategy.MaxHeight", reader, StringComparison.Ordinal);
        Assert.Contains("image.ImageOpened += InlineImage_ImageOpened", reader, StringComparison.Ordinal);
        Assert.Contains("_documentText.InvalidateMeasure()", reader, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = 960", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ReusesItsVisualTreeUntilContentOrWidthDependentLayoutChanges()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("private bool _renderInvalidated = true", reader, StringComparison.Ordinal);
        Assert.Contains("if (!_renderInvalidated || !_isLoaded || _renderQueued)", reader, StringComparison.Ordinal);
        Assert.Contains("QueueWidthDependentRender(args.NewSize.Width)", reader, StringComparison.Ordinal);
        Assert.Contains("WidthRenderDebounceMilliseconds = 160", reader, StringComparison.Ordinal);
        Assert.Contains("_renderDependsOnWidth = true;", reader, StringComparison.Ordinal);
        Assert.Contains("public void Refresh() => InvalidateRender(\"explicit-refresh\");", reader, StringComparison.Ordinal);
        Assert.Contains("_renderInvalidationRequiresRender", reader, StringComparison.Ordinal);
        Assert.Contains("GetRenderState().Equals(_lastRenderedState)", reader, StringComparison.Ordinal);
        Assert.Contains("private readonly record struct MarkdownRenderState", reader, StringComparison.Ordinal);
        Assert.Contains("PerformanceLogger.RecordMarkdownRender();", reader, StringComparison.Ordinal);
        Assert.Contains("PerformanceLogger.RecordMarkdownInlineImageDecode();", reader, StringComparison.Ordinal);
        Assert.Contains("CreateDetailAttachmentRenderKey", quickCapture, StringComparison.Ordinal);

        int presentationStart = quickCapture.IndexOf(
            "private void RefreshDetailPresentation()",
            StringComparison.Ordinal);
        int presentationEnd = quickCapture.IndexOf(
            "private void DetailEditButton_Click",
            presentationStart,
            StringComparison.Ordinal);
        Assert.True(presentationStart >= 0 && presentationEnd > presentationStart);
        Assert.DoesNotContain(
            "DetailMarkdownView.Refresh",
            quickCapture[presentationStart..presentationEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentedTabs_LeaveWidthCalculationToToolkitDuringResponsiveLayout()
    {
        string helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetSegmentedLayoutHelper.cs"));

        Assert.Contains("EqualPanel", helper, StringComparison.Ordinal);
        Assert.Contains("item.Width = double.NaN", helper, StringComparison.Ordinal);
        Assert.Contains("item.MaxWidth = double.PositiveInfinity", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Width = itemWidth", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyEqualItemWidthsCore", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoSegmentedTabs_WaitForASafeLayoutSlotBeforeBecomingVisible()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));

        Assert.Contains("x:Name=\"TodoFilterSegmented\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"96\"", xaml, StringComparison.Ordinal);
        Assert.Contains("QueueTodoSegmentedRestore", code, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering", code, StringComparison.Ordinal);
        Assert.Contains("_todoSegmentedStableFrameCount < 3", code, StringComparison.Ordinal);
        Assert.Contains("WidgetSegmentedLayoutHelper.MinimumSafeWidth", code, StringComparison.Ordinal);
        Assert.Contains("CancelTodoSegmentedRestore", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleExpansion_PreparesSegmentedTabsBeforeTheFirstAnimationFrame()
    {
        string quickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));

        Assert.Contains("PrepareSegmentedForExpansion(targetContentWidth)", quickCapture, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureViewSegmented.Visibility = Visibility.Visible", quickCapture, StringComparison.Ordinal);
        Assert.Contains("PrepareTodoSegmentedForExpansion(targetContentWidth)", todo, StringComparison.Ordinal);
        Assert.Contains("TodoFilterSegmented.Visibility = Visibility.Visible", todo, StringComparison.Ordinal);
        Assert.Contains("ApplySegmentedLayout(allowDuringTransition: true)", todo, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupContentAnimation_HasACompletionFallback()
    {
        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));

        Assert.Contains("completionFallback", shell, StringComparison.Ordinal);
        Assert.Contains("profile.DurationMilliseconds + 250", shell, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(completionFallbackDelay)", shell, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(() => Settle(cancelled: false))", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupContentSwitch_CannotBeCancelledAfterTheLivePresenterSwap()
    {
        string groups = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.Groups.cs"));

        int begin = groups.IndexOf(
            "preparation.BeginTransition()",
            StringComparison.Ordinal);
        int end = groups.IndexOf(
            "SaveWidgetGroupActiveMemberDeferred()",
            begin,
            StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);

        string committedTransaction = groups[begin..end];
        Assert.Contains("new CancellationTokenSource()", committedTransaction, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", committedTransaction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "request.CancellationToken.ThrowIfCancellationRequested()",
            committedTransaction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_widgetGroupSwitchRequests.IsCurrent(request)",
            committedTransaction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Todo_UsesSharedResponsiveSplitterAndMarkdownDetailControls()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string layout = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.MasterDetail.cs"));
        string detail = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs"));
        string titleSizing = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.TitleEditorSizing.cs"));

        Assert.Contains("<toolkit:GridSplitter", xaml, StringComparison.Ordinal);
        Assert.Contains("<toolkit:PropertySizer", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding Height, ElementName=DetailTitleTextBox, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextChanged=\"DetailTitleTextBox_TextChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("EnsureWideDetailSelection", layout, StringComparison.Ordinal);
        Assert.Contains("ViewModel?.LayoutPreference", layout, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMetadataGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DetailMetadataGrid_SizeChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("DetailMetadataColumn3", xaml, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(600)", detail, StringComparison.Ordinal);
        Assert.Contains("TryToggleTask", detail, StringComparison.Ordinal);
        Assert.Contains("TodoTitleEditorHeightPolicy.ResolveHeight", titleSizing, StringComparison.Ordinal);
        Assert.Contains("MeasureDetailTitleContentHeight", titleSizing, StringComparison.Ordinal);
        Assert.Contains("PersistTitleEditorHeight", titleSizing, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_UsesOneSharedSurfaceForStandaloneAndGroupedHosts()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string features = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string transientState = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Models/WidgetMemberTransientStates.cs"));

        Assert.Contains("x:Name=\"PaneSplitter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"-3,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource WidgetMasterDetailSplitterStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMaterialSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailDeleteButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailAddFileButton\"\n                            Grid.Row=\"2\"", xaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMarkdownEditor\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RightTapped=\"QuickCaptureItem_RightTapped\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MasterPaneWidthMetadataKey", surface, StringComparison.Ordinal);
        Assert.Contains("IWidgetAddActionContent", surface, StringComparison.Ordinal);
        Assert.Contains("DetailMarkdownView_TaskToggleRequested", surface, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(DetailAutoSaveDelayMs)", surface, StringComparison.Ordinal);
        Assert.Contains("DetailMarkdownEditor_EditorTextChanged", surface, StringComparison.Ordinal);
        Assert.Contains("BeginDetailEditing", surface, StringComparison.Ordinal);
        Assert.Contains("SaveDetailAsync(completeEditing: false)", surface, StringComparison.Ordinal);
        Assert.Contains("CreateAppearanceContextSubmenu", surface, StringComparison.Ordinal);
        Assert.Contains("RestorePendingDetailState", surface, StringComparison.Ordinal);
        Assert.Contains("SelectedDetailItemId", transientState, StringComparison.Ordinal);
        Assert.Contains("DetailDraft", transientState, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.QuickCapture,\n                async request => await CreateContentWidgetFromConfigAsync", manager.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("CreateContentWidgetFromConfigAsync(config)", features, StringComparison.Ordinal);
        Assert.Contains("return FeatureWidgetSettings.IsFeatureWidget(kind);", features, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedQuickCapture_UsesTheSameLayoutAndTabPreferences()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("x:Name=\"QuickCaptureViewSegmented\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PaneSplitter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureWideLayoutSinglePane", code, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureWideLayoutDualPane", code, StringComparison.Ordinal);
        Assert.Contains("WidgetSegmentedStyleHelper.Apply", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TabStyle", code, StringComparison.Ordinal);
        Assert.Contains("Config.Metadata[MasterPaneWidthMetadataKey]", code, StringComparison.Ordinal);
        Assert.Contains("IWidgetResponsiveLayoutContent", code, StringComparison.Ordinal);
        Assert.Contains("IWidgetHostViewportContent", code, StringComparison.Ordinal);
        Assert.Contains("OnHostViewportSizeChanged", code, StringComparison.Ordinal);
        Assert.Contains("_hostViewportWidth", code, StringComparison.Ordinal);
        Assert.Contains("_isResponsiveLayoutTransitionActive = true;", code, StringComparison.Ordinal);
        Assert.Contains("if (!isCollapsing &&", code, StringComparison.Ordinal);
        Assert.Contains("_hostViewportWidth = targetContentWidth;", code, StringComparison.Ordinal);
        Assert.Contains("Width = targetContentWidth;", code, StringComparison.Ordinal);
        Assert.Contains("_isResponsiveLayoutTransitionActive ||", code, StringComparison.Ordinal);
        Assert.Contains("MasterColumn.MinWidth = 0;", code, StringComparison.Ordinal);
        Assert.Contains("DetailColumn.MinWidth = 0;", code, StringComparison.Ordinal);
        Assert.Contains("DetailColumn.Width = new GridLength(layout.DetailWidth);", code, StringComparison.Ordinal);

        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        Assert.Contains(
            "NotifyHostedContentViewportSize(e.NewSize.Width, e.NewSize.Height)",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewportContent.OnHostViewportSizeChanged(width, height)",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAndQuickCapture_ReadSurfaces_LookEditableAndSupportDoubleClickEditing()
    {
        string todoXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string quickCaptureXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string quickCaptureCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string todoCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs"));

        Assert.Contains("x:Name=\"DetailNotesReaderHost\"", todoXaml, StringComparison.Ordinal);
        Assert.Contains("DetailNotesReaderHost.AddHandler", todoCode, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", todoCode, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource WidgetLayerFillSecondaryBrush}\"", todoXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBodyReaderSurface\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("DetailBodyReaderSurface.AddHandler", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", quickCaptureCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=\"4,0,0,0\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"4,0,4,6\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailHeaderActions\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"40\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"2,2,2,2\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailHeader_SizeChanged", quickCaptureXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactDetailHeaderWidth", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("_detailItem?.IsRecent == true", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("BeginDetailEditing()", quickCaptureCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAndQuickCapture_CardSurfacesShareTheCompactCornerRadius()
    {
        string todoXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string quickCaptureXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));

        Assert.DoesNotContain("WidgetCornerRadiusMedium", todoXaml, StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DetailNotesReaderHost\"",
            todoXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"TodoDetailSelectionIndicator\"",
            todoXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Width=\"2\"\n                                    Margin=\"-8,-6\"\n                                    HorizontalAlignment=\"Left\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DetailMaterialSurface\"",
            quickCaptureXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"8\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.True(
            todoXaml.Split("WidgetCornerRadiusSmall", StringSplitOptions.None).Length > 10,
            "Todo card, hover, selection, metadata, and note surfaces should share the compact radius.");
        Assert.True(
            quickCaptureXaml.Split("WidgetCornerRadiusSmall", StringSplitOptions.None).Length > 5,
            "Quick Capture add, list, selection, and detail surfaces should share the compact radius.");
        Assert.Contains(
            "Margin=\"0,2\"\n                            MinHeight=\"50\"\n                            Padding=\"8,6\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "MinHeight=\"42\"\n                        Padding=\"8,5\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "BorderBrush=\"Transparent\"\n                        BorderThickness=\"0\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "BorderBrush=\"Transparent\"\n                BorderThickness=\"0\"",
            quickCaptureXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_ViewChangesClearStaleDetailsAndReconcileAfterRefresh()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string itemSyncCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.ItemSync.cs"));
        string viewModelCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.cs"));
        string operationsCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.Operations.cs"));
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement timestamp = XDocument.Parse(xaml)
            .Descendants()
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xamlNamespace + "Name"),
                    "DetailTimestampText",
                    StringComparison.Ordinal));

        Assert.Contains("x:Name=\"DetailEmptyState\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding EmptyStateTitle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding EmptyStateText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"AddNoteCardButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding InputAreaVisibility}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged += ViewModel_PropertyChanged", code, StringComparison.Ordinal);
        Assert.Contains("nameof(QuickCaptureWidgetViewModel.ItemsViewTransitionToken)", code, StringComparison.Ordinal);
        Assert.Contains("ClearDetailForViewChange();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.Items.Count > 0", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged -= ViewModel_PropertyChanged", code, StringComparison.Ordinal);
        Assert.Contains("(IsRecordsView || IsPinnedView) && !IsSearchExpanded", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("bool addPinned = IsPinnedView;", operationsCode, StringComparison.Ordinal);
        Assert.Contains("pin: addPinned", operationsCode, StringComparison.Ordinal);
        Assert.Contains("EmptyStateVisibility = hasItems", itemSyncCode, StringComparison.Ordinal);
        Assert.Contains("(IsRecordsView || IsPinnedView) && !HasSearchText", itemSyncCode, StringComparison.Ordinal);
        Assert.Equal(
            "DetailContent",
            (string?)timestamp.Parent?.Attribute(xamlNamespace + "Name"));
        Assert.DoesNotContain(
            "SelectedView is QuickCaptureViewMode.Pinned or QuickCaptureViewMode.Recent",
            operationsCode,
            StringComparison.Ordinal);
        Assert.True(
            itemSyncCode.IndexOf("SetViewSwitchLoading(false);", StringComparison.Ordinal) <
            itemSyncCode.IndexOf("ItemsViewTransitionToken++;", StringComparison.Ordinal),
            "The completed view state must be visible before detail subscribers reconcile the empty tab.");
    }

    [Fact]
    public void QuickCapture_ReattachKeepsListStillAndRestoresSelectedDetail()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement itemsList = XDocument.Parse(xaml)
            .Descendants()
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xamlNamespace + "Name"),
                    "ItemsList",
                    StringComparison.Ordinal));
        XElement transitions = itemsList.Elements()
            .Single(element =>
                string.Equals(
                    element.Name.LocalName,
                    "ListView.ItemContainerTransitions",
                    StringComparison.Ordinal));

        Assert.Equal("TransitionCollection", transitions.Elements().Single().Name.LocalName);
        Assert.Empty(transitions.Elements().Single().Elements());
        Assert.Contains("Unloaded += OnUnloaded", code, StringComparison.Ordinal);
        Assert.Contains("Unloaded -= OnUnloaded", code, StringComparison.Ordinal);
        Assert.Contains("_deferDetailReaderUntilTransitionCompletes = false;", code, StringComparison.Ordinal);
        Assert.Contains("if (_isInitialized && !_isCreatingDetail)", code, StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.Items.FirstOrDefault(item => item.IsDetailSelected)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (ReferenceEquals(refreshed, _detailItem))",
            code,
            StringComparison.Ordinal);
        Assert.Contains("ItemsList.SelectedItem is QuickCaptureItemViewModel listSelection", code, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_AddNoteCards_UseNeutralAddIcons()
    {
        string surfaceXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string legacyWindowXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));

        Assert.Contains(
            "Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" Glyph=\"&#xE710;\"",
            surfaceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"",
            legacyWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE710;\"", legacyWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AccentTextFillColorPrimaryBrush",
            surfaceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AccentTextFillColorPrimaryBrush",
            legacyWindowXaml,
            StringComparison.Ordinal);
    }
}
