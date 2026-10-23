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

    // ── Restore facade (batch 3c-2) ─────────────────────────────────
    public const string RestorePrepare = "restore.prepare";
    public const string RestoreConfirm = "restore.confirm";
    public const string RestoreCancel = "restore.cancel";
    public const string RestoreInventory = "restore.inventory";
    public const string RestoreDeleteSnapshot = "restore.delete-snapshot";
    public const string RestoreAttachmentHealth = "restore.attachment-health";

    // ── Widget facade (batch 3c-2) ──────────────────────────────────
    public const string WidgetIsFeatureEnabled = "widget.is-feature-enabled";
    public const string WidgetSyncFeatureEnabled = "widget.sync-feature-enabled";
    public const string WidgetResetFeature = "widget.reset-feature";
    public const string WidgetRefreshLayers = "widget.refresh-layers";
    public const string WidgetNotifyGroupPresentation = "widget.notify-group-presentation";
    public const string WidgetReorderGroupMember = "widget.reorder-group-member";
    public const string WidgetRemoveFromGroup = "widget.remove-from-group";
    public const string WidgetDissolveGroup = "widget.dissolve-group";
    public const string WidgetGetOrphans = "widget.get-orphans";
    public const string WidgetRestoreOrphans = "widget.restore-orphans";
    public const string WidgetMoveOrphanToDesktop = "widget.move-orphan-to-desktop";
    public const string WidgetDeleteOrphan = "widget.delete-orphan";
    public const string WidgetManagedCount = "widget.managed-count";
    public const string WidgetMigratePreview = "widget.migrate-preview";
    public const string WidgetMigrateRoot = "widget.migrate-root";
    public const string WidgetMigrationProgress = "widget.migration-progress";
    public const string WidgetGlanceList = "widget.glance-list";
    public const string WidgetGlanceFeatureEnabled = "widget.glance-feature-enabled";
    public const string WidgetGlanceCreate = "widget.glance-create";
    public const string WidgetGlanceSetEnabled = "widget.glance-set-enabled";
    public const string WidgetGlanceLocate = "widget.glance-locate";
    public const string WidgetGlanceRemove = "widget.glance-remove";
    public const string WidgetGlanceRename = "widget.glance-rename";
    public const string WidgetMoveAll = "widget.move-all";
    public const string WidgetMoveAllUndo = "widget.move-all-undo";
    public const string WidgetApplyScreenBinding = "widget.apply-screen-binding";
    public const string WidgetFolderWatcherHealth = "widget.folder-watcher-health";
    public const string WidgetLatestUndoableOrgEntry = "widget.latest-undoable-org-entry";
    public const string WidgetOrgHistoryEntry = "widget.org-history-entry";
    public const string WidgetOrgUndo = "widget.org-undo";
    public const string WidgetOrgAbandon = "widget.org-abandon";

    // ── App facade (batch 3c-2) ─────────────────────────────────────
    public const string AppShutdownForRestart = "app.shutdown-for-restart";
    public const string AppShutdownForUpdate = "app.shutdown-for-update";
    public const string AppScheduleRestart = "app.schedule-restart";
    public const string AppRelaunchAsExplorerUser = "app.relaunch-as-explorer-user";
    public const string AppShowOnboarding = "app.show-onboarding";
    public const string AppShowDesktopOrgWindow = "app.show-desktop-org-window";
    public const string AppOpenReleaseNotes = "app.open-release-notes";
    public const string AppUpdateTrayIcon = "app.update-tray-icon";
    public const string AppSetSnapEnabled = "app.set-snap-enabled";
    public const string AppSetSnapSpacing = "app.set-snap-spacing";
    public const string AppScheduleLightCleanup = "app.schedule-light-cleanup";
    public const string AppScheduleHiddenCleanup = "app.schedule-hidden-cleanup";
    public const string AppRaiseBand = "app.raise-band";
    public const string AppReleaseBand = "app.release-band";
    public const string AppHasShortcut = "app.has-shortcut";
    public const string AppCreateShortcut = "app.create-shortcut";
    public const string AppRemoveShortcut = "app.remove-shortcut";
    public const string AppSyncShortcut = "app.sync-shortcut";
    public const string AppExportDiagnostics = "app.export-diagnostics";
    public const string AppUploadDiagnostics = "app.upload-diagnostics";
    public const string AppRuntimeHealth = "app.runtime-health";
    public const string AppForceExternalRecovery = "app.force-external-recovery";
    public const string AppDiagnoseDragDrop = "app.diagnose-drag-drop";
    public const string AppRepairDragDrop = "app.repair-drag-drop";
    public const string AppDisplayTopologyChanged = "app.display-topology-changed";
    public const string AppScreenHomeChanged = "app.screen-home-changed";
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

// ── Batch 3c-2 wire shapes ─────────────────────────────────────────

public sealed record SettingsIpcStringPair(string A, string B);

public sealed record SettingsIpcKindArg(int Kind, bool Enabled);

public sealed record SettingsIpcOrphanDelete(string Path, long OwnerHwnd);

public sealed record SettingsIpcOrgUndo(string HistoryId, long OwnerHwnd);

public sealed record SettingsIpcScreenBinding(string WidgetId, int Mode, string? BoundScreenId, bool Persist);

public sealed record SettingsIpcGlanceSetEnabled(string WidgetId, bool Enabled);

public sealed record SettingsIpcGlanceRename(string WidgetId, string NewName);

public sealed record SettingsIpcGlanceCreate(string? SourceWidgetId);

public sealed record SettingsIpcMoveAllResult(int MovedCount, long Handle);

public sealed record SettingsIpcMigrationRequest(string NewRootPath, bool RestartIfCopiesRemoved);

public sealed record SettingsIpcReleaseNotes(
    string? ManifestJson,
    string Version,
    bool AllowLazyFetch,
    long OwnerHwnd);

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
[JsonSerializable(typeof(SettingsIpcStringPair))]
[JsonSerializable(typeof(SettingsIpcKindArg))]
[JsonSerializable(typeof(SettingsIpcOrphanDelete))]
[JsonSerializable(typeof(SettingsIpcOrgUndo))]
[JsonSerializable(typeof(SettingsIpcScreenBinding))]
[JsonSerializable(typeof(SettingsIpcGlanceSetEnabled))]
[JsonSerializable(typeof(SettingsIpcGlanceRename))]
[JsonSerializable(typeof(SettingsIpcGlanceCreate))]
[JsonSerializable(typeof(SettingsIpcMoveAllResult))]
[JsonSerializable(typeof(SettingsIpcMigrationRequest))]
[JsonSerializable(typeof(SettingsIpcReleaseNotes))]
[JsonSerializable(typeof(DeskBoxRestorePreparation))]
[JsonSerializable(typeof(DeskBoxBackupSnapshotInfo))]
[JsonSerializable(typeof(List<DeskBoxBackupSnapshotInfo>))]
[JsonSerializable(typeof(DeskBoxAttachmentHealthReport))]
[JsonSerializable(typeof(ManagedStorageFolderCleanupCandidate))]
[JsonSerializable(typeof(List<ManagedStorageFolderCleanupCandidate>))]
[JsonSerializable(typeof(ManagedStorageMigrationPreview))]
[JsonSerializable(typeof(ManagedStorageMigrationResult))]
[JsonSerializable(typeof(ManagedStorageMigrationProgress))]
[JsonSerializable(typeof(GlanceWidgetInstanceInfo))]
[JsonSerializable(typeof(List<GlanceWidgetInstanceInfo>))]
[JsonSerializable(typeof(FolderWatcherHealthSnapshot))]
[JsonSerializable(typeof(List<FolderWatcherHealthSnapshot>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(OrganizationHistoryEntry))]
[JsonSerializable(typeof(DesktopOrganizationUndoResult))]
[JsonSerializable(typeof(AppRelaunchScheduleResult))]
[JsonSerializable(typeof(AppRuntimeHealthSnapshot))]
[JsonSerializable(typeof(AppUpdateManifest))]
[JsonSerializable(typeof(DragDropPermissionDiagnostic))]
[JsonSerializable(typeof(DragDropPermissionRepairResult))]
internal sealed partial class SettingsIpcJsonContext : JsonSerializerContext;
