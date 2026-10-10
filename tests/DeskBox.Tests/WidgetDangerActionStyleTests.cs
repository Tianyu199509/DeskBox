using System.Text.Json;

namespace DeskBox.Tests;

public sealed class WidgetDangerActionStyleTests
{
    [Fact]
    public void WidgetCloseActions_UseTheSharedFluentCriticalStyle()
    {
        string root = FindRepositoryRoot();
        string sharedStyle = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetDangerActionStyle.cs"));
        string contentMenus = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string confirmationBuilder = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetCompactConfirmationMenuBuilder.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml"));

        Assert.Contains(
            "SystemFillColorCriticalBrush",
            sharedStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetDangerActionStyle.Apply(disableWidget)",
            contentMenus,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetDangerActionStyle.Apply(confirmItem)",
            confirmationBuilder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\"",
            shell,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.Red", contentMenus, StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.Red", confirmationBuilder, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedFolderRecycle_RequiresASecondConfirmationWithCancelFirst()
    {
        string root = FindRepositoryRoot();
        string contentMenus = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string confirmationBuilder = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetCompactConfirmationMenuBuilder.cs"));

        Assert.Contains(
            "ShowDeleteManagedFolderConfirmationAsync",
            contentMenus,
            StringComparison.Ordinal);
        Assert.Contains(
            "CancelFirst = true",
            contentMenus,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (options.CancelFirst && cancelItem is not null)",
            confirmationBuilder,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedFolderCloseMenu_UsesRequestedCopyAndQuestionIcon()
    {
        string root = FindRepositoryRoot();
        string contentMenus = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "src/DeskBox/Strings/zh-CN.json")));

        Assert.Equal(
            "格子内的文件怎么处理?",
            strings.RootElement.GetProperty("Widget.DeleteManagedInfo").GetString());
        Assert.Equal(
            "同时移入回收站",
            strings.RootElement.GetProperty("Widget.DeleteFolderToRecycleBin").GetString());
        Assert.Contains(
            "Icon = new FontIcon { Glyph = \"\\uE897\" }",
            contentMenus,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
