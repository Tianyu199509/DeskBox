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

    public bool HasPendingJournal => File.Exists(_journalPath);

    public async Task<DesktopOrganizationRecoveryJournal?> LoadAsync()
    {
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(_journalPath);
        return JsonSerializer.Deserialize(
            json,
            DesktopRecoveryJsonContext.Default.RecoveryJournal);
    }

    public Task SaveAsync(DesktopOrganizationRecoveryJournal journal) => Task.Run(() => Save(journal));

    // Called on the Shell STA between items so a resolved collision name is
    // durable before the next move. No UI dispatcher is involved.
    public void Save(DesktopOrganizationRecoveryJournal journal)
    {
        string? directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = $"{_journalPath}.tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal, DesktopRecoveryJsonContext.Default.RecoveryJournal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _journalPath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(_journalPath))
        {
            File.Delete(_journalPath);
        }
    }
}
