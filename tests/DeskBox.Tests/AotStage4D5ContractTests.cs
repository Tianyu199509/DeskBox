using DeskBox.Platform;

namespace DeskBox.Tests;

public sealed class AotStage4D5ContractTests
{
    [Fact]
    public void NativeTrayHost_ExposesBlittableWin32AbiWithoutManagedNotifyIconDependency()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.DoesNotContain("H.NotifyIcon", project, StringComparison.Ordinal);
        Assert.Equal(IntPtr.Size == 8 ? 976 : 956, NativeTrayIconHost.SizeOfNotifyIconDataW);
        Assert.True(NativeTrayIconHost.ProbeWin32EntryPoints());
        Assert.True(ShellIconNativeMethods.ProbeIconBitmapEntryPoints());
    }

    [Fact]
    public void TrayIdentity_UsesTypedNativeTrayHostWithoutReflection()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.Tray.cs");

        Assert.Contains("new NativeTrayIconHost(", source, StringComparison.Ordinal);
        Assert.Contains("trayHost.WindowHandle", source, StringComparison.Ordinal);
        Assert.Contains("trayHost.IconId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("H.NotifyIcon", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"TrayIcon\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"WindowHandle\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"Id\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection.BindingFlags", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayBehavior_UsesNativePopupMenuAndTaskbarEdgePlacement()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.Tray.cs");

        Assert.Contains("trayHost.ShowContextMenu(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_trayHost.ShowContextMenu(", source, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.TryGetNotifyIconRect", source, StringComparison.Ordinal);
        Assert.Contains("GetFallbackTrayContextMenuAnchorPoint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Window { Title = \"DeskBox Tray Host\" }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D5ZeroWarningAndLegacyReflectionGates()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 59", audit, StringComparison.Ordinal);
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
