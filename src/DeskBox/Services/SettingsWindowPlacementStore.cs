using System.Text;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public readonly record struct SettingsWindowPlacement(
    int X,
    int Y,
    int WidthDip,
    int HeightDip,
    bool IsMaximized);

public readonly record struct SettingsWindowWorkArea(
    int X,
    int Y,
    int Width,
    int Height);

public readonly record struct SettingsWindowResolvedBounds(
    int X,
    int Y,
    int WidthPx,
    int HeightPx,
    bool IsMaximized);

/// <summary>
/// Persists the Settings window's machine-local normal bounds (physical origin +
/// DIP size) and maximized state under <c>data/settings-window-placement.json</c>.
/// Stored outside <c>settings.json</c> so cross-machine backups never carry
/// monitor-specific window coordinates and the frozen AppSettings wire schema
/// remains untouched.
/// </summary>
public sealed class SettingsWindowPlacementStore
{
    public const string FileName = "settings-window-placement.json";

    public static SettingsWindowPlacementStore Shared { get; } = new();

    private readonly object _gate = new();
    private readonly string _filePath;

    public SettingsWindowPlacementStore(string? customFilePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(customFilePath)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, FileName)
            : customFilePath;
    }

    public string FilePath => _filePath;

    public bool TryLoad(out SettingsWindowPlacement placement)
    {
        lock (_gate)
        {
            placement = default;
            try
            {
                if (!File.Exists(_filePath))
                {
                    return false;
                }

                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return false;
                }

                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!TryReadInt32(root, "x", out int x) ||
                    !TryReadInt32(root, "y", out int y) ||
                    !TryReadInt32(root, "widthDip", out int widthDip) ||
                    !TryReadInt32(root, "heightDip", out int heightDip))
                {
                    return false;
                }

                if (widthDip <= 0 || heightDip <= 0 || x <= -30000 || y <= -30000)
                {
                    return false;
                }

                bool isMaximized = root.TryGetProperty("isMaximized", out JsonElement maxEl) &&
                                   maxEl.ValueKind == JsonValueKind.True;

                placement = new SettingsWindowPlacement(x, y, widthDip, heightDip, isMaximized);
                return true;
            }
            catch
            {
                placement = default;
                return false;
            }
        }
    }

    public void Save(SettingsWindowPlacement placement)
    {
        if (placement.WidthDip <= 0 ||
            placement.HeightDip <= 0 ||
            placement.X <= -30000 ||
            placement.Y <= -30000)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("x", placement.X);
                    writer.WriteNumber("y", placement.Y);
                    writer.WriteNumber("widthDip", placement.WidthDip);
                    writer.WriteNumber("heightDip", placement.HeightDip);
                    writer.WriteBoolean("isMaximized", placement.IsMaximized);
                    writer.WriteEndObject();
                }

                string content = Encoding.UTF8.GetString(stream.ToArray());
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_filePath, content);
            }
            catch
            {
                // Non-fatal local window placement write failure.
            }
        }
    }

    public static SettingsWindowResolvedBounds ResolveInitialBounds(
        SettingsWindowPlacement? savedPlacement,
        SettingsWindowWorkArea workArea,
        double dpiScale,
        int defaultWidthDip,
        int defaultHeightDip,
        int minWidthDip,
        int minHeightDip,
        int workAreaMarginDip)
    {
        double scale = NormalizeDpiScale(dpiScale);
        int safeWorkWidth = Math.Max(1, workArea.Width);
        int safeWorkHeight = Math.Max(1, workArea.Height);
        int minWidthPx = Math.Min(safeWorkWidth, ToPhysicalPixels(minWidthDip, scale));
        int minHeightPx = Math.Min(safeWorkHeight, ToPhysicalPixels(minHeightDip, scale));

        if (savedPlacement is { WidthDip: > 0, HeightDip: > 0 } saved &&
            saved.X > -30000 &&
            saved.Y > -30000)
        {
            int desiredWidthPx = ToPhysicalPixels(saved.WidthDip, scale);
            int desiredHeightPx = ToPhysicalPixels(saved.HeightDip, scale);
            int widthPx = Math.Clamp(desiredWidthPx, minWidthPx, safeWorkWidth);
            int heightPx = Math.Clamp(desiredHeightPx, minHeightPx, safeWorkHeight);
            int maxLeft = Math.Max(workArea.X, workArea.X + safeWorkWidth - widthPx);
            int maxTop = Math.Max(workArea.Y, workArea.Y + safeWorkHeight - heightPx);
            int clampedX = Math.Clamp(saved.X, workArea.X, maxLeft);
            int clampedY = Math.Clamp(saved.Y, workArea.Y, maxTop);
            return new SettingsWindowResolvedBounds(clampedX, clampedY, widthPx, heightPx, saved.IsMaximized);
        }

        int defaultWidthPx = ToPhysicalPixels(defaultWidthDip, scale);
        int defaultHeightPx = ToPhysicalPixels(defaultHeightDip, scale);
        int workMarginPx = ToPhysicalPixels(workAreaMarginDip, scale);
        int initialMaxWidthPx = Math.Max(minWidthPx, safeWorkWidth - workMarginPx);
        int initialMaxHeightPx = Math.Max(minHeightPx, safeWorkHeight - workMarginPx);
        int centeredWidthPx = Math.Clamp(defaultWidthPx, minWidthPx, initialMaxWidthPx);
        int centeredHeightPx = Math.Clamp(defaultHeightPx, minHeightPx, initialMaxHeightPx);
        int centeredX = workArea.X + Math.Max(0, (safeWorkWidth - centeredWidthPx) / 2);
        int centeredY = workArea.Y + Math.Max(0, (safeWorkHeight - centeredHeightPx) / 2);
        return new SettingsWindowResolvedBounds(centeredX, centeredY, centeredWidthPx, centeredHeightPx, false);
    }

    internal static double NormalizeDpiScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1.0;

    internal static int ToPhysicalPixels(int logicalPixels, double scale) =>
        Math.Max(1, (int)Math.Round(logicalPixels * NormalizeDpiScale(scale), MidpointRounding.AwayFromZero));

    private static bool TryReadInt32(JsonElement root, string propertyName, out int value)
    {
        if (root.TryGetProperty(propertyName, out JsonElement prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
