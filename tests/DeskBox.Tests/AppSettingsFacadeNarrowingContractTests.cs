using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch 51 schema-equivalent narrowing contract for the AppSettings facade.
/// The 220 passthrough properties are the frozen settings.json wire contract:
/// every passthrough is an on-disk schema member, so the facade may neither
/// grow (new schema field without a versioned migration) nor shrink (dropping
/// a serialized field from every saved file) as part of ordinary refactoring.
/// Serializing slice members directly was evaluated and rejected: the flat
/// member order interleaves all 13 slices and the legacy
/// <c>widgetCapsuleModeEnabled</c> wire attributes live on the facade, so no
/// byte-equivalent slice-serialization path exists (the disk schema is
/// frozen; <see cref="SettingsSliceContractBaselineTests"/> pins the exact
/// member order and pre-slice defaults).
/// </summary>
public sealed class AppSettingsFacadeNarrowingContractTests
{
    private static readonly PropertyInfo[] Passthroughs = typeof(AppSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p is { CanRead: true, CanWrite: true }
                    && p.Name != nameof(AppSettings.SchemaVersion)
                    && !p.PropertyType.Name.EndsWith("SettingsSlice", StringComparison.Ordinal))
        .ToArray();

    private static readonly PropertyInfo[] SliceAccessors = typeof(AppSettings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.PropertyType.Name.EndsWith("SettingsSlice", StringComparison.Ordinal))
        .ToArray();

    private static readonly HashSet<string> SerializedMemberNames = SerializeMemberNames();

    [Fact]
    public void EveryPassthrough_IsAFrozenSchemaMember_AndViceVersa()
    {
        var expected = Passthroughs
            .Select(WireName)
            .ToHashSet(StringComparer.Ordinal);
        expected.Add("schemaVersion");
        Assert.True(
            SerializedMemberNames.SetEquals(expected),
            "Serialized member set must equal schemaVersion + all passthrough wire names.");
        Assert.Equal(expected.Count, SerializedMemberNames.Count);
        Assert.All(SerializedMemberNames, name =>
            Assert.DoesNotContain(name, SliceAccessors.Select(p => WireName(p))));
    }

    [Fact]
    public void WireLevelAttributes_AreFrozenOnTheFacade()
    {
        // The single legacy wire-attribute pair (rename + null omission for
        // the migration-era widgetCapsuleModeEnabled key) is a root cause of
        // why slice-direct serialization cannot be byte-equivalent. It must
        // stay exactly here, exactly this shape, exactly this count. The
        // schema-v10 background fields share the WhenWritingNull omission so
        // untouched profiles stay byte-stable, but carry no rename.
        PropertyInfo[] renamed = Passthroughs
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .ToArray();
        PropertyInfo[] conditionallyIgnored = Passthroughs
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition
                            == JsonIgnoreCondition.WhenWritingNull)
            .ToArray();

        Assert.Single(renamed);
        Assert.Equal(6, conditionallyIgnored.Length);
        Assert.Same(renamed[0], conditionallyIgnored.Single(p => p.Name == nameof(AppSettings.LegacyWidgetCapsuleModeEnabled)));
        Assert.Equal(nameof(AppSettings.LegacyWidgetCapsuleModeEnabled), renamed[0].Name);
        Assert.Equal(
            "widgetCapsuleModeEnabled",
            renamed[0].GetCustomAttribute<JsonPropertyNameAttribute>()!.Name);
        Assert.Equal(
            new[]
            {
                nameof(AppSettings.LegacyWidgetCapsuleModeEnabled),
                nameof(AppSettings.WidgetBackgroundMode),
                nameof(AppSettings.WidgetBackgroundUnifiedImage),
                nameof(AppSettings.WidgetBackgroundPanoramaImage),
                nameof(AppSettings.WidgetBackgroundDim),
                nameof(AppSettings.WidgetBackgroundUnifiedFit)
            }.Order(),
            conditionallyIgnored.Select(p => p.Name).Order());
    }

    private static HashSet<string> SerializeMemberNames()
    {
        // Legacy prop and the schema-v10 background fields set non-default so
        // every WhenWritingNull member is emitted too.
        var settings = new AppSettings
        {
            LegacyWidgetCapsuleModeEnabled = true,
            WidgetBackgroundMode = "Panorama",
            WidgetBackgroundUnifiedImage = "background.png",
            WidgetBackgroundPanoramaImage = "panorama.png",
            WidgetBackgroundDim = 42,
            WidgetBackgroundUnifiedFit = "Contain"
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            settings, SettingsJsonContext.Default.AppSettings));
        return doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string WireName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
}
