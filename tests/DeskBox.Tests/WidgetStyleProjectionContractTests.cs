using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// A-4 drift ratchet for <see cref="WidgetStyleBackupProjection"/>: the
/// backup/sync style whitelist is a hand-maintained wire-name set, so a new
/// style field added to the owning slice would silently never be backed up.
/// These tests require every serialized field to be consciously classified —
/// whitelisted or explicitly excluded — so the whitelist cannot drift.
/// </summary>
public sealed class WidgetStyleProjectionContractTests
{
    /// <summary>
    /// Facade-level renames: the slice property name differs from the wire
    /// name because the AppSettings facade property carries a
    /// [JsonPropertyName]. Keyed by slice property name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ShellWireNameOverrides =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // AppSettings.LegacyWidgetCapsuleModeEnabled → wire "widgetCapsuleModeEnabled".
            ["LegacyWidgetCapsuleModeEnabled"] = "widgetCapsuleModeEnabled",
        };

    /// <summary>
    /// Shell-slice wire names intentionally kept out of the style document:
    /// ordering/coordinates are layout (not style), and the version counter
    /// is internal schema state. Anything added to
    /// <see cref="WidgetShellSettingsSlice"/> must be classified here or in
    /// the whitelist before this test passes again.
    /// </summary>
    private static readonly HashSet<string> ExcludedShellWireNames = new(StringComparer.Ordinal)
    {
        "widgetCapsuleBarOrder",       // stable order — layout, not style
        "widgetCapsuleFreePlacements", // free-layout coordinates — layout
        "widgetCompactSettingsVersion", // internal migration counter
        // Layout + behavior stay device-local; only visual style syncs.
        "defaultWidgetWidth", "defaultWidgetHeight",           // default geometry
        "iconSize", "textSize",                                // content scale
        "layoutDensity", "layoutDensityScale",                 // density preset/scale
        "horizontalSpacingScale", "verticalSpacingScale",      // spacing scale
        "widgetCapsuleArrangementMode",                        // capsule arrangement
        "widgetCapsuleBarSpacing", "widgetCapsuleBarPlacement",
        "widgetCapsuleBarDirection",                           // capsule bar placement
        "widgetCollapseBehavior",                              // compact-state policy
        "widgetCapsuleModeEnabled",                            // legacy compact gate
        "widgetCompactWidthMode", "widgetCompactExpansionDirection", // compact geometry
        "widgetCompactExpandDelayMs", "widgetCompactCollapseDelayMs", // compact hover timing
        "widgetLayerMode",                                     // window z-layer
        "keepWidgetsVisibleOnShowDesktop",                     // window behavior
        "resizeSnapEnabled", "widgetSnapSpacing",              // snap geometry
        "focusClickedWidgetOnRaise",                           // raise behavior
        // Global widget background: the mode and image file names are device
        // local — the shared image files never join the backup, so syncing
        // the keys would restore a half-state; it self-heals to material.
        "widgetBackgroundMode", "widgetBackgroundUnifiedImage",
        "widgetBackgroundPanoramaImage", "widgetBackgroundDim",
        "widgetBackgroundUnifiedFit",
        // Pure visual device preference (schema v11) — syncs nothing the
        // remote side could not re-derive locally.
        "widgetTextShadowEnabled"
    };

    /// <summary>
    /// Per-widget wire names intentionally kept out of the style document:
    /// identity/metadata, geometry/placement, file bindings and runtime
    /// state never leave the device. Anything added to
    /// <see cref="WidgetConfig"/> must be classified here or in the
    /// whitelist before this test passes again.
    /// </summary>
    private static readonly HashSet<string> ExcludedWidgetWireNames = new(StringComparer.Ordinal)
    {
        // identity + metadata
        "id", "widgetKind", "metadata",
        // geometry + placement
        "x", "y", "width", "height",
        "needsInitialPlacement", "positionAnchor",
        "positionMarginX", "positionMarginY",
        "positionMonitorKey", "positionMonitorDeviceName",
        "positionMonitorStableId", "positionMonitorWasPrimary",
        "boundsCoordinateVersion",
        "screenBindingMode", "boundScreenId",
        "compactPlacement",
        // file bindings + item payloads
        "mappedFolderPath", "followsDefaultStoragePath", "managedFolderName",
        "items", "fileAddedAtByPath", "fileAddedAtTrackingInitialized",
        // visibility + lock state
        "isVisible", "isDisabled", "isPositionLocked", "isSizeLocked",
        // compact/collapse state + per-widget density — layout, not style
        "isCollapsed", "compactWidth", "iconSizeOverride",
    };

    [Fact]
    public void ShellWhitelist_AccountsForEverySliceWireName()
    {
        // Ground truth: the real serialized DOM. The legacy capsule flag and
        // the schema-v10 background fields are WhenWritingNull, so set them
        // to make their wire names appear.
        var settings = new AppSettings
        {
            LegacyWidgetCapsuleModeEnabled = true,
            WidgetBackgroundMode = "Panorama",
            WidgetBackgroundUnifiedImage = "background.png",
            WidgetBackgroundPanoramaImage = "panorama.png",
            WidgetBackgroundDim = 42,
            WidgetBackgroundUnifiedFit = "Contain"
        };
        JsonObject dom = JsonSerializer
            .SerializeToNode(settings, SettingsJsonContext.Default.AppSettings)!
            .AsObject();
        var domKeys = new HashSet<string>(
            dom.Select(pair => pair.Key), StringComparer.Ordinal);

        HashSet<string> sliceWireNames = WireNames(
            typeof(WidgetShellSettingsSlice), ShellWireNameOverrides);

        // Every derived wire name must be a real settings.json key — this
        // binds the override map and the camelCase assumption to the actual
        // serializer instead of duplicating its naming logic.
        string[] notInDom = sliceWireNames.Where(name => !domKeys.Contains(name)).ToArray();
        Assert.True(
            notInDom.Length == 0,
            "Slice wire names missing from serialized settings.json:\n  " +
            string.Join("\n  ", notInDom));

        HashSet<string> classified = new(WidgetStyleBackupProjection.ShellKeys, StringComparer.Ordinal);
        classified.UnionWith(ExcludedShellWireNames);

        string[] unclassified = sliceWireNames.Where(name => !classified.Contains(name)).ToArray();
        Assert.True(
            unclassified.Length == 0,
            "New shell-slice fields must be added to the style whitelist or " +
            "explicitly excluded — they cannot silently skip backup:\n  " +
            string.Join("\n  ", unclassified));

        string[] stale = classified.Where(name => !sliceWireNames.Contains(name)).ToArray();
        Assert.True(
            stale.Length == 0,
            "Whitelist/exclusion entries no longer backed by a slice field:\n  " +
            string.Join("\n  ", stale));
    }

    [Fact]
    public void WidgetWhitelist_AccountsForEveryConfigWireName()
    {
        HashSet<string> configWireNames = WireNames(
            typeof(WidgetConfig), overrides: null);

        HashSet<string> classified = new(WidgetStyleBackupProjection.WidgetKeys, StringComparer.Ordinal);
        classified.UnionWith(ExcludedWidgetWireNames);

        string[] unclassified = configWireNames.Where(name => !classified.Contains(name)).ToArray();
        Assert.True(
            unclassified.Length == 0,
            "New WidgetConfig fields must be added to the style whitelist or " +
            "explicitly excluded — they cannot silently skip backup:\n  " +
            string.Join("\n  ", unclassified));

        string[] stale = classified.Where(name => !configWireNames.Contains(name)).ToArray();
        Assert.True(
            stale.Length == 0,
            "Whitelist/exclusion entries no longer backed by a WidgetConfig field:\n  " +
            string.Join("\n  ", stale));
    }

    private static HashSet<string> WireNames(
        Type type,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite ||
                property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition ==
                    JsonIgnoreCondition.Always)
            {
                continue;
            }

            if (overrides?.TryGetValue(property.Name, out string? overridden) == true)
            {
                names.Add(overridden);
                continue;
            }

            names.Add(
                property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                JsonNamingPolicy.CamelCase.ConvertName(property.Name));
        }

        return names;
    }
}
