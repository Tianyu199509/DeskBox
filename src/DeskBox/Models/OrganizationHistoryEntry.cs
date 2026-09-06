using System.Text.Json.Serialization;
using DeskBox.Services;

namespace DeskBox.Models;

public class OrganizationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public string WidgetId { get; set; } = string.Empty;

    public string WidgetName { get; set; } = string.Empty;

    public string ActionType { get; set; } = OrganizationActionType.ManagedDrop;

    public string TransferMode { get; set; } = "Move";

    public bool CanUndo { get; set; }

    public bool IsUndone { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UndoStarted { get; set; }

    public string? ErrorMessage { get; set; }

    public List<OrganizationHistoryItem> Items { get; set; } = [];

    /// <summary>All widget destinations participating in a desktop batch.</summary>
    public List<OrganizationHistoryTarget> Targets { get; set; } = [];

    [JsonIgnore]
    public bool IsFailed => !string.IsNullOrWhiteSpace(ErrorMessage);

    [JsonIgnore]
    public int ItemCount => Items.Count;

    [JsonIgnore]
    public string DisplayTitle => ActionType switch
    {
        OrganizationActionType.ManagedDrop => TransferMode == "Copy"
            ? Localize("History.Title.CopyToManaged")
            : Localize("History.Title.MoveToManaged"),
        OrganizationActionType.MoveBackToDesktop => Localize("History.Title.MoveBackToDesktop"),
        _ => Localize("History.Title.DesktopOrganization")
    };

    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            string target = string.IsNullOrWhiteSpace(WidgetName) ? Localize("History.CurrentWidget") : WidgetName;
            string itemLabel = LocalizeFormat("FileInfo.FolderItems", ItemCount);

            if (IsFailed)
            {
                return $"{target} · {itemLabel} · {Localize("Common.Failed")}";
            }

            if (IsUndone)
            {
                return $"{target} · {itemLabel} · {Localize("Common.Undone")}";
            }

            return $"{target} · {itemLabel}";
        }
    }

    [JsonIgnore]
    public string DisplayTimeText => TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm");

    [JsonIgnore]
    public string DisplayDetail
    {
        get
        {
            if (IsFailed)
            {
                return ErrorMessage ?? Localize("History.OperationFailed");
            }

            if (Items.Count == 0)
            {
                return Localize("History.NoItems");
            }

            var firstItem = Items[0];
            if (Items.Count == 1)
            {
                return firstItem.Name;
            }

            return LocalizeFormat("History.ItemSummary", firstItem.Name, Items.Count);
        }
    }

    [JsonIgnore]
    public string UndoButtonText => ActionType switch
    {
        OrganizationActionType.MoveBackToDesktop => Localize("History.UndoMoveBackToDesktop"),
        _ => Localize("History.UndoLastMove")
    };

    private static string Localize(string key)
    {
        return TryGetLocalizationService()?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private static string LocalizeFormat(string key, params object[] args)
    {
        return TryGetLocalizationService()?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static LocalizationService? TryGetLocalizationService()
    {
        try
        {
            return global::DeskBox.App.Current?.LocalizationService;
        }
        catch
        {
            return null;
        }
    }
}

public class OrganizationHistoryItem
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DesktopOrganizationSourceScope SourceScope { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRestored { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RestoredPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastWriteTimeUtc { get; set; }

    public string Name { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string DestinationPath { get; set; } = string.Empty;

    public string TargetWidgetId { get; set; } = string.Empty;

    public string TargetWidgetName { get; set; } = string.Empty;
}

public sealed class OrganizationHistoryTarget
{
    public string WidgetId { get; set; } = string.Empty;

    public string WidgetName { get; set; } = string.Empty;

    public string DirectoryPath { get; set; } = string.Empty;

    public bool WasCreated { get; set; }
}

public static class OrganizationActionType
{
    public const string ManagedDrop = "ManagedDrop";
    public const string MoveBackToDesktop = "MoveBackToDesktop";
    public const string DesktopOrganization = "DesktopOrganization";
}
