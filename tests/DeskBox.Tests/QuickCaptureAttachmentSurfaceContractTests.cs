namespace DeskBox.Tests;

public sealed class QuickCaptureAttachmentSurfaceContractTests
{
    [Fact]
    public void UnifiedSurface_OffersAttachmentRemovalOnlyWhileEditing()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("<controls:AttachmentTileStrip", xaml, StringComparison.Ordinal);
        Assert.Contains("RemoveRequested=\"DetailAttachmentStrip_RemoveRequested\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "DetailAttachmentStrip.CanRemove = hasDetail && _isDetailEditing;",
            code,
            StringComparison.Ordinal);
        Assert.Contains("DetailRemoveAttachmentText", xaml, StringComparison.Ordinal);
        Assert.Contains("_pendingDetailAttachments.RemoveAll", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.DeleteAttachmentAsync", code, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(DetailMarkdownEditor.Text)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAndQuickCapture_ShareSquareScrollableAttachmentTiles()
    {
        string controlXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/AttachmentTileStrip.xaml"));
        string controlCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/AttachmentTileStrip.xaml.cs"));
        string quickXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string todoXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));

        Assert.Contains("Width=\"76\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"76\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode=\"Enabled\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("Orientation=\"Horizontal\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"{x:Bind Thumbnail, Mode=OneWay}\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"{x:Bind Glyph, Mode=OneWay}\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RemoveAttachmentButton\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("PointerEntered=\"AttachmentTile_PointerEntered\"", controlXaml, StringComparison.Ordinal);
        Assert.Contains("await attachment.EnsureThumbnailAsync()", controlCode, StringComparison.Ordinal);
        Assert.Contains("<controls:AttachmentTileStrip", quickXaml, StringComparison.Ordinal);
        Assert.Contains("<controls:AttachmentTileStrip", todoXaml, StringComparison.Ordinal);
    }
}
