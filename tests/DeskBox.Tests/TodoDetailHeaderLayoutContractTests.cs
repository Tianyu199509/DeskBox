namespace DeskBox.Tests;

public sealed class TodoDetailHeaderLayoutContractTests
{
    [Fact]
    public void DetailActions_AreDirectlyAccessibleAndTitleUsesFullWidth()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));
        string masterDetailCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.MasterDetail.cs"));

        Assert.Contains("x:Name=\"DetailHeaderActions\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DetailHeaderActions\"\n                            Grid.Column=\"2\"\n                            HorizontalAlignment=\"Right\"",
            xaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DetailCompletionCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DetailCompletionButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailCompletionButton_Click", code, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DetailSaveButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailSaveButton_Click", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuickAddBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_titleAutosaveTimer", code, StringComparison.Ordinal);
        Assert.Contains("FlushDetailTitleAsync", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailImportantButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailDeleteButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DetailDeleteButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailMoreButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailDeleteMenuItem", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailDeleteMenuItem_Click", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.FinalizeDetailAsync(", code, StringComparison.Ordinal);
        Assert.Contains("ShowTodoStatus(\"Todo.Status.Saved\")", code, StringComparison.Ordinal);
        Assert.Contains(
            "if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedDetailItem))",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (ViewModel.IsCreatingDetailItem)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.UpdateItemTextAsync(item.Id, DetailTitleTextBox.Text)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "DetailBackColumn.Width = new GridLength(0)",
            masterDetailCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Grid.Row=\"2\"\n                Margin=\"8,0,8,4\"",
            xaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "Text=\"{Binding CreatedText}\""));

        Assert.Contains(
            "Style=\"{StaticResource TodoNativeCompletionCheckBoxStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TodoDetailActionIconSize\">13", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBackButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE72B;\"", xaml[xaml.IndexOf(
            "x:Name=\"DetailBackButton\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("Spacing=\"2\"", xaml[xaml.IndexOf(
            "x:Name=\"DetailHeaderActions\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("Width\" Value=\"28\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyDetailCompletionVisualState", code, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"30\" />\n                            <ColumnDefinition Width=\"*\" />\n                            <ColumnDefinition Width=\"30\" />", xaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionControls_UseNativeNeutralCheckBoxes()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs"));

        Assert.Contains("x:Key=\"TodoNativeCompletionCheckBoxStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource DefaultCheckBoxStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TodoCompletionCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DetailCompletionCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CheckBoxCheckBackgroundFillChecked", xaml, StringComparison.Ordinal);
        Assert.Contains("ResourceKey=\"TextFillColorPrimaryBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DeskBoxCheckFillCheckedBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("DeskBoxCheckGlyphBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"5\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StrokeEndLineCap=\"Round\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource WidgetCompactStepCheckBoxStyle}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TodoCompletionBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"DetailCompletionBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("completionAccentBrush", code, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTonedCheckPalette_TonesDownOverlyBrightAccentsAndMaintainsContrast()
    {
        Windows.UI.Color[] testAccents =
        [
            DeskBox.Helpers.AccentColorHelper.FromHex("#0078D4"), // Default Windows Blue
            DeskBox.Helpers.AccentColorHelper.FromHex("#60CDFF"), // Bright Win11 Sky Cyan
            DeskBox.Helpers.AccentColorHelper.FromHex("#FFEB3B"), // Bright Yellow
            DeskBox.Helpers.AccentColorHelper.FromHex("#00FF66"), // Neon Green
            DeskBox.Helpers.AccentColorHelper.FromHex("#FFFFFF"), // Pure White
            DeskBox.Helpers.AccentColorHelper.FromHex("#050508")  // Ultra-Dark Black
        ];

        foreach (bool isDark in new[] { true, false })
        {
            foreach (Windows.UI.Color accent in testAccents)
            {
                var palette = DeskBox.Helpers.AccentColorHelper.BuildTonedCheckPalette(accent, isDark);
                double fillLum = DeskBox.Helpers.AccentColorHelper.GetRelativeLuminance(palette.FillChecked);
                double glyphLum = DeskBox.Helpers.AccentColorHelper.GetRelativeLuminance(palette.GlyphForeground);

                Assert.True(fillLum <= (isDark ? 0.24 : 0.28),
                    $"FillChecked luminance {fillLum:F3} is too bright for accent {DeskBox.Helpers.AccentColorHelper.ToHex(accent)} (isDark={isDark}).");
                if (isDark)
                {
                    Assert.True(fillLum >= 0.08,
                        $"FillChecked luminance {fillLum:F3} is too dark in dark mode for accent {DeskBox.Helpers.AccentColorHelper.ToHex(accent)}.");
                }

                double lighter = Math.Max(fillLum, glyphLum);
                double darker = Math.Min(fillLum, glyphLum);
                double contrast = (lighter + 0.05) / (darker + 0.05);
                Assert.True(contrast >= 3.5,
                    $"Checkmark contrast {contrast:F2}:1 is too low for accent {DeskBox.Helpers.AccentColorHelper.ToHex(accent)} (isDark={isDark}).");
            }
        }
    }

    [Fact]
    public void FilterChange_FinalizesAnActiveDraftBeforeChangingContext()
    {
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));

        Assert.Contains(
            "private async void TodoFilterSegmented_SelectionChanged",
            code,
            StringComparison.Ordinal);
        Assert.Contains("if (ViewModel.IsCreatingDetailItem)", code, StringComparison.Ordinal);
        Assert.Contains("closeDetail: true", code, StringComparison.Ordinal);
        Assert.Contains("SelectFilter(filter);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ListCompletionAndRetainedDetailActions_ReportSpecificStatusFeedback()
    {
        string contentCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));
        string interactionCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs"));
        string feedbackCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs"));

        Assert.Contains("SetImportantWithFeedbackAsync", contentCode, StringComparison.Ordinal);
        Assert.Contains("SetCompletedWithFeedbackAsync", interactionCode, StringComparison.Ordinal);
        Assert.Contains("SetImportantWithFeedbackAsync", interactionCode, StringComparison.Ordinal);
        Assert.Contains("Todo.Status.Saved", feedbackCode, StringComparison.Ordinal);
        Assert.Contains("Todo.Status.MarkedCompleted", feedbackCode, StringComparison.Ordinal);
        Assert.Contains("Todo.Status.MarkedActive", feedbackCode, StringComparison.Ordinal);
        Assert.Contains("Todo.Status.MarkedImportant", feedbackCode, StringComparison.Ordinal);
        Assert.Contains("Todo.Status.UnmarkedImportant", feedbackCode, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_EditAndSaveUseTheLeftActionAndCreateRequiresExplicitCommit()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.DoesNotContain("DetailCreateSaveButton", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailDoneButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DetailDoneButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailEditSaveHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml[xaml.IndexOf(
            "x:Name=\"DetailEditSaveHost\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.True(
            xaml.IndexOf("x:Name=\"DetailDoneButton\"", StringComparison.Ordinal) <
            xaml.IndexOf("x:Name=\"DetailHeaderActions\"", StringComparison.Ordinal));
        Assert.DoesNotContain("showDualPaneCreateSave", code, StringComparison.Ordinal);
        Assert.Contains(
            "DetailBackColumn.Width = new GridLength(8)",
            code,
            StringComparison.Ordinal);
        Assert.Contains("private void ScheduleDetailAutoSave()", code, StringComparison.Ordinal);
        Assert.Contains("if (!_isCreatingDetail && _detailHasUnsavedChanges)", code, StringComparison.Ordinal);
        Assert.Contains(
            "if (_isCreatingDetail && !HasNewDetailContent())\n        {\n            ClearDetailForViewChange();",
            code.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "if (string.IsNullOrWhiteSpace(body) && _pendingDetailAttachments.Count == 0)",
            code,
            StringComparison.Ordinal);
        string timestampMarkup = xaml[xaml.IndexOf(
            "x:Name=\"DetailTimestampText\"",
            StringComparison.Ordinal)..];
        Assert.Contains("Grid.Row=\"2\"", timestampMarkup, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"QuickCaptureDetailActionIconSize\">13", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"QuickCaptureSharedDetailIconButtonStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width\" Value=\"28\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground\" Value=\"{ThemeResource TextFillColorSecondaryBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"13\" Height=\"13\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Spacing=\"2\"", xaml[xaml.IndexOf(
            "x:Name=\"DetailHeaderActions\"", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("DetailBackColumn.Width = new GridLength(28)", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
