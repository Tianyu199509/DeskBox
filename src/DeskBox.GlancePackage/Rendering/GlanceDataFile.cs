using System.Text.Json;
using DeskBox.GlancePackage.Services;
using DeskBox.Models;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Package view of the migrated GlanceWidgetData file (glance-data.json,
/// camelCase properties, string enums, legacy integer enums accepted).
/// Round-trips LOSSLESSLY: the original document is kept verbatim and Save
/// only replaces properties the package owns, so settings the native view
/// has not wired yet - and future unknown fields from newer hosts - survive
/// every write (audit round 18 P0). Corrupt reads degrade to null so
/// callers fall back to model defaults.
/// </summary>
internal static class GlanceDataFile
{
    internal const string FileName = "glance-data.json";

    internal static GlanceData? Load(string instanceDataRoot)
    {
        string? content = PackageFileStore.TryReadText(Path.Combine(instanceDataRoot, FileName));
        if (content is null) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonElement raw = document.RootElement.Clone();

            var settings = new GlanceWidgetData();
            if (TryBool(raw, "showChineseFestivals", out bool festivals)) settings.ShowChineseFestivals = festivals;
            if (TryEnum<GlanceTraditionalCalendarMode>(raw, "traditionalCalendarMode", out var mode)) settings.TraditionalCalendarMode = mode;
            if (TryDouble(raw, "rotationIntervalMinutes", out double minutes)) settings.RotationIntervalMinutes = minutes;
            if (TryBool(raw, "randomOrder", out bool random)) settings.RandomOrder = random;
            if (TryEnum<GlanceBackgroundSource>(raw, "backgroundSource", out var source)) settings.BackgroundSource = source;
            if (raw.TryGetProperty("localImagePaths", out JsonElement paths) && paths.ValueKind == JsonValueKind.Array)
            {
                settings.LocalImagePaths = paths.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(path => path.Length > 0)
                    .ToList();
            }
            if (TryString(raw, "localFolderPath", out string? folder) && !string.IsNullOrWhiteSpace(folder)) settings.LocalFolderPath = folder;
            if (TryEnum<GlanceImageFitMode>(raw, "imageFit", out var fit)) settings.ImageFit = fit;
            if (TryBool(raw, "showPhotoControls", out bool controls)) settings.ShowPhotoControls = controls;
            return new GlanceData(settings, raw);
        }
        catch
        {
            return null;
        }
    }

    internal static void Save(GlanceData data, string instanceDataRoot) =>
        PackageFileStore.WriteAtomically(
            Path.Combine(instanceDataRoot, FileName),
            writer => WritePreserving(writer, data));

    /// <summary>
    /// Re-emit the original document, replacing only owned properties with
    /// current values; everything else is copied verbatim, and owned fields
    /// missing from the original are appended.
    /// </summary>
    private static void WritePreserving(Utf8JsonWriter writer, GlanceData data)
    {
        GlanceWidgetData settings = data.Settings;
        writer.WriteStartObject();
        var written = new HashSet<string>(StringComparer.Ordinal);
        if (data.Raw.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in data.Raw.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "version":
                        writer.WriteNumber(property.Name, GlanceWidgetData.CurrentVersion);
                        break;
                    case "showChineseFestivals":
                        writer.WriteBoolean(property.Name, settings.ShowChineseFestivals);
                        break;
                    case "traditionalCalendarMode":
                        writer.WriteString(property.Name, settings.TraditionalCalendarMode.ToString());
                        break;
                    case "rotationIntervalMinutes":
                        writer.WriteNumber(property.Name, settings.RotationIntervalMinutes);
                        break;
                    case "randomOrder":
                        writer.WriteBoolean(property.Name, settings.RandomOrder);
                        break;
                    case "backgroundSource":
                        writer.WriteString(property.Name, settings.BackgroundSource.ToString());
                        break;
                    case "localImagePaths":
                        writer.WriteStartArray(property.Name);
                        foreach (string path in settings.LocalImagePaths) writer.WriteStringValue(path);
                        writer.WriteEndArray();
                        break;
                    case "localFolderPath":
                        writer.WriteString(property.Name, settings.LocalFolderPath);
                        break;
                    case "imageFit":
                        writer.WriteString(property.Name, settings.ImageFit.ToString());
                        break;
                    case "showPhotoControls":
                        writer.WriteBoolean(property.Name, settings.ShowPhotoControls);
                        break;
                    default:
                        property.WriteTo(writer);
                        break;
                }
                written.Add(property.Name);
            }
        }
        if (!written.Contains("version")) writer.WriteNumber("version", GlanceWidgetData.CurrentVersion);
        if (!written.Contains("showChineseFestivals")) writer.WriteBoolean("showChineseFestivals", settings.ShowChineseFestivals);
        if (!written.Contains("traditionalCalendarMode")) writer.WriteString("traditionalCalendarMode", settings.TraditionalCalendarMode.ToString());
        if (!written.Contains("rotationIntervalMinutes")) writer.WriteNumber("rotationIntervalMinutes", settings.RotationIntervalMinutes);
        if (!written.Contains("randomOrder")) writer.WriteBoolean("randomOrder", settings.RandomOrder);
        if (!written.Contains("backgroundSource")) writer.WriteString("backgroundSource", settings.BackgroundSource.ToString());
        if (!written.Contains("localImagePaths"))
        {
            writer.WriteStartArray("localImagePaths");
            foreach (string path in settings.LocalImagePaths) writer.WriteStringValue(path);
            writer.WriteEndArray();
        }
        if (!written.Contains("localFolderPath")) writer.WriteString("localFolderPath", settings.LocalFolderPath);
        if (!written.Contains("imageFit")) writer.WriteString("imageFit", settings.ImageFit.ToString());
        if (!written.Contains("showPhotoControls")) writer.WriteBoolean("showPhotoControls", settings.ShowPhotoControls);
        writer.WriteEndObject();
    }

    private static bool TryBool(JsonElement root, string property, out bool value)
    {
        value = default;
        if (!root.TryGetProperty(property, out JsonElement element)) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: return false;
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
        if (!root.TryGetProperty(property, out JsonElement element)) return false;
        if (element.ValueKind == JsonValueKind.String)
        {
            return Enum.TryParse(element.GetString(), ignoreCase: true, out value);
        }
        // Legacy integer enums: the host stores historically wrote numbers
        // and still read them back; the package must keep that compatibility
        // (audit round 18, repo golden StringEnumStoreGoldens).
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out int number) &&
            number >= 0)
        {
            TEnum candidate = (TEnum)(object)number;
            if (Enum.IsDefined(candidate))
            {
                value = candidate;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// A loaded glance data document: the typed subset the native view renders
/// plus the original raw JSON kept for lossless re-emission.
/// </summary>
internal sealed class GlanceData(GlanceWidgetData settings, JsonElement raw)
{
    public GlanceWidgetData Settings { get; } = settings;
    public JsonElement Raw { get; } = raw;
}
