using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioPreviewFrameThemeRenderer : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioPreviewFrameThemeRenderer(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }

    private BitmapImage? _cachedWallpaperBitmap;
    private ElementTheme? _cachedWallpaperTheme;
    private bool _wallpaperLoadAttempted;
    private int _stagePulseToken;

    private TextBlock? _blueprintTitleText;
    private Border? _blueprintFrame;
    private Border? _blueprintDivider;
    private readonly List<(Border Block, bool UseSecondaryWash)> _blueprintSkeletonBlocks = [];

    private Border? _capsulePreviewFrame;
    private TextBlock? _capsuleEmojiText;
    private FontIcon? _capsuleGlyphIcon;
    private TextBlock? _capsuleTitleText;
    private TextBlock? _capsuleSummaryText;
    private Border? _capsuleBadgeBorder;
    private TextBlock? _capsuleBadgeText;
    private Button? _capsulePrimaryActionButton;
    private FontIcon? _capsulePrimaryActionIcon;
    private TextBlock? _capsulePrimaryActionEmojiText;
    private ProgressBar? _capsuleProgressBar;

    internal ElementTheme? CachedWallpaperTheme { get => _cachedWallpaperTheme; set => _cachedWallpaperTheme = value; }
    internal TextBlock? BlueprintTitleText => _blueprintTitleText;
    internal Border? CapsulePreviewFrame => _capsulePreviewFrame;

    internal void StopBlueprintSkeletonPulses()
    {
        FluentMotionHelper.StopSkeletonPulse(_blueprintFrame);
        foreach ((Border block, _) in _blueprintSkeletonBlocks)
        {
            FluentMotionHelper.StopSkeletonPulse(block);
        }
    }

    internal void ResetPreviewFrameElements()
    {
        StopBlueprintSkeletonPulses();
        _blueprintTitleText = null;
        _blueprintFrame = null;
        _blueprintDivider = null;
        _blueprintSkeletonBlocks.Clear();

        _capsulePreviewFrame = null;
        _capsuleEmojiText = null;
        _capsuleGlyphIcon = null;
        _capsuleTitleText = null;
        _capsuleSummaryText = null;
        _capsuleBadgeBorder = null;
        _capsuleBadgeText = null;
        _capsulePrimaryActionButton = null;
        _capsulePrimaryActionIcon = null;
        _capsulePrimaryActionEmojiText = null;
        _capsuleProgressBar = null;
    }

    internal async Task EnsureWallpaperLoadedAsync()
    {
        if (_wallpaperLoadAttempted || !_session.HasSystemWallpaperAvailable)
        {
            return;
        }

        _wallpaperLoadAttempted = true;
        try
        {
            string? path = DesktopWallpaperReadOps.TryResolveCurrentWallpaperImagePath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            (byte[]? bytes, ElementTheme? sampledTheme) = await Task.Run(async () =>
            {
                using FileStream? fs = DesktopWallpaperReadOps.TryOpenSharedReadStream(path);
                if (fs is null || fs.Length <= 0 || fs.Length > 28 * 1024 * 1024)
                {
                    return ((byte[]?)null, (ElementTheme?)null);
                }

                using var ms = new MemoryStream((int)fs.Length);
                await fs.CopyToAsync(ms);
                byte[] buffer = ms.ToArray();
                ElementTheme? sampled = await TrySampleWallpaperThemeAsync(buffer);
                return ((byte[]?)buffer, sampled);
            });

            if (bytes is null || bytes.Length == 0 || _shell.IsClosed)
            {
                return;
            }

            _cachedWallpaperTheme = sampledTheme;
            var bmp = new BitmapImage { DecodePixelWidth = 680 };
            using var memStream = new MemoryStream(bytes, writable: false);
            using Windows.Storage.Streams.IRandomAccessStream ras = memStream.AsRandomAccessStream();
            await bmp.SetSourceAsync(ras);
            if (_shell.IsClosed)
            {
                return;
            }

            _cachedWallpaperBitmap = bmp;
            ApplyStageBackdropVisuals();
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Load wallpaper backdrop failed: {ex.Message}");
        }
    }

    private static async Task<ElementTheme?> TrySampleWallpaperThemeAsync(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            using Windows.Storage.Streams.IRandomAccessStream ras = ms.AsRandomAccessStream();
            Windows.Graphics.Imaging.BitmapDecoder decoder =
                await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask();
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = 16,
                ScaledHeight = 16,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };
            Windows.Graphics.Imaging.PixelDataProvider pixelProvider = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask();
            byte[] rgba = pixelProvider.DetachPixelData();
            if (rgba.Length < 4)
            {
                return null;
            }

            double totalLuma = 0;
            int pixelCount = rgba.Length / 4;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                byte b = rgba[i];
                byte g = rgba[i + 1];
                byte r = rgba[i + 2];
                totalLuma += 0.2126 * r + 0.7152 * g + 0.0722 * b;
            }

            double avgLuma = totalLuma / Math.Max(1, pixelCount);
            return avgLuma < 140.0 ? ElementTheme.Dark : ElementTheme.Light;
        }
        catch
        {
            return null;
        }
    }

    internal void ApplyStageBackdropVisuals()
    {
        if (_shell.StageViewport.StageBorder is null)
        {
            return;
        }

        StageBackdropMode mode = _session.StageBackdropMode == StageBackdropMode.Wallpaper && !_session.HasSystemWallpaperAvailable
            ? (Win32Helper.IsSystemDarkMode() ? StageBackdropMode.Dark : StageBackdropMode.Light)
            : _session.StageBackdropMode;

        if (mode == StageBackdropMode.Wallpaper)
        {
            _shell.StageViewport.StageBorder.RequestedTheme = ElementTheme.Dark;
            _shell.StageViewport.StageBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x1A, 0x22));
            if (_shell.StageViewport.StageWallpaperLayer is not null)
            {
                if (_cachedWallpaperBitmap is not null)
                {
                    _shell.StageViewport.StageWallpaperLayer.Background = new ImageBrush
                    {
                        ImageSource = _cachedWallpaperBitmap,
                        Stretch = Stretch.UniformToFill
                    };
                    _shell.StageViewport.StageWallpaperLayer.Visibility = Visibility.Visible;
                }
                else
                {
                    _shell.StageViewport.StageWallpaperLayer.Visibility = Visibility.Collapsed;
                }
            }

            if (_shell.StageViewport.StageWallpaperScrim is not null)
            {
                _shell.StageViewport.StageWallpaperScrim.Visibility = Visibility.Visible;
            }

            ElementTheme desktopWidgetTheme = ResolveEffectiveStageWidgetTheme(_cachedWallpaperTheme ?? ElementTheme.Dark);
            ApplyStageWidgetTheme(desktopWidgetTheme);
            _shell.StageViewport.RefreshStageToolbarChrome();
            return;
        }

        if (_shell.StageViewport.StageWallpaperLayer is not null)
        {
            _shell.StageViewport.StageWallpaperLayer.Visibility = Visibility.Collapsed;
        }
        if (_shell.StageViewport.StageWallpaperScrim is not null)
        {
            _shell.StageViewport.StageWallpaperScrim.Visibility = Visibility.Collapsed;
        }

        bool isDark = mode == StageBackdropMode.Dark;
        ElementTheme targetTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        _shell.StageViewport.StageBorder.RequestedTheme = targetTheme;
        _shell.StageViewport.StageBorder.Background = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1F))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE6, 0xEA, 0xF1));
        ApplyStageWidgetTheme(ResolveEffectiveStageWidgetTheme(targetTheme));
        _shell.StageViewport.RefreshStageToolbarChrome();
    }

    private ElementTheme ResolveEffectiveStageWidgetTheme(ElementTheme defaultBackdropWidgetTheme) =>
        _session.StageWidgetTheme switch
        {
            CustomWidgetStudioPreferencesStore.StageThemeLight => ElementTheme.Light,
            CustomWidgetStudioPreferencesStore.StageThemeDark => ElementTheme.Dark,
            _ => defaultBackdropWidgetTheme
        };

    internal void OnHostAppearanceChanged()
    {
        if (_shell.IsClosed)
        {
            return;
        }

        _shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (_shell.IsClosed)
            {
                return;
            }

            if (_session.Page == StudioPage.Preview)
            {
                ApplyStageBackdropVisuals();
            }
        });
    }

    private WidgetChromeMode ResolvePreviewWidgetChromeMode(
        CustomWidgetManifest? manifest,
        string? existingWidgetId = null)
    {
        if (_session.Page == StudioPage.Preview)
        {
            if (_session.StageChromeMode != WidgetChromeMode.System)
            {
                return _session.StageChromeMode;
            }

            if (_session.HasDirtyChromeModeOverride)
            {
                return _hostBridge.ResolvePreviewWidgetChromeMode(manifest, existingWidgetId: null, _loc);
            }
        }

        return _hostBridge.ResolvePreviewWidgetChromeMode(manifest, existingWidgetId, _loc);
    }

    private double ResolvePreviewOuterCornerRadius()
    {
        StudioShellVisualSettings shellVisual = _hostBridge.GetShellVisualSettings();
        return WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
            WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                shellVisual.WidgetCornerPreference));
    }

    internal static Windows.UI.Color ResolveStaticThumbnailSurfaceColor(
        StudioShellVisualSettings shellVisual,
        bool isDark,
        Windows.UI.Color accentColor)
    {
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            shellVisual.WidgetMaterialType);
        double surfaceOpacity = Math.Clamp(shellVisual.WidgetOpacity, 0.0, 1.0);

        if (SettingsService.IsMicaMaterial(materialType))
        {
            bool useAlt = materialType == SettingsService.WidgetMaterialTypeMicaAlt;
            Windows.UI.Color baseColor = useAlt
                ? WidgetMaterialVisualCalculator.BuildMicaFallbackColor(isDark, useAlt: true)
                : WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            return Windows.UI.Color.FromArgb(0xFF, baseColor.R, baseColor.G, baseColor.B);
        }

        if (SettingsService.IsAcrylicMaterial(materialType))
        {
            Windows.UI.Color tintColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            return Windows.UI.Color.FromArgb(0xFF, tintColor.R, tintColor.G, tintColor.B);
        }

        Windows.UI.Color solidColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
            isDark,
            accentColor,
            surfaceOpacity);
        if (solidColor.A == 0xFF)
        {
            return solidColor;
        }

        // Composite translucent solid setting over the gallery preview-well base so
        // RenderTargetBitmap thumbnails stay crisp and visually consistent across all cards.
        Windows.UI.Color wellBase = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xF6, 0xFA);
        double alpha = solidColor.A / 255.0;
        byte r = (byte)Math.Clamp(Math.Round(solidColor.R * alpha + wellBase.R * (1.0 - alpha)), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(solidColor.G * alpha + wellBase.G * (1.0 - alpha)), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(solidColor.B * alpha + wellBase.B * (1.0 - alpha)), 0, 255);
        return Windows.UI.Color.FromArgb(0xFF, r, g, b);
    }

    private Brush BuildPreviewSurfaceMaterialBrush(
        bool isDark,
        Windows.UI.Color accentColor,
        bool forceStaticSurface = false)
    {
        StudioShellVisualSettings shellVisual = _hostBridge.GetShellVisualSettings();
        if (forceStaticSurface)
        {
            return new SolidColorBrush(ResolveStaticThumbnailSurfaceColor(shellVisual, isDark, accentColor));
        }

        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            shellVisual.WidgetMaterialType);
        double surfaceOpacity = Math.Clamp(shellVisual.WidgetOpacity, 0.0, 1.0);
        double materialIntensity = shellVisual.WidgetMaterialIntensity;

        if (SettingsService.IsMicaMaterial(materialType))
        {
            bool useAlt = materialType == SettingsService.WidgetMaterialTypeMicaAlt;
            WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateMica(
                isDark,
                useAlt,
                materialIntensity);
            Windows.UI.Color tintColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            Windows.UI.Color fallbackColor = WidgetMaterialVisualCalculator.BuildMicaFallbackColor(isDark, useAlt);
            return new AcrylicBrush
            {
                TintColor = tintColor,
                FallbackColor = fallbackColor,
                TintOpacity = profile.TintOpacity,
                TintLuminosityOpacity = profile.LuminosityOpacity
            };
        }

        if (SettingsService.IsAcrylicMaterial(materialType))
        {
            bool useBase = materialType == SettingsService.WidgetMaterialTypeAcrylicBase;
            WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
                isDark,
                useBase,
                surfaceOpacity,
                materialIntensity);
            Windows.UI.Color tintColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            return new AcrylicBrush
            {
                TintColor = tintColor,
                FallbackColor = Windows.UI.Color.FromArgb(0xFF, tintColor.R, tintColor.G, tintColor.B),
                TintOpacity = profile.TintOpacity,
                TintLuminosityOpacity = profile.LuminosityOpacity
            };
        }

        Windows.UI.Color solidColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
            isDark,
            accentColor,
            surfaceOpacity);
        return new SolidColorBrush(solidColor);
    }

    private void SyncPreviewFrameShadowPlate(UIElement scopeOwner, bool isDark)
    {
        if (!WidgetTextShadow.GetScope(scopeOwner))
        {
            return;
        }

        StudioShellVisualSettings shellVisual = _hostBridge.GetShellVisualSettings();
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            shellVisual.WidgetMaterialType);
        double surfaceOpacity = Math.Clamp(shellVisual.WidgetOpacity, 0.0, 1.0);
        double materialIntensity = shellVisual.WidgetMaterialIntensity;

        double effectivePlateOpacity = WidgetMaterialVisualCalculator.ResolveEffectivePlateOpacity(
            materialType,
            isDark,
            surfaceOpacity,
            materialIntensity,
            acrylicControllerAttached: SettingsService.IsAcrylicMaterial(materialType),
            micaControllerAttached: SettingsService.IsMicaMaterial(materialType),
            legacyAccentBackdropActive: false,
            usesLegacyWindowAcrylic: false,
            hasOpaqueCustomColor: false);

        WidgetTextShadow.SetPlateOpaque(
            scopeOwner,
            WidgetTextShadow.ShouldSuppressForOpaquePlate(
                effectivePlateOpacity,
                materialType,
                hasImageBackground: false));
        WidgetTextShadow.SetEnabled(shellVisual.WidgetTextShadowEnabled);
    }

    private WidgetBorderVisuals ResolvePreviewBorderVisuals(
        bool isDark,
        Windows.UI.Color accentColor,
        string? existingWidgetId) =>
        _hostBridge.ResolvePreviewBorderVisuals(isDark, accentColor, existingWidgetId);

    private void SyncStudioPreviewTitleIcon(
        DeskBox.Controls.WidgetTitleIcon titleIcon,
        string rawEmojiOrGlyph,
        string title,
        double iconSize,
        Windows.UI.Color accentColor,
        bool isDark)
    {
        SyncTitleIconVisuals(
            titleIcon,
            rawEmojiOrGlyph,
            title,
            iconSize,
            accentColor,
            isDark,
            _hostBridge.GetShellVisualSettings().WidgetTitleIconMode);
    }

    internal static void SyncTitleIconVisuals(
        DeskBox.Controls.WidgetTitleIcon titleIcon,
        string rawEmojiOrGlyph,
        string title,
        double iconSize,
        Windows.UI.Color accentColor,
        bool isDark,
        string? configuredMode = null)
    {
        titleIcon.Mode = string.IsNullOrWhiteSpace(configuredMode)
            ? WidgetTitleIconModeNames.Color
            : configuredMode;
        titleIcon.IconSize = iconSize;
        titleIcon.LabelText = title;
        titleIcon.AccentColor = Windows.UI.Color.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        if (CustomWidgetTemplates.IsEmojiIcon(rawEmojiOrGlyph))
        {
            titleIcon.CustomEmoji = rawEmojiOrGlyph.Trim();
            titleIcon.IconKind = WidgetTitleIconKindNames.Custom;
            titleIcon.Glyph = "\uE71D";
        }
        else
        {
            titleIcon.CustomEmoji = string.Empty;
            string mapped = WidgetTitleIconKindNames.FromLegacyGlyph(rawEmojiOrGlyph);
            titleIcon.IconKind = mapped != WidgetTitleIconKindNames.Default
                ? mapped
                : WidgetTitleIconKindNames.Custom;
            titleIcon.Glyph = string.IsNullOrWhiteSpace(rawEmojiOrGlyph) ? "\uE71D" : rawEmojiOrGlyph;
        }
    }

    internal static Border CreateStandaloneCaptureFrame(
        DeskBox.ViewModels.CustomWidgetViewModel vm,
        UIElement? bodyElement,
        string resolvedTitle,
        double frameWidth,
        double frameHeight,
        bool renderCapsule,
        bool isDark,
        Windows.UI.Color accentColor,
        double textSize,
        double iconSize,
        WidgetChromeMode chromeMode = WidgetChromeMode.Standard,
        string? titleIconMode = null)
    {
        ElementTheme effective = isDark ? ElementTheme.Dark : ElementTheme.Light;
        Windows.UI.Color tintColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
        Windows.UI.Color fallbackBg = WidgetMaterialVisualCalculator.BuildMicaFallbackColor(isDark, useAlt: false);
        Windows.UI.Color strokeColor = isDark
            ? Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00);
        Windows.UI.Color primaryFg = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xE8, 0x00, 0x00, 0x00);
        Windows.UI.Color secondaryFg = isDark
            ? Windows.UI.Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x9E, 0x00, 0x00, 0x00);

        double outerRadius = renderCapsule ? 24 : 8;
        var frame = new Border
        {
            Width = frameWidth,
            Height = frameHeight,
            RequestedTheme = effective,
            CornerRadius = new CornerRadius(outerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(strokeColor),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                0xFF,
                (byte)((fallbackBg.R * 230 + tintColor.R * 25) / 255),
                (byte)((fallbackBg.G * 230 + tintColor.G * 25) / 255),
                (byte)((fallbackBg.B * 230 + tintColor.B * 25) / 255))),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        if (!isDark)
        {
            frame.Resources["WidgetLayerFillSecondaryBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            frame.Resources["WidgetLayerStrokeBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        }

        string rawGlyph = !string.IsNullOrWhiteSpace(vm.Manifest.Icon) ? vm.Manifest.Icon : "\uE71D";
        if (renderCapsule)
        {
            frame.Padding = new Thickness(14, 7, 12, 7);
            var outerGrid = new Grid { RowSpacing = 4 };
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var mainRow = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string capGlyph = CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback(
                !string.IsNullOrWhiteSpace(vm.CapsuleGlyph) ? vm.CapsuleGlyph : rawGlyph,
                "\uE71D");
            UIElement iconEl = CustomWidgetTemplates.IsSegoeFluentGlyph(capGlyph)
                ? new FontIcon
                {
                    Glyph = capGlyph,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(accentColor),
                    VerticalAlignment = VerticalAlignment.Center
                }
                : new TextBlock
                {
                    Text = CustomWidgetTemplates.IsEmojiIcon(capGlyph) ? capGlyph.Trim() : "\u2728",
                    FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center
                };
            mainRow.Children.Add(iconEl);

            var textCol = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(vm.CapsuleTitle) ? vm.CapsuleTitle : resolvedTitle,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(vm.CapsuleSummary))
            {
                textCol.Children.Add(new TextBlock
                {
                    Text = vm.CapsuleSummary,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(secondaryFg),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }
            Grid.SetColumn(textCol, 1);
            mainRow.Children.Add(textCol);

            if (!string.IsNullOrWhiteSpace(vm.CapsuleBadge))
            {
                var badgeBorder = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(isDark
                        ? Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
                        : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00)),
                    Child = new TextBlock
                    {
                        Text = vm.CapsuleBadge,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(secondaryFg)
                    }
                };
                Grid.SetColumn(badgeBorder, 2);
                mainRow.Children.Add(badgeBorder);
            }

            outerGrid.Children.Add(mainRow);
            if (vm.CapsuleProgress is { } prog)
            {
                var bar = new ProgressBar
                {
                    Height = 3,
                    Minimum = 0,
                    Maximum = 1,
                    Value = Math.Clamp(prog, 0.0, 1.0),
                    Margin = new Thickness(4, 0, 6, 0)
                };
                Grid.SetRow(bar, 1);
                outerGrid.Children.Add(bar);
            }

            frame.Child = outerGrid;
            return frame;
        }

        double baseTextSize = SettingsService.NormalizeTextSize(textSize);
        double baseIconSize = SettingsService.NormalizeIconSize(iconSize);
        double titleIconSize = Math.Clamp(Math.Round(baseIconSize * 0.72 * 0.56 * 0.54), 11, 18);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? baseTextSize
            : Math.Min(SettingsService.MaxTextSize + 2, baseTextSize + 3);
        WidgetTitleBarMetrics metrics = WidgetTitleBarMetricsCalculator.Create(
            titleIconSize,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);
        bool showHeader = chromeMode is WidgetChromeMode.Standard or WidgetChromeMode.Compact;

        var grid = new Grid { CornerRadius = new CornerRadius(outerRadius) };
        grid.RowDefinitions.Add(new RowDefinition { Height = showHeader ? metrics.RowHeight : new GridLength(0) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(showHeader ? 1 : 0) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (showHeader)
        {
            var head = new Grid
            {
                ColumnSpacing = 8,
                Padding = WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var titleIcon = new DeskBox.Controls.WidgetTitleIcon { VerticalAlignment = VerticalAlignment.Center };
            SyncTitleIconVisuals(titleIcon, rawGlyph, resolvedTitle, metrics.TitleIconSize, accentColor, isDark, titleIconMode);
            head.Children.Add(titleIcon);

            var titleTb = new TextBlock
            {
                Text = resolvedTitle,
                FontSize = metrics.TitleTextSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleTb, 1);
            head.Children.Add(titleTb);
            grid.Children.Add(head);

            var divider = new Border { Background = new SolidColorBrush(strokeColor) };
            Grid.SetRow(divider, 1);
            grid.Children.Add(divider);
        }

        if (bodyElement is not null)
        {
            var bodyHost = new Grid
            {
                CornerRadius = showHeader
                    ? new CornerRadius(0, 0, outerRadius, outerRadius)
                    : new CornerRadius(outerRadius)
            };
            bodyHost.Children.Add(bodyElement);
            Grid.SetRow(bodyHost, 2);
            grid.Children.Add(bodyHost);
        }

        frame.Child = grid;
        return frame;
    }

    private void ApplyStageWidgetTheme(ElementTheme widgetTheme)
    {
        ElementTheme effective = widgetTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;

        if (_shell.StageViewport.WidgetFrame is not null)
        {
            ApplyWidgetFrameTheme(
                _shell.StageViewport.WidgetFrame,
                _shell.StageViewport.PreviewContent,
                _shell.StageViewport.FrameTitleText,
                _shell.StageViewport.FrameEditGlyph,
                _shell.StageViewport.FrameDivider,
                effective);
        }

        if (_blueprintFrame is not null)
        {
            ApplyWidgetFrameTheme(
                _blueprintFrame,
                content: null,
                _blueprintTitleText,
                editGlyph: null,
                _blueprintDivider,
                effective);
            foreach ((Border block, bool useSecondaryWash) in _blueprintSkeletonBlocks)
            {
                block.Background = StudioUiPrimitives.StageThemeBrush(_shell, useSecondaryWash ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush", effective);
                if (block.BorderThickness.Top > 0)
                {
                    block.BorderBrush = StudioUiPrimitives.StageThemeBrush(_shell, "DividerStrokeColorDefaultBrush", effective);
                }
            }
        }

        ApplyCapsulePreviewCardTheme(effective);
    }

    internal void ApplyWidgetFrameTheme(
        Border frame,
        CustomWidgetContent? content,
        TextBlock? titleText,
        FontIcon? editGlyph,
        Border? divider,
        ElementTheme widgetTheme,
        CustomWidgetManifest? manifestOverride = null)
    {
        ElementTheme effective = widgetTheme == ElementTheme.Default
            ? StudioUiPrimitives.GetStudioExplicitTheme(_shell)
            : widgetTheme;
        bool isDark = effective != ElementTheme.Light;
        Windows.UI.Color accentColor = _hostBridge.GetEffectiveAccentColor();

        frame.RequestedTheme = effective;
        if (!isDark)
        {
            frame.Resources["WidgetLayerFillSecondaryBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            frame.Resources["WidgetLayerStrokeBrush"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        }
        else
        {
            frame.Resources.Remove("WidgetLayerFillSecondaryBrush");
            frame.Resources.Remove("WidgetLayerStrokeBrush");
        }

        StudioWidgetFrameVisualState? frameVisualState = frame.Tag as StudioWidgetFrameVisualState;
        string? existingId = frameVisualState?.ExistingWidgetId;
        bool forceStaticSurface = frameVisualState?.ClipViewport == true;
        double outerRadius = ResolvePreviewOuterCornerRadius();
        CornerRadius outerCorner = new(outerRadius);
        WidgetBorderVisuals borderVisuals = ResolvePreviewBorderVisuals(isDark, accentColor, existingId);

        frame.CornerRadius = outerCorner;
        frame.Background = BuildPreviewSurfaceMaterialBrush(isDark, accentColor, forceStaticSurface);
        SyncPreviewFrameShadowPlate(frame, isDark);

        if (frame.Tag is StudioWidgetFrameVisualState state)
        {
            CustomWidgetManifest? effectiveManifest = manifestOverride ??
                (state.IsBlueprint ? null : (content?.ViewModel.Manifest ?? state.Content?.ViewModel.Manifest));
            WidgetChromeMode chromeMode = ResolvePreviewWidgetChromeMode(effectiveManifest, state.ExistingWidgetId);
            StudioShellVisualSettings shellVisual = _hostBridge.GetShellVisualSettings();
            double baseTextSize = SettingsService.NormalizeTextSize(shellVisual.TextSize);
            double baseIconSize = SettingsService.NormalizeIconSize(shellVisual.IconSize);
            double titleIconSize = Math.Clamp(Math.Round(baseIconSize * 0.72 * 0.56 * 0.54), 11, 18);
            double titleTextSize = chromeMode == WidgetChromeMode.Compact
                ? baseTextSize
                : Math.Min(SettingsService.MaxTextSize + 2, baseTextSize + 3);
            WidgetTitleBarMetrics metrics = WidgetTitleBarMetricsCalculator.Create(
                titleIconSize,
                titleTextSize,
                includeInnerPadding: false,
                chromeMode);

            bool showHeader = chromeMode is WidgetChromeMode.Standard or WidgetChromeMode.Compact;
            state.RootGrid.CornerRadius = outerCorner;
            state.RootGrid.RowDefinitions[0].Height = showHeader ? metrics.RowHeight : new GridLength(0);
            state.RootGrid.RowDefinitions[1].Height = new GridLength(showHeader && borderVisuals.DividerColor.A > 0 ? 1 : 0);
            state.HeaderHost.Visibility = showHeader ? Visibility.Visible : Visibility.Collapsed;
            state.Divider.Visibility = showHeader && borderVisuals.DividerColor.A > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            state.HeaderInnerGrid.Padding = WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode);
            if (state.HeaderHost is Button hb)
            {
                hb.CornerRadius = new CornerRadius(outerRadius, outerRadius, 0, 0);
            }

            state.TitleText.FontSize = metrics.TitleTextSize;
            SyncStudioPreviewTitleIcon(
                state.TitleIcon,
                state.RawEmojiOrGlyph,
                state.TitleText.Text,
                metrics.TitleIconSize,
                accentColor,
                isDark);

            state.BodyClipHost.CornerRadius = showHeader
                ? new CornerRadius(0, 0, outerRadius, outerRadius)
                : outerCorner;
            state.OutlineBorder.CornerRadius = outerCorner;
            state.OutlineBorder.BorderThickness = new Thickness(borderVisuals.Thickness);
            state.OutlineBorder.BorderBrush = new SolidColorBrush(borderVisuals.BorderColor);

            if (state.OverlayGripHandle is not null && state.OverlayGripBar is not null)
            {
                bool enableOverlayGrip = !state.ClipViewport &&
                                         !state.IsBlueprint &&
                                         chromeMode == WidgetChromeMode.Overlay;
                state.OverlayGripHandle.Visibility = enableOverlayGrip ? Visibility.Visible : Visibility.Collapsed;
                state.OverlayGripHandle.Opacity = enableOverlayGrip && state.IsPointerOverFrame ? 0.9 : 0;
                state.OverlayGripBar.Background = new SolidColorBrush(
                    isDark
                        ? Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)
                        : Windows.UI.Color.FromArgb(0x70, 0x00, 0x00, 0x00));
            }
        }
        else
        {
            frame.BorderThickness = new Thickness(borderVisuals.Thickness);
            frame.BorderBrush = new SolidColorBrush(borderVisuals.BorderColor);
        }

        if (titleText is not null)
        {
            titleText.Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorPrimaryBrush", effective);
        }

        if (editGlyph is not null)
        {
            editGlyph.Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorTertiaryBrush", effective);
        }

        if (divider is not null)
        {
            divider.Background = borderVisuals.DividerColor.A > 0
                ? new SolidColorBrush(borderVisuals.DividerColor)
                : StudioUiPrimitives.StageThemeBrush(_shell, "DividerStrokeColorDefaultBrush", effective);
        }

        if (content is not null)
        {
            bool themeChanged = content.RequestedTheme != effective;
            content.RequestedTheme = effective;
            if (themeChanged)
            {
                if (content.IsLoaded)
                {
                    content.RebuildNativeTree();
                }
                else
                {
                    content.RequestCoalescedRebuild();
                }
            }
        }
    }

    internal Border BuildBlueprintStagePlaceholder()
    {
        string displayPending = !string.IsNullOrWhiteSpace(_session.PendingDisplayPrompt)
            ? _session.PendingDisplayPrompt!
            : _session.PendingPrompt;
        string initialTitle = !string.IsNullOrWhiteSpace(_session.InFlightDetectedWidgetName)
            ? _session.InFlightDetectedWidgetName
            : (_session.IsInPlaceGenerating
                ? _loc.T("CustomWidget.Workshop.Stage.BlueprintPreparing")
                : (!string.IsNullOrWhiteSpace(displayPending)
                    ? CustomWidgetWorkshopFlow.SummarizePrompt(displayPending, 32)
                    : _loc.T("CustomWidget.Title")));

        ElementTheme initialWidgetTheme = _session.StageBackdropMode switch
        {
            StageBackdropMode.Light => ElementTheme.Light,
            StageBackdropMode.Dark => ElementTheme.Dark,
            _ => StudioUiPrimitives.GetStudioExplicitTheme(_shell)
        };

        var frame = new Border
        {
            Width = 320,
            MinHeight = 228,
            BorderThickness = new Thickness(0)
        };
        _blueprintFrame = frame;
        _blueprintSkeletonBlocks.Clear();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string initialEmoji = ResolveDisplayEmoji(null);
        var blueprintIcon = new DeskBox.Controls.WidgetTitleIcon
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(blueprintIcon);

        _blueprintTitleText = new TextBlock
        {
            Text = initialTitle,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_blueprintTitleText, 1);
        head.Children.Add(_blueprintTitleText);
        grid.Children.Add(head);

        var divider = new Border();
        _blueprintDivider = divider;
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);

        var bodyStack = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16, 18, 16, 18),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (_session.IsInPlaceGenerating)
        {
            var skelTop = new Border
            {
                Height = 16,
                Width = 176,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(5)
            };
            FluentMotionHelper.StartSkeletonPulse(skelTop, delayMs: 0);
            _blueprintSkeletonBlocks.Add((skelTop, true));
            bodyStack.Children.Add(skelTop);

            var skelMid = new Border
            {
                Height = 46,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1)
            };
            FluentMotionHelper.StartSkeletonPulse(skelMid, delayMs: 110);
            _blueprintSkeletonBlocks.Add((skelMid, false));
            bodyStack.Children.Add(skelMid);

            var bottomRow = new Grid { ColumnSpacing = 8 };
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var leftBlock = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(6)
            };
            FluentMotionHelper.StartSkeletonPulse(leftBlock, delayMs: 210);
            _blueprintSkeletonBlocks.Add((leftBlock, true));
            bottomRow.Children.Add(leftBlock);
            var rightBlock = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(6)
            };
            FluentMotionHelper.StartSkeletonPulse(rightBlock, delayMs: 290);
            _blueprintSkeletonBlocks.Add((rightBlock, false));
            Grid.SetColumn(rightBlock, 1);
            bottomRow.Children.Add(rightBlock);
            bodyStack.Children.Add(bottomRow);
        }
        else
        {
            StudioChatMessage? latestAssistant = _session.ChatMessages
                .LastOrDefault(static m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            bool isUnsupported = latestAssistant is not null &&
                                 string.Equals(latestAssistant.Kind, "unsupported", StringComparison.OrdinalIgnoreCase);

            var idleIcon = new FontIcon
            {
                Glyph = isUnsupported ? "\uE7BA" : "\uE8BD",
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = StudioUiPrimitives.StageThemeBrush(
                    _shell,
                    isUnsupported ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush",
                    initialWidgetTheme)
            };
            bodyStack.Children.Add(idleIcon);

            bodyStack.Children.Add(new TextBlock
            {
                Text = _loc.T(isUnsupported
                    ? "CustomWidget.Workshop.Stage.IdleUnsupportedTitle"
                    : "CustomWidget.Workshop.Stage.IdleConsultTitle"),
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorPrimaryBrush", initialWidgetTheme)
            });

            bodyStack.Children.Add(new TextBlock
            {
                Text = _loc.T(isUnsupported
                    ? "CustomWidget.Workshop.Stage.IdleUnsupportedHint"
                    : "CustomWidget.Workshop.Stage.IdleConsultHint"),
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorSecondaryBrush", initialWidgetTheme)
            });

            var browseTemplatesBtn = new Button
            {
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 4, 0, 0),
                CornerRadius = new CornerRadius(6),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                RequestedTheme = initialWidgetTheme,
                Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8A9", _loc.T("CustomWidget.Workshop.Stage.BrowseTemplates"))
            };
            browseTemplatesBtn.Click += (_, _) =>
            {
                if (_session.IsAnyStudioJobRunning)
                {
                    return;
                }

                string preservedIdea = _session.StartDraftIdea;
                _shell.ResetSession();
                _session.TargetWidgetId = null;
                _session.TargetWidgetName = null;
                _shell.UpdateWindowTitle();
                _session.StartDraftIdea = preservedIdea;
                _shell.StartGallery.ShowStartPage(preservedIdea);
            };
            bodyStack.Children.Add(browseTemplatesBtn);
        }

        var bodyClipHost = new Grid();
        bodyClipHost.Children.Add(bodyStack);
        Grid.SetRow(bodyClipHost, 2);
        grid.Children.Add(bodyClipHost);

        var outlineBorder = new Border
        {
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(outlineBorder, 3);
        grid.Children.Add(outlineBorder);

        frame.Tag = new StudioWidgetFrameVisualState
        {
            IsBlueprint = true,
            ExistingWidgetId = _session.TargetWidgetId,
            RootGrid = grid,
            HeaderHost = head,
            HeaderInnerGrid = head,
            TitleIcon = blueprintIcon,
            TitleText = _blueprintTitleText,
            Divider = divider,
            BodyClipHost = bodyClipHost,
            OutlineBorder = outlineBorder,
            RawEmojiOrGlyph = initialEmoji
        };
        frame.Child = grid;

        ApplyWidgetFrameTheme(frame, null, _blueprintTitleText, null, divider, initialWidgetTheme);
        foreach ((Border block, bool useSecondaryWash) in _blueprintSkeletonBlocks)
        {
            block.Background = StudioUiPrimitives.StageThemeBrush(_shell, useSecondaryWash ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTertiaryBrush", initialWidgetTheme);
            if (block.BorderThickness.Top > 0)
            {
                block.BorderBrush = StudioUiPrimitives.StageThemeBrush(_shell, "DividerStrokeColorDefaultBrush", initialWidgetTheme);
            }
        }

        return frame;
    }

    internal Border BuildInteractiveCapsulePreviewCard()
    {
        var frame = new Border
        {
            Width = 320,
            MinHeight = WidgetCompactBoundsCalculator.SmartDetailHeight,
            CornerRadius = new CornerRadius(ResolvePreviewOuterCornerRadius()),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 7, 10, 7),
            IsDoubleTapEnabled = true,
            Visibility = Visibility.Collapsed
        };
        _capsulePreviewFrame = frame;
        frame.DoubleTapped += (_, _) =>
        {
            if (!_session.IsCapsulePreviewMode)
            {
                return;
            }

            if (_shell.StageViewport.StageViewportModeSegmented is { Items.Count: > 0 } seg)
            {
                seg.SelectedIndex = 0;
            }
            else
            {
                _session.IsCapsulePreviewMode = false;
                _shell.StageViewport.RefreshStageViewportModeVisuals(animateTransition: true);
            }
        };
        WidgetTextShadow.SetScope(frame, true);
        FluentMotionHelper.AttachSmoothBoundsImplicitAnimations(frame);

        var outerGrid = new Grid { RowSpacing = 4 };
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var mainRow = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new Grid
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleEmojiText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        WidgetTextShadow.SetCast(_capsuleEmojiText, false);
        _capsuleGlyphIcon = new FontIcon
        {
            Glyph = "\uE71D",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(_capsuleEmojiText);
        iconHost.Children.Add(_capsuleGlyphIcon);
        mainRow.Children.Add(iconHost);

        var textCol = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleTitleText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _capsuleSummaryText = new TextBlock
        {
            FontSize = 11.5,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textCol.Children.Add(_capsuleTitleText);
        textCol.Children.Add(_capsuleSummaryText);
        Grid.SetColumn(textCol, 1);
        mainRow.Children.Add(textCol);

        _capsuleBadgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsuleBadgeBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _capsuleBadgeText
        };
        Grid.SetColumn(_capsuleBadgeBorder, 2);
        mainRow.Children.Add(_capsuleBadgeBorder);

        _capsulePrimaryActionIcon = new FontIcon
        {
            Glyph = "\uE710",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _capsulePrimaryActionEmojiText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        WidgetTextShadow.SetCast(_capsulePrimaryActionEmojiText, false);
        var capsuleActionIconHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        capsuleActionIconHost.Children.Add(_capsulePrimaryActionIcon);
        capsuleActionIconHost.Children.Add(_capsulePrimaryActionEmojiText);
        _capsulePrimaryActionButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Content = capsuleActionIconHost
        };
        _capsulePrimaryActionButton.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(
            _shell,
            "CapsulePrimaryAction",
            async () =>
            {
                if (_shell.StageViewport.PreviewVm is not null)
                {
                    await _shell.StageViewport.PreviewVm.ExecuteCapsulePrimaryActionAsync();
                    RefreshCapsulePreviewCardContent();
                }
            });
        Grid.SetColumn(_capsulePrimaryActionButton, 3);
        mainRow.Children.Add(_capsulePrimaryActionButton);

        outerGrid.Children.Add(mainRow);

        _capsuleProgressBar = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 1,
            Margin = new Thickness(4, 0, 6, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_capsuleProgressBar, 1);
        outerGrid.Children.Add(_capsuleProgressBar);

        frame.Child = outerGrid;
        ApplyCapsulePreviewCardTheme(_shell.StageViewport.StageBorder?.RequestedTheme ?? _shell.ShellRootGrid.ActualTheme);
        return frame;
    }

    private void ApplyCapsulePreviewCardTheme(ElementTheme widgetTheme)
    {
        if (_capsulePreviewFrame is null)
        {
            return;
        }

        ElementTheme effective = widgetTheme == ElementTheme.Default
            ? StudioUiPrimitives.GetStudioExplicitTheme(_shell)
            : widgetTheme;
        bool isDark = effective != ElementTheme.Light;
        Windows.UI.Color accentColor = _hostBridge.GetEffectiveAccentColor();
        WidgetBorderVisuals borderVisuals = ResolvePreviewBorderVisuals(isDark, accentColor, _session.TargetWidgetId);

        _capsulePreviewFrame.RequestedTheme = effective;
        _capsulePreviewFrame.CornerRadius = new CornerRadius(ResolvePreviewOuterCornerRadius());
        _capsulePreviewFrame.Background = BuildPreviewSurfaceMaterialBrush(isDark, accentColor);
        _capsulePreviewFrame.BorderThickness = new Thickness(borderVisuals.Thickness);
        _capsulePreviewFrame.BorderBrush = new SolidColorBrush(borderVisuals.BorderColor);
        SyncPreviewFrameShadowPlate(_capsulePreviewFrame, isDark);

        if (_capsuleTitleText is not null)
        {
            _capsuleTitleText.Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorPrimaryBrush", effective);
        }

        if (_capsuleSummaryText is not null)
        {
            _capsuleSummaryText.Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorSecondaryBrush", effective);
        }

        if (_capsuleGlyphIcon is not null)
        {
            _capsuleGlyphIcon.Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "AccentTextFillColorPrimaryBrush", effective);
        }

        RefreshCapsulePreviewCardContent();
    }

    internal void RefreshCapsulePreviewCardContent()
    {
        if (_capsulePreviewFrame is null || _shell.StageViewport.PreviewVm is null)
        {
            return;
        }

        ElementTheme effective = _capsulePreviewFrame.RequestedTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;
        bool isLight = effective == ElementTheme.Light;

        string glyph = CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback(
            _shell.StageViewport.PreviewVm.CapsuleGlyph,
            string.Empty);
        bool isFluentGlyph = CustomWidgetTemplates.IsSegoeFluentGlyph(glyph);
        if (_capsuleGlyphIcon is not null && _capsuleEmojiText is not null)
        {
            if (isFluentGlyph)
            {
                _capsuleGlyphIcon.Glyph = glyph;
                _capsuleGlyphIcon.Visibility = Visibility.Visible;
                _capsuleEmojiText.Visibility = Visibility.Collapsed;
            }
            else
            {
                _capsuleEmojiText.Text = !string.IsNullOrWhiteSpace(glyph)
                    ? glyph
                    : ResolveDisplayEmoji(_shell.StageViewport.PreviewVm.Manifest);
                _capsuleEmojiText.Visibility = Visibility.Visible;
                _capsuleGlyphIcon.Visibility = Visibility.Collapsed;
            }
        }

        if (_capsuleTitleText is not null)
        {
            string title = _shell.StageViewport.PreviewVm.CapsuleTitle;
            _capsuleTitleText.Text = string.IsNullOrWhiteSpace(title)
                ? _shell.StageViewport.PreviewVm.Manifest.ResolveLocalizedName(_session.EffectivePreviewCultureName)
                : title;
        }

        if (_capsuleSummaryText is not null)
        {
            string summary = _shell.StageViewport.PreviewVm.CapsuleSummary;
            _capsuleSummaryText.Text = summary;
            _capsuleSummaryText.Visibility = string.IsNullOrWhiteSpace(summary)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (_capsuleBadgeBorder is not null && _capsuleBadgeText is not null)
        {
            string badge = _shell.StageViewport.PreviewVm.CapsuleBadge;
            if (string.IsNullOrWhiteSpace(badge))
            {
                _capsuleBadgeBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                _capsuleBadgeText.Text = badge;
                if (_shell.StageViewport.PreviewVm.CapsuleBadgeIsWarning)
                {
                    _capsuleBadgeBorder.Background = new SolidColorBrush(
                        isLight
                            ? Windows.UI.Color.FromArgb(0x28, 0xD9, 0x77, 0x06)
                            : Windows.UI.Color.FromArgb(0x38, 0xF5, 0x9E, 0x0B));
                    _capsuleBadgeText.Foreground = new SolidColorBrush(
                        isLight
                            ? Windows.UI.Color.FromArgb(0xFF, 0xB4, 0x53, 0x09)
                            : Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24));
                }
                else
                {
                    _capsuleBadgeBorder.Background = StudioUiPrimitives.StageThemeBrush(_shell, "SubtleFillColorSecondaryBrush", effective);
                    _capsuleBadgeText.Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "TextFillColorSecondaryBrush", effective);
                }

                _capsuleBadgeBorder.Visibility = Visibility.Visible;
            }
        }

        if (_capsulePrimaryActionButton is not null &&
            _capsulePrimaryActionIcon is not null &&
            _capsulePrimaryActionEmojiText is not null)
        {
            if (_shell.StageViewport.PreviewVm.CapsuleHasPrimaryAction)
            {
                string actGlyph = CustomWidgetViewModel.NormalizeRuntimeGlyphOrFallback(
                    _shell.StageViewport.PreviewVm.CapsulePrimaryActionGlyph,
                    "\uE710");
                if (CustomWidgetTemplates.IsSegoeFluentGlyph(actGlyph))
                {
                    _capsulePrimaryActionIcon.Glyph = actGlyph;
                    _capsulePrimaryActionIcon.Visibility = Visibility.Visible;
                    _capsulePrimaryActionEmojiText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _capsulePrimaryActionEmojiText.Text = actGlyph;
                    _capsulePrimaryActionEmojiText.Visibility = Visibility.Visible;
                    _capsulePrimaryActionIcon.Visibility = Visibility.Collapsed;
                }
                string tip = _shell.StageViewport.PreviewVm.CapsulePrimaryActionTooltip;
                ToolTipService.SetToolTip(_capsulePrimaryActionButton, string.IsNullOrWhiteSpace(tip) ? null : tip);
                AutomationProperties.SetName(
                    _capsulePrimaryActionButton,
                    string.IsNullOrWhiteSpace(tip) ? _loc.T("CustomWidget.Workshop.Stage.ViewCapsule") : tip);
                _capsulePrimaryActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                _capsulePrimaryActionButton.Visibility = Visibility.Collapsed;
            }
        }

        if (_capsuleProgressBar is not null)
        {
            if (_shell.StageViewport.PreviewVm.CapsuleProgress is { } prog)
            {
                _capsuleProgressBar.Value = Math.Clamp(prog, 0.0, 1.0);
                _capsuleProgressBar.Visibility = Visibility.Visible;
            }
            else
            {
                _capsuleProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    internal Task PulseStageAsync()
    {
        ++_stagePulseToken;
        UIElement? pulseTarget = _session.IsCapsulePreviewMode && _capsulePreviewFrame is not null
            ? _capsulePreviewFrame
            : ((UIElement?)_shell.StageViewport.WidgetFrame ?? _shell.StageViewport.StageBorder);
        if (pulseTarget is not null && !_shell.IsClosed)
        {
            FluentMotionHelper.PlayDesktopArrivalPulse(pulseTarget);
        }

        return Task.CompletedTask;
    }

    internal string ResolveDisplayEmoji(CustomWidgetManifest? manifest)
    {
        if (CustomWidgetTemplates.IsEmojiIcon(_session.SelectedEmoji))
        {
            return _session.SelectedEmoji!.Trim();
        }

        return StudioManifestInspector.ResolveFallbackEmojiForManifest(manifest);
    }

    internal Border CreateWidgetFrame(
        string emoji,
        string title,
        CustomWidgetContent content,
        CustomWidgetSizeSpec size,
        bool clipViewport,
        out DeskBox.Controls.WidgetTitleIcon titleIcon,
        out TextBlock titleText,
        string? existingWidgetId = null)
    {
        var frame = new Border
        {
            Width = Math.Clamp(size.Width, 160, 640),
            Height = Math.Clamp(size.Height, 96, 900),
            BorderThickness = new Thickness(0)
        };
        if (!clipViewport)
        {
            WidgetTextShadow.SetScope(frame, true);
        }
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        titleIcon = new DeskBox.Controls.WidgetTitleIcon
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        head.Children.Add(titleIcon);
        Grid.SetColumn(titleText, 1);
        head.Children.Add(titleText);

        FontIcon? editGlyph = null;
        FrameworkElement headerHost;
        if (clipViewport)
        {
            headerHost = head;
            grid.Children.Add(head);
        }
        else
        {
            editGlyph = new FontIcon
            {
                Glyph = "\uE70F",
                FontSize = 11,
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                VerticalAlignment = VerticalAlignment.Center
            };
            _shell.StageViewport.FrameEditGlyph = editGlyph;
            Grid.SetColumn(editGlyph, 2);
            head.Children.Add(editGlyph);

            var headerBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Content = head
            };
            headerBtn.PointerEntered += (_, _) => editGlyph.Opacity = 0.85;
            headerBtn.PointerExited += (_, _) =>
            {
                if (headerBtn.FocusState == FocusState.Unfocused)
                {
                    editGlyph.Opacity = 0;
                }
            };
            headerBtn.GotFocus += (_, _) => editGlyph.Opacity = 0.85;
            headerBtn.LostFocus += (_, _) => editGlyph.Opacity = 0;
            ToolTipService.SetToolTip(headerBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            AutomationProperties.SetName(headerBtn, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            headerBtn.Flyout = _shell.StageDialogs.BuildHeaderQuickEditFlyout();
            headerHost = headerBtn;
            grid.Children.Add(headerBtn);
        }

        var divider = new Border();
        if (!clipViewport)
        {
            _shell.StageViewport.FrameDivider = divider;
        }
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);

        UIElement bodyElement = clipViewport
            ? new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            }
            : content;
        var bodyClipHost = new Grid();
        bodyClipHost.Children.Add(bodyElement);
        Grid.SetRow(bodyClipHost, 2);
        grid.Children.Add(bodyClipHost);

        Border? overlayGripHandle = null;
        Border? overlayGripBar = null;
        if (!clipViewport)
        {
            overlayGripBar = new Border
            {
                Width = 34,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            overlayGripHandle = new Border
            {
                Width = 168,
                Height = 14,
                Margin = new Thickness(0, 2, 0, 0),
                CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Opacity = 0,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) },
                Visibility = Visibility.Collapsed,
                Child = overlayGripBar
            };
            ToolTipService.SetToolTip(overlayGripHandle, _loc.T("CustomWidget.Workshop.CoCreate.QuickHeaderTip"));
            overlayGripHandle.RightTapped += (s, e) =>
            {
                e.Handled = true;
                if (s is FrameworkElement anchor)
                {
                    _shell.StageDialogs.BuildHeaderQuickEditFlyout().ShowAt(anchor);
                }
            };
            overlayGripHandle.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                if (!_session.IsCapsulePreviewMode)
                {
                    _session.IsCapsulePreviewMode = true;
                    _shell.StageViewport.RefreshStageViewportModeVisuals(animateTransition: true);
                }
            };
            Grid.SetRowSpan(overlayGripHandle, 3);
            grid.Children.Add(overlayGripHandle);
        }

        var outlineBorder = new Border
        {
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(outlineBorder, 3);
        grid.Children.Add(outlineBorder);

        var frameState = new StudioWidgetFrameVisualState
        {
            Content = content,
            ExistingWidgetId = existingWidgetId,
            IsBlueprint = false,
            ClipViewport = clipViewport,
            RootGrid = grid,
            HeaderHost = headerHost,
            HeaderInnerGrid = head,
            TitleIcon = titleIcon,
            TitleText = titleText,
            EditGlyph = editGlyph,
            Divider = divider,
            BodyClipHost = bodyClipHost,
            OverlayGripHandle = overlayGripHandle,
            OverlayGripBar = overlayGripBar,
            OutlineBorder = outlineBorder,
            RawEmojiOrGlyph = emoji
        };
        frame.Tag = frameState;

        if (overlayGripHandle is not null)
        {
            frame.PointerEntered += (_, _) =>
            {
                frameState.IsPointerOverFrame = true;
                if (overlayGripHandle.Visibility == Visibility.Visible)
                {
                    overlayGripHandle.Opacity = 0.9;
                }
            };
            frame.PointerExited += (_, _) =>
            {
                frameState.IsPointerOverFrame = false;
                if (overlayGripHandle.Visibility == Visibility.Visible)
                {
                    overlayGripHandle.Opacity = 0;
                }
            };
        }

        frame.Child = grid;

        ApplyWidgetFrameTheme(frame, content, titleText, editGlyph, divider, _shell.ShellRootGrid.ActualTheme);
        return frame;
    }

    public void Dispose()
    {
        ResetPreviewFrameElements();
        _shell.StageViewport.ClearWallpaperLayerBackground();
        _cachedWallpaperBitmap = null;
    }
}

internal sealed class StudioWidgetFrameVisualState
{
    public CustomWidgetContent? Content { get; init; }
    public string? ExistingWidgetId { get; set; }
    public bool IsBlueprint { get; init; }
    public bool ClipViewport { get; init; }
    public Grid RootGrid { get; init; } = null!;
    public FrameworkElement HeaderHost { get; init; } = null!;
    public Grid HeaderInnerGrid { get; init; } = null!;
    public DeskBox.Controls.WidgetTitleIcon TitleIcon { get; init; } = null!;
    public TextBlock TitleText { get; init; } = null!;
    public FontIcon? EditGlyph { get; init; }
    public Border Divider { get; init; } = null!;
    public Grid BodyClipHost { get; init; } = null!;
    public Border? OverlayGripHandle { get; init; }
    public Border? OverlayGripBar { get; init; }
    public Border OutlineBorder { get; init; } = null!;
    public string RawEmojiOrGlyph { get; set; } = string.Empty;
    public bool IsPointerOverFrame { get; set; }
}
