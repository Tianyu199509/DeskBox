using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Wire envelope for the settings-host IPC pipe (batch 3b of the
/// settings-process split): newline-delimited UTF-8 JSON over a duplex named
/// pipe. Payloads ride as JSON-or-plain strings so every command shape stays
/// source-generated at the envelope boundary and new methods need no wire
/// schema change.
/// </summary>
public sealed record SettingsIpcEnvelope(
    long Id,
    string Type,
    string Method,
    bool Ok,
    string? Payload,
    string? Error)
{
    public const string TypeRequest = "req";
    public const string TypeResponse = "res";
    public const string TypeEvent = "evt";
}

/// <summary>Method names on the settings-host pipe.</summary>
public static class SettingsIpcMethods
{
    /// <summary>Liveness probe; payload is ignored, response ok.</summary>
    public const string Ping = "ping";

    /// <summary>
    /// Settings host → main: apply a committed settings snapshot. Payload is
    /// "&lt;commitId&gt;\n&lt;settingsJson&gt;" (the commit id rides first so the
    /// response and the reload push can echo-suppress).
    /// </summary>
    public const string SettingsCommit = "settings-commit";

    /// <summary>
    /// Main → settings host: the durable settings changed on the main side;
    /// payload is "&lt;mainCommitId&gt;|&lt;hostCommitIdOrNull&gt;" (see
    /// <see cref="SettingsIpcReloadPayload"/>).
    /// </summary>
    public const string SettingsReloaded = "settings-reloaded";

    // ── Hotkey facade (batch 3c-1) ───────────────────────────────────
    public const string HotkeyState = "hotkey.state";
    public const string HotkeyApply = "hotkey.apply";
    public const string HotkeyReset = "hotkey.reset";
    public const string HotkeyRefreshRegistration = "hotkey.refresh-registration";
    public const string HotkeySetEnabled = "hotkey.set-enabled";
    public const string HotkeyGestureOwned = "hotkey.gesture-owned";
    public const string HotkeyDoubleClickSet = "hotkey.double-click-set";
    public const string HotkeySuspend = "hotkey.suspend";
    public const string HotkeyResume = "hotkey.resume";
    public const string HotkeyRegistrationChanged = "hotkey.registration-changed";

    // ── Data facade (batch 3c-1) ─────────────────────────────────────
    public const string DataQuickCaptureCounts = "data.qc-counts";
    public const string DataQuickCaptureClear = "data.qc-clear";
    public const string DataQuickCaptureClearRecent = "data.qc-clear-recent";
    public const string DataQuickCaptureCacheCleanup = "data.qc-cache-cleanup";
    public const string DataQuickCaptureCacheInfo = "data.qc-cache-info";
}

/// <summary>
/// Reload-push payload: the main commit that changed the file, plus the host
/// commit it applied (when the change came from a settings-commit) so the
/// host can echo-suppress its own writes.
/// </summary>
public static class SettingsIpcReloadPayload
{
    public static string Compose(long mainCommitId, long? hostCommitId) =>
        hostCommitId is { } host
            ? $"{mainCommitId}|{host}"
            : $"{mainCommitId}|";

    public static bool TryParse(string? payload, out long mainCommitId, out long? hostCommitId)
    {
        mainCommitId = 0;
        hostCommitId = null;
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        string[] parts = payload.Split('|');
        if (parts.Length != 2 || !long.TryParse(parts[0], out mainCommitId))
        {
            return false;
        }

        if (parts[1].Length > 0 && long.TryParse(parts[1], out long host))
        {
            hostCommitId = host;
        }

        return true;
    }
}

/// <summary>Two ints over the wire (Quick Capture counts).</summary>
public sealed record SettingsIpcIntPair(int A, int B);

/// <summary>Payload encode/decode helpers over the source-generated context.</summary>
public static class SettingsIpcPayload
{
    public static string Encode<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    public static T? Decode<T>(string? json, JsonTypeInfo<T> typeInfo) =>
        string.IsNullOrEmpty(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, typeInfo);

    public static T? DecodeStruct<T>(string? json, JsonTypeInfo<T> typeInfo) where T : struct =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<T>(json, typeInfo);

    public static string EncodeBoolean(bool value) => value ? "true" : "false";

    public static bool DecodeBoolean(string? payload) => payload == "true";
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsIpcEnvelope))]
[JsonSerializable(typeof(GlobalHotkeyRuntimeState))]
[JsonSerializable(typeof(DesktopDoubleClickToggleResult))]
[JsonSerializable(typeof(GlobalHotkeyActivation))]
[JsonSerializable(typeof(GlobalHotkeyGesture))]
[JsonSerializable(typeof(QuickCaptureImageCacheInfo))]
[JsonSerializable(typeof(QuickCaptureImageCacheCleanupResult))]
[JsonSerializable(typeof(SettingsIpcIntPair))]
internal sealed partial class SettingsIpcJsonContext : JsonSerializerContext;
