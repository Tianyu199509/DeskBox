using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Default-verb dispatch: template documents (feedback 366 / confirmed 81)
/// and shortcuts to local-filesystem targets (#459 folders, feedback 246
/// apps) must be opened with the Shell's default verb — the desktop
/// double-click dispatch, launched locally — because an explicit "open" verb
/// overwrites template semantics and dies silently inside Explorer's
/// fire-and-forget dispatch on machines whose OS-side "open" resolution is
/// damaged. Every other kind keeps the existing explorer-first pipeline, and
/// the #339 search-popup boundary stays untouched.
/// </summary>
public sealed class FileServiceOpenItemTests
{
    [Fact]
    public void SelectOpenDispatchMode_TemplatesUseTheDefaultVerb()
    {
        // The template table wins for plain items of every template kind,
        // whatever the case of the extension.
        foreach (string path in new[]
                 {
                     @"C:\Templates\Book.xltx",
                     @"C:\Templates\Book.xltm",
                     @"C:\Templates\Letter.dotx",
                     @"C:\Templates\Letter.dotm",
                     @"C:\Templates\Deck.potx",
                     @"C:\Templates\Deck.potm",
                     @"C:\Templates\Flowchart.vstx",
                     @"C:\Templates\Flowchart.vstm",
                     @"C:\Templates\Ledger.xlt",
                     @"C:\Templates\Memo.dot",
                     @"C:\Templates\Sheet.ots",
                     @"C:\Templates\Minutes.ott",
                     @"C:\Templates\UPPER.XLTX"
                 })
        {
            Assert.Equal(
                OpenItemDispatchMode.LocalDefaultVerb,
                FileService.SelectOpenDispatchMode(
                    isShellLinkShortcut: false,
                    targetKind: ShortcutTargetKind.LocalFileSystem,
                    pathToOpen: path));
        }
    }

    [Fact]
    public void SelectOpenDispatchMode_LocalShortcutsUseTheDefaultVerb()
    {
        // Folder shortcuts (#459): the Folder default handler — possibly a
        // third-party file manager — must take over.
        Assert.Equal(
            OpenItemDispatchMode.LocalDefaultVerb,
            FileService.SelectOpenDispatchMode(
                isShellLinkShortcut: true,
                targetKind: ShortcutTargetKind.LocalFileSystem,
                pathToOpen: @"C:\Tools\folder.lnk"));

        // App/document shortcuts to existing local files (feedback 246):
        // the explorer-hosted explicit-"open" dispatch is fire-and-forget,
        // so these join the local default-verb dispatch too.
        Assert.Equal(
            OpenItemDispatchMode.LocalDefaultVerb,
            FileService.SelectOpenDispatchMode(
                isShellLinkShortcut: true,
                targetKind: ShortcutTargetKind.LocalFileSystem,
                pathToOpen: @"C:\Tools\app.lnk"));
    }

    [Fact]
    public void SelectOpenDispatchMode_NonLocalShortcutTargetsKeepExplorer()
    {
        // UNC, network and URI/shell-namespace targets stay with Explorer,
        // which owns credentials and shell-item resolution for those.
        foreach (ShortcutTargetKind kind in new[]
                 {
                     ShortcutTargetKind.Unc,
                     ShortcutTargetKind.NetworkDrive,
                     ShortcutTargetKind.UriOrShellNamespace,
                     ShortcutTargetKind.Unknown
                 })
        {
            Assert.Equal(
                OpenItemDispatchMode.ShellDispatch,
                FileService.SelectOpenDispatchMode(
                    isShellLinkShortcut: true,
                    targetKind: kind,
                    pathToOpen: @"C:\Tools\shortcut.lnk"));
        }
    }

    [Fact]
    public void SelectOpenDispatchMode_PlainFoldersAndFilesKeepExplorer()
    {
        // Plain folders and files are the #339 scope, not the default-verb
        // table: unchanged.
        Assert.Equal(
            OpenItemDispatchMode.ShellDispatch,
            FileService.SelectOpenDispatchMode(
                isShellLinkShortcut: false,
                targetKind: ShortcutTargetKind.LocalFileSystem,
                pathToOpen: @"C:\Users\simon\Documents"));
        Assert.Equal(
            OpenItemDispatchMode.ShellDispatch,
            FileService.SelectOpenDispatchMode(
                isShellLinkShortcut: false,
                targetKind: ShortcutTargetKind.LocalFileSystem,
                pathToOpen: @"C:\Users\simon\Documents\Report.docx"));
    }

    [Fact]
    public void ShortcutTargetProbe_ReportsDirectoryTargets()
    {
        string shortcutPath = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.Tests-{Guid.NewGuid():N}.lnk");
        string targetDirectory = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.Tests-{Guid.NewGuid():N}.dir");
        string targetFile = Path.Combine(
            Path.GetTempPath(),
            $"DeskBox.Tests-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(shortcutPath, "placeholder");
        File.WriteAllText(targetFile, "placeholder");
        try
        {
            ShortcutTargetProbeResult directoryResult = ShortcutTargetProbe.Probe(
                shortcutPath,
                targetDirectory);
            Assert.Equal(ShortcutTargetKind.LocalFileSystem, directoryResult.Kind);
            Assert.Equal(ShortcutTargetStatus.Existing, directoryResult.Status);
            Assert.True(directoryResult.TargetIsDirectory);

            ShortcutTargetProbeResult fileResult = ShortcutTargetProbe.Probe(
                shortcutPath,
                targetFile);
            Assert.Equal(ShortcutTargetKind.LocalFileSystem, fileResult.Kind);
            Assert.Equal(ShortcutTargetStatus.Existing, fileResult.Status);
            Assert.False(fileResult.TargetIsDirectory);
        }
        finally
        {
            File.Delete(shortcutPath);
            File.Delete(targetFile);
            Directory.Delete(targetDirectory);
        }
    }

    [Fact]
    public void OpenItemCore_DispatchesTemplatesAndLocalShortcutsThroughTheDefaultVerbBranch()
    {
        string openItem = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/FileService.OpenItem.cs"));

        Assert.Contains(
            "OpenItemDispatchMode.LocalDefaultVerb",
            openItem,
            StringComparison.Ordinal);
        Assert.Contains(
            "Win32Helper.OpenWithDefaultVerbLocally(",
            openItem,
            StringComparison.Ordinal);
        Assert.Contains(
            "TemplateDocumentVerbPolicy.IsTemplateDocument(pathToOpen)",
            openItem,
            StringComparison.Ordinal);
        // The legacy pipeline stays for everything else.
        Assert.Contains(
            "Win32Helper.OpenFile(ownerHwnd, pathToOpen)",
            openItem,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultVerbLaunch_ReusesTheLocalPipelineWithoutTheOpenVerbOrExplorer()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));
        string method = Slice(
            source,
            "internal static bool OpenWithDefaultVerbLocally",
            "private static IDisposable? SuppressElectronRunAsNodeForChildLaunch");

        Assert.DoesNotContain("Verb = \"open\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ExplorerShellLaunchService", method, StringComparison.Ordinal);
        // The desktop double-click dispatch keeps the local pipeline: the
        // ELECTRON_RUN_AS_NODE scrub and the pending observation must apply
        // here exactly as they do for the explicit-"open" local fallback.
        Assert.Contains(
            "SuppressElectronRunAsNodeForChildLaunch();",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "WatchPendingLocalShellExecute(path);",
            method,
            StringComparison.Ordinal);

        string fallback = Slice(
            source,
            "public static bool OpenFileOrChooseApp",
            "internal static string ResolveShellLaunchDirectory");
        Assert.Contains(
            "SuppressElectronRunAsNodeForChildLaunch();",
            fallback,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the feedback 246 half of the fix: neither the local fallback in
    /// <c>OpenFileOrChooseApp</c> nor the drop-on-shortcut launcher may force
    /// an explicit "open" verb. The desktop double-click dispatch is a NULL
    /// verb, and only that dispatch survives machines whose OS-side "open"
    /// resolution alone is damaged.
    /// </summary>
    [Fact]
    public void LocalLaunchFallbacks_UseTheDefaultVerbNotExplicitOpen()
    {
        string win32Helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));
        string openFile = Slice(
            win32Helper,
            "public static bool OpenFileOrChooseApp",
            "internal static bool OpenWithDefaultVerbLocally");
        Assert.DoesNotContain("Verb = \"open\"", openFile, StringComparison.Ordinal);

        string shortcutLauncher = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/ShortcutFileLauncher.cs"));
        Assert.DoesNotContain(
            "Verb = \"open\"",
            shortcutLauncher,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the #339 boundary: the search popup's hard-coded explorer.exe
    /// dispatch for folders is a separate issue and must not move in #459's
    /// wake.
    /// </summary>
    [Fact]
    public void SearchPopupFolderOpen_StillUsesExplorerForNow()
    {
        string search = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/SearchPopupViewModel.cs"));

        Assert.Contains(
            """System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");""",
            search,
            StringComparison.Ordinal);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
