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
        Assert.Contains("screenY,", surface, StringComparison.Ordinal);
        // The host labels the rename entry the native proxy appends; the
        // label reuses the shared localized "Rename" string.
        Assert.Contains(
            "T(\"Common.Rename\")",
            surface,
            StringComparison.Ordinal);
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
    public void ManagedProxy_UsesPersistentServerProcessHandshakeAndBoundedWaits()
    {
        string proxy = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuProxy.cs");

        Assert.Contains(
            "ShellThumbnailProxy.ExecutableName",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", proxy, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput = true", proxy, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardInput = true", proxy, StringComparison.Ordinal);
        Assert.Contains(
            "ArgumentList.Add(ServerArgument)",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("ReadyMessage = \"ready\"", proxy, StringComparison.Ordinal);
        Assert.Contains(
            "ServerArgument = \"--context-menu-server\"",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("\"shown\"", proxy, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", proxy, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(10)", proxy, StringComparison.Ordinal);
        Assert.Contains("_process.Kill(entireProcessTree: true)", proxy, StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.CompareExchange(ref s_menuInFlight, 1, 0)",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("public static void Prewarm()", proxy, StringComparison.Ordinal);
        Assert.Contains("ReadProtocolLineAsync", proxy, StringComparison.Ordinal);

        // A server round that fails before any menu became visible must fall
        // back to the self-contained one-shot round instead of reporting a
        // failure for a feature that demonstrably works.
        Assert.Contains(
            "OneShotArgument = \"--context-menu\"",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("TryShowOneShotAsync", proxy, StringComparison.Ordinal);

        // Encoding.UTF8 writes a BOM on the first stdin write, which the native
        // parser discards as noise; every menu command after a fresh spawn then
        // fails the 15s build budget. The protocol encoding must be BOM-free.
        Assert.Contains(
            "new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)",
            proxy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StandardInputEncoding = Encoding.UTF8",
            proxy,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShellContextMenuHelper.ShowContextMenu(",
            proxy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Prewarm_IsWiredToStartupAndSettingsToggle()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        string preferences = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.PreferenceCallbacks.cs");

        Assert.Contains("ScheduleShellContextMenuPrewarm", app, StringComparison.Ordinal);
        Assert.Contains(
            "Settings.FileItemSystemContextMenuEnabled",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShellContextMenuProxy.Prewarm();",
            preferences,
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
        Assert.Contains("\"--context-menu-server\"", native, StringComparison.Ordinal);
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

        // The proxy must share the host's PerMonitorV2 coordinate space, or
        // every scaled display misplaces and bitmap-stretches the menu.
        Assert.Contains(
            "SetProcessDpiAwarenessContext",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2",
            native,
            StringComparison.Ordinal);

        // Invocation must use the extended Unicode contract, and the menu
        // owner must sit in the TOPMOST band above the widget surfaces.
        Assert.Contains("CMINVOKECOMMANDINFOEX", native, StringComparison.Ordinal);
        Assert.Contains("CMIC_MASK_UNICODE", native, StringComparison.Ordinal);
        Assert.Contains("CMIC_MASK_PTINVOKE", native, StringComparison.Ordinal);
        Assert.Contains("WS_EX_TOPMOST", native, StringComparison.Ordinal);

        // Post-invoke grace pump and the persistent server protocol.
        Assert.Contains("pump_messages", native, StringComparison.Ordinal);
        Assert.Contains("PeekMessageW", native, StringComparison.Ordinal);
        Assert.Contains("\"menu\\t\"", native, StringComparison.Ordinal);
        Assert.Contains("b\"shown\\n\"", native, StringComparison.Ordinal);
        Assert.Contains("result {result_code}", native, StringComparison.Ordinal);
        Assert.Contains("b\"bye\\n\"", native, StringComparison.Ordinal);
        Assert.Contains("\"warmup\"", native, StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_SERVER_MAX_MENUS",
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
        Assert.Contains(
            "\"Win32_UI_HiDpi\"",
            manifest,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Win32_UI_Input_KeyboardAndMouse\"",
            manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpenMenuIsDismissedOnClicksInsideDeskBoxSurfaces()
    {
        string proxy = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuProxy.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");
        string popover = ReadRepositoryFile(
            "src/DeskBox/Views/StackPopoverHostWindow.cs");
        string native = ReadRepositoryFile(
            "native/deskbox-thumbnail-proxy/src/main.rs");

        // Widget windows are created without activation, so a click on them
        // never deactivates the menu's owner. The menu must be closed on
        // request instead of waiting for a foreground change.
        Assert.Contains("public static void CancelOpenMenu()", proxy, StringComparison.Ordinal);
        Assert.Contains("SendCommandAsync(\"cancel\"", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Interlocked.Exchange(ref s_menuInFlight, 0);\r\n        }\r\n\r\n        return MenuResult.Cancelled;",
            proxy,
            StringComparison.Ordinal);
        // A right-click while a menu is open is remembered and shown after it.
        Assert.Contains("s_pendingRequest", proxy, StringComparison.Ordinal);
        Assert.Contains("ShowOneRequestAsync", proxy, StringComparison.Ordinal);

        Assert.Contains("ShellContextMenuProxy.CancelOpenMenu();", surface, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.WM_LBUTTONDOWN", surface, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.WM_RBUTTONDOWN", surface, StringComparison.Ordinal);
        Assert.Contains("ShellContextMenuProxy.CancelOpenMenu();", popover, StringComparison.Ordinal);

        Assert.Contains("\"cancel\"", native, StringComparison.Ordinal);
        Assert.Contains("dismiss_open_menu", native, StringComparison.Ordinal);
        Assert.Contains("VK_ESCAPE", native, StringComparison.Ordinal);
        Assert.Contains("PostMessageW(Some(owner), WM_KEYDOWN", native, StringComparison.Ordinal);

        // Activation alone cannot dismiss the menu over DeskBox's topmost,
        // non-activating widget windows, so the proxy also watches mouse input
        // directly while a menu is up.
        Assert.Contains("SetWindowsHookExW", native, StringComparison.Ordinal);
        Assert.Contains("WH_MOUSE_LL", native, StringComparison.Ordinal);
        Assert.Contains("menu_mouse_hook_proc", native, StringComparison.Ordinal);
        Assert.Contains("WindowFromPoint", native, StringComparison.Ordinal);
        Assert.Contains("MENU_WINDOW_CLASS", native, StringComparison.Ordinal);
        Assert.Contains("MENU_CANCELLED_BY_OUTSIDE_CLICK", native, StringComparison.Ordinal);

        // A low-level hook whose owning thread blocks inside InvokeCommand (or
        // a slow submenu handler) stops receiving mouse input and stalls the
        // whole desktop input pipeline, so the hook must live on a dedicated
        // pumping thread and be dropped the moment the menu closes - before
        // any handler work starts.
        Assert.Contains(
            "\"menu-mouse-hook\"",
            native,
            StringComparison.Ordinal);
        Assert.Contains("PostThreadMessageW", native, StringComparison.Ordinal);
        Assert.Contains("GetMessageW", native, StringComparison.Ordinal);
        Assert.Contains(
            "drop(_mouse_hook)",
            native,
            StringComparison.Ordinal);

        // DeskBox must grant the proxy the foreground right TrackPopupMenuEx
        // wants, and the round must report how the menu actually ended.
        Assert.Contains("AllowSetForegroundWindow", proxy, StringComparison.Ordinal);
        Assert.Contains("GrantForegroundTo", proxy, StringComparison.Ordinal);
        Assert.Contains("native={detail}", proxy, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeMenu_UsesShellThemingAndPopulatesDelayGeneratedSubmenus()
    {
        string native = ReadRepositoryFile(
            "native/deskbox-thumbnail-proxy/src/main.rs");
        string proxy = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuProxy.cs");

        // TPM_NONOTIFY suppresses WM_INITMENUPOPUP, which is what fills the
        // Shell's delay-generated submenus; without it "Send to" and "Open
        // with" open as empty flyouts. TPM_RETURNCMD already returns the
        // selection, so the flag must never come back into the flags value.
        Assert.DoesNotContain("TPM_NONOTIFY.0", native, StringComparison.Ordinal);
        Assert.Contains("TPM_RETURNCMD.0 |", native, StringComparison.Ordinal);

        // Classic menus cannot be themed through a supported API: the proxy
        // opts the process in through uxtheme's private entry points.
        Assert.Contains(
            "UXTHEME_PREFERRED_APP_MODE_ORDINAL: usize = 135",
            native,
            StringComparison.Ordinal);
        Assert.Contains("LoadLibraryExW", native, StringComparison.Ordinal);
        Assert.Contains("SetWindowTheme", native, StringComparison.Ordinal);
        Assert.Contains("apply_menu_theme", native, StringComparison.Ordinal);
        Assert.Contains("menu theme applied", native, StringComparison.Ordinal);
        // DeskBox tells the proxy which theme its menu must match, both for the
        // persistent server and for the one-shot fallback round.
        Assert.Contains("MenuThemeToken", proxy, StringComparison.Ordinal);
        Assert.Contains("IsDarkMenuExpected", proxy, StringComparison.Ordinal);
        Assert.Contains("MenuThemeToken()}", proxy, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add(MenuThemeToken())", proxy, StringComparison.Ordinal);
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
