namespace DeskBox.Tests;

public sealed class QuickCaptureImagePreviewContractTests
{
    [Fact]
    public void Surface_ShowsClipboardImagesAsListThumbnailsAndDetailContent()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string itemViewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/QuickCaptureItemViewModel.cs"));
        string attachmentViewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/TodoAttachmentViewModel.cs"));
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/QuickCaptureService.cs"));

        Assert.Contains(
            "Content=\"{Binding PrimaryImageAttachment}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ListImagePreviewVisibility}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Loaded=\"QuickCaptureAttachmentPreview_Loaded\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataContextChanged=\"QuickCaptureAttachmentPreview_DataContextChanged\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:DataType=\"viewModels:TodoAttachmentViewModel\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Source=\"{x:Bind Thumbnail, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind ThumbnailVisibility, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind FileIconVisibility, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Source=\"{Binding Thumbnail}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding TextPreviewVisibility}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailPrimaryImageHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailPrimaryImage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailPrimaryImageLoadingRing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RefreshDetailPrimaryImageAsync(primaryImagePath)", code, StringComparison.Ordinal);
        Assert.Contains(
            "_detailItem.SourceKind is QuickCaptureSourceKind.Clipboard or QuickCaptureSourceKind.Image",
            code,
            StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = DetailImageDecodePixelWidth", code, StringComparison.Ordinal);
        Assert.Contains("await attachment.EnsureThumbnailAsync()", code, StringComparison.Ordinal);
        Assert.Contains(
            "public TodoAttachmentViewModel? PrimaryImageAttachment => ImageAttachments.FirstOrDefault()",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "public Visibility ListImagePreviewVisibility => PrimaryImageAttachment is not null",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "public object[] ImageAttachmentItemsSource",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ImageAttachments.Cast<object>().ToArray()",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(ImageAttachmentItemsSource))",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "_thumbnailLoadAttempted = Thumbnail is not null",
            attachmentViewModel,
            StringComparison.Ordinal);

        string addImageFileMethod = SliceMethod(
            service,
            "public async Task<QuickCaptureItem?> AddImageFileItemAsync",
            "public async Task<string?> CreateImageExportFileAsync");
        Assert.True(
            addImageFileMethod.IndexOf("await _gate.WaitAsync()", StringComparison.Ordinal) <
            addImageFileMethod.IndexOf("await SaveImageFileAsync", StringComparison.Ordinal));
        Assert.Contains("await CreateImageThumbnailAsync(imagePath)", service, StringComparison.Ordinal);
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}
