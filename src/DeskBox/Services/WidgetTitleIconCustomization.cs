using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Normalizes and resolves the per-widget custom title-icon override.
/// Values live in <see cref="WidgetConfig.Metadata"/> following the same
/// no-schema-migration rule as the per-widget foreground overrides: an
/// emoji entry stores the emoji text itself, an image entry stores the
/// file name inside the widget's asset directory.
/// </summary>
public static class WidgetTitleIconCustomization
{
    public const string EmojiMetadataKey = "TitleIconEmoji";
    public const string ImageMetadataKey = "TitleIconImage";

    /// <summary>Base name of the stored icon image, e.g. <c>title-icon.png</c>.</summary>
    public const string ImageFileStem = "title-icon";

    /// <summary>Custom icon images never decode larger than this (physical pixels).</summary>
    public const int MaxDecodedIconPixels = 128;

    public const long MaxImageFileBytes = 10L * 1024 * 1024;

    /// <summary>Emoji ZWJ sequences stay well under this many UTF-16 code units.</summary>
    public const int MaxEmojiLength = 16;

    public static string? GetEmojiOverride(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Metadata is not null &&
            config.Metadata.TryGetValue(EmojiMetadataKey, out string? value) &&
            IsValidEmoji(value))
        {
            return value.Trim();
        }

        return null;
    }

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

    public static bool HasCustomIcon(WidgetConfig config) =>
        GetEmojiOverride(config) is not null || GetImageFileNameOverride(config) is not null;

    /// <summary>
    /// Selecting an emoji replaces any image override: the picker exposes a
    /// single choice at a time, so the two entries stay mutually exclusive.
    /// </summary>
    public static void SetEmojiOverride(WidgetConfig config, string? emoji)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsValidEmoji(emoji))
        {
            Remove(config, EmojiMetadataKey);
            return;
        }

        config.Metadata ??= [];
        config.Metadata[EmojiMetadataKey] = emoji!.Trim();
        Remove(config, ImageMetadataKey);
    }

    public static void SetImageOverride(WidgetConfig config, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsValidImageFileName(fileName))
        {
            Remove(config, ImageMetadataKey);
            return;
        }

        config.Metadata ??= [];
        config.Metadata[ImageMetadataKey] = fileName!;
        Remove(config, EmojiMetadataKey);
    }

    public static void Clear(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Remove(config, EmojiMetadataKey);
        Remove(config, ImageMetadataKey);
    }

    public static bool NormalizeOverrides(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Metadata ??= [];
        bool changed = false;

        if (config.Metadata.TryGetValue(EmojiMetadataKey, out string? emoji) &&
            !IsValidEmoji(emoji))
        {
            config.Metadata.Remove(EmojiMetadataKey);
            changed = true;
        }

        if (config.Metadata.TryGetValue(ImageMetadataKey, out string? image) &&
            !IsValidImageFileName(image))
        {
            config.Metadata.Remove(ImageMetadataKey);
            changed = true;
        }

        // Both entries surviving the checks above means an inconsistent
        // selection; the image loses because the emoji needs no asset file.
        if (config.Metadata.ContainsKey(EmojiMetadataKey) &&
            config.Metadata.Remove(ImageMetadataKey))
        {
            changed = true;
        }

        return changed;
    }

    public static bool IsSupportedImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        string normalized = extension.ToLowerInvariant();
        return normalized is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico" or ".svg";
    }

    public static bool IsValidImageFileName(string? fileName)
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

        return IsSupportedImageExtension(Path.GetExtension(fileName));
    }

    public static bool IsValidEmoji(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEmojiLength)
        {
            return false;
        }

        bool hasNonAscii = false;
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }

            if (c > 0x7F)
            {
                hasNonAscii = true;
            }
        }

        return hasNonAscii;
    }

    private static void Remove(WidgetConfig config, string key)
    {
        config.Metadata?.Remove(key);
    }
}
