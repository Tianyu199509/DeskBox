using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.FileSafety;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(DesktopOrganizationHistoryData),
    TypeInfoPropertyName = "DesktopOrganizationHistoryData")]
internal sealed partial class DesktopOrganizationHistoryJsonContext : JsonSerializerContext
{
}

internal sealed class DesktopOrganizationHistoryData
{
    public List<OrganizationHistoryEntry> Items { get; set; } = [];
}

/// <summary>
/// FileSafety-domain store for desktop-organization undo receipts
/// (<see cref="OrganizationHistoryEntry"/>). This is local-layer data:
/// machine-local transaction state that must never join the sync layer.
/// Owns <c>desktop-organization-history.json</c> beside the recovery
/// journal and adopts the legacy settings.json recentOrganizationHistory
/// list on first load (fail-closed: the settings copy is only cleared
/// after this file is authoritative).
/// </summary>
public sealed class DesktopOrganizationHistoryStore
{
    private readonly string _historyPath;

    public DesktopOrganizationHistoryStore(string? historyPath = null)
    {
        _historyPath = historyPath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "desktop-organization-history.json");
    }

    /// <summary>
    /// Live receipt list. Callers mutate it in place (insert/remove/cap) or
    /// replace it wholesale on transaction rollback — the same contract the
    /// settings list used to have. Persist mutations via
    /// <see cref="SaveAsync"/>/<see cref="SaveCheckedAsync"/>.
    /// </summary>
    public List<OrganizationHistoryEntry> Entries { get; set; } = [];

    /// <summary>
    /// Loads the durable store. When the history file exists it wins — the
    /// legacy settings copy is stale by definition. When it does not, the
    /// legacy seed is adopted, normalized, and written BEFORE the caller may
    /// clear the settings list, so the receipt data survives every crash
    /// window of the migration itself.
    /// </summary>
    /// <returns>
    /// True when this store is authoritative for the session (file loaded,
    /// migration written, or nothing to migrate). False when the migration
    /// write failed — the caller must keep the legacy list so a later
    /// launch retries the adoption instead of losing the receipts.
    /// </returns>
    public async Task<bool> LoadAsync(IReadOnlyList<OrganizationHistoryEntry>? legacySeed = null)
    {
        // ResilientJsonStore owns the corruption protocol: an unreadable
        // primary is quarantined as .corrupt-<timestamp>, the .bak backup is
        // tried next and restored as primary, and only a total miss falls
        // through — the same protection settings.json had when the receipts
        // lived inside it.
        ResilientJsonLoadResult<DesktopOrganizationHistoryData> result =
            await ResilientJsonStore.LoadWithResultAsync(
                _historyPath,
                static json => JsonSerializer.Deserialize(
                    json,
                    DesktopOrganizationHistoryJsonContext.Default.DesktopOrganizationHistoryData)
                    ?? new DesktopOrganizationHistoryData(),
                static () => new DesktopOrganizationHistoryData(),
                "DesktopOrganizationHistory");

        if (result.Source is ResilientJsonLoadSource.Primary or ResilientJsonLoadSource.Backup)
        {
            bool normalized;
            (Entries, normalized) = Normalize(result.Value.Items ?? []);
            if (normalized)
            {
                await SaveCheckedAsync();
            }

            return true;
        }

        (Entries, _) = Normalize(legacySeed ?? []);
        return Entries.Count == 0 || await SaveCheckedAsync();
    }

    /// <summary>
    /// Persists via <see cref="ResilientJsonStore"/>: unique temp file, atomic
    /// replace, verified <c>.bak</c> backup, and the in-place fallback — the
    /// same commit protocol settings.json had.
    /// </summary>
    public Task SaveAsync() =>
        ResilientJsonStore.SaveAsync(_historyPath, WriteTempFileAsync);


    /// <summary>
    /// Same load-bearing semantics as <c>SettingsService.SaveCheckedAsync</c>:
    /// a silent failure must never let a caller clear the recovery journal
    /// with no durable commit anywhere.
    /// </summary>
    public async Task<bool> SaveCheckedAsync()
    {
        try
        {
            await SaveAsync();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[DesktopOrganization] History save failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Rebases persisted target and receipt paths for <paramref name="widgetId"/> when its
    /// managed folder is renamed or migrated to a new storage root. When
    /// <paramref name="refreshCopiedDestinationIdentities"/> is <see langword="true"/>
    /// (storage copy-migration where untouched originals still match the undo receipt),
    /// refreshes <see cref="OrganizationHistoryItem.DestinationIdentity"/> to the verified
    /// copied file at <paramref name="newFolderPath"/> so Undo preserves identity authority.
    /// </summary>
    public bool RebaseWidgetDirectory(
        string widgetId,
        string? oldFolderPath,
        string? newFolderPath,
        bool refreshCopiedDestinationIdentities = false)
    {
        if (string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(oldFolderPath) ||
            string.IsNullOrWhiteSpace(newFolderPath))
        {
            return false;
        }

        string oldRoot;
        string newRoot;
        try
        {
            oldRoot = Path.GetFullPath(oldFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            newRoot = Path.GetFullPath(newFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool changed = false;
        foreach (OrganizationHistoryEntry entry in Entries)
        {
            if (entry.Targets is not null)
            {
                foreach (OrganizationHistoryTarget target in entry.Targets)
                {
                    if (!string.Equals(target.WidgetId, widgetId, StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(target.DirectoryPath))
                    {
                        continue;
                    }

                    string rebasedDir = WidgetPathRebaseHelper.RebasePath(
                        target.DirectoryPath,
                        oldRoot,
                        newRoot);
                    if (!string.Equals(target.DirectoryPath, rebasedDir, StringComparison.Ordinal))
                    {
                        target.DirectoryPath = rebasedDir;
                        changed = true;
                    }
                }
            }

            if (entry.Items is null)
            {
                continue;
            }

            bool isMoveBackToDesktop = string.Equals(
                entry.ActionType,
                OrganizationActionType.MoveBackToDesktop,
                StringComparison.Ordinal);

            foreach (OrganizationHistoryItem item in entry.Items)
            {
                string effectiveWidgetId = string.IsNullOrWhiteSpace(item.TargetWidgetId)
                    ? entry.WidgetId
                    : item.TargetWidgetId;
                if (!string.Equals(effectiveWidgetId, widgetId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (isMoveBackToDesktop && !string.IsNullOrWhiteSpace(item.SourcePath))
                {
                    string rebasedSource = WidgetPathRebaseHelper.RebasePath(
                        item.SourcePath,
                        oldRoot,
                        newRoot);
                    if (!string.Equals(item.SourcePath, rebasedSource, StringComparison.Ordinal))
                    {
                        item.SourcePath = rebasedSource;
                        changed = true;
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.DestinationPath))
                {
                    string oldDestination = item.DestinationPath;
                    string rebasedDestination = WidgetPathRebaseHelper.RebasePath(
                        oldDestination,
                        oldRoot,
                        newRoot);
                    if (!string.Equals(oldDestination, rebasedDestination, StringComparison.Ordinal))
                    {
                        if (refreshCopiedDestinationIdentities &&
                            !item.IsRestored &&
                            item.DestinationIdentity is not null &&
                            FileService.UndoReceiptStillMatches(oldDestination, item.DestinationIdentity) &&
                            (File.Exists(rebasedDestination) || Directory.Exists(rebasedDestination)))
                        {
                            item.DestinationIdentity = FileService.CaptureUndoReceiptIdentity(rebasedDestination);
                        }

                        item.DestinationPath = rebasedDestination;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private async Task WriteTempFileAsync(string temporaryPath)
    {
        await using var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(
            stream,
            new DesktopOrganizationHistoryData { Items = Entries },
            DesktopOrganizationHistoryJsonContext.Default.DesktopOrganizationHistoryData);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Sort + field defaults only. The entry cap deliberately stays out: it
    /// belongs to <see cref="OrganizationHistoryPolicy"/>, which knows about
    /// active undos and journal-protected entries. Capping during load —
    /// before startup recovery — could trim an entry whose receipts are
    /// still transaction state.
    /// </summary>
    internal static (List<OrganizationHistoryEntry> Entries, bool Changed) Normalize(
        IEnumerable<OrganizationHistoryEntry> source)
    {
        bool changed = false;
        List<OrganizationHistoryEntry> entries = source
            .Where(entry => entry is not null)
            .OrderByDescending(entry => entry.TimestampUtc)
            .ToList();

        foreach (OrganizationHistoryEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString();
                changed = true;
            }

            entry.WidgetId ??= string.Empty;
            entry.WidgetName ??= string.Empty;
            entry.ActionType = string.IsNullOrWhiteSpace(entry.ActionType)
                ? OrganizationActionType.ManagedDrop
                : entry.ActionType;
            entry.TransferMode = entry.TransferMode is "Move" or "Copy"
                ? entry.TransferMode
                : SettingsService.ManagedDropActionMove;
            entry.Items ??= [];
            entry.Targets ??= [];
            foreach (OrganizationHistoryItem item in entry.Items)
            {
                item.TargetWidgetId ??= string.Empty;
                item.TargetWidgetName ??= string.Empty;
            }
        }

        return (entries, changed);
    }
}
