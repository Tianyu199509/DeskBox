using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class RenameExtensionChangeTests
{
    private static string Resolve(
        string originalFileName,
        string sanitizedName,
        bool isFolder = false,
        bool isShortcut = false,
        bool showFileExtensions = true)
    {
        return FileService.ResolveRenameDestination(
            originalFileName,
            sanitizedName,
            isFolder,
            isShortcut,
            showFileExtensions,
            out _);
    }

    private static bool RequiresConfirmation(
        string originalFileName,
        string sanitizedName,
        bool isFolder = false,
        bool isShortcut = false,
        bool showFileExtensions = true)
    {
        FileService.ResolveRenameDestination(
            originalFileName,
            sanitizedName,
            isFolder,
            isShortcut,
            showFileExtensions,
            out bool requiresConfirmation);
        return requiresConfirmation;
    }

    // ── Rule 1: folders never touch extension logic ──

    [Fact]
    public void Folder_ReturnsInputWithoutExtensionLogic()
    {
        Assert.Equal("新建文件夹", Resolve("old folder", "新建文件夹", isFolder: true));
        Assert.False(RequiresConfirmation("old folder", "新建文件夹", isFolder: true));
    }

    // ── Rule 2: shortcuts always keep their original extension ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shortcut_AlwaysKeepsOriginalExtension(bool showFileExtensions)
    {
        Assert.Equal(
            "game.lnk",
            Resolve("game.lnk", "game", isShortcut: true, showFileExtensions: showFileExtensions));
        Assert.Equal(
            "game.exe.lnk",
            Resolve("game.lnk", "game.exe", isShortcut: true, showFileExtensions: showFileExtensions));
        Assert.Equal(
            "game.LNK",
            Resolve("game.lnk", "game.LNK", isShortcut: true, showFileExtensions: showFileExtensions));
        Assert.False(RequiresConfirmation(
            "game.lnk", "game.exe", isShortcut: true, showFileExtensions: showFileExtensions));
    }

    // ── Rule 3: hidden extensions append the original extension ──

    [Fact]
    public void HiddenExtensions_AppendsOriginalExtensionWithoutConfirmation()
    {
        Assert.Equal("report.txt", Resolve("report.txt", "report", showFileExtensions: false));
        Assert.Equal(
            "report.docx.txt",
            Resolve("report.txt", "report.docx", showFileExtensions: false));
        Assert.False(RequiresConfirmation("report.txt", "report.docx", showFileExtensions: false));
    }

    [Fact]
    public void HiddenExtensions_OriginalWithoutExtensionKeepsInputVerbatim()
    {
        // Explorer-parity quirk: an extensionless original can gain one silently
        // while extensions are hidden because the user cannot see it either way.
        Assert.Equal("notes.md", Resolve("README", "notes.md", showFileExtensions: false));
    }

    // ── Rule 4: same extension (case-insensitive) is a plain rename ──

    [Fact]
    public void VisibleExtensions_SameExtensionKeepsInputWithoutConfirmation()
    {
        Assert.Equal("b.txt", Resolve("a.txt", "b.txt"));
        Assert.Equal("a.txt", Resolve("a.TXT", "a.txt"));
        Assert.False(RequiresConfirmation("a.txt", "b.txt"));
    }

    [Fact]
    public void VisibleExtensions_DoubleExtensionInputIsNotAChange()
    {
        Assert.Equal("b.mp3.txt", Resolve("a.txt", "b.mp3.txt"));
        Assert.False(RequiresConfirmation("a.txt", "b.mp3.txt"));
    }

    [Fact]
    public void VisibleExtensions_InputOfBareExtensionKeepsIt()
    {
        Assert.Equal(".txt", Resolve("a.txt", ".txt"));
        Assert.False(RequiresConfirmation("a.txt", ".txt"));
    }

    // ── Rule 5: input without extension is a name-only edit ──

    [Fact]
    public void VisibleExtensions_InputWithoutExtensionAppendsOriginal()
    {
        Assert.Equal("song.mp3", Resolve("track.mp3", "song"));
        Assert.False(RequiresConfirmation("track.mp3", "song"));
    }

    // ── Rule 6: a different typed extension requires confirmation ──

    [Fact]
    public void VisibleExtensions_DifferentExtensionRequiresConfirmation()
    {
        Assert.Equal("xxx.mp3", Resolve("xxx.txt", "xxx.mp3"));
        Assert.True(RequiresConfirmation("xxx.txt", "xxx.mp3"));
    }

    [Fact]
    public void VisibleExtensions_GainingAnExtensionOnExtensionlessFileRequiresConfirmation()
    {
        Assert.Equal("notes.md", Resolve("README", "notes.md"));
        Assert.True(RequiresConfirmation("README", "notes.md"));
    }

    // ── Dotfile originals: Path.GetExtension treats the whole name as the extension ──

    [Fact]
    public void VisibleExtensions_DotfileOriginalSameNameIsNoChange()
    {
        Assert.Equal(".gitignore", Resolve(".gitignore", ".gitignore"));
        Assert.False(RequiresConfirmation(".gitignore", ".gitignore"));
    }

    [Fact]
    public void VisibleExtensions_DotfileOriginalWithoutLeadingDotAppendsBack()
    {
        // Pinned odd-but-deterministic outcome for the rare case of deleting a
        // dotfile's leading dot while extensions are visible.
        Assert.Equal("gitignore.gitignore", Resolve(".gitignore", "gitignore"));
        Assert.False(RequiresConfirmation(".gitignore", "gitignore"));
    }

    [Fact]
    public void VisibleExtensions_DotfileOriginalDifferentExtensionRequiresConfirmation()
    {
        Assert.Equal(".mp3", Resolve(".gitignore", ".mp3"));
        Assert.True(RequiresConfirmation(".gitignore", ".mp3"));
    }

    // ── Wiring contract: the confirmation gate exists in the rename pipeline ──

    [Fact]
    public void RenamePipeline_ConfirmationPrecedesCollisionCheckAndDeclineIsSilent()
    {
        string root = FindRepositoryRoot();
        string operations = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs"));

        int handlerIndex = operations.IndexOf(
            "ConfirmExtensionChangeHandler?.Invoke(sourcePath, destinationPath) != true",
            StringComparison.Ordinal);
        int existsIndex = operations.IndexOf(
            "Widget.Validation.TargetExists",
            StringComparison.Ordinal);
        Assert.True(handlerIndex >= 0, "extension-change confirmation is missing");
        Assert.True(existsIndex > handlerIndex, "confirmation must run before the collision check");
        Assert.Contains("requiresExtensionChangeConfirmation", operations, StringComparison.Ordinal);
        // A declined rename returns silently; it must not throw a validation error.
        Assert.Contains("Rename extension change declined", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamePipeline_LegacyAppendHelperIsGone()
    {
        string root = FindRepositoryRoot();
        string sortAndWatchers = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/WidgetViewModel.SortingAndWatchers.cs"));
        Assert.DoesNotContain("BuildRenameFileName", sortAndWatchers, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamePipeline_SurfaceWiresTheShellConfirmationToTheHostWindow()
    {
        string root = FindRepositoryRoot();
        string adapter = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileWidgetContentAdapter.cs"));
        Assert.Contains(
            "_viewModel.ConfirmExtensionChangeHandler = ConfirmExtensionRename;",
            adapter,
            StringComparison.Ordinal);
        Assert.Contains("Win32Helper.ConfirmExtensionChange(", adapter, StringComparison.Ordinal);
        Assert.Contains("Widget.Rename.ExtensionChangeWarning", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamePipeline_AllLanguagesShipTheWarningText()
    {
        string root = FindRepositoryRoot();
        string stringsDir = Path.Combine(root, "src/DeskBox/Strings");
        string[] languageFiles = Directory.GetFiles(stringsDir, "*.json");
        Assert.True(languageFiles.Length >= 12);
        foreach (string languageFile in languageFiles)
        {
            string content = File.ReadAllText(languageFile);
            Assert.True(
                content.Contains("Widget.Rename.ExtensionChangeWarning", StringComparison.Ordinal),
                $"Missing warning text in {Path.GetFileName(languageFile)}");
        }
    }

    [Fact]
    public void RenamePipeline_AppManifestEnablesThemedMessageBoxButtons()
    {
        // MessageBoxW renders its buttons in the caller's process; without the
        // comctl32 v6 activation context they fall back to the classic square
        // Win32 look instead of Explorer's themed buttons.
        string root = FindRepositoryRoot();
        string manifest = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/app.manifest"));
        Assert.Contains("Microsoft.Windows.Common-Controls", manifest, StringComparison.Ordinal);
        Assert.Contains("version=\"6.0.0.0\"", manifest, StringComparison.Ordinal);
        Assert.Contains("publicKeyToken=\"6595b64144ccf1df\"", manifest, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "DeskBox")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
