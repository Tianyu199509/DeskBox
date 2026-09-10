using DeskBox.GlancePackage.Services;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>Owns local brushes and cancellable image sampling for one instance.</summary>
internal sealed class GlanceAppearanceController : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly Border _surface;
    private readonly GlanceImagePaletteService _paletteService = new();
    private readonly SolidColorBrush _solid = new();
    private readonly LinearGradientBrush _gradient = new()
    {
        StartPoint = new(0, 0), EndPoint = new(1, 1),
        GradientStops = { new() { Offset = 0 }, new() { Offset = 1 } },
    };
    private PackageAppearance? _lastTheme;
    private bool _lastImageVisible;
    private PackageAppearance _host = PackageAppearance.Default;
    private GlanceWidgetData _settings = new();
    private GlanceImagePalette? _palette;
    private string? _imagePath;
    private string? _sampledPath;
    private string? _pendingPath;
    private CancellationTokenSource? _paletteCts;
    private bool _loaded;
    private bool _active = true;
    private bool _disposed;

    internal GlanceAppearanceController(FrameworkElement root)
    {
        _root = root;
        _dispatcher = root.DispatcherQueue;
        _surface = root.FindName("CalendarMaterialSurface").As<Border>();
    }

    internal void Update(PackageAppearance host, GlanceWidgetData settings, string? imagePath, bool imageVisible)
    {
        if (_disposed) return;
        _host = host;
        _settings = settings;
        _imagePath = imagePath;
        if (_lastTheme != host || _lastImageVisible != imageVisible)
        {
            ApplyTheme(host, imageVisible);
            _lastTheme = host;
            _lastImageVisible = imageVisible;
        }
        QueuePalette();
        ApplyMaterial();
    }

    internal void SetLoaded(bool loaded)
    {
        _loaded = loaded;
        if (loaded && !_disposed) QueuePalette();
        else CancelPalette();
    }

    internal void SetActive(bool active)
    {
        _active = active;
        if (active && _loaded && !_disposed) QueuePalette();
        else CancelPalette();
    }

    private void ApplyTheme(PackageAppearance host, bool imageVisible)
    {
        _root.RequestedTheme = host.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        // Mutate existing brushes: StaticResource references in the loaded
        // day template must see the change without replacing dictionaries.
        SetTextBrushes("", host.IsDark);
        SetTextBrushes("Image", host.IsDark || imageVisible);
        SetBrush("TextFillColorDisabledBrush", host.IsDark ? 0x5DFFFFFFu : 0x5D000000u);
        double accentLuminance = host.Accent.R * 0.2126 + host.Accent.G * 0.7152 + host.Accent.B * 0.0722;
        SetBrush("TextOnAccentFillColorPrimaryBrush", accentLuminance > 160 ? 0xFF000000u : 0xFFFFFFFFu);
        SetBrush("SubtleFillColorSecondaryBrush", host.IsDark ? 0x0FFFFFFFu : 0x09000000u);
        SetBrush("SubtleFillColorTertiaryBrush", host.IsDark ? 0x0AFFFFFFu : 0x06000000u);
        SetBrush("SolidBackgroundFillColorBaseBrush", host.IsDark ? 0xFF202020u : 0xFFF3F3F3u);
        SetBrush("SettingsSurfaceBrush", host.IsDark ? 0xF0202020u : 0xF0F3F3F3u);
        _root.As<Grid>().Background = _root.Resources["SolidBackgroundFillColorBaseBrush"].As<SolidColorBrush>();
        _root.Resources["AccentFillColorDefaultBrush"].As<SolidColorBrush>().Color = host.Accent;
    }

    private void SetTextBrushes(string prefix, bool dark)
    {
        SetBrush(prefix + "TextFillColorPrimaryBrush", dark ? 0xFFFFFFFFu : 0xE4000000u);
        SetBrush(prefix + "TextFillColorSecondaryBrush", dark ? 0xC5FFFFFFu : 0x9E000000u);
    }

    private void SetBrush(string key, uint argb) =>
        _root.Resources[key].As<SolidColorBrush>().Color = Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private void ApplyMaterial()
    {
        Color tint = WidgetMaterialVisualCalculator.BuildContentTintColor(_host.IsDark, _host.Accent);
        if (_settings.CalendarMaterialMode == GlanceCalendarMaterialMode.FollowImage)
        {
            GlanceImagePalette palette = _palette ?? new(_host.Accent, tint);
            var colors = WidgetMaterialVisualCalculator.BuildImagePaletteGradient(_host.IsDark, palette);
            _gradient.GradientStops[0].Color = colors.StartColor;
            _gradient.GradientStops[1].Color = colors.EndColor;
            _surface.Background = _gradient;
            _surface.Opacity = 1 - _settings.CalendarImageMaterialTransparency;
            return;
        }
        _surface.Opacity = 1;
        if (_host.MaterialType is "Acrylic" or "AcrylicBase")
        {
            var brush = _root.Resources["GlanceCalendarAcrylicBrush"].As<AcrylicBrush>();
            var profile = WidgetMaterialVisualCalculator.CalculateAcrylic(_host.IsDark,
                _host.MaterialType == "AcrylicBase", _host.MaterialOpacity, _host.MaterialIntensity);
            brush.TintColor = tint;
            brush.FallbackColor = tint;
            brush.TintOpacity = profile.TintOpacity;
            brush.TintLuminosityOpacity = profile.LuminosityOpacity;
            _surface.Background = brush;
            return;
        }
        _solid.Color = _host.MaterialType is "Mica" or "MicaAlt"
            ? WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(_host.IsDark,
                _host.Accent, _host.MaterialType == "MicaAlt", _host.MaterialIntensity)
            : WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(_host.IsDark,
                _host.Accent, _host.MaterialOpacity);
        _surface.Background = _solid;
    }

    private void QueuePalette()
    {
        if (!_loaded || !_active || _settings.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage)
        {
            CancelPalette();
            return;
        }
        if (string.IsNullOrWhiteSpace(_imagePath))
        {
            CancelPalette();
            _palette = null;
            _sampledPath = null;
            return;
        }
        if (string.Equals(_sampledPath, _imagePath, StringComparison.OrdinalIgnoreCase) ||
            (_paletteCts is not null && string.Equals(_pendingPath, _imagePath, StringComparison.OrdinalIgnoreCase))) return;
        CancelPalette();
        _palette = null;
        _sampledPath = null;
        _pendingPath = _imagePath;
        var cts = new CancellationTokenSource();
        _paletteCts = cts;
        _ = SampleAsync(_imagePath, cts);
    }

    private async Task SampleAsync(string path, CancellationTokenSource cts)
    {
        GlanceImagePalette? palette = null;
        CancellationToken token = cts.Token;
        try
        {
            palette = await _paletteService.GetPaletteAsync(path, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] palette update failed: {error.Message}"); }
        // Separate NativeAOT runtimes cannot rely on the host's managed
        // SynchronizationContext. Every post-decode visual write goes through
        // the owning WinRT dispatcher, including cancellation cleanup.
        try
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (token.IsCancellationRequested || _disposed || !_loaded || !_active ||
                        !ReferenceEquals(_paletteCts, cts) ||
                        _settings.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage ||
                        !string.Equals(path, _imagePath, StringComparison.OrdinalIgnoreCase)) return;
                    _sampledPath = path;
                    _palette = palette;
                    ApplyMaterial();
                }
                catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] palette display failed: {error.Message}"); }
                finally
                {
                    if (ReferenceEquals(_paletteCts, cts))
                    {
                        _paletteCts = null;
                        _pendingPath = null;
                    }
                }
            });
        }
        catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] palette dispatcher unavailable: {error.Message}"); }
        finally
        {
            // Disposal cannot depend on an accepted dispatcher callback being
            // executed; shutdown is allowed to drop queued work.
            lock (cts) cts.Dispose();
        }
    }

    private void CancelPalette()
    {
        var cts = _paletteCts;
        _paletteCts = null;
        _pendingPath = null;
        if (cts is not null)
        {
            lock (cts)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { } // dispatcher rejected completion during shutdown
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPalette();
    }
}
