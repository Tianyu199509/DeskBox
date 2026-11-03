namespace DeskBox.Tests;

/// <summary>
/// Pins the Windows 11-aligned context menu shape: naming keys, item
/// ordering, the split View / Sort by submenus, and the process-isolation
/// boundary (Open With and New go through association/registry data only;
/// the classic IContextMenu path stays with the out-of-process proxy).
/// </summary>
public sealed class ContextMenuWin11AlignmentContractTests
{
    [Fact]
    public void ItemMenu_UsesWin11NamingKeys()
    {
        string builder = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemMenuBuilder.cs");

        foreach (string key in new[]
                 {
                     "Widget.Open",
                     "Widget.RunAsAdministrator",
                     "Widget.ExtractAll",
                     "Widget.EmptyRecycleBin",
                     "Widget.CreateShortcut",
                     "Common.Cut",
                     "Common.Copy",
                     "Common.Rename",
                     "Widget.CompressTo",
                     "Widget.CompressTo.Zip",
                     "Widget.CompressTo.Tar",
                     "Widget.CopyPath",
                     "Widget.MoveToRecycleBin",
                     "Widget.ShowInExplorer",
                     "Common.Properties",
                     "Widget.MoreSystemOperations",
                 })
        {
            Assert.Contains(key, builder, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ItemMenu_SingleColumn_ClassicOrder_EscapeHatchLast()
    {
        string builder = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemMenuBuilder.cs");

        // Single-column MenuFlyout in the native classic layout (2026-10-21
        // baseline-B decision): the open family leads (open, open with, open
        // file location in Explorer's second slot), extension-style verbs
        // follow (admin, extract, empty-bin, compress), then the stable tail
        // — copy-as-path, cut/copy, create-shortcut/delete/rename — with
        // properties and the system-menu escape hatch last. Scoped to the
        // single-item builder region; helper definitions sit after both
        // builders, so the row-helper literal order is pinned separately.
        int singleStart = builder.IndexOf(
            "public static MenuFlyout CreateItemFlyout(",
            StringComparison.Ordinal);
        int singleEnd = builder.IndexOf(
            "public static MenuFlyout CreateMultiSelectionFlyout(",
            StringComparison.Ordinal);
        Assert.True(singleStart >= 0 && singleEnd > singleStart);
        string single = builder[singleStart..singleEnd];

        int openRow = single.IndexOf(
            "AddOpenRow(flyout, item, actions);",
            StringComparison.Ordinal);
        int openWith = single.IndexOf(
            "AddOpenWithRow(flyout, item, actions);",
            StringComparison.Ordinal);
        int showInExplorer = single.IndexOf(
            "AddShowInExplorerRow(flyout, item, actions);",
            StringComparison.Ordinal);
        int admin = single.IndexOf(
            "\"Widget.RunAsAdministrator\"",
            StringComparison.Ordinal);
        int extract = single.IndexOf(
            "\"Widget.ExtractAll\"",
            StringComparison.Ordinal);
        int emptyBin = single.IndexOf(
            "AddEmptyRecycleBinRow(flyout, actions);",
            StringComparison.Ordinal);
        int compress = single.IndexOf(
            "AddCompressToZipRow(flyout, actions);",
            StringComparison.Ordinal);
        int copyPath = single.IndexOf(
            "\"Widget.CopyPath\"",
            StringComparison.Ordinal);
        int cutCopy = single.IndexOf(
            "AddCutCopyRows(flyout, actions);",
            StringComparison.Ordinal);
        int createShortcut = single.IndexOf(
            "AddCreateShortcutRow(flyout, item, actions);",
            StringComparison.Ordinal);
        int deleteRow = single.IndexOf(
            "AddDeleteRow(flyout, actions);",
            StringComparison.Ordinal);
        int renameRow = single.IndexOf(
            "AddRenameRow(flyout, item, actions);",
            StringComparison.Ordinal);
        int properties = single.IndexOf(
            "\"Common.Properties\"",
            StringComparison.Ordinal);
        int moreOptions = single.IndexOf(
            "AddShowMoreOptionsRow(flyout, item, actions);",
            StringComparison.Ordinal);

        Assert.True(openRow >= 0 && openWith > openRow,
            "Open family leads the menu");
        Assert.True(showInExplorer > openWith,
            "Open file location sits in Explorer's second-row slot");
        Assert.True(admin > showInExplorer && extract > admin,
            "Admin and extract stay in the extension-verb zone");
        Assert.True(emptyBin > extract && compress > emptyBin,
            "Empty Recycle Bin rides after the open family, before compress");
        Assert.True(copyPath > compress,
            "Copy as path opens the stable file tail");
        Assert.True(cutCopy > copyPath && createShortcut > cutCopy &&
            deleteRow > createShortcut && renameRow > deleteRow,
            "Classic two-group tail: cut/copy, then create-shortcut/delete/rename");
        Assert.True(properties > renameRow,
            "Properties stays visible in the rows");
        Assert.True(moreOptions > properties,
            "Show more options is the last row");

        // Row helper literal order: cut / copy together; rename lives in its
        // own helper (moved out of the clipboard group by the classic
        // two-group decision).
        int cut = builder.IndexOf("\"Common.Cut\"", StringComparison.Ordinal);
        int copy = builder.IndexOf("\"Common.Copy\"", StringComparison.Ordinal);
        int renameHelper = builder.IndexOf(
            "private static void AddRenameRow(",
            StringComparison.Ordinal);
        Assert.True(cut >= 0 && copy > cut && renameHelper > copy,
            "Cut / copy pair, rename in its own classic-group helper");

        // The retired CommandBarFlyout workarounds must not come back: the
        // AppBarElementContainer submenu wrapper, the chevron resource hack,
        // the manual hover debounce, and the AlwaysExpanded opener.
        Assert.DoesNotContain("AppBarElementContainer", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("AppBarButtonHasFlyoutChevronVisibility", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutBase.ShowAttachedFlyout(row)", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("AlwaysExpanded", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandBarFlyout", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemMenu_NamespaceShortcuts_FollowTargetDesktopMenu()
    {
        string builder = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemMenuBuilder.cs");

        // Baseline B: a PIDL-only namespace link (This PC / Recycle Bin /
        // Control Panel materialized as .lnk) inherits its target's desktop
        // verbs, while rows that need a filesystem target hide exactly the
        // way Explorer's menu hides them.
        Assert.Contains(
            "NamespaceShortcutHelper.ResolveKind(item)",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "namespaceKind == NamespaceShortcutKind.RecycleBin",
            builder,
            StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(builder, "if (!isNamespaceShortcut)") == 2,
            "Open file location and Compress both hide for namespace links");

        // The empty-bin state check must stay off the right-tap callback
        // frame (shell aggregate query on a low-priority UI pass instead).
        Assert.Contains(
            "ScheduleRecycleBinEmptyStateCheck(row)",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "DispatcherQueuePriority.Low",
            builder,
            StringComparison.Ordinal);

        // Host verbs: the surface supplies create-shortcut and empty-bin.
        Assert.Contains(
            "Func<WidgetItem, Task>? CreateItemShortcutAsync = null",
            builder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Func<Task>? EmptyRecycleBinAsync = null",
            builder,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    [Fact]
    public void MultiSelectionMenu_AddsOpenAndCompress()
    {
        string builder = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemMenuBuilder.cs");

        string multiStart = builder.Substring(builder.IndexOf(
            "CreateMultiSelectionFlyout",
            StringComparison.Ordinal));
        Assert.Contains("Widget.Open", multiStart, StringComparison.Ordinal);
        Assert.Contains(
            "Widget.CompressTo",
            multiStart,
            StringComparison.Ordinal);
        Assert.Contains(
            "await actions.OpenItemAsync(item);",
            multiStart,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MenuMaterial_DirectAcrylic()
    {
        // Direct acrylic: ThemedMenuAcrylicBackdrop on the implicit presenter
        // styles (MenuFlyoutPresenter and CommandBarFlyoutCommandBar) resolves
        // Light/Dark from the presenter / owning XamlRoot.Content instead of
        // WinUI's built-in DesktopAcrylicBackdrop, whose PopupRoot-scoped
        // GetDefaultSystemBackdropConfiguration stays pinned to the startup OS
        // theme (painting dark #2C2C2C acrylic behind black Light-mode text).
        string appXaml = ReadRepositoryFile("src/DeskBox/App.xaml");
        string settingsAppXaml = ReadRepositoryFile("src/DeskBox.Settings/SettingsHostApp.xaml");
        Assert.Contains(
            "<helpers:ThemedMenuAcrylicBackdrop />",
            appXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<helpers:ThemedMenuAcrylicBackdrop />",
            settingsAppXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<media:DesktopAcrylicBackdrop />",
            appXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<media:DesktopAcrylicBackdrop />",
            settingsAppXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DeskBoxMenuFallbackBackgroundBrush",
            appXaml,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Light, true, Microsoft.UI.Xaml.ElementTheme.Light, true, false)]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Dark, true, Microsoft.UI.Xaml.ElementTheme.Light, true, false)]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Dark, true, Microsoft.UI.Xaml.ElementTheme.Dark, false, true)]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Dark, true, Microsoft.UI.Xaml.ElementTheme.Light, false, true)]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Xaml.ElementTheme.Light, true, Microsoft.UI.Xaml.ElementTheme.Dark, true, false)]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, false, Microsoft.UI.Xaml.ElementTheme.Light, true, false)]
    [InlineData(Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Xaml.ElementTheme.Default, false, Microsoft.UI.Xaml.ElementTheme.Default, true, true)]
    public void ThemedMenuAcrylicBackdrop_ResolvesThemeAcrossAppAndOsMismatch(
        Microsoft.UI.Xaml.ElementTheme presenterRequested,
        Microsoft.UI.Xaml.ElementTheme popupRequested,
        Microsoft.UI.Xaml.ElementTheme rootRequested,
        Microsoft.UI.Xaml.ElementTheme rootActual,
        Microsoft.UI.Xaml.ElementTheme presenterActual,
        bool presenterIsLoaded,
        Microsoft.UI.Xaml.ElementTheme appEffective,
        bool systemIsDark,
        bool expectedIsDark)
    {
        bool actual = DeskBox.Helpers.ThemedMenuAcrylicBackdrop.ResolveIsDarkTheme(
            presenterRequested,
            popupRequested,
            rootRequested,
            rootActual,
            presenterActual,
            presenterIsLoaded,
            appEffective,
            systemIsDark);

        Assert.Equal(expectedIsDark, actual);
    }

    [Fact]
    public void ContentAreaMenu_SplitsViewAndSort_AndAddsNewSubmenu()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/" +
            "FileSurfaceContent.SelectionAndMenus.cs");

        Assert.Contains(
            "T(\"Widget.View\")",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "T(\"Widget.SortBy\")",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "private MenuFlyoutSubItem CreateNewSubMenu",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShellNewMenuHelper.GetEntries()",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "T(\"Widget.ViewAndSort\")",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpenWithAndNew_Paths_NeverHostClassicContextMenu()
    {
        string openWithInterop = ReadRepositoryFile(
            "src/DeskBox/Platform/ShellOpenWithInterop.cs");
        string openWithHelper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellOpenWithHelper.cs");
        string shellNew = ReadRepositoryFile(
            "src/DeskBox/Helpers/ShellNewMenuHelper.cs");

        foreach (string source in new[] { openWithInterop, openWithHelper })
        {
            Assert.DoesNotContain(
                "IContextMenu",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "QueryContextMenu",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "TrackPopupMenuEx",
                source,
                StringComparison.Ordinal);
        }

        // The association API entry points stay pinned so the interop can
        // never silently drift onto the classic menu path.
        Assert.Contains(
            "SHAssocEnumHandlers",
            openWithInterop,
            StringComparison.Ordinal);
        Assert.Contains(
            "AssocFilterRecommended",
            openWithInterop,
            StringComparison.Ordinal);
        Assert.Contains(
            "Registry.ClassesRoot",
            shellNew,
            StringComparison.Ordinal);

        // 2026-10-16 crash pins: interface-method arrays default to
        // SAFEARRAY marshaling and the SafeArray marshaler hard-crashes the
        // CLR on IntPtr[]; Invoke requires a real IDataObject obtained via
        // BindToHandler(BHID_DataObject). The 2026-10-20 AOT migration
        // replaced the ComImport declaration with raw vtable delegates
        // (classic RCW marshaling does not exist on NativeAOT — the retail
        // Open With submenu shipped empty), so the array guard now pins the
        // delegate shape: a plain IntPtr[] parameter never engages the
        // SAFEARRAY marshaler.
        Assert.Contains(
            "IntPtr self, uint celt, IntPtr[] rgelt, out uint pceltFetched",
            openWithInterop,
            StringComparison.Ordinal);
        Assert.Contains(
            "b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4",
            openWithInterop,
            StringComparison.Ordinal);
        Assert.Contains(
            "BindToHandler",
            openWithInterop,
            StringComparison.Ordinal);

        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/" +
            "FileSurfaceContent.SelectionAndMenus.cs");
        // The row label is built via Localize in the builder; the host
        // supplies the submenu entries including the choose-app tail.
        Assert.Contains(
            "\"Widget.OpenWith\"",
            ReadRepositoryFile("src/DeskBox/Controls/FileItemMenuBuilder.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "T(\"Widget.OpenWith.ChooseApp\")",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResultsMenu_KeepsRunAsAdministratorEntry()
    {
        string searchWindow = ReadRepositoryFile(
            "src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        Assert.Contains(
            "ElevatedFileLauncher.CanRunAsAdministrator(item.DetailPath",
            searchWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Widget.RunAsAdministrator",
            searchWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Localization_NewKeysPresent_RetiredKeyGone_AllLanguages()
    {
        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] files = Directory.GetFiles(stringsDir, "*.json");
        Assert.True(files.Length >= 12, "expected the full language set");

        string[] newKeys =
        [
            "Widget.OpenWith",
            "Widget.OpenWith.ChooseApp",
            "Widget.ExtractAll",
            "Widget.EmptyRecycleBin",
            "Widget.DropOnRecycleBinHint",
            "Widget.CompressTo",
            "Widget.CompressTo.Zip",
            "Widget.CompressTo.Tar",
            "Widget.New",
            "Widget.View",
            "Widget.SortBy",
            "Widget.NewFileName",
            "Widget.Zip.CompressFailed",
            "Widget.Zip.ExtractFailed",
        ];

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string key in newKeys)
            {
                Assert.Contains($"\"{key}\":", text, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(
                "\"Widget.ViewAndSort\":",
                text,
                StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
