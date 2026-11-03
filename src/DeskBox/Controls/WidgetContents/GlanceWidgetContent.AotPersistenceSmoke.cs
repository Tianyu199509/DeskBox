#if DESKBOX_NATIVE_AOT
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class GlanceWidgetContent
{
    internal async Task<AotGlanceSurfaceSnapshot> WaitForAotGlanceSurfaceAsync(
        string? expectedImagePath,
        GlanceLayoutMode expectedLayout,
        bool expectImage,
        double expectedBackgroundImageOpacity)
    {
        AotGlanceSurfaceSnapshot last = CaptureAotGlanceSurface();
        for (int attempt = 0; attempt < 120; attempt++)
        {
            last = CaptureAotGlanceSurface();
            bool imageReady = expectImage
                ? string.Equals(
                        last.DecodedImagePath,
                        expectedImagePath,
                        StringComparison.OrdinalIgnoreCase) &&
                    last.ActiveBackgroundIsImageBrush &&
                    last.ActiveBackgroundOpacity > 0.99
                : last.DecodedImagePath is null &&
                    !last.BackgroundAHasBrush &&
                    !last.BackgroundBHasBrush;
            bool layoutReady = expectedLayout switch
            {
                GlanceLayoutMode.Immersive => last.ImmersiveLayoutVisible,
                GlanceLayoutMode.Centered => last.CenteredLayoutVisible,
                GlanceLayoutMode.Editorial => last.EditorialLayoutVisible,
                GlanceLayoutMode.Calendar => last.CalendarLayoutVisible,
                _ => false
            };
            if (last.IsLoaded &&
                last.HasXamlRoot &&
                last.DataContextMatchesViewModel &&
                imageReady &&
                Math.Abs(last.BackgroundImageLayerOpacity - expectedBackgroundImageOpacity) < 0.001 &&
                layoutReady)
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"The Glance surface did not stabilize. Snapshot={last}");
    }

    private string? _decodedImagePath => _background.FramePath;

    private AotGlanceSurfaceSnapshot CaptureAotGlanceSurface()
    {
        Border active = _isAActive ? BackgroundA : BackgroundB;
        ImageBrush? activeBrush = active.Background as ImageBrush;
        string? activeImageUri = (activeBrush?.ImageSource as BitmapImage)?.UriSource?.LocalPath;
        return new AotGlanceSurfaceSnapshot(
            _isLoaded,
            XamlRoot is not null,
            ReferenceEquals(DataContext, _viewModel),
            ActualWidth,
            ActualHeight,
            _decodedImagePath,
            BackgroundA.Background is not null,
            BackgroundB.Background is not null,
            activeBrush is not null,
            activeImageUri,
            active.Opacity,
            BackgroundImageLayer.Opacity,
            activeBrush?.Stretch.ToString(),
            activeBrush?.AlignmentX.ToString(),
            activeBrush?.AlignmentY.ToString(),
            ImmersiveLayoutRoot.Visibility == Visibility.Visible,
            CenteredLayoutRoot.Visibility == Visibility.Visible,
            EditorialLayoutRoot.Visibility == Visibility.Visible,
            CalendarLayoutRoot.Visibility == Visibility.Visible,
            ReadabilityLayer.Visibility == Visibility.Visible,
            ReadabilityLayer.Opacity,
            ActionLayer.Visibility == Visibility.Visible);
    }
}

internal sealed record AotGlanceSurfaceSnapshot(
    bool IsLoaded,
    bool HasXamlRoot,
    bool DataContextMatchesViewModel,
    double ActualWidth,
    double ActualHeight,
    string? DecodedImagePath,
    bool BackgroundAHasBrush,
    bool BackgroundBHasBrush,
    bool ActiveBackgroundIsImageBrush,
    string? ActiveImageUri,
    double ActiveBackgroundOpacity,
    double BackgroundImageLayerOpacity,
    string? ImageStretch,
    string? ImageAlignmentX,
    string? ImageAlignmentY,
    bool ImmersiveLayoutVisible,
    bool CenteredLayoutVisible,
    bool EditorialLayoutVisible,
    bool CalendarLayoutVisible,
    bool ReadabilityLayerVisible,
    double ReadabilityLayerOpacity,
    bool ActionLayerVisible);
#endif
