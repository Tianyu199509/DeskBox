using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private string? _customTitleIconSourcePath;
    private ImageSource? _customTitleIconSource;
    private string? _customBackgroundSourcePath;
    private ImageSource? _customBackgroundSource;
    private string? _customPanoramaSourcePath;
    private ImageSource? _customPanoramaSource;
    private bool _customBackgroundSurfaceReleased;

    /// <summary>
    /// Pushes the per-widget custom title-icon override (emoji or image)
    /// onto the shell. A null/absent override leaves every title-icon host
    /// on the global icon mode, so this is safe to call on every appearance
    /// refresh. Decoded sources are memoized per path so appearance refreshes
    /// reuse the surface instead of reallocating an ImageSource each time.
    /// </summary>
    protected void ApplyCustomTitleIcon()
    {
        WidgetShell shell = WidgetShellControl;
        string? emoji = WidgetTitleIconCustomization.GetEmojiOverride(Config);
        shell.TitleIconCustomEmoji = emoji ?? string.Empty;
        shell.TitleIconCustomSource = emoji is null
            ? ResolveCustomTitleIconSource(WidgetTitleIconAssetStore.Current.ResolveImagePath(
                Config.Id,
                WidgetTitleIconCustomization.GetImageFileNameOverride(Config)))
            : null;
    }

    /// <summary>
    /// Resolves the widget background: a per-widget image wins, then a
    /// per-widget solid color, then the global appearance mode (unified image
    /// or panorama sampling), then the global window material. Per-widget
    /// fit/dim overrides apply on top of whichever background is active.
    /// </summary>
    protected void ApplyCustomWidgetBackground()
    {
        WidgetShell shell = WidgetShellControl;
        AppSettings settings = SettingsService.Settings;
        WidgetShellSettingsSlice shellSettings = settings.WidgetShell;
        string mode = WidgetBackgroundModeKinds.Normalize(shellSettings.WidgetBackgroundMode);
        string? ownFile = WidgetBackgroundCustomization.GetImageFileNameOverride(Config);
        string? ownColor = WidgetBackgroundCustomization.GetColorOverride(Config);

        ImageSource? panoramaSource = null;
        string? imagePath = null;
        string fit = WidgetBackgroundCustomization.FitFill;

        if (ownFile is not null)
        {
            imagePath = WidgetTitleIconAssetStore.Current.ResolveBackgroundPath(Config.Id, ownFile);
            fit = WidgetBackgroundCustomization.GetFitOverride(Config) ??
                WidgetBackgroundCustomization.FitFill;
        }
        else if (mode == WidgetBackgroundModeKinds.Panorama)
        {
            panoramaSource = ResolveCustomPanoramaSource(
                WidgetTitleIconAssetStore.Current.ResolvePanoramaBackgroundPath(
                    shellSettings.WidgetBackgroundPanoramaImage));
        }
        else if (mode == WidgetBackgroundModeKinds.UnifiedImage)
        {
            imagePath = WidgetTitleIconAssetStore.Current.ResolveUnifiedBackgroundPath(
                shellSettings.WidgetBackgroundUnifiedImage);
            fit = WidgetBackgroundCustomization.GetFitOverride(Config) ??
                WidgetBackgroundCustomization.NormalizeFit(shellSettings.WidgetBackgroundUnifiedFit);
        }

        shell.CustomBackgroundPanoramaSource = panoramaSource;
        shell.CustomBackgroundSource = ResolveCustomBackgroundSource(imagePath);
        shell.CustomBackgroundFit = fit;
        // The color only shows when no image layer resolved — image wins,
        // per-widget color beats the global image modes, the material is the
        // floor for everything.
        shell.CustomBackgroundColorHex = imagePath is null && panoramaSource is null
            ? ownColor
            : null;
        shell.CustomBackgroundDim =
            WidgetBackgroundCustomization.ResolveEffectiveDimPercent(Config, settings) / 100d;
        _customBackgroundSurfaceReleased = false;
        if (panoramaSource is not null)
        {
            UpdateCustomPanoramaViewport();
        }

        OnCustomWidgetBackgroundChanged(
            panoramaSource is not null || shell.IsCustomBackgroundActive);
    }

    /// <summary>
    /// Notifies content surfaces that yield their own visuals to a user-set
    /// background (Glance hides its internal background layer).
    /// </summary>
    protected virtual void OnCustomWidgetBackgroundChanged(bool active)
    {
    }

    /// <summary>
    /// Drops the memoized sources after a commit: a re-pick writes a new
    /// image to the SAME stored file name, so the path alone cannot detect
    /// content changes.
    /// </summary>
    protected void InvalidateCustomAssetSources()
    {
        _customTitleIconSourcePath = null;
        _customTitleIconSource = null;
        _customBackgroundSourcePath = null;
        _customBackgroundSource = null;
        _customPanoramaSourcePath = null;
        _customPanoramaSource = null;
    }

    /// <summary>
    /// Releases the decoded custom-background surfaces for a long-hidden
    /// widget (bounded-decode budget discipline: hidden widgets keep no
    /// decoded bitmaps). The next activation re-applies them from disk.
    /// </summary>
    protected void ReleaseCustomBackgroundSurface()
    {
        if (_customBackgroundSource is null && _customPanoramaSource is null)
        {
            return;
        }

        InvalidateCustomAssetSources();
        _customBackgroundSurfaceReleased = true;
        WidgetShellControl.CustomBackgroundSource = null;
        WidgetShellControl.CustomBackgroundPanoramaSource = null;
    }

    protected void RecoverCustomBackgroundIfReleased()
    {
        if (_customBackgroundSurfaceReleased)
        {
            ApplyCustomWidgetBackground();
        }
    }

    /// <summary>
    /// Repositions the shared panorama image so the slice under the window's
    /// desktop position lands inside the plate. Pure math plus Win32 rect
    /// reads — no decode — so window drags update the sampled slice in real
    /// time. The fit is per-monitor (the wallpaper model: each monitor fills
    /// itself with the image); fitting the whole virtual-desktop union only
    /// works on single-DPI setups and rendered zoomed slices on mixed-DPI
    /// screens that never recovered after switching back.
    /// </summary>
    protected void UpdateCustomPanoramaViewport()
    {
        if (_customPanoramaSource is not BitmapImage bitmap ||
            bitmap.PixelWidth <= 0 ||
            bitmap.PixelHeight <= 0 ||
            HWnd == IntPtr.Zero ||
            !Win32Helper.GetWindowRect(HWnd, out Win32Helper.RECT windowRect) ||
            !Win32Helper.TryGetWindowMonitorRect(HWnd, out Win32Helper.RECT monitorRect))
        {
            return;
        }

        double monitorWidth = monitorRect.Right - monitorRect.Left;
        double monitorHeight = monitorRect.Bottom - monitorRect.Top;
        (double offsetX, double offsetY, double fittedWidth, double fittedHeight) =
            WidgetBackgroundPanoramaCalculator.ComputeMonitorFit(
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                monitorWidth,
                monitorHeight);
        double fittedX = monitorRect.Left + offsetX;
        double fittedY = monitorRect.Top + offsetY;
        double dipScale = Win32Helper.GetDpiScaleForWindow(HWnd, xamlRoot: null);
        if (!double.IsFinite(dipScale) || dipScale <= 0)
        {
            dipScale = 1;
        }

        WidgetShellControl.SetCustomPanoramaPlacement(
            fittedWidth / dipScale,
            fittedHeight / dipScale,
            (fittedX - windowRect.Left) / dipScale,
            (fittedY - windowRect.Top) / dipScale);
    }

    private ImageSource? ResolveCustomTitleIconSource(string? path)
    {
        if (!string.Equals(_customTitleIconSourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _customTitleIconSourcePath = path;
            _customTitleIconSource = WidgetTitleIconImageSourceFactory.TryCreate(path);
        }

        return _customTitleIconSource;
    }

    private ImageSource? ResolveCustomBackgroundSource(string? path)
    {
        if (!string.Equals(_customBackgroundSourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _customBackgroundSourcePath = path;
            _customBackgroundSource = WidgetBackgroundImageSourceFactory.TryCreate(path);
        }

        return _customBackgroundSource;
    }

    private ImageSource? ResolveCustomPanoramaSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!string.Equals(_customPanoramaSourcePath, path, StringComparison.OrdinalIgnoreCase))
        {
            // One shared decode for every widget: all windows reference the
            // same panorama URI, so the XAML image cache holds one surface.
            int canvasWidth = Win32Helper.GetSystemMetrics(Win32Helper.SM_CXVIRTUALSCREEN);
            int decodeWidth = Math.Clamp(
                canvasWidth > 0 ? canvasWidth : 1920,
                720,
                2560);
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = decodeWidth
            };
            bitmap.UriSource = new Uri(path);
            bitmap.ImageOpened += (_, _) =>
                DispatcherQueue.TryEnqueue(UpdateCustomPanoramaViewport);

            _customPanoramaSourcePath = path;
            _customPanoramaSource = bitmap;
        }

        return _customPanoramaSource;
    }

    /// <summary>
    /// Builds the icon customizer as an anchored flyout. A ContentDialog
    /// inside a small widget window gets clipped by the window bounds, so
    /// the customizer follows the same flyout placement the widget context
    /// menus and the foreground color picker already use.
    /// </summary>
    /// <param name="reopenCustomizer">
    /// Re-shows the customizer after the system image picker ran; the
    /// picker steals window focus, so the flyout cannot survive it.
    /// </param>
    protected Flyout BuildTitleIconCustomizerFlyout(Action reopenCustomizer)
    {
        var customizer = new WidgetIconCustomizer();
        customizer.Initialize(Config);
        customizer.Changed += (_, _) =>
        {
            InvalidateCustomAssetSources();
            ApplyCustomTitleIconAndGroupIdentity();
        };
        customizer.PickImageRequested += (_, _) =>
            _ = RunTitleIconImagePickAsync(reopenCustomizer);
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Content = customizer
        };
        customizer.HostFlyout = flyout;
        return flyout;
    }

    /// <summary>
    /// A grouped widget shows its identity through the group title switcher,
    /// whose per-member snapshots are rebuilt from the configs — so a custom
    /// icon commit must refresh both the shell hosts and the group
    /// presentation, not just the shell properties.
    /// </summary>
    protected void ApplyCustomTitleIconAndGroupIdentity()
    {
        ApplyCustomTitleIcon();
        RefreshWidgetGroupPresentation();
    }

    /// <summary>
    /// Builds the background customizer flyout. Same relay rule as the icon
    /// customizer: the system picker cannot live inside a light-dismiss
    /// flyout, so image picking closes this flyout and reopens afterwards.
    /// </summary>
    protected Flyout BuildWidgetBackgroundCustomizerFlyout(Action reopenCustomizer)
    {
        var customizer = new WidgetBackgroundCustomizer();
        customizer.Initialize(Config);
        customizer.Changed += (_, _) =>
        {
            InvalidateCustomAssetSources();
            ApplyCustomWidgetBackground();
            ApplyCustomTitleIcon();
        };
        customizer.PickImageRequested += (_, _) =>
            _ = RunWidgetBackgroundImagePickAsync(reopenCustomizer);
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Content = customizer
        };
        customizer.HostFlyout = flyout;
        return flyout;
    }

    private async Task RunTitleIconImagePickAsync(Action reopenCustomizer)
    {
        string? pickedPath = await PickWidgetImageAsync();
        if (pickedPath is null)
        {
            DispatcherQueue.TryEnqueue(() => reopenCustomizer());
            return;
        }

        WidgetTitleIconAssetResult result = await WidgetTitleIconAssetStore.Current
            .CopyImageAsync(Config.Id, pickedPath);
        string feedbackKey;
        switch (result)
        {
            case WidgetTitleIconAssetResult.Copied:
                WidgetTitleIconCustomization.SetImageOverride(
                    Config,
                    WidgetTitleIconAssetStore.GetStoredFileName(pickedPath));
                if (App.Current.SettingsService is { } settingsService)
                {
                    // Per-widget chrome: nothing else subscribes to this
                    // change, so skip the global settings notification.
                    settingsService.UpdateWidget(Config, notifySubscribers: false);
                }

                InvalidateCustomAssetSources();
                ApplyCustomTitleIconAndGroupIdentity();
                DispatcherQueue.TryEnqueue(() => reopenCustomizer());
                return;

            case WidgetTitleIconAssetResult.TooLarge:
                feedbackKey = "Widget.CustomIcon.TooLarge";
                break;

            case WidgetTitleIconAssetResult.UnsupportedFormat:
                feedbackKey = "Widget.CustomIcon.UnsupportedFormat";
                break;

            default:
                feedbackKey = "Widget.CustomIcon.ImageFailed";
                break;
        }

        WidgetShellControl.ShowFeedback(new WidgetFeedbackRequest(
            App.Current.LocalizationService.T(feedbackKey),
            WidgetFeedbackSeverity.Error,
            "widget-title-icon-image-error"));
        DispatcherQueue.TryEnqueue(() => reopenCustomizer());
    }

    private async Task RunWidgetBackgroundImagePickAsync(Action reopenCustomizer)
    {
        string? pickedPath = await PickWidgetImageAsync();
        if (pickedPath is null)
        {
            DispatcherQueue.TryEnqueue(() => reopenCustomizer());
            return;
        }

        WidgetTitleIconAssetResult result = await WidgetTitleIconAssetStore.Current
            .CopyBackgroundImageAsync(Config.Id, pickedPath);
        string feedbackKey;
        switch (result)
        {
            case WidgetTitleIconAssetResult.Copied:
                WidgetBackgroundCustomization.SetImageOverride(
                    Config,
                    WidgetTitleIconAssetStore.GetStoredBackgroundFileName(pickedPath));
                if (App.Current.SettingsService is { } settingsService)
                {
                    settingsService.UpdateWidget(Config, notifySubscribers: false);
                }

                InvalidateCustomAssetSources();
                ApplyCustomWidgetBackground();
                DispatcherQueue.TryEnqueue(() => reopenCustomizer());
                return;

            case WidgetTitleIconAssetResult.TooLarge:
                feedbackKey = "Widget.CustomIcon.TooLarge";
                break;

            case WidgetTitleIconAssetResult.UnsupportedFormat:
                feedbackKey = "Widget.CustomIcon.UnsupportedFormat";
                break;

            default:
                feedbackKey = "Widget.CustomIcon.ImageFailed";
                break;
        }

        WidgetShellControl.ShowFeedback(new WidgetFeedbackRequest(
            App.Current.LocalizationService.T(feedbackKey),
            WidgetFeedbackSeverity.Error,
            "widget-background-image-error"));
        DispatcherQueue.TryEnqueue(() => reopenCustomizer());
    }

    private async Task<string?> PickWidgetImageAsync()
    {
        return await FileOpenPickerService.PickSingleFileAsync(
            HWnd,
            [".png", ".jpg", ".jpeg", ".bmp", ".ico", ".svg"],
            PickerLocationId.PicturesLibrary);
    }
}
