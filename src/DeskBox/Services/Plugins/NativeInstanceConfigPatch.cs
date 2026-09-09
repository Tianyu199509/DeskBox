using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services.Plugins;

/// <summary>
/// The package-owned settings patch for the write-through channel (HostApi
/// v3, audit round 19): under host authority a native setting mutation must
/// commit to the built-in GlanceWidgetStore, never only to the package's
/// local copy. Parsing is JsonDocument-based (reflection JSON stays out of
/// the plugin host layer) and type-strict: a malformed patch is rejected
/// before anything touches the authoritative store.
/// </summary>
internal sealed record InstanceConfigPatch(
    bool? ShowChineseFestivals = null,
    GlanceTraditionalCalendarMode? TraditionalCalendarMode = null,
    double? RotationIntervalMinutes = null,
    bool? RandomOrder = null,
    GlanceBackgroundSource? BackgroundSource = null,
    IReadOnlyList<string>? LocalImagePaths = null,
    string? LocalFolderPath = null,
    GlanceImageFitMode? ImageFit = null,
    bool? ShowPhotoControls = null)
{
    public void ApplyTo(GlanceWidgetData data)
    {
        if (ShowChineseFestivals is { } festivals) data.ShowChineseFestivals = festivals;
        if (TraditionalCalendarMode is { } mode) data.TraditionalCalendarMode = mode;
        if (RotationIntervalMinutes is { } minutes) data.RotationIntervalMinutes = minutes;
        if (RandomOrder is { } random) data.RandomOrder = random;
        if (BackgroundSource is { } source) data.BackgroundSource = source;
        if (LocalImagePaths is { } paths) data.LocalImagePaths = [.. paths];
        // An empty string clears the folder; the model stores null.
        if (LocalFolderPath is not null) data.LocalFolderPath = LocalFolderPath.Length == 0 ? null : LocalFolderPath;
        if (ImageFit is { } fit) data.ImageFit = fit;
        if (ShowPhotoControls is { } controls) data.ShowPhotoControls = controls;
    }

    public static InstanceConfigPatch? TryParse(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonElement root = document.RootElement;
            // Readers distinguish "absent" (null value) from "present but
            // wrong type" (false return): one wrong-typed field rejects the
            // whole patch before anything touches the authoritative store.
            if (!TryReadBool(root, "showChineseFestivals", out bool? festivals)) return null;
            if (!TryReadEnum<GlanceTraditionalCalendarMode>(root, "traditionalCalendarMode", out var mode)) return null;
            if (!TryReadNumber(root, "rotationIntervalMinutes", out double? minutes)) return null;
            if (!TryReadBool(root, "randomOrder", out bool? random)) return null;
            if (!TryReadEnum<GlanceBackgroundSource>(root, "backgroundSource", out var source)) return null;
            if (!TryReadStringArray(root, "localImagePaths", out IReadOnlyList<string>? paths)) return null;
            if (!TryReadString(root, "localFolderPath", out string? folder)) return null;
            if (!TryReadEnum<GlanceImageFitMode>(root, "imageFit", out var fit)) return null;
            if (!TryReadBool(root, "showPhotoControls", out bool? controls)) return null;
            return new InstanceConfigPatch(
                ShowChineseFestivals: festivals,
                TraditionalCalendarMode: mode,
                RotationIntervalMinutes: minutes,
                RandomOrder: random,
                BackgroundSource: source,
                LocalImagePaths: paths,
                LocalFolderPath: folder,
                ImageFit: fit,
                ShowPhotoControls: controls);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadBool(JsonElement root, string property, out bool? value)
    {
        value = null;
        if (!root.TryGetProperty(property, out JsonElement element)) return true;
        switch (element.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: return false;
        }
    }

    private static bool TryReadNumber(JsonElement root, string property, out double? value)
    {
        value = null;
        if (!root.TryGetProperty(property, out JsonElement element)) return true;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number))
        {
            value = number;
            return true;
        }
        return false;
    }

    private static bool TryReadString(JsonElement root, string property, out string? value)
    {
        value = null;
        return !root.TryGetProperty(property, out JsonElement element)
            || (element.ValueKind == JsonValueKind.String && (value = element.GetString()) is not null);
    }

    private static bool TryReadStringArray(JsonElement root, string property, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!root.TryGetProperty(property, out JsonElement element)) return true;
        if (element.ValueKind != JsonValueKind.Array) return false;
        var list = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            list.Add(item.GetString() ?? string.Empty);
        }
        values = list;
        return true;
    }

    private static bool TryReadEnum<TEnum>(JsonElement root, string property, out TEnum? value)
        where TEnum : struct, Enum
    {
        value = null;
        if (!root.TryGetProperty(property, out JsonElement element)) return true;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (!Enum.TryParse(element.GetString(), ignoreCase: true, out TEnum parsed)) return false;
            value = parsed;
            return true;
        }
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out int number) &&
            number >= 0)
        {
            TEnum candidate = (TEnum)(object)number;
            if (!Enum.IsDefined(candidate)) return false;
            value = candidate;
            return true;
        }
        return false;
    }
}
