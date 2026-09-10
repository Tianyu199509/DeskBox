using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Controls.WidgetContents;

public sealed class GlanceWidgetContentAdapter :
    IWidgetContent,
    IWidgetCompactBackgroundContent,
    IWidgetResponsiveLayoutContent,
    IDisposable
{
    private readonly Func<GlanceWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;
    private string? _compactBackgroundPath;
    private ImageSource? _compactBackground;
    private long _compactBackgroundEstimatedBytes;

    public GlanceWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        GlanceWidgetStore? store = null,
        GlanceImageService? imageService = null,
        ICalendarPresentationSource? calendarSource = null,
        Func<GlanceWidgetViewModel, FrameworkElement>? viewFactory = null,
        SettingsService? settingsService = null)
    {
        Config = config;
        ViewModel = new GlanceWidgetViewModel(
            config,
            localizationService,
            store,
            imageService,
            calendarSource,
            settingsService: settingsService);
        _viewFactory = viewFactory ?? (viewModel => new GlanceWidgetContent(viewModel));
    }

    public WidgetConfig Config { get; }
    public string WidgetId => Config.Id;
    public WidgetKind WidgetKind => Config.WidgetKind;
    public FrameworkElement View => _view ??= _viewFactory(ViewModel);
    public GlanceWidgetViewModel ViewModel { get; }

    public Task InitializeAsync() => ViewModel.InitializeAsync();
    public Task RefreshAsync() => ViewModel.RefreshAsync();
    public void ApplyAppearance() => ViewModel.ApplyAppearance();
    public void OnActivated() => ViewModel.OnActivated();
    public void OnDeactivated() => ViewModel.OnDeactivated();
    public void OnWindowVisibilityChanged(bool visible) => ViewModel.OnWindowVisibilityChanged(visible);
    public void OnWindowRevealCompleted() => ViewModel.OnWindowRevealCompleted();
    public void OnCompactStateChanged(bool collapsed) => ViewModel.OnCompactStateChanged(collapsed);

    public void BeginResponsiveLayoutTransition(double targetContentWidth, double targetContentHeight, bool isCollapsing)
        => ViewModel.UpdateAvailableSize(targetContentWidth, targetContentHeight);

    public void CompleteResponsiveLayoutTransition(double finalContentWidth, double finalContentHeight)
        => ViewModel.UpdateAvailableSize(finalContentWidth, finalContentHeight);

    public void CancelResponsiveLayoutTransition()
    {
    }

    public WidgetCompactBackgroundSnapshot? GetCompactBackground()
    {
        double opacity = ViewModel.BackgroundImageOpacity;
        if (!ViewModel.HasVisibleCurrentImage || !double.IsFinite(opacity) || opacity <= 0 || opacity > 1)
            return null;
        ImageSource? image = GetCompactBackgroundImage();
        return image is null ? null : new WidgetCompactBackgroundSnapshot(image, opacity);
    }

    public ImageSource? GetCompactBackgroundImage()
    {
        string? path = ViewModel.CurrentImagePath;
        if (string.Equals(path, _compactBackgroundPath, StringComparison.OrdinalIgnoreCase))
        {
            return _compactBackground;
        }

        _compactBackgroundPath = path;
        _compactBackground = null;
        SetCompactBackgroundEstimatedBytes(0);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = 768,
                UriSource = new Uri(path, UriKind.Absolute)
            };
            _compactBackground = bitmap;
            PerformanceLogger.RecordGlanceCompactBackgroundDecode();
            SetCompactBackgroundEstimatedBytes(768L * 768 * 4);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[GlanceWidget] Compact background failed for '{path}': {ex.Message}");
        }

        return _compactBackground;
    }

    private void SetCompactBackgroundEstimatedBytes(long estimatedBytes)
    {
        long normalizedBytes = Math.Max(0, estimatedBytes);
        long delta = normalizedBytes - _compactBackgroundEstimatedBytes;
        _compactBackgroundEstimatedBytes = normalizedBytes;
        if (delta != 0)
        {
            PerformanceLogger.AdjustGlanceCompactBackgroundEstimatedBytes(
                delta);
        }
    }

    public void Dispose()
    {
        _compactBackground = null;
        _compactBackgroundPath = null;
        SetCompactBackgroundEstimatedBytes(0);
        ViewModel.Dispose();
    }
}
