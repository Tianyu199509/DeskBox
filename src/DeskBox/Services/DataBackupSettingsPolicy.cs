using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Runtime options for automatic data snapshots, derived from
/// <see cref="AppSettings"/> and pushed into <see cref="DeskBoxDataBackupService"/>.
/// </summary>
public sealed record AutomaticBackupOptions(
    bool IsEnabled,
    int IntervalMinutes,
    int RetentionCount,
    string? CustomDirectory)
{
    public static AutomaticBackupOptions Default { get; } = new(
        DataBackupSettingsPolicy.DefaultEnabled,
        DataBackupSettingsPolicy.DefaultIntervalMinutes,
        DataBackupSettingsPolicy.DefaultRetentionCount,
        CustomDirectory: null);
}

/// <summary>
/// Central policy for the automatic-backup settings: the enabled flag, the
/// discrete interval and retention presets, and the custom snapshot directory.
/// Values outside the presets are normalized so hand-edited settings files can
/// never reach the backup engine.
/// </summary>
public static class DataBackupSettingsPolicy
{
    public const bool DefaultEnabled = true;
    public const int DefaultIntervalMinutes = 24 * 60;
    public const int DefaultRetentionCount = 7;

    /// <summary>Interval presets offered in settings, in ascending order.</summary>
    public static readonly int[] SupportedIntervalMinutes =
        [5, 30, 60, 12 * 60, 24 * 60, 5 * 24 * 60];

    /// <summary>Retention presets offered in settings, in ascending order.</summary>
    public static readonly int[] SupportedRetentionCounts = [3, 5, 7, 14, 30];

    public static int NormalizeIntervalMinutes(int value) =>
        SupportedIntervalMinutes.Contains(value) ? value : DefaultIntervalMinutes;

    public static int NormalizeRetentionCount(int value) =>
        SupportedRetentionCounts.Contains(value) ? value : DefaultRetentionCount;

    public static string? NormalizeCustomDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    public static void SetIntervalMinutes(AppSettings settings, int value) =>
        settings.AutomaticBackupIntervalMinutes = NormalizeIntervalMinutes(value);

    public static void SetRetentionCount(AppSettings settings, int value) =>
        settings.AutomaticBackupRetentionCount = NormalizeRetentionCount(value);

    public static AutomaticBackupOptions GetOptions(AppSettings settings) => new(
        settings.AutomaticBackupEnabled,
        NormalizeIntervalMinutes(settings.AutomaticBackupIntervalMinutes),
        NormalizeRetentionCount(settings.AutomaticBackupRetentionCount),
        NormalizeCustomDirectory(settings.AutomaticBackupDirectory));

    /// <summary>
    /// Reads the automatic-backup fields from a raw settings file before the
    /// full settings load runs, so the pre-normalization startup snapshot can
    /// already respect the user's schedule and folder. Any missing or invalid
    /// field falls back to the defaults; this path must never throw.
    /// </summary>
    public static AutomaticBackupOptions ReadStartupOptions(string? settingsPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            {
                return AutomaticBackupOptions.Default;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            JsonElement root = document.RootElement;
            bool isEnabled = DefaultEnabled;
            if (root.TryGetProperty("automaticBackupEnabled", out JsonElement enabled))
            {
                isEnabled = enabled.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => DefaultEnabled
                };
            }

            return new AutomaticBackupOptions(
                isEnabled,
                NormalizeIntervalMinutes(
                    root.TryGetProperty("automaticBackupIntervalMinutes", out JsonElement interval) &&
                    interval.ValueKind == JsonValueKind.Number
                        ? interval.GetInt32()
                        : DefaultIntervalMinutes),
                NormalizeRetentionCount(
                    root.TryGetProperty("automaticBackupRetentionCount", out JsonElement retention) &&
                    retention.ValueKind == JsonValueKind.Number
                        ? retention.GetInt32()
                        : DefaultRetentionCount),
                NormalizeCustomDirectory(
                    root.TryGetProperty("automaticBackupDirectory", out JsonElement directory) &&
                    directory.ValueKind == JsonValueKind.String
                        ? directory.GetString()
                        : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return AutomaticBackupOptions.Default;
        }
    }

    internal static bool Normalize(AppSettings settings)
    {
        bool changed = false;

        int normalizedInterval = NormalizeIntervalMinutes(settings.AutomaticBackupIntervalMinutes);
        if (settings.AutomaticBackupIntervalMinutes != normalizedInterval)
        {
            settings.AutomaticBackupIntervalMinutes = normalizedInterval;
            changed = true;
        }

        int normalizedRetention = NormalizeRetentionCount(settings.AutomaticBackupRetentionCount);
        if (settings.AutomaticBackupRetentionCount != normalizedRetention)
        {
            settings.AutomaticBackupRetentionCount = normalizedRetention;
            changed = true;
        }

        string normalizedDirectory = NormalizeCustomDirectory(settings.AutomaticBackupDirectory) ?? string.Empty;
        if (!string.Equals(settings.AutomaticBackupDirectory, normalizedDirectory, StringComparison.Ordinal))
        {
            settings.AutomaticBackupDirectory = normalizedDirectory;
            changed = true;
        }

        return changed;
    }
}
