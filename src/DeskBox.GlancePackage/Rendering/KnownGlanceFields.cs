using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Single source of truth for every package-owned glance setting field.
/// Adding a field = adding one entry to the Fields array plus the typed
/// property on GlanceWidgetData. Load, Save, migration validation, and
/// write-through patches all dispatch through this table, so a missing
/// update is a compile-time switch-case error rather than a silent omission.
///
/// AOT-safe: explicit delegates, no reflection.
/// </summary>
internal static class KnownGlanceFields
{
    internal readonly record struct FieldDef(
        string JsonName,
        Type ValueType,
        Func<JsonElement, bool> TryReadInto,
        Action<Utf8JsonWriter, GlanceWidgetData> WriteValue,
        Func<JsonElement, bool> IsShapeValid);

    private static bool ReadBool(JsonElement e, GlanceWidgetData s, string prop)
    {
        if (!e.TryGetProperty(prop, out var el)) return false;
        if (el.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        switch (prop)
        {
            case nameof(GlanceWidgetData.ShowChineseFestivals): s.ShowChineseFestivals = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.RandomOrder): s.RandomOrder = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.ShowPhotoControls): s.ShowPhotoControls = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.ShowTime): s.ShowTime = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.ShowDate): s.ShowDate = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.ShowYear): s.ShowYear = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.ShowWeekday): s.ShowWeekday = el.GetBoolean(); break;
            case nameof(GlanceWidgetData.ShowCalendar): s.ShowCalendar = el.GetBoolean(); break;
            default: return false;
        }
        return true;
    }

    private static void WriteBool(Utf8JsonWriter w, GlanceWidgetData s, string prop)
    {
        switch (prop)
        {
            case nameof(GlanceWidgetData.ShowChineseFestivals): w.WriteBoolean(prop, s.ShowChineseFestivals); break;
            case nameof(GlanceWidgetData.RandomOrder): w.WriteBoolean(prop, s.RandomOrder); break;
            case nameof(GlanceWidgetData.ShowPhotoControls): w.WriteBoolean(prop, s.ShowPhotoControls); break;
            case nameof(GlanceWidgetData.ShowTime): w.WriteBoolean(prop, s.ShowTime); break;
            case nameof(GlanceWidgetData.ShowDate): w.WriteBoolean(prop, s.ShowDate); break;
            case nameof(GlanceWidgetData.ShowYear): w.WriteBoolean(prop, s.ShowYear); break;
            case nameof(GlanceWidgetData.ShowWeekday): w.WriteBoolean(prop, s.ShowWeekday); break;
            case nameof(GlanceWidgetData.ShowCalendar): w.WriteBoolean(prop, s.ShowCalendar); break;
        }
    }

    private static bool IsBoolShape(JsonElement el) =>
        el.ValueKind is JsonValueKind.True or JsonValueKind.False;

    /// <summary>Every package-owned field, in write order.</summary>
    internal static readonly string[] OwnedNames =
    [
        nameof(GlanceWidgetData.ShowChineseFestivals),
        nameof(GlanceWidgetData.TraditionalCalendarMode),
        nameof(GlanceWidgetData.RotationIntervalMinutes),
        nameof(GlanceWidgetData.RandomOrder),
        nameof(GlanceWidgetData.BackgroundSource),
        nameof(GlanceWidgetData.LocalImagePaths),
        nameof(GlanceWidgetData.LocalFolderPath),
        nameof(GlanceWidgetData.ImageFit),
        nameof(GlanceWidgetData.ImageFocus),
        nameof(GlanceWidgetData.ShowPhotoControls),
        nameof(GlanceWidgetData.ShowTime),
        nameof(GlanceWidgetData.ShowDate),
        nameof(GlanceWidgetData.ShowYear),
        nameof(GlanceWidgetData.ShowWeekday),
        nameof(GlanceWidgetData.ShowCalendar),
        nameof(GlanceWidgetData.TimeFormat),
        nameof(GlanceWidgetData.Layout),
        nameof(GlanceWidgetData.Transition),
        nameof(GlanceWidgetData.TransitionSpeed),
        nameof(GlanceWidgetData.Readability),
        nameof(GlanceWidgetData.BackgroundImageTransparency),
        nameof(GlanceWidgetData.TimeFontFamily),
        nameof(GlanceWidgetData.TimeScale),
    ];

    /// <summary>
    /// Writes all owned fields (or only the named subset) as a JSON object.
    /// Used by BuildOwnedPatch and WritePreserving.
    /// </summary>
    public static void WriteOwnedFields(Utf8JsonWriter writer, GlanceWidgetData s, HashSet<string>? skip = null, HashSet<string>? only = null)
    {
        bool ShouldWrite(string name) =>
            (skip?.Contains(name) != true) && (only?.Contains(name) != false);

        if (ShouldWrite(OwnedNames[0])) writer.WriteBoolean(OwnedNames[0], s.ShowChineseFestivals);
        if (ShouldWrite(OwnedNames[1])) writer.WriteString(OwnedNames[1], s.TraditionalCalendarMode.ToString());
        if (ShouldWrite(OwnedNames[2])) writer.WriteNumber(OwnedNames[2], s.RotationIntervalMinutes);
        if (ShouldWrite(OwnedNames[3])) writer.WriteBoolean(OwnedNames[3], s.RandomOrder);
        if (ShouldWrite(OwnedNames[4])) writer.WriteString(OwnedNames[4], s.BackgroundSource.ToString());
        if (ShouldWrite(OwnedNames[5]))
        {
            writer.WriteStartArray(OwnedNames[5]);
            foreach (string path in s.LocalImagePaths) writer.WriteStringValue(path);
            writer.WriteEndArray();
        }
        if (ShouldWrite(OwnedNames[6])) writer.WriteString(OwnedNames[6], s.LocalFolderPath);
        if (ShouldWrite(OwnedNames[7])) writer.WriteString(OwnedNames[7], s.ImageFit.ToString());
        if (ShouldWrite(OwnedNames[8])) writer.WriteString(OwnedNames[8], s.ImageFocus.ToString());
        if (ShouldWrite(OwnedNames[9])) writer.WriteBoolean(OwnedNames[9], s.ShowPhotoControls);
        if (ShouldWrite(OwnedNames[10])) writer.WriteBoolean(OwnedNames[10], s.ShowTime);
        if (ShouldWrite(OwnedNames[11])) writer.WriteBoolean(OwnedNames[11], s.ShowDate);
        if (ShouldWrite(OwnedNames[12])) writer.WriteBoolean(OwnedNames[12], s.ShowYear);
        if (ShouldWrite(OwnedNames[13])) writer.WriteBoolean(OwnedNames[13], s.ShowWeekday);
        if (ShouldWrite(OwnedNames[14])) writer.WriteBoolean(OwnedNames[14], s.ShowCalendar);
        if (ShouldWrite(OwnedNames[15])) writer.WriteString(OwnedNames[15], s.TimeFormat.ToString());
        if (ShouldWrite(OwnedNames[16])) writer.WriteString(OwnedNames[16], s.Layout.ToString());
        if (ShouldWrite(OwnedNames[17])) writer.WriteString(OwnedNames[17], s.Transition.ToString());
        if (ShouldWrite(OwnedNames[18])) writer.WriteString(OwnedNames[18], s.TransitionSpeed.ToString());
        if (ShouldWrite(OwnedNames[19])) writer.WriteString(OwnedNames[19], s.Readability.ToString());
        if (ShouldWrite(OwnedNames[20])) writer.WriteNumber(OwnedNames[20], s.BackgroundImageTransparency);
        if (ShouldWrite(OwnedNames[21])) writer.WriteString(OwnedNames[21], s.TimeFontFamily);
        if (ShouldWrite(OwnedNames[22])) writer.WriteNumber(OwnedNames[22], s.TimeScale);
    }

    /// <summary>
    /// Migration shape validation: checks that a JSON property's type matches
    /// what the typed reader expects. Enum fields accept names or defined
    /// integers (matching built-in's JsonStringEnumConverter + Normalize).
    /// </summary>
    public static bool IsFieldTypeValid(string propertyName, JsonElement value)
    {
        return propertyName switch
        {
            nameof(GlanceWidgetData.ShowChineseFestivals) or
            nameof(GlanceWidgetData.RandomOrder) or
            nameof(GlanceWidgetData.ShowPhotoControls) or
            nameof(GlanceWidgetData.ShowTime) or
            nameof(GlanceWidgetData.ShowDate) or
            nameof(GlanceWidgetData.ShowYear) or
            nameof(GlanceWidgetData.ShowWeekday) or
            nameof(GlanceWidgetData.ShowCalendar) => value.ValueKind is JsonValueKind.True or JsonValueKind.False,

            nameof(GlanceWidgetData.RotationIntervalMinutes) or
            nameof(GlanceWidgetData.BackgroundImageTransparency) or
            nameof(GlanceWidgetData.TimeScale) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out _),

            nameof(GlanceWidgetData.LocalImagePaths) => value.ValueKind == JsonValueKind.Array &&
                value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),

            nameof(GlanceWidgetData.LocalFolderPath) or
            nameof(GlanceWidgetData.TimeFontFamily) => value.ValueKind is JsonValueKind.String or JsonValueKind.Null,

            nameof(GlanceWidgetData.TraditionalCalendarMode) => IsIntOrDefinedName<GlanceTraditionalCalendarMode>(value),
            nameof(GlanceWidgetData.BackgroundSource) => IsIntOrDefinedName<GlanceBackgroundSource>(value),
            nameof(GlanceWidgetData.ImageFit) => IsIntOrDefinedName<GlanceImageFitMode>(value),
            nameof(GlanceWidgetData.ImageFocus) => IsIntOrDefinedName<GlanceImageFocus>(value),
            nameof(GlanceWidgetData.TimeFormat) => IsIntOrDefinedName<GlanceTimeFormatMode>(value),
            nameof(GlanceWidgetData.Layout) => IsIntOrDefinedName<GlanceLayoutMode>(value),
            nameof(GlanceWidgetData.Transition) => IsIntOrDefinedName<GlanceTransitionMode>(value),
            nameof(GlanceWidgetData.TransitionSpeed) => IsIntOrDefinedName<GlanceTransitionSpeed>(value),
            nameof(GlanceWidgetData.Readability) => IsIntOrDefinedName<GlanceReadabilityMode>(value),

            _ => true,
        };
    }

    private static bool IsIntOrDefinedName<TEnum>(JsonElement element)
        where TEnum : struct, Enum
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return Enum.TryParse(element.GetString(), ignoreCase: true, out TEnum parsed) &&
                   Enum.IsDefined(parsed);
        }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number) && number >= 0)
        {
            return Enum.IsDefined((TEnum)(object)number);
        }
        return false;
    }
}
