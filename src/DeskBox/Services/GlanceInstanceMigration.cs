using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Glance's implementation of the official-package data handoff (audit
/// round 20 §18): the generic plugin layer routes through
/// ILegacyInstanceMigration, and everything glance-specific — legacy store
/// paths, recovery candidates, settings shape, and the authoritative
/// write-through — lives here beside GlanceWidgetStore.
/// </summary>
internal sealed class GlanceInstanceMigration : ILegacyInstanceMigration
{
    internal static GlanceInstanceMigration Instance { get; } = new();

    private GlanceInstanceMigration()
    {
    }

    // The package's GlanceDataFile reader defines this name.
    public string DataFileName => "glance-data.json";

    public void PreparePackageData(string dataDirectory, string packageDataRoot)
    {
        // Versioned, package-wide hint, NOT a completion marker. Never touch
        // legacy image bytes on the UI thread. The package owns retry policy.
        Directory.CreateDirectory(packageDataRoot);
        string path = Path.Combine(packageDataRoot, "legacy-glance-cache.json");
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("sourceRoot", Path.GetFullPath(Path.Combine(dataDirectory, "cache", "glance")));
                writer.WriteEndObject();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IDisposable SubscribeChanges(string instanceId, Action changed) =>
        SubscribeChanges(GlanceWidgetStore.ForWidget(instanceId), changed);

    internal static IDisposable SubscribeChanges(GlanceWidgetStore store, Action changed) =>
        new StoreSubscription(store, changed);

    private sealed class StoreSubscription : IDisposable
    {
        private GlanceWidgetStore? _store;
        private readonly EventHandler _handler;

        internal StoreSubscription(GlanceWidgetStore store, Action changed)
        {
            _store = store;
            _handler = (_, _) => changed();
            store.Changed += _handler;
        }

        public void Dispose()
        {
            GlanceWidgetStore? store = Interlocked.Exchange(ref _store, null);
            if (store is not null) store.Changed -= _handler;
        }
    }

    // ---- Legacy content resolution (host-authoritative sync) ----

    public string? ResolveLegacyContent(string dataDirectory, string instanceId)
    {
        string widgetFile = Path.Combine(
            dataDirectory, "glance", "widgets",
            $"{GlanceWidgetStore.GetSafeWidgetFileName(instanceId)}.json");
        string legacyFile = Path.Combine(dataDirectory, "glance", "glance.json");
        foreach (string candidate in new[] { widgetFile, widgetFile + ".bak", legacyFile, legacyFile + ".bak" })
        {
            if (TryReadValidSettings(candidate, out string? content))
            {
                return content;
            }
        }
        return null;
    }

    private static bool TryReadValidSettings(string path, out string? content)
    {
        content = null;
        try
        {
            if (!File.Exists(path)) return false;
            string text = File.ReadAllText(path);
            if (!IsValidSettingsShape(text)) return false;
            content = text;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Structural + semantic validation: the candidate must parse AND the
    /// glance settings fields must be type-valid, mirroring what the
    /// built-in deserializer would accept (audits 19-20). Only the fields
    /// the sync target consumes are checked; the built-in store owns the
    /// rest of the schema and its own recovery.
    /// </summary>
    private static bool IsValidSettingsShape(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            JsonElement root = document.RootElement;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                bool typeValid = property.Name switch
                {
                    "showChineseFestivals" or "randomOrder" or "showPhotoControls" or
                    "showTime" or "showDate" or "showYear" or "showWeekday" or "showCalendar" =>
                        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "rotationIntervalMinutes" =>
                        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out _),
                    "localImagePaths" =>
                        // Strict (audit 20): the built-in deserializer rejects
                        // a non-array here, so the sync must too.
                        property.Value.ValueKind == JsonValueKind.Array &&
                        property.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                    "localFolderPath" =>
                        property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                    "traditionalCalendarMode" => IsValidEnumValue<GlanceTraditionalCalendarMode>(property.Value),
                    "backgroundSource" => IsValidEnumValue<GlanceBackgroundSource>(property.Value),
                    "imageFit" => IsValidEnumValue<GlanceImageFitMode>(property.Value),
                    "imageFocus" => IsValidEnumValue<GlanceImageFocus>(property.Value),
                    "timeFormat" => IsValidEnumValue<GlanceTimeFormatMode>(property.Value),
                    "layout" => IsValidEnumValue<GlanceLayoutMode>(property.Value),
                    "transition" => IsValidEnumValue<GlanceTransitionMode>(property.Value),
                    "transitionSpeed" => IsValidEnumValue<GlanceTransitionSpeed>(property.Value),
                    "readability" => IsValidEnumValue<GlanceReadabilityMode>(property.Value),
                    "backgroundImageTransparency" =>
                        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out _),
                    "timeFontFamily" =>
                        property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                    "timeScale" =>
                        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out _),
                    _ => true,
                };
                if (!typeValid) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

/// <summary>
/// Enums travel as names or legacy integers (the built-in accepts both);
/// an undefined integer is still deserializable by JsonStringEnumConverter
/// and will be corrected by Normalize, so migration recovery accepts any
/// integer. Strings must be defined enum names. (Audit round 21 R2 repair.)
/// </summary>
private static bool IsValidEnumValue<TEnum>(JsonElement element)
    where TEnum : struct, Enum
{
    if (element.ValueKind == JsonValueKind.String)
    {
        return Enum.TryParse(element.GetString(), ignoreCase: true, out TEnum parsed) &&
               Enum.IsDefined(parsed);
    }
    // Any integer is accepted (matching built-in's JsonStringEnumConverter
    // default allowIntegerValues=true); Normalize fixes undefined values.
    if (element.ValueKind == JsonValueKind.Number)
    {
        return element.TryGetInt32(out _);
    }
    return false;
}

    // ---- Write-through (native settings patch → authoritative store) ----

    public bool TryApplyPatch(string instanceId, string jsonPatch)
    {
        GlanceInstanceConfigPatch? patch = GlanceInstanceConfigPatch.TryParse(jsonPatch);
        if (patch is null)
        {
            App.LogVerbose($"[NativePackage] rejected malformed instance config patch for {instanceId}");
            return false;
        }
        // Fire-and-forget: the callback must never block the UI thread on
        // the store's async pipeline; a failed persistence is logged inside
        // CommitAsync and self-heals on the next create-time sync.
        _ = GlanceInstanceConfigPatch.CommitAsync(GlanceWidgetStore.ForWidget(instanceId), patch);
        return true;
    }
}

/// <summary>
/// The package-owned settings patch for the glance write-through channel.
/// Parsing is JsonDocument-based (reflection JSON stays out of the plugin
/// host layer) and type-strict: absent fields are skipped, one wrong-typed
/// field rejects the whole patch before anything touches the authoritative
/// store (audits 19-20).
/// </summary>
internal sealed record GlanceInstanceConfigPatch(
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

    /// <summary>
    /// Commits an accepted patch through the authoritative store.
    /// Deliberately OUTSIDE the unsafe HostApi bridge (await is not allowed
    /// in an unsafe context); failures are observed and logged here so a
    /// lost async commit never disappears silently.
    /// </summary>
    public static async Task CommitAsync(GlanceWidgetStore store, GlanceInstanceConfigPatch patch)
    {
        try
        {
            await store.UpdateAsync(data => patch.ApplyTo(data));
        }
        catch (Exception error)
        {
            App.Log($"[NativePackage] instance config commit failed: {error.Message}");
        }
    }

    public static GlanceInstanceConfigPatch? TryParse(string payload)
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
            return new GlanceInstanceConfigPatch(
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
            // TryParse also accepts numeric STRINGS into undefined values -
            // IsDefined closes that hole (audit round 20).
            if (!Enum.TryParse(element.GetString(), ignoreCase: true, out TEnum parsed) || !Enum.IsDefined(parsed))
            {
                return false;
            }
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
