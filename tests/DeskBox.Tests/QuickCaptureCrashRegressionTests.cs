using System.Reflection;
using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class QuickCaptureCrashRegressionTests
{
    [Fact]
    public void PopoutWindow_OnWindowClosed_IsSynchronousAndDetachesContentBeforeAsyncPersistence()
    {
        string popoutCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCapturePopoutWindow.xaml.cs"));

        Assert.DoesNotContain(
            "async void OnWindowClosed",
            popoutCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OnWindowClosed(object sender, WindowEventArgs args)",
            popoutCode,
            StringComparison.Ordinal);

        int onClosedStart = popoutCode.IndexOf(
            "private void OnWindowClosed(object sender, WindowEventArgs args)",
            StringComparison.Ordinal);
        int persistMethodStart = popoutCode.IndexOf(
            "private async Task PersistCloseStateAsync(",
            onClosedStart,
            StringComparison.Ordinal);
        Assert.True(onClosedStart >= 0 && persistMethodStart > onClosedStart);

        string onClosedBody = popoutCode[onClosedStart..persistMethodStart];
        int contentNullIdx = onClosedBody.IndexOf("Content = null;", StringComparison.Ordinal);
        int persistCallIdx = onClosedBody.IndexOf("PersistCloseStateAsync(", StringComparison.Ordinal);
        Assert.True(contentNullIdx > 0 && persistCallIdx > contentNullIdx);

        string persistBody = popoutCode[persistMethodStart..];
        Assert.DoesNotContain("Content = null", persistBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RootGrid.Children.Clear()", persistBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialBrushCache_UsesInstanceScopedBrushesWithoutStaticDependencyObjects()
    {
        FieldInfo[] staticFields = typeof(QuickCaptureMaterialBrushCache)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(
            staticFields,
            field => typeof(SolidColorBrush).IsAssignableFrom(field.FieldType) ||
                     field.FieldType.FullName?.Contains("SolidColorBrush", StringComparison.Ordinal) == true);

        Assert.False(QuickCaptureMaterialBrushCache.TryGetSurfaceColor(
            QuickCaptureAppearancePreset.Default,
            isDarkTheme: false,
            out _));
        Assert.False(QuickCaptureMaterialBrushCache.TryGetSwatchColor(
            QuickCaptureAppearancePreset.Default,
            isDarkTheme: true,
            out _));

        foreach (QuickCaptureAppearancePreset preset in new[]
        {
            QuickCaptureAppearancePreset.Paper,
            QuickCaptureAppearancePreset.StickyYellow,
            QuickCaptureAppearancePreset.Rose,
            QuickCaptureAppearancePreset.Mint,
            QuickCaptureAppearancePreset.MistBlue
        })
        {
            Assert.True(QuickCaptureMaterialBrushCache.TryGetSurfaceColor(preset, isDarkTheme: false, out Color lightSurface));
            Assert.True(QuickCaptureMaterialBrushCache.TryGetSurfaceColor(preset, isDarkTheme: true, out Color darkSurface));
            Assert.NotEqual(lightSurface, darkSurface);

            Assert.True(QuickCaptureMaterialBrushCache.TryGetSwatchColor(preset, isDarkTheme: false, out Color lightSwatch));
            Assert.True(QuickCaptureMaterialBrushCache.TryGetSwatchColor(preset, isDarkTheme: true, out Color darkSwatch));
            Assert.NotEqual(lightSwatch, darkSwatch);
        }
    }

    [Fact]
    public void SurfaceAndPopout_DeferClickAndDoubleTapVisualCollapseOutOfPointerGestureStack()
    {
        string surfaceCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string batchCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.BatchAndExport.cs"));
        string popoutCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/QuickCapturePopoutWindow.xaml.cs"));

        Assert.DoesNotContain("async void ItemsList_ItemClick", surfaceCode, StringComparison.Ordinal);

        string itemClickBody = SliceBetween(
            surfaceCode,
            "private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)",
            "private void SearchButton_Click");
        Assert.Contains("DispatcherQueue.TryEnqueue", itemClickBody, StringComparison.Ordinal);
        Assert.Contains("RunAsync(() => OpenDetailAfterSavingAsync(item))", itemClickBody, StringComparison.Ordinal);

        string readerDoubleTapBody = SliceBetween(
            surfaceCode,
            "private void DetailBodyReaderSurface_DoubleTapped(",
            "private bool BeginDetailEditing()");
        Assert.Contains("DispatcherQueue.TryEnqueue", readerDoubleTapBody, StringComparison.Ordinal);
        Assert.Contains("BeginDetailEditing(ratio)", readerDoubleTapBody, StringComparison.Ordinal);

        string listDoubleTapBody = SliceBetween(
            batchCode,
            "private void QuickCaptureItem_DoubleTapped(",
            "private async void DetailPopoutButton_Click");
        Assert.Contains("DispatcherQueue.TryEnqueue", listDoubleTapBody, StringComparison.Ordinal);

        string popoutDoubleTapBody = SliceBetween(
            popoutCode,
            "private void ReaderEditorHost_DoubleTapped(",
            "private bool BeginEditing(");
        Assert.Contains("DispatcherQueue.TryEnqueue", popoutDoubleTapBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceXamlAndMarkdownView_GuardNullAttachmentTemplateDataContextAndCollapsedShadowRender()
    {
        string surfaceXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string pasteAndZoom = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.PasteAndZoom.cs"));
        string markdownView = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains(
            "DataContext=\"{Binding Content, RelativeSource={RelativeSource TemplatedParent}}\"",
            surfaceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DetailPageTransitionHelper.PlayNavigationEnter(target",
            pasteAndZoom,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsEffectivelyVisibleInTree()",
            markdownView,
            StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }
}
