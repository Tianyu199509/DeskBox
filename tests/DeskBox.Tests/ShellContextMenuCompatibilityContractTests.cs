using DeskBox.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Tests;

public sealed class ShellContextMenuCompatibilityContractTests
{
    [Fact]
    public void ProductPath_UsesAsyncOutOfProcessProxyWithoutInProcessFallback()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");
        string menuBuilder = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemMenuBuilder.cs");
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuHelper.cs");

        Assert.Contains(
            "private async Task ShowSystemContextMenuAsync(WidgetItem item)",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ShellContextMenuProxy.ShowAsync(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains("item.Path,", surface, StringComparison.Ordinal);
        Assert.Contains("screenX,", surface, StringComparison.Ordinal);
        Assert.Contains("screenY);", surface, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShellContextMenuHelper.ShowContextMenu(",
            surface,
            StringComparison.Ordinal);

        Assert.Contains(
            "Func<WidgetItem, Task>? ShowSystemContextMenuAsync",
            menuBuilder,
            StringComparison.Ordinal);
        Assert.Contains(
            "await actions.ShowSystemContextMenuAsync(item);",
            menuBuilder,
            StringComparison.Ordinal);

        Assert.DoesNotContain("IContextMenu", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryContextMenu", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackPopupMenuEx", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedProxy_UsesExistingNativeExecutableHandshakeAndBoundedWaits()
    {
        string proxy = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuProxy.cs");

        Assert.Contains(
            "ShellThumbnailProxy.ExecutableName",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", proxy, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput = true", proxy, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add(\"--context-menu\")", proxy, StringComparison.Ordinal);
        Assert.Contains("ReadyMessage = \"ready\"", proxy, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", proxy, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(10)", proxy, StringComparison.Ordinal);
        Assert.Contains("TryKill(process)", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShellContextMenuHelper.ShowContextMenu(",
            proxy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedProxy_MapsCancellationAndNativeFailuresWithoutThrowingIntoProductPath()
    {
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Invoked,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.InvokedExitCode));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Cancelled,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.CancelledExitCode));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Failed,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.FailedExitCode));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Failed,
            ShellContextMenuProxy.MapExitCode(
                unchecked((int)0xC0000005)));
    }

    [Fact]
    public async Task NativeProxy_InvalidContextMenuRequestFailsInChildProcess()
    {
        string proxyPath = GetBuiltProxyPath();
        Assert.True(File.Exists(proxyPath), $"Proxy not found: {proxyPath}");
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox-missing-context-menu-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo
        {
            FileName = proxyPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--context-menu");
        startInfo.ArgumentList.Add(missingPath);
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("0");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(ShellContextMenuProxy.FailedExitCode, process.ExitCode);
        Assert.DoesNotContain("ready", await outputTask, StringComparison.Ordinal);
        Assert.Contains(
            "Shell context menu source does not exist",
            await errorTask,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProxy_OwnsStaWindowAndForwardsShellMenuMessages()
    {
        string native = ReadRepositoryFile(
            "native/deskbox-thumbnail-proxy/src/main.rs");
        string manifest = ReadRepositoryFile(
            "native/deskbox-thumbnail-proxy/Cargo.toml");

        Assert.Contains("\"--context-menu\"", native, StringComparison.Ordinal);
        Assert.Contains("COINIT_APARTMENTTHREADED", native, StringComparison.Ordinal);
        Assert.Contains("create_context_menu_window", native, StringComparison.Ordinal);
        Assert.Contains("SHParseDisplayName", native, StringComparison.Ordinal);
        Assert.Contains("SHBindToParent", native, StringComparison.Ordinal);
        Assert.Contains("GetUIObjectOf", native, StringComparison.Ordinal);
        Assert.Contains("IContextMenu3", native, StringComparison.Ordinal);
        Assert.Contains("HandleMenuMsg2", native, StringComparison.Ordinal);
        Assert.Contains("TrackPopupMenuEx", native, StringComparison.Ordinal);
        Assert.Contains("InvokeCommand", native, StringComparison.Ordinal);
        Assert.Contains("write_stdout(b\"ready\\n\")", native, StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_EXIT_CANCELLED: i32 = 2",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_EXIT_FAILED: i32 = 3",
            native,
            StringComparison.Ordinal);

        Assert.Contains(
            "\"Win32_UI_Shell_Common\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Win32_UI_WindowsAndMessaging\"",
            manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StackPopover_RemainsAliveUntilProxyMenuCompletes()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");
        string rightClickHost = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        Assert.Contains(
            "bool fromStackPopover = _stackPopoverItemsView?.Items",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackPopoverContextMenuOpen = true;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_stackPopoverSystemContextMenuOpen = true;",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!_stackPopoverSystemContextMenuOpen)",
            rightClickHost,
            StringComparison.Ordinal);
        Assert.Contains("finally", surface, StringComparison.Ordinal);
        Assert.Contains(
            "CompleteStackPopoverContextMenu();",
            surface,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static string GetBuiltProxyPath()
    {
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        string platform = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "ARM64"
            : "x64";
        string outputRoot = Path.Combine(
            "src",
            "DeskBox",
            "bin",
            platform,
            configuration,
            "net10.0-windows10.0.22621.0");
        string canonicalPath = TestPaths.FromRepository(Path.Combine(
            outputRoot,
            ShellThumbnailProxy.ExecutableName));
        if (File.Exists(canonicalPath))
        {
            return canonicalPath;
        }

        return TestPaths.FromRepository(Path.Combine(
            outputRoot,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64"
                : "win-x64",
            ShellThumbnailProxy.ExecutableName));
    }
}
