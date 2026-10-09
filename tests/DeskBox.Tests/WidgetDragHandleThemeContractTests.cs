namespace DeskBox.Tests;

public sealed class WidgetDragHandleThemeContractTests
{
    [Fact]
    public void FloatingDragHandles_UseOpaqueThemeAdaptiveBrush()
    {
        string appXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));
        string widgetShellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string widgetShellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string searchXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.xaml"));
        string searchCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs"));

        Assert.Equal(2, CountOccurrences(appXaml, "x:Key=\"WidgetDragHandleBrush\""));
        Assert.Contains(
            "x:Key=\"WidgetDragHandleBrush\" Color=\"#6B6B6B\"",
            appXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"WidgetDragHandleBrush\" Color=\"#D6D6D6\"",
            appXaml,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(widgetShellXaml, "{ThemeResource WidgetDragHandleBrush}"));
        Assert.Contains("x:Name=\"OverlayDragGrip\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding=\"True\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"1.5,0,0,1.5\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"0,1.5,1.5,0\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("Canvas.ZIndex=\"1\"", widgetShellXaml, StringComparison.Ordinal);
        Assert.Contains("const double gripOpacity = 1", widgetShellCode, StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{ThemeResource WidgetDragHandleBrush}\"",
            searchXaml,
            StringComparison.Ordinal);
        Assert.Contains("TopDragHandle.Opacity = 1;", searchCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TopDragHandle.Opacity = 0.72", searchCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatingWidgetWindowDragHandle_IsFullyOpaque()
    {
        string windowXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml"));
        string foregroundCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Foreground.cs"));

        // The floating widget window seeds the handle brush with an opaque
        // value (not #CC… translucency) for every foreground appearance.
        Assert.Contains(
            "x:Key=\"WidgetDragHandleBrush\" Color=\"#FFF5F5F5\"",
            windowXaml,
            StringComparison.Ordinal);

        // The in-place foreground recolor maps the handle onto the full-alpha
        // primary tone; the tertiary tone keeps its translucency for text only.
        string primaryGroup = ExtractBrushGroup(foregroundCode, "palette.Primary", "palette.Secondary");
        string tertiaryGroup = ExtractBrushGroup(foregroundCode, "palette.Tertiary", "palette.Disabled");
        Assert.Contains("WidgetDragHandleBrush", primaryGroup, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetDragHandleBrush", tertiaryGroup, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapseCueRestoresOnPhysicalExit_WithStoryboardHoldingFix()
    {
        string widgetShellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string windowCollapseCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));

        // The cue tracks the physical cursor: entry hints are physically
        // verified, and the restore waits out an outside-streak of the
        // physical cursor, so spurious WM_MOUSEMOVE under bounds animations
        // can neither arm nor restore it falsely.
        Assert.Contains(
            "public event EventHandler? OverlayDragCueArmRequested;",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "OverlayDragCueArmRequested?.Invoke(this, EventArgs.Empty);",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "public void SetOverlayDragCueActive(bool active)",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool IsOverlayDragCueArmed => _isPointerOverDragHandle;",
            widgetShellCode,
            StringComparison.Ordinal);
        // The morph must survive its own storyboard: StoryboardSlot's
        // completion stops the clocks, which on WinUI releases held values
        // back to the flat bar — the completion callback re-applies them.
        // Without this, the arrow visually reverts ~167ms after arming even
        // while the armed flag stays true.
        Assert.Contains(
            "onCompleted: () =>",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "OverlayDragGripLeftRotation.Angle = leftAngle;",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "OverlayDragGripRightRotation.Angle = rightAngle;",
            widgetShellCode,
            StringComparison.Ordinal);
        // No continuous event tracking on the shell: the move-based recompute
        // and the exit handler must not exist.
        Assert.DoesNotContain(
            "private void ShellRoot_PointerMoved(",
            widgetShellCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private void OverlayDragHandle_PointerExited(",
            widgetShellCode,
            StringComparison.Ordinal);
        // The window runs the exit poll with an outside-streak confirmation.
        Assert.Contains(
            "WidgetShellControl.OverlayDragCueArmRequested += WidgetShellControl_OverlayDragCueArmRequested;",
            windowCollapseCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OverlayDragCuePollTimer_Tick()",
            windowCollapseCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int OverlayDragCuePollIntervalMs = 150",
            windowCollapseCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int OverlayDragCueRestoreOutsideTicks = 2",
            windowCollapseCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetShellControl.SetOverlayDragCueActive(true);",
            windowCollapseCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetShellControl.SetOverlayDragCueActive(false);",
            windowCollapseCode,
            StringComparison.Ordinal);
        // Arm requests are dropped while the widget's bounds morph: the bar
        // sweeping under a stationary cursor fires false entries.
        Assert.Contains(
            "if (IsCompactTransitionActive)",
            windowCollapseCode,
            StringComparison.Ordinal);
    }

    private static string ExtractBrushGroup(
        string source,
        string groupStart,
        string nextGroup)
    {
        int start = source.IndexOf(
            $"SetBrushColor({groupStart},",
            StringComparison.Ordinal);
        int end = source.IndexOf(
            $"SetBrushColor({nextGroup},",
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Brush group {groupStart} not found.");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
