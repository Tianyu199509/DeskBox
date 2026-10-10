using DeskBox.Helpers;

namespace DeskBox.Tests;

/// <summary>
/// Contracts for the native context-menu protocol extension (host-owned
/// rename entry with a reserved id range, classified InvokeCommand failures)
/// and for pasting Shell namespace objects as shortcuts.
/// </summary>
public sealed class NativeMenuCustomRenameAndPasteContractTests
{
    [Fact]
    public void MapExitCode_ExtendsToCustomCommandAndClassifiedFailures()
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
            ShellContextMenuProxy.MenuResult.CustomRename,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.CustomRenameExitCode));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.FailedUnsupported,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.FailedUnsupportedExitCode));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.FailedCancelled,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.FailedCancelledExitCode));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.FailedAccessDenied,
            ShellContextMenuProxy.MapExitCode(
                ShellContextMenuProxy.FailedAccessDeniedExitCode));

        // Unknown codes (including proxy-level failures and crashes) still
        // collapse into the generic failure instead of throwing.
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Failed,
            ShellContextMenuProxy.MapExitCode(4));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Failed,
            ShellContextMenuProxy.MapExitCode(9));
        Assert.Equal(
            ShellContextMenuProxy.MenuResult.Failed,
            ShellContextMenuProxy.MapExitCode(
                unchecked((int)0xC0000005)));
    }

    [Fact]
    public void NativeProxy_AppendsHostRenameItemOutsideTheShellIdRange()
    {
        string native = ReadRepositoryFile(
            "native/deskbox-thumbnail-proxy/src/main.rs");

        // The reserved host id range must sit above the ids the Shell
        // handlers are offered so a handler can never mint it.
        Assert.Contains(
            "CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_CUSTOM_COMMAND_FIRST: u32 = 0x7001",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_EXIT_CUSTOM_COMMAND: i32 = 5",
            native,
            StringComparison.Ordinal);
        Assert.Contains("AppendMenuW", native, StringComparison.Ordinal);
        Assert.Contains(
            "append_host_rename_item",
            native,
            StringComparison.Ordinal);
        // Rename sits above the Shell's Properties entry when its verb can be
        // probed; the end-append above is only the fallback placement.
        Assert.Contains(
            "insert_host_rename_item",
            native,
            StringComparison.Ordinal);
        Assert.Contains("GCS_VERBW", native, StringComparison.Ordinal);
        // The result line MUST terminate with a newline: the host reads it
        // with a line reader, and a missing terminator stalls the round until
        // the 10-minute interaction budget and swallows every later
        // right-click (regression shipped 2026-10-10, fixed same day).
        Assert.Contains(
            "detail.push('\\n');",
            native,
            StringComparison.Ordinal);
        // A custom selection must NOT be routed into InvokeCommand.
        Assert.Contains(
            "Ok(CONTEXT_MENU_EXIT_CUSTOM_COMMAND)",
            native,
            StringComparison.Ordinal);
        // The label rides along both transports (server line and one-shot
        // argv) and is optional so unlabeled callers keep the old menu.
        Assert.Contains("splitn(5, '\\t')", native, StringComparison.Ordinal);
        Assert.Contains(
            "rename_label: Option<String>",
            native,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProxy_ClassifiesInvokeCommandFailuresAndReportsHresult()
    {
        string native = ReadRepositoryFile(
            "native/deskbox-thumbnail-proxy/src/main.rs");

        Assert.Contains(
            "classify_invoke_failure",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_EXIT_FAILED_UNSUPPORTED: i32 = 6",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_EXIT_FAILED_CANCELLED: i32 = 7",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "CONTEXT_MENU_EXIT_FAILED_ACCESS_DENIED: i32 = 8",
            native,
            StringComparison.Ordinal);
        // Diagnostics: the failing HRESULT surfaces in the server result
        // line and the custom selection is named.
        Assert.Contains(
            "custom=rename",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "LAST_INVOKE_FAILURE_HR",
            native,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedProxy_TransportsRenameLabelAndMapsNewExitCodes()
    {
        string proxy = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellContextMenuProxy.cs");

        Assert.Contains(
            "string? renameLabel = null",
            proxy,
            StringComparison.Ordinal);
        // Both transports carry the label when one was localized.
        Assert.Contains(
            "command += \"\\t\" + renameLabel;",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains(
            "startInfo.ArgumentList.Add(renameLabel);",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains(
            "CustomRenameExitCode => MenuResult.CustomRename",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains(
            "FailedUnsupportedExitCode => MenuResult.FailedUnsupported",
            proxy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Host_RoutesCustomRenameToBuiltInPipelineAndClassifiesToasts()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");

        // The menu opens with the localized rename label...
        Assert.Contains(
            "await ShellContextMenuProxy.ShowAsync(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "T(\"Common.Rename\")",
            surface,
            StringComparison.Ordinal);
        // ...and a custom-rename result runs the built-in rename pipeline
        // (the same one the WinUI flyout uses), not the Shell.
        Assert.Contains(
            "MenuResult.CustomRename",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "await RenameItemAsync(item);",
            surface,
            StringComparison.Ordinal);
        // Classified failures get their own toast keys instead of the
        // generic "operation did not complete".
        Assert.Contains(
            "SystemMenuFailureText",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Widget.SystemMenu.Unsupported\"",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Widget.SystemMenu.Cancelled\"",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Widget.SystemMenu.AccessDenied\"",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Paste_ReadsShellIdListAndMaterializesShortcutsForVirtualItems()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string shortcuts = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ShortcutDrop.cs");
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellClipboardHelper.cs");

        // The paste path consults CFSTR_SHELLIDLIST once WinUI paths and
        // CF_HDROP have yielded nothing...
        Assert.Contains(
            "ShellClipboardHelper.TryGetShellIdListItems(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateShortcutsForPastedNamespaceItemsAsync(",
            surface,
            StringComparison.Ordinal);
        // ...Ctrl+V is enabled for namespace-only clipboards...
        Assert.Contains(
            "ShellClipboardHelper.HasShellIdList()",
            surface,
            StringComparison.Ordinal);
        // ...virtual items land as PIDL-backed shortcuts, real paths import.
        Assert.Contains(
            "CreateShortcutsForPastedNamespaceItemsAsync",
            shortcuts,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutHelper.CreateShellNamespaceShortcut(",
            shortcuts,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetShellIdListItems",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "HasShellIdList",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutHelper_ExposesNamespaceShortcutForAnyParsingName()
    {
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShortcutHelper.cs");

        Assert.Contains(
            "public static void CreateShellNamespaceShortcut(",
            helper,
            StringComparison.Ordinal);
        // The AppsFolder entry point stays and delegates to the generalized
        // parsing-name writer.
        Assert.Contains(
            "public static void CreateShellApplicationShortcut(",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SystemMenuFailureTexts_ExistInEverySupportedLanguage()
    {
        string[] languages =
        [
            "ar-SA", "bn-BD", "de-DE", "en-US", "es-ES", "fr-FR",
            "hi-IN", "ja-JP", "pt-BR", "ru-RU", "zh-CN", "zh-TW"
        ];

        foreach (string language in languages)
        {
            string json = ReadRepositoryFile(
                $"src/DeskBox/Strings/{language}.json");
            Assert.Contains(
                "\"Widget.SystemMenu.Unsupported\"",
                json,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"Widget.SystemMenu.Cancelled\"",
                json,
                StringComparison.Ordinal);
            Assert.Contains(
                "\"Widget.SystemMenu.AccessDenied\"",
                json,
                StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
