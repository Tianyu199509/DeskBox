using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class FileStackCustomRuleEditor : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExtensionsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PriorityText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    public FileStackCustomRule ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Extensions = ParseExtensions(ExtensionsText).ToList()
    };

    public static FileStackCustomRuleEditor FromModel(FileStackCustomRule rule) => new()
    {
        Id = string.IsNullOrWhiteSpace(rule.Id)
            ? Guid.NewGuid().ToString("N")
            : rule.Id,
        Name = rule.Name ?? string.Empty,
        ExtensionsText = string.Join(" ",
            SettingsService.NormalizeFileStackExtensions(rule.Extensions))
    };

    public static IReadOnlyList<string> ParseExtensions(string? text) =>
        SettingsService.NormalizeFileStackExtensions(
            (text ?? string.Empty).Split(
                [' ', '\t', '\r', '\n', ',', ';', '，', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
