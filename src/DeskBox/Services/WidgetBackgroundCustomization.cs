using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using Windows.UI;

namespace DeskBox.Services;

/// <summary>
/// Normalizes and resolves the per-widget custom background override.
/// Values live in <see cref="WidgetConfig.Metadata"/> like the title-icon
/// override: the image entry stores the file name inside the widget's asset
/// directory, the fit entry is Fill/Contain, and the dim entry is the scrim
/// strength in percent (0-100). The color entry is an opaque #RRGGBB solid
/// background — the same storage shape as the per-widget foreground color.
/// Image and color are mutually exclusive kinds of per-widget background.
/// </summary>
public static class WidgetBackgroundCustomization
{
    public const string ImageMetadataKey = "BackgroundImage";
    public const string FitMetadataKey = "BackgroundFit";
    public const string DimMetadataKey = "BackgroundDim";
    public const string ColorMetadataKey = "BackgroundColor";

    public const string ImageFileStem = "background";

    public const string FitFill = Contracts.WidgetBackgroundKinds.FitFill;
    public const string FitContain = Contracts.WidgetBackgroundKinds.FitContain;

    /// <summary>Default scrim strength in percent, applied over the image.</summary>
    public const double DefaultDimPercent = Contracts.WidgetBackgroundKinds.DefaultDimPercent;

    public const double MinDimPercent = Contracts.WidgetBackgroundKinds.MinDimPercent;
    public const double MaxDimPercent = Contracts.WidgetBackgroundKinds.MaxDimPercent;

    public static string? GetImageFileNameOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata is not null &&
            config.Metadata.TryGetValue(ImageMetadataKey, out string? value) &&
            IsValidImageFileName(value))
        {
            return value;
        }

        return null;
    }

    public static bool HasCustomBackground(WidgetConfig config) =>
        GetImageFileNameOverride(config) is not null ||
        GetColorOverride(config) is not null;

    /// <summary>
    /// Per-widget solid background color as an opaque #RRGGBB hex string, or
    /// null when no color override applies.
    /// </summary>
    public static string? GetColorOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata is not null &&
            config.Metadata.TryGetValue(ColorMetadataKey, out string? value) &&
            AccentColorHelper.TryParseHex(value, out _))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Sets the per-widget solid background color (null clears it). Writing a
    /// color drops the image override — the two are mutually exclusive kinds
    /// of per-widget background, and a color quietly layered under a missing
    /// image file would only confuse ("cleared the image, still colored").
    /// </summary>
    public static void SetColorOverride(WidgetConfig config, Color? color)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (color is null)
        {
            config.Metadata?.Remove(ColorMetadataKey);
            return;
        }

        config.Metadata ??= [];
        config.Metadata[ColorMetadataKey] = AccentColorHelper.ToHex(
            Color.FromArgb(0xFF, color.Value.R, color.Value.G, color.Value.B));
        config.Metadata.Remove(ImageMetadataKey);
        config.Metadata.Remove(FitMetadataKey);
        config.Metadata.Remove(DimMetadataKey);
    }

    /// <summary>Returns Fill or Contain; null follows the Fill default.</summary>
    public static string? GetFitOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata is not null &&
            config.Metadata.TryGetValue(FitMetadataKey, out string? value))
        {
            return NormalizeFit(value);
        }

        return null;
    }

    /// <summary>Resolved scrim strength in the 0-100 range.</summary>
    public static double ResolveDimPercent(WidgetConfig config)
    {
        double? value = GetDimPercentOverride(config);
        return value ?? DefaultDimPercent;
    }

    /// <summary>
    /// Tri-state scrim override: null follows the global dim (or the default
    /// when no image background applies at all).
    /// </summary>
    public static double? GetDimPercentOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata is not null &&
            config.Metadata.TryGetValue(DimMetadataKey, out string? value))
        {
            string normalized = Math.Clamp(
                double.TryParse(value, out double parsed) && double.IsFinite(parsed)
                    ? parsed
                    : DefaultDimPercent,
                MinDimPercent,
                MaxDimPercent).ToString("0.#");
            if (double.TryParse(normalized, out double result))
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the global appearance background is active for a widget that
    /// has no per-widget image: the mode selects an image background and the
    /// shared image file actually exists (missing file self-heals to the
    /// material, e.g. after restoring a settings backup on another machine).
    /// </summary>
    public static bool IsGlobalImageBackgroundActive(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Reads through the slice so Views files stay out of the facade
        // access ratchet.
        WidgetShellSettingsSlice shell = settings.WidgetShell;
        string mode = WidgetBackgroundModeKinds.Normalize(shell.WidgetBackgroundMode);
        if (mode == WidgetBackgroundModeKinds.Material)
        {
            return false;
        }

        string? fileName = mode == WidgetBackgroundModeKinds.Panorama
            ? shell.WidgetBackgroundPanoramaImage
            : shell.WidgetBackgroundUnifiedImage;
        return fileName is not null &&
            (mode == WidgetBackgroundModeKinds.Panorama
                ? WidgetTitleIconAssetStore.Current.ResolvePanoramaBackgroundPath(fileName)
                : WidgetTitleIconAssetStore.Current.ResolveUnifiedBackgroundPath(fileName)) is not null;
    }

    /// <summary>Resolved scrim strength across both override layers.</summary>
    public static double ResolveEffectiveDimPercent(WidgetConfig config, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GetDimPercentOverride(config) ??
            settings.WidgetShell.WidgetBackgroundDim ??
            DefaultDimPercent;
    }

    /// <summary>
    /// Normalizes the global background fields; reads and writes through the
    /// slice so it stays out of the facade-access ratchet.
    /// </summary>
    public static bool NormalizeGlobal(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WidgetShellSettingsSlice shell = settings.WidgetShell;
        bool changed = false;

        string mode = WidgetBackgroundModeKinds.Normalize(shell.WidgetBackgroundMode);
        if (!string.Equals(shell.WidgetBackgroundMode, mode, StringComparison.Ordinal))
        {
            shell.WidgetBackgroundMode = mode == WidgetBackgroundModeKinds.Material ? null : mode;
            changed = true;
        }

        if (shell.WidgetBackgroundUnifiedFit is { } fit)
        {
            string normalizedFit = WidgetBackgroundCustomization.NormalizeFit(fit);
            string? stored = normalizedFit == FitFill ? null : normalizedFit;
            if (!string.Equals(shell.WidgetBackgroundUnifiedFit, stored, StringComparison.Ordinal))
            {
                shell.WidgetBackgroundUnifiedFit = stored;
                changed = true;
            }
        }

        double? dim = shell.WidgetBackgroundDim;
        if (dim is { } dimValue)
        {
            double normalized = Math.Clamp(dimValue, MinDimPercent, MaxDimPercent);
            if (Math.Abs(normalized - dimValue) > 0.0001)
            {
                shell.WidgetBackgroundDim = normalized;
                changed = true;
            }
        }

        return changed;
    }

    public static void SetImageOverride(WidgetConfig config, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsValidImageFileName(fileName))
        {
            config.Metadata?.Remove(ImageMetadataKey);
            return;
        }

        config.Metadata ??= [];
        config.Metadata[ImageMetadataKey] = fileName!;
        // Image and color are mutually exclusive; the image wins.
        config.Metadata.Remove(ColorMetadataKey);
    }

    public static void SetFitOverride(WidgetConfig config, string? fit)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? normalized = string.IsNullOrWhiteSpace(fit) ? null : NormalizeFit(fit);
        if (normalized is null || normalized == FitFill)
        {
            // Fill is the default: storing it would only add noise.
            config.Metadata?.Remove(FitMetadataKey);
            return;
        }

        config.Metadata ??= [];
        config.Metadata[FitMetadataKey] = normalized;
    }

    public static void SetDimPercent(WidgetConfig config, double dimPercent)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!double.IsFinite(dimPercent))
        {
            return;
        }

        double dim = Math.Clamp(dimPercent, MinDimPercent, MaxDimPercent);
        config.Metadata ??= [];
        config.Metadata[DimMetadataKey] = dim.ToString("0.#");
    }

    public static void Clear(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata?.Remove(ImageMetadataKey);
        config.Metadata?.Remove(FitMetadataKey);
        config.Metadata?.Remove(DimMetadataKey);
        config.Metadata?.Remove(ColorMetadataKey);
    }

    public static bool NormalizeOverrides(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata ??= [];
        bool changed = false;

        if (config.Metadata.TryGetValue(ImageMetadataKey, out string? image) &&
            !IsValidImageFileName(image))
        {
            config.Metadata.Remove(ImageMetadataKey);
            changed = true;
        }

        if (config.Metadata.TryGetValue(FitMetadataKey, out string? fit))
        {
            if (NormalizeFit(fit) is { } normalized && normalized != FitFill)
            {
                if (!string.Equals(fit, normalized, StringComparison.Ordinal))
                {
                    config.Metadata[FitMetadataKey] = normalized;
                    changed = true;
                }
            }
            else
            {
                // Unsupported or the redundant default: drop the entry.
                config.Metadata.Remove(FitMetadataKey);
                changed = true;
            }
        }

        if (config.Metadata.TryGetValue(DimMetadataKey, out string? dim))
        {
            if (TryNormalizeDim(dim, out double normalized))
            {
                if (!string.Equals(dim, normalized.ToString("0.#"), StringComparison.Ordinal))
                {
                    config.Metadata[DimMetadataKey] = normalized.ToString("0.#");
                    changed = true;
                }
            }
            else
            {
                config.Metadata.Remove(DimMetadataKey);
                changed = true;
            }
        }

        // Fit/dim without an image are meaningless noise; drop them.
        if (!config.Metadata.ContainsKey(ImageMetadataKey))
        {
            if (config.Metadata.Remove(FitMetadataKey))
            {
                changed = true;
            }

            if (config.Metadata.Remove(DimMetadataKey))
            {
                changed = true;
            }
        }

        if (config.Metadata.TryGetValue(ColorMetadataKey, out string? colorHex))
        {
            if (config.Metadata.ContainsKey(ImageMetadataKey) ||
                !AccentColorHelper.TryParseHex(colorHex, out Color parsed))
            {
                // A color under an image is unreachable (image wins), and a
                // hand-edited invalid hex must self-heal away.
                config.Metadata.Remove(ColorMetadataKey);
                changed = true;
            }
            else
            {
                string normalized = AccentColorHelper.ToHex(
                    Color.FromArgb(0xFF, parsed.R, parsed.G, parsed.B));
                if (!string.Equals(colorHex, normalized, StringComparison.Ordinal))
                {
                    config.Metadata[ColorMetadataKey] = normalized;
                    changed = true;
                }
            }
        }

        return changed;
    }

    public static string NormalizeFit(string? value) =>
        Contracts.WidgetBackgroundKinds.NormalizeFit(value);

    private static bool TryNormalizeDim(string? value, out double dimPercent)
    {
        dimPercent = DefaultDimPercent;
        if (!double.TryParse(value, out double parsed) || !double.IsFinite(parsed))
        {
            return false;
        }

        dimPercent = Math.Clamp(parsed, MinDimPercent, MaxDimPercent);
        return true;
    }

    internal static bool IsValidImageFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!string.Equals(stem, ImageFileStem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return WidgetTitleIconCustomization.IsSupportedImageExtension(
            Path.GetExtension(fileName));
    }
}
