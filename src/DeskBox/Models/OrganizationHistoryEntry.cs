using System.Text.Json.Serialization;

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

    public string? ErrorMessage { get; set; }

    public List<OrganizationHistoryItem> Items { get; set; } = [];

    [JsonIgnore]
    public bool IsFailed => !string.IsNullOrWhiteSpace(ErrorMessage);

    [JsonIgnore]
    public int ItemCount => Items.Count;

    [JsonIgnore]
    public string DisplayTitle => ActionType switch
    {
        OrganizationActionType.ManagedDrop => TransferMode == "Copy" ? "复制到收纳组件" : "移动到收纳组件",
        OrganizationActionType.MoveBackToDesktop => "移回桌面",
        OrganizationActionType.EnableManagedStorage => "启用收纳模式",
        _ => "桌面整理"
    };

    [JsonIgnore]
    public string DisplaySubtitle
    {
        get
        {
            string target = string.IsNullOrWhiteSpace(WidgetName) ? "当前组件" : WidgetName;
            string itemLabel = ItemCount == 1 ? "1 项" : $"{ItemCount} 项";

            if (IsFailed)
            {
                return $"{target} · {itemLabel} · 失败";
            }

            if (IsUndone)
            {
                return $"{target} · {itemLabel} · 已撤销";
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
                return ErrorMessage ?? "操作失败";
            }

            if (Items.Count == 0)
            {
                return "没有记录到项目。";
            }

            var firstItem = Items[0];
            if (Items.Count == 1)
            {
                return firstItem.Name;
            }

            return $"{firstItem.Name} 等 {Items.Count} 项";
        }
    }

    [JsonIgnore]
    public string UndoButtonText => ActionType switch
    {
        OrganizationActionType.MoveBackToDesktop => "撤销移回桌面",
        _ => "撤销上次移动"
    };
}

public class OrganizationHistoryItem
{
    public string Name { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string DestinationPath { get; set; } = string.Empty;
}

public static class OrganizationActionType
{
    public const string ManagedDrop = "ManagedDrop";
    public const string MoveBackToDesktop = "MoveBackToDesktop";
    public const string EnableManagedStorage = "EnableManagedStorage";
}
