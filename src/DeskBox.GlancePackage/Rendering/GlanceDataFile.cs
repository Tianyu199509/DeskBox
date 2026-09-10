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

    // Layout/clock/toggle changes must not reshuffle the current image list
    // or return the slideshow to its first image.
    internal static bool SameImageSources(GlanceWidgetData left, GlanceWidgetData right) =>
        left.BackgroundSource == right.BackgroundSource &&
        left.RandomOrder == right.RandomOrder &&
        string.Equals(left.LocalFolderPath, right.LocalFolderPath, StringComparison.OrdinalIgnoreCase) &&
        left.LocalImagePaths.SequenceEqual(right.LocalImagePaths, StringComparer.OrdinalIgnoreCase);

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
            // Display-element toggles + time format (parity batch: built-in
            // UpdateClockTimer/visibility semantics consume these).
            if (TryBool(raw, "showTime", out bool showTime)) settings.ShowTime = showTime;
            if (TryBool(raw, "showDate", out bool showDate)) settings.ShowDate = showDate;
            if (TryBool(raw, "showYear", out bool showYear)) settings.ShowYear = showYear;
            if (TryBool(raw, "showWeekday", out bool showWeekday)) settings.ShowWeekday = showWeekday;
            if (TryBool(raw, "showCalendar", out bool showCalendar)) settings.ShowCalendar = showCalendar;
            if (TryEnum<GlanceTimeFormatMode>(raw, "timeFormat", out var timeFormat)) settings.TimeFormat = timeFormat;
            if (TryEnum<GlanceLayoutMode>(raw, "layout", out var layout)) settings.Layout = layout;
            if (TryEnum<GlanceTransitionMode>(raw, "transition", out var transition)) settings.Transition = transition;
            if (TryEnum<GlanceTransitionSpeed>(raw, "transitionSpeed", out var speed)) settings.TransitionSpeed = speed;
            if (TryEnum<GlanceReadabilityMode>(raw, "readability", out var readability)) settings.Readability = readability;
            if (TryDouble(raw, "backgroundImageTransparency", out double transparency)) settings.BackgroundImageTransparency = transparency;
            if (TryEnum<GlanceCalendarMaterialMode>(raw, "calendarMaterialMode", out var materialMode)) settings.CalendarMaterialMode = materialMode;
            if (TryDouble(raw, "calendarImageMaterialTransparency", out double materialTransparency)) settings.CalendarImageMaterialTransparency = materialTransparency;
            if (TryEnum<GlanceImageFocus>(raw, "imageFocus", out var focus)) settings.ImageFocus = focus;
            if (TryString(raw, "timeFontFamily", out string? fontFamily) && !string.IsNullOrWhiteSpace(fontFamily)) settings.TimeFontFamily = fontFamily;
            // TimeScale: always assign (Normalize will clamp 0 or negative to 0.75).
            // This matches built-in behavior: JSON 0 → app default → clamp → 0.75.
            if (TryDouble(raw, "timeScale", out double scale)) settings.TimeScale = scale;
            // Built-in parity (audit 21 R2): apply the same Normalize rules
            // the host store applies, so legacy/hand-edited data produces the
            // same effective settings as the built-in widget.
            GlanceSettingsNormalizer.Normalize(settings);
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
    /// missing from the original are appended. The schema "version" is NOT
    /// owned by this partial writer (audit round 19): the package has not
    /// run the full legacy migration pipeline, so re-stamping an old file
    /// would falsely claim it, and a future host's newer version must
    /// survive untouched.
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
        // Fresh file (no original document): stamp the current schema
        // version once; afterwards the version travels untouched above.
        if (!written.Contains("version")) writer.WriteNumber("version", GlanceWidgetData.CurrentVersion);
        WriteOwnedProperties(writer, settings, written);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The package-owned settings as a standalone patch document for the
    /// host write-through channel (audit round 20): pass ONLY the mutated
    /// field names - a full owned snapshot would overwrite concurrent
    /// host-side setting changes with this widget's stale cache. With no
    /// field names the full owned set is written (fresh-file path).
    /// </summary>
    internal static string BuildOwnedPatch(GlanceWidgetData settings, params string[] onlyFields)
    {
        HashSet<string>? only = onlyFields.Length == 0 ? null : onlyFields.ToHashSet(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteOwnedProperties(writer, settings, skip: null, only);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOwnedProperties(Utf8JsonWriter writer, GlanceWidgetData settings, HashSet<string>? skip, HashSet<string>? only = null)
    {
        if (skip?.Contains("showChineseFestivals") != true && only?.Contains("showChineseFestivals") != false) writer.WriteBoolean("showChineseFestivals", settings.ShowChineseFestivals);
        if (skip?.Contains("traditionalCalendarMode") != true && only?.Contains("traditionalCalendarMode") != false) writer.WriteString("traditionalCalendarMode", settings.TraditionalCalendarMode.ToString());
        if (skip?.Contains("rotationIntervalMinutes") != true && only?.Contains("rotationIntervalMinutes") != false) writer.WriteNumber("rotationIntervalMinutes", settings.RotationIntervalMinutes);
        if (skip?.Contains("randomOrder") != true && only?.Contains("randomOrder") != false) writer.WriteBoolean("randomOrder", settings.RandomOrder);
        if (skip?.Contains("backgroundSource") != true && only?.Contains("backgroundSource") != false) writer.WriteString("backgroundSource", settings.BackgroundSource.ToString());
        if (skip?.Contains("localImagePaths") != true && only?.Contains("localImagePaths") != false)
        {
            writer.WriteStartArray("localImagePaths");
            foreach (string path in settings.LocalImagePaths) writer.WriteStringValue(path);
            writer.WriteEndArray();
        }
        if (skip?.Contains("localFolderPath") != true && only?.Contains("localFolderPath") != false) writer.WriteString("localFolderPath", settings.LocalFolderPath);
        if (skip?.Contains("imageFit") != true && only?.Contains("imageFit") != false) writer.WriteString("imageFit", settings.ImageFit.ToString());
        if (skip?.Contains("imageFocus") != true && only?.Contains("imageFocus") != false) writer.WriteString("imageFocus", settings.ImageFocus.ToString());
        if (skip?.Contains("showPhotoControls") != true && only?.Contains("showPhotoControls") != false) writer.WriteBoolean("showPhotoControls", settings.ShowPhotoControls);
        // Display-element toggles: the package never mutates these (host
        // settings UI owns them pre-cutover), but they ride the typed
        // round-trip so a full-cache save preserves them exactly.
        if (skip?.Contains("showTime") != true && only?.Contains("showTime") != false) writer.WriteBoolean("showTime", settings.ShowTime);
        if (skip?.Contains("showDate") != true && only?.Contains("showDate") != false) writer.WriteBoolean("showDate", settings.ShowDate);
        if (skip?.Contains("showYear") != true && only?.Contains("showYear") != false) writer.WriteBoolean("showYear", settings.ShowYear);
        if (skip?.Contains("showWeekday") != true && only?.Contains("showWeekday") != false) writer.WriteBoolean("showWeekday", settings.ShowWeekday);
        if (skip?.Contains("showCalendar") != true && only?.Contains("showCalendar") != false) writer.WriteBoolean("showCalendar", settings.ShowCalendar);
        if (skip?.Contains("timeFormat") != true && only?.Contains("timeFormat") != false) writer.WriteString("timeFormat", settings.TimeFormat.ToString());
        if (skip?.Contains("layout") != true && only?.Contains("layout") != false) writer.WriteString("layout", settings.Layout.ToString());
        if (skip?.Contains("transition") != true && only?.Contains("transition") != false) writer.WriteString("transition", settings.Transition.ToString());
        if (skip?.Contains("transitionSpeed") != true && only?.Contains("transitionSpeed") != false) writer.WriteString("transitionSpeed", settings.TransitionSpeed.ToString());
        if (skip?.Contains("readability") != true && only?.Contains("readability") != false) writer.WriteString("readability", settings.Readability.ToString());
        if (skip?.Contains("backgroundImageTransparency") != true && only?.Contains("backgroundImageTransparency") != false) writer.WriteNumber("backgroundImageTransparency", settings.BackgroundImageTransparency);
        if (skip?.Contains("timeFontFamily") != true && only?.Contains("timeFontFamily") != false) writer.WriteString("timeFontFamily", settings.TimeFontFamily);
        if (skip?.Contains("timeScale") != true && only?.Contains("timeScale") != false) writer.WriteNumber("timeScale", settings.TimeScale);
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
            // Enum.TryParse also accepts numeric STRINGS like "999" into
            // undefined values - IsDefined keeps the reader as strict as the
            // patch parser and the migration validator (regression pass).
            return Enum.TryParse(element.GetString(), ignoreCase: true, out value) &&
                   Enum.IsDefined(value);
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
