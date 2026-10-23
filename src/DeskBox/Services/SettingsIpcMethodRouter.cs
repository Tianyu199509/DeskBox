using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Main-side method router for the settings-host pipe (batch 3c-1): maps
/// wire methods onto the in-process facade implementations, so the settings
/// host's remote facades talk to the exact same behavior the in-process
/// settings window uses. Also remembers the host commit id of the last
/// applied external snapshot so the reload push can echo-suppress it.
/// </summary>
public sealed class SettingsIpcMethodRouter
{
    private readonly SettingsService? _settings;
    private readonly ISettingsHotkeyActions? _hotkey;
    private readonly ISettingsDataActions? _data;

    public SettingsIpcMethodRouter(
        SettingsService? settings,
        ISettingsHotkeyActions? hotkey,
        ISettingsDataActions? data)
    {
        _settings = settings;
        _hotkey = hotkey;
        _data = data;
    }

    /// <summary>
    /// The host commit id of the last settings-commit applied through this
    /// router (0 = the last durable change was not host-initiated).
    /// </summary>
    public long LastAppliedHostCommitId { get; private set; }

    public async Task<(bool Ok, string? Payload, string? Error)> DispatchAsync(
        string method,
        string? payload,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case SettingsIpcMethods.Ping:
                return (true, "pong", null);

            case SettingsIpcMethods.SettingsCommit:
                if (_settings is null)
                {
                    return (false, null, "settings service unavailable");
                }

                (bool ok, string? resultPayload, string? error) =
                    await SettingsIpcDispatch.SettingsCommitAsync(_settings, payload);
                if (ok && long.TryParse(resultPayload, out long hostCommitId))
                {
                    LastAppliedHostCommitId = hostCommitId;
                }

                return (ok, resultPayload, error);

            case SettingsIpcMethods.HotkeyState:
                if (_hotkey is null) return unavailable(method);
                return (true, SerializeNullable(_hotkey.GetRuntimeState(),
                    SettingsIpcJsonContext.Default.GlobalHotkeyRuntimeState), null);

            case SettingsIpcMethods.HotkeyApply:
                if (_hotkey is null) return unavailable(method);
                if (SettingsIpcPayload.DecodeStruct(payload, SettingsIpcJsonContext.Default.GlobalHotkeyActivation)
                    is not { } activation)
                {
                    return (false, null, "missing activation payload");
                }

                bool applied = _hotkey.TryApplyActivation(activation, out string? applyError);
                return (applied, null, applyError);

            case SettingsIpcMethods.HotkeyReset:
                if (_hotkey is null) return unavailable(method);
                bool reset = _hotkey.ResetToDefault(out string? resetError);
                return (reset, null, resetError);

            case SettingsIpcMethods.HotkeyRefreshRegistration:
                if (_hotkey is null) return unavailable(method);
                _hotkey.RefreshRegistration();
                return (true, null, null);

            case SettingsIpcMethods.HotkeySetEnabled:
                if (_hotkey is null) return unavailable(method);
                _hotkey.SetEnabled(SettingsIpcPayload.DecodeBoolean(payload));
                return (true, null, null);

            case SettingsIpcMethods.HotkeyGestureOwned:
                if (_hotkey is null) return unavailable(method);
                if (SettingsIpcPayload.DecodeStruct(payload, SettingsIpcJsonContext.Default.GlobalHotkeyGesture)
                    is not { } gesture)
                {
                    return (false, null, "missing gesture payload");
                }

                return (true, SettingsIpcPayload.EncodeBoolean(_hotkey.IsGestureOwnedBySearchHotkey(gesture)), null);

            case SettingsIpcMethods.HotkeyDoubleClickSet:
                if (_hotkey is null) return unavailable(method);
                DesktopDoubleClickToggleResult toggle =
                    _hotkey.TrySetDesktopDoubleClickEnabled(SettingsIpcPayload.DecodeBoolean(payload));
                return (true, SettingsIpcPayload.Encode(
                    toggle,
                    SettingsIpcJsonContext.Default.DesktopDoubleClickToggleResult), null);

            case SettingsIpcMethods.HotkeySuspend:
                if (_hotkey is null) return unavailable(method);
                _hotkey.SuspendHotkeysForRecording();
                return (true, null, null);

            case SettingsIpcMethods.HotkeyResume:
                if (_hotkey is null) return unavailable(method);
                _hotkey.ResumeHotkeysAfterRecording();
                return (true, null, null);

            case SettingsIpcMethods.DataQuickCaptureCounts:
                if (_data is null) return unavailable(method);
                (int active, int recent) = await _data.GetQuickCaptureCountsAsync();
                return (true, SettingsIpcPayload.Encode(
                    new SettingsIpcIntPair(active, recent),
                    SettingsIpcJsonContext.Default.SettingsIpcIntPair), null);

            case SettingsIpcMethods.DataQuickCaptureClear:
                if (_data is null) return unavailable(method);
                await _data.ClearQuickCaptureDataAsync();
                return (true, null, null);

            case SettingsIpcMethods.DataQuickCaptureClearRecent:
                if (_data is null) return unavailable(method);
                await _data.ClearQuickCaptureRecentAsync();
                return (true, null, null);

            case SettingsIpcMethods.DataQuickCaptureCacheCleanup:
                if (_data is null) return unavailable(method);
                QuickCaptureImageCacheCleanupResult cleanup =
                    await _data.ClearUnusedQuickCaptureImageCacheAsync();
                return (true, SerializeNullable(cleanup,
                    SettingsIpcJsonContext.Default.QuickCaptureImageCacheCleanupResult), null);

            case SettingsIpcMethods.DataQuickCaptureCacheInfo:
                if (_data is null) return unavailable(method);
                QuickCaptureImageCacheInfo? info = await _data.GetQuickCaptureImageCacheInfoAsync();
                return (true, SerializeNullable(info,
                    SettingsIpcJsonContext.Default.QuickCaptureImageCacheInfo), null);

            default:
                return (false, null, $"unknown method {method}");
        }
    }

    private static (bool, string?, string?) unavailable(string method) =>
        (false, null, $"{method}: host service unavailable");

    private static string? SerializeNullable<T>(T? value, JsonTypeInfo<T> typeInfo) where T : class =>
        value is null ? null : SettingsIpcPayload.Encode(value, typeInfo);
}
