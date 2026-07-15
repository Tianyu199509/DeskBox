using System.Text.Json.Serialization;

namespace DeskBox.Models;

public sealed class TodoAttachment
{
    public const string LinkedStorageMode = "linked";
    public const string ManagedStorageMode = "managed";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FilePath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Type { get; set; } = "file";

    public string StorageMode { get; set; } = LinkedStorageMode;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsManagedCopy => string.Equals(StorageMode, ManagedStorageMode, StringComparison.Ordinal);

    public static string NormalizeStorageMode(string? storageMode)
    {
        return string.Equals(storageMode, ManagedStorageMode, StringComparison.OrdinalIgnoreCase)
            ? ManagedStorageMode
            : LinkedStorageMode;
    }

    public TodoAttachment Clone()
    {
        return new TodoAttachment
        {
            Id = Id,
            FilePath = FilePath,
            DisplayName = DisplayName,
            Type = Type,
            StorageMode = StorageMode,
            AddedAt = AddedAt
        };
    }
}
