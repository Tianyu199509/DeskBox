using H.NotifyIcon;
using System.Reflection;

namespace DeskBox.Tests;

public sealed class AotStage4D5ContractTests
{
    [Fact]
    public void NotifyIconDependency_ExposesTheRequiredPublicTrayContracts()
    {
        const BindingFlags publicInstance = BindingFlags.Instance | BindingFlags.Public;

        PropertyInfo? trayIconProperty = typeof(TaskbarIcon).GetProperty("TrayIcon", publicInstance);
        EventInfo? openedEvent = typeof(TaskbarIcon).GetEvent(
            "SecondWindowContextMenuOpened",
            publicInstance);
        PropertyInfo? windowHandleProperty = typeof(H.NotifyIcon.Core.TrayIcon).GetProperty(
            "WindowHandle",
            publicInstance);
        PropertyInfo? idProperty = typeof(H.NotifyIcon.Core.TrayIcon).GetProperty(
            "Id",
            publicInstance);

        Assert.NotNull(trayIconProperty);
        Assert.Equal(typeof(H.NotifyIcon.Core.TrayIcon), trayIconProperty!.PropertyType);
        Assert.NotNull(openedEvent);
        Assert.NotNull(windowHandleProperty);
        Assert.Equal(typeof(nint), windowHandleProperty!.PropertyType);
        Assert.NotNull(idProperty);
        Assert.Equal(typeof(Guid), idProperty!.PropertyType);
    }

    [Fact]
    public void TrayIdentity_UsesThePublicTypedContractWithoutReflection()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.Tray.cs");

        Assert.Contains("_trayIcon.TrayIcon", source, StringComparison.Ordinal);
        Assert.Contains("trayIcon.WindowHandle", source, StringComparison.Ordinal);
        Assert.Contains("trayIcon.Id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"TrayIcon\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"WindowHandle\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"Id\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondWindowPresenter_UsesPublicLifecycleAndVisualTreeWithoutPrivateReflection()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.Tray.cs");

        Assert.Contains("SecondWindowContextMenuOpened +=", source, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetParent", source, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetOpenPopupsForXamlRoot", source, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutPresenter", source, StringComparison.Ordinal);
        Assert.Contains("Popup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetSecondWindowContextMenuFlyout", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"ContextMenuFlyout\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection.BindingFlags", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayBehavior_KeepsSecondWindowPlacementAndNoScrollConstraints()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.Tray.cs");

        Assert.Contains("ContextMenuMode = ContextMenuMode.SecondWindow", source, StringComparison.Ordinal);
        Assert.Contains("_trayIcon.ShowContextMenu(point)", source, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.TryGetNotifyIconRect", source, StringComparison.Ordinal);
        Assert.Contains("GetFallbackTrayContextMenuAnchorPoint", source, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.VerticalScrollModeProperty", source, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibilityProperty", source, StringComparison.Ordinal);
        Assert.Contains("double.PositiveInfinity", source, StringComparison.Ordinal);
        Assert.Contains("ShouldConstrainToRootBounds = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D5ZeroWarningAndLegacyReflectionGates()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 60", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4D5SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4D5LegacyReflectionPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4D5LegacyReflectionSourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4D5WarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4D-5 tray reflection patterns remain", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4D-5 tray sources produced AOT warnings", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_PreservesTheStage4D5TrayBoundaryInTheCurrentStage()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("tray", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DeskBoxRustNative=true", project, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
