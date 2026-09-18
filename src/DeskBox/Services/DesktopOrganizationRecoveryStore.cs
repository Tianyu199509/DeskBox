using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(DesktopOrganizationRecoveryJournal),
    TypeInfoPropertyName = "RecoveryJournal")]
internal sealed partial class DesktopRecoveryJsonContext : JsonSerializerContext
{
}

public sealed class DesktopOrganizationRecoveryStore
{
    private readonly string _journalPath;

    public DesktopOrganizationRecoveryStore(string? journalPath = null)
    {
        _journalPath = journalPath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "desktop-organization-recovery.json");
    }

    // A surviving .bak is itself a valid recovery source (LoadAsync would
    // resurrect it), so a backup-only journal still counts as pending.
    public bool HasPendingJournal =>
        File.Exists(_journalPath) ||
        File.Exists(ResilientJsonStore.GetBackupPath(_journalPath));

    public async Task<DesktopOrganizationRecoveryJournal?> LoadAsync()
    {
        // ResilientJsonStore owns the corruption protocol: an unreadable
        // primary is quarantined as .corrupt-<timestamp>, the .bak backup is
        // tried next and restored as the new primary. A total miss reads as
        // "no pending journal" so callers keep their null branches.
        ResilientJsonLoadResult<DesktopOrganizationRecoveryJournal?> result =
            await ResilientJsonStore.LoadWithResultAsync<DesktopOrganizationRecoveryJournal?>(
                _journalPath,
                static json => JsonSerializer.Deserialize(
                    json,
                    DesktopRecoveryJsonContext.Default.RecoveryJournal),
                static () => null,
                "DesktopOrganizationRecovery");
        return result.Value;
    }

    public Task SaveAsync(DesktopOrganizationRecoveryJournal journal) =>
        ResilientJsonStore.SaveAsync(_journalPath, tempPath => WriteTempFileAsync(tempPath, journal));

    // Called on the Shell STA between items so a resolved collision name is
    // durable before the next move. The async write protocol runs on a pool
    // thread so the synchronous wait cannot deadlock on the STA sync context.
    public void Save(DesktopOrganizationRecoveryJournal journal) =>
        Task.Run(() => SaveAsync(journal)).GetAwaiter().GetResult();

    private static async Task WriteTempFileAsync(
        string temporaryPath,
        DesktopOrganizationRecoveryJournal journal)
    {
        await using var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(
            stream, journal, DesktopRecoveryJsonContext.Default.RecoveryJournal);
        stream.Flush(flushToDisk: true);
    }

    public void Clear()
    {
        // The backup must die BEFORE the primary: a crash between the two
        // deletes must leave the primary (the most recent journal state, e.g.
        // IsAbandoned) as the only recoverable copy. Deleting primary first
        // would let a stale .bak resurrect an older, pre-finalize journal.
        string backupPath = ResilientJsonStore.GetBackupPath(_journalPath);
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        if (File.Exists(_journalPath))
        {
            File.Delete(_journalPath);
        }
    }
}
