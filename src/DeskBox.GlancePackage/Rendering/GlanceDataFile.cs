using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Loads/saves the migrated GlanceWidgetData file (glance-data.json,
/// camelCase properties, string enums - the built-in store's wire format).
/// JsonDocument/Utf8JsonWriter only: the package must not grow reflection
/// JSON serialization (host frozen JSON baseline). Reads the subset the
/// native view actually renders today; missing fields (files as old as v7
/// exist on real machines) keep the GlanceWidgetData defaults, unknown enum
/// strings are ignored, and a corrupt file degrades to null.
/// </summary>
internal static class GlanceDataFile
{
    internal const string FileName = "glance-data.json";

    internal static GlanceWidgetData? Load(string instanceDataRoot)
    {
        string path = Path.Combine(instanceDataRoot, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            var data = new GlanceWidgetData();
            if (TryBool(root, "showChineseFestivals", out bool festivals)) data.ShowChineseFestivals = festivals;
            if (TryEnum<GlanceTraditionalCalendarMode>(root, "traditionalCalendarMode", out var mode)) data.TraditionalCalendarMode = mode;
            if (TryDouble(root, "rotationIntervalMinutes", out double minutes)) data.RotationIntervalMinutes = minutes;
            if (TryBool(root, "randomOrder", out bool random)) data.RandomOrder = random;
            if (TryEnum<GlanceBackgroundSource>(root, "backgroundSource", out var source)) data.BackgroundSource = source;
            if (root.TryGetProperty("localImagePaths", out JsonElement paths) && paths.ValueKind == JsonValueKind.Array)
            {
                data.LocalImagePaths = paths.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(path => path.Length > 0)
                    .ToList();
            }
            if (TryString(root, "localFolderPath", out string? folder) && !string.IsNullOrWhiteSpace(folder)) data.LocalFolderPath = folder;
            if (TryEnum<GlanceImageFitMode>(root, "imageFit", out var fit)) data.ImageFit = fit;
            if (TryBool(root, "showPhotoControls", out bool controls)) data.ShowPhotoControls = controls;
            return data;
        }
        catch
        {
            return null;
        }
    }

    internal static void Save(GlanceWidgetData data, string instanceDataRoot)
    {
        Directory.CreateDirectory(instanceDataRoot);
        using var stream = File.Create(Path.Combine(instanceDataRoot, FileName));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", GlanceWidgetData.CurrentVersion);
        writer.WriteBoolean("showChineseFestivals", data.ShowChineseFestivals);
        writer.WriteString("traditionalCalendarMode", data.TraditionalCalendarMode.ToString());
        writer.WriteNumber("rotationIntervalMinutes", data.RotationIntervalMinutes);
        writer.WriteBoolean("randomOrder", data.RandomOrder);
        writer.WriteString("backgroundSource", data.BackgroundSource.ToString());
        writer.WriteStartArray("localImagePaths");
        foreach (string path in data.LocalImagePaths) writer.WriteStringValue(path);
        writer.WriteEndArray();
        writer.WriteString("localFolderPath", data.LocalFolderPath);
        writer.WriteString("imageFit", data.ImageFit.ToString());
        writer.WriteBoolean("showPhotoControls", data.ShowPhotoControls);
        writer.WriteEndObject();
    }

    private static bool TryBool(JsonElement root, string property, out bool value)
    {
        value = default;
        return root.TryGetProperty(property, out JsonElement element) && TryGetBool(element, out value);
    }

    private static bool TryGetBool(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: value = default; return false;
        }
    }

    private static bool TryString(JsonElement root, string property, out string? value)
    {
        value = null;
        return root.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && (value = element.GetString()) is not null;
    }

    private static bool TryDouble(JsonElement root, string property, out double value)
    {
        value = default;
        return root.TryGetProperty(property, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value);
    }

    private static bool TryEnum<TEnum>(JsonElement root, string property, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return TryString(root, property, out string? text)
            && Enum.TryParse(text, ignoreCase: true, out value);
    }
}
