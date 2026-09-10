using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Effective host environment gates, not instance preferences. Image rotation
/// follows the host performance resolver; image transitions follow the host's
/// system accessibility/effects policy. Instance and lifecycle gates still apply.
/// </summary>
internal sealed record PackagePerformancePolicy(bool AllowImageAutoRotation, bool AllowDecorativeAnimations)
{
    // Matches built-in Glance without a settings service and the host's
    // animation fallback when system UI settings cannot be read.
    internal static PackagePerformancePolicy Default { get; } = new(true, true);

    internal static PackagePerformancePolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Default;

            bool ReadBoolean(string key, bool fallback) =>
                root.TryGetProperty(key, out JsonElement value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean() : fallback;

            return new(
                ReadBoolean("allowImageAutoRotation", Default.AllowImageAutoRotation),
                ReadBoolean("allowDecorativeAnimations", Default.AllowDecorativeAnimations));
        }
        catch (JsonException) { return Default; }
    }
}
