namespace DeskBox.Tests;

public sealed class ItemHoverActionContractTests
{
    [Fact]
    public void QuickCaptureActions_AreCenteredOverlayButtonsWithCopyAndPin()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("PointerEntered=\"QuickCaptureItem_PointerEntered\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerExited=\"QuickCaptureItem_PointerExited\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuickCaptureItemActionButtons\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource WidgetOverlaySurfaceBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuickCaptureCopyItemButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CopyItemButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QuickCapturePinItemButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"PinItemButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SetQuickCaptureItemActionButtonsVisible(sender as DependencyObject, true)", code, StringComparison.Ordinal);
        Assert.Contains("actionButtons.IsHitTestVisible = isVisible", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TogglePinnedAsync(item)", code, StringComparison.Ordinal);
        Assert.Contains("CopyItemWithFeedbackAsync(item)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapturePinAndDetailActions_ExposeDistinctStateAndFeedback()
    {
        string root = FindRepositoryRoot();
        string pinControlXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/PinStateIcon.xaml"));
        string pinControlCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/PinStateIcon.xaml.cs"));
        string surfaceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string surfaceCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("x:Class=\"DeskBox.Controls.PinStateIcon\"", pinControlXaml, StringComparison.Ordinal);
        Assert.Contains("L8.4,15.5 L7.6,15.5", pinControlXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PinnedPath\"", pinControlXaml, StringComparison.Ordinal);
        Assert.Contains("Fill=\"{x:Bind Foreground, Mode=OneWay}\"", pinControlXaml, StringComparison.Ordinal);
        Assert.Contains("public bool IsPinned", pinControlCode, StringComparison.Ordinal);
        Assert.Contains("controls:PinStateIcon", surfaceXaml, StringComparison.Ordinal);
        Assert.Contains("IsPinned=\"{Binding IsPinned}\"", surfaceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("E841", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("QuickCapture.PinnedSuccess", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("QuickCapture.UnpinnedSuccess", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("QuickCapture.Copied", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("QuickCapture.Saved", surfaceCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoActions_AreCenteredSolidOverlayButtonsWithCopyAndImportant()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs"));
        string clipboardCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.ClipboardSelection.cs"));

        Assert.Contains("x:Name=\"TodoCopyItemButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CopyTodoItemButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TodoImportantItemButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TodoItemActionHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource WidgetOverlaySurfaceBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource WidgetCompactIconButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"26\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Path=DataContext.SmallIconSize", xaml, StringComparison.Ordinal);
        Assert.Contains("FindVisualChild<Border>(itemRoot, \"TodoItemActionHost\")", code, StringComparison.Ordinal);
        Assert.Contains("actions.Opacity = isHovered ? 1 : 0", code, StringComparison.Ordinal);
        Assert.Contains("actions.IsHitTestVisible = isHovered", code, StringComparison.Ordinal);
        Assert.DoesNotContain("actions.Background =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("actions.BorderBrush =", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyActionButtonTheme", code, StringComparison.Ordinal);
        Assert.Contains("CopyTodoItemText(item)", clipboardCode, StringComparison.Ordinal);
        Assert.Contains("Todo.Copied", clipboardCode, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "DeskBox")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
