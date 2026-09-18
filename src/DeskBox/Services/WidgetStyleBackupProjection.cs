using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Projects widget STYLE settings into a portable backup document and
/// patches them back onto a settings.json file (roadmap §10, "格子样式"
/// domain). Style syncs; layout does not — absolute positions, monitor
/// topology, capsule ordering/free placements and internal version
/// counters are deliberately absent from the whitelists below.
///
/// The projection works on the serialized JSON DOM, not the typed model:
/// settings.json is a flat facade schema, so whitelists are expressed in
/// wire names and any untouched content round-trips byte-for-byte.
/// </summary>
internal static class WidgetStyleBackupProjection
{
    internal const int DocumentSchemaVersion = 1;
    internal const string DocumentKind = "widget-style";

    /// <summary>
    /// Flat settings.json keys carrying widget-shell style/display
    /// preferences — the wire-name whitelist for both directions.
    /// Excluded on purpose: widgetCapsuleBarOrder (ordering = layout),
    /// widgetCapsuleFreePlacements (coordinates), widgetCompactSettingsVersion
    /// (internal schema counter), and every WidgetLayoutSettingsSlice key.
    /// </summary>
    private static readonly HashSet<string> ShellKeys = new(StringComparer.Ordinal)
    {
        "defaultWidgetWidth", "defaultWidgetHeight",
        "widgetOpacity", "widgetMaterialType", "widgetMaterialIntensity",
        "widgetForegroundMode", "widgetForegroundColor",
        "widgetBorderColorMode", "widgetBorderStyle",
        "widgetCornerPreference",
        "widgetAnimationEffect", "widgetAnimationSpeed",
        "widgetAnimationSlideDirection", "widgetAnimationEasingIntensity",
        "widgetLayerMode",
        "keepWidgetsVisibleOnShowDesktop",
        "displayWidgetChromeMode", "interactiveWidgetChromeMode",
        "widgetCollapseBehavior",
        // JsonPropertyName("widgetCapsuleModeEnabled") on the facade — the
        // wire name differs from the slice property name.
        "widgetCapsuleModeEnabled",
        "widgetCompactWidthMode", "widgetCompactExpansionDirection",
        "widgetCapsuleArrangementMode",
        "widgetCapsuleBarSpacing", "widgetCapsuleBarPlacement",
        "widgetCapsuleBarDirection",
        "widgetCollapsedStyle", "widgetCompactContentMode",
        "widgetCompactHideSensitiveContent",
        "widgetCompactAnimationEffect", "widgetCompactAnimationDurationMs",
        "widgetCompactExpandDelayMs", "widgetCompactCollapseDelayMs",
        "widgetCompactMediaCornerMode",
        "widgetTitleIconMode", "showHoverButtons", "widgetHoverButtonActions",
        "resizeSnapEnabled", "widgetSnapSpacing", "focusClickedWidgetOnRaise",
        "iconSize", "textSize", "layoutDensity",
        "layoutDensityScale", "horizontalSpacingScale", "verticalSpacingScale"
    };

    /// <summary>
    /// Per-widget style fields whitelisted out of each WidgetConfig element:
    /// display/title/sort preferences only. Geometry (x, y, width, height,
    /// position*, compactPlacement), file bindings (mappedFolderPath, items,
    /// fileAddedAt*), state (isVisible, isDisabled, *Locked) and metadata
    /// never leave the device.
    /// </summary>
    private static readonly HashSet<string> WidgetKeys = new(StringComparer.Ordinal)
    {
        "name", "isDefaultTitle", "viewMode", "iconSizeOverride",
        "isCollapsed", "compactWidth", "sortMode", "sortDescending"
    };

    internal sealed record ApplyResult(
        bool Applied,
        int ShellFieldsPatched,
        int WidgetsPatched,
        string? SkippedReason);

    /// <summary>
    /// Builds the widget-style document from the live settings object.
    /// Returns the UTF-8 JSON payload stored as widget-style.json inside a
    /// scoped backup archive.
    /// </summary>
    internal static byte[] Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        JsonObject settingsDom = JsonSerializer
            .SerializeToNode(settings, SettingsJsonContext.Default.AppSettings)!
            .AsObject();

        var shell = new JsonObject();
        foreach (string key in ShellKeys)
        {
            if (settingsDom.TryGetPropertyValue(key, out JsonNode? value) && value is not null)
            {
                shell[key] = value.DeepClone();
            }
        }

        var widgets = new JsonObject();
        if (settingsDom.TryGetPropertyValue("widgets", out JsonNode? widgetsNode) &&
            widgetsNode is JsonArray widgetsArray)
        {
            foreach (JsonNode? node in widgetsArray)
            {
                if (node is not JsonObject element ||
                    element["id"] is not JsonValue idValue ||
                    !idValue.TryGetValue(out string? id) ||
                    string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var style = new JsonObject();
                foreach (string key in WidgetKeys)
                {
                    if (element.TryGetPropertyValue(key, out JsonNode? value))
                    {
                        style[key] = value?.DeepClone();
                    }
                }

                // widgetKind is carried for verification only — it is never
                // applied; a kind mismatch skips the widget defensively.
                if (element.TryGetPropertyValue("widgetKind", out JsonNode? kindNode))
                {
                    style["widgetKind"] = kindNode?.DeepClone();
                }

                widgets[id] = style;
            }
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = DocumentSchemaVersion,
            ["kind"] = DocumentKind,
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["sourceDeviceId"] = DeviceIdentity.Id,
            ["shell"] = shell,
            ["widgets"] = widgets
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    /// <summary>
    /// Patches the style document onto a settings.json file, atomically via
    /// the same resilient-write path the settings store itself uses. Only
    /// whitelisted keys are written; widgets are matched by id and verified
    /// by widgetKind before any field is touched.
    /// </summary>
    internal static async Task<ApplyResult> ApplyToSettingsFileAsync(
        byte[] documentBytes,
        string settingsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        JsonObject document;
        try
        {
            document = JsonNode.Parse(documentBytes)?.AsObject()
                       ?? throw new InvalidDataException("The widget-style document is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The widget-style document is invalid.", ex);
        }

        if (document["schemaVersion"] is not JsonValue versionValue ||
            !versionValue.TryGetValue(out int version) ||
            version > DocumentSchemaVersion)
        {
            throw new InvalidDataException("The widget-style document schema is not supported.");
        }

        if (document["kind"] is not JsonValue kindValue ||
            !kindValue.TryGetValue(out string? kind) ||
            !string.Equals(kind, DocumentKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The widget-style document kind is invalid.");
        }

        if (!File.Exists(settingsPath))
        {
            return new ApplyResult(false, 0, 0, "settings.json does not exist");
        }

        JsonObject settingsDom;
        await using (var input = new FileStream(
                         settingsPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 81920,
                         useAsync: true))
        {
            settingsDom = (await JsonNode.ParseAsync(
                    input,
                    cancellationToken: cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("settings.json is empty.");
        }

        int shellPatched = 0;
        if (document["shell"] is JsonObject shellDoc)
        {
            foreach ((string key, JsonNode? value) in shellDoc)
            {
                if (!ShellKeys.Contains(key))
                {
                    continue;
                }

                settingsDom[key] = value?.DeepClone();
                shellPatched++;
            }
        }

        int widgetsPatched = 0;
        if (document["widgets"] is JsonObject widgetsDoc &&
            settingsDom.TryGetPropertyValue("widgets", out JsonNode? widgetsNode) &&
            widgetsNode is JsonArray widgetsArray)
        {
            foreach (JsonNode? node in widgetsArray)
            {
                if (node is not JsonObject element ||
                    element["id"] is not JsonValue idValue ||
                    !idValue.TryGetValue(out string? id) ||
                    string.IsNullOrEmpty(id) ||
                    widgetsDoc[id] is not JsonObject style)
                {
                    continue;
                }

                string? docKind = style["widgetKind"] is JsonValue docKindValue &&
                                  docKindValue.TryGetValue(out string? dk) ? dk : null;
                string? localKind = element["widgetKind"] is JsonValue localKindValue &&
                                    localKindValue.TryGetValue(out string? lk) ? lk : null;
                if (!string.Equals(docKind, localKind, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string key in WidgetKeys)
                {
                    if (style.TryGetPropertyValue(key, out JsonNode? value))
                    {
                        element[key] = value?.DeepClone();
                    }
                }

                widgetsPatched++;
            }
        }

        string patched = settingsDom.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await ResilientJsonStore.SaveAsync(settingsPath, patched);
        return new ApplyResult(true, shellPatched, widgetsPatched, null);
    }
}
