using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class ShellNewMenuHelperTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "deskbox-shellnew-tests-" +
            Guid.NewGuid().ToString("N"));

    public ShellNewMenuHelperTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void BuildEntries_KeepsNullFileTemplateAndDataRegistrations()
    {
        string template = Path.Combine(_root, "tpl.docx");
        File.WriteAllText(template, "template");

        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".txt", null, "文本文档", null, "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".docx", null, "Word 文档", null, null,
                "tpl.docx", null, template),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".zip", null, "ZIP 压缩文件", "新建压缩(zipped)文件夹",
                "", null, null, null, [0x50, 0x4B, 0x05, 0x06]),
            // Data-only shape (the nested CompressedFolder layout): no
            // NullFile, the blob alone is the creation payload.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".7z", null, "7-Zip", null, null, null, null, null,
                [0x37, 0x7A]),
        ]);

        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, e =>
            e.Extension == ".txt" && e.CreatesEmptyFile);
        Assert.Contains(entries, e =>
            e.Extension == ".docx" &&
            e.TemplatePath == template &&
            !e.CreatesEmptyFile);
        Assert.Contains(entries, e =>
            e.Extension == ".zip" &&
            e.TemplateData is { Length: > 0 });
        Assert.Contains(entries, e =>
            e.Extension == ".7z" &&
            e.TemplateData is [0x37, 0x7A]);
    }

    [Fact]
    public void BuildEntries_SkipsUnsupportedRegistrations()
    {
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            // Command-only: launches an app instead of creating a file.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".cmd", null, "CMD", null, null, null, "make.cmd", null),
            // Template file no longer exists.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".bmp", null, "BMP", null, null, "gone.bmp", null,
                Path.Combine(_root, "missing.bmp")),
            // Value-less ShellNew key (the VMware family): nothing to create.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".vmx", null, "VMware", null, null, null, null, null),
            // Shortcut is a separate wizard entry.
            new ShellNewMenuHelper.ShellNewKeyData(
                ".lnk", "快捷方式", "Shortcut", null, "", null, null, null),
            // Not an extension-shaped key.
            new ShellNewMenuHelper.ShellNewKeyData(
                "txtfile", null, "Text", null, "", null, null, null),
        ]);

        Assert.Empty(entries);
    }

    [Fact]
    public void BuildEntries_MenuTextBeatsTypeName()
    {
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".rtf", "富文本文档", "RTF 文档", null, "", null, null, null),
        ]);

        Assert.Equal("富文本文档", Assert.Single(entries).DisplayName);
    }

    [Fact]
    public void BuildEntries_NamelessRegistrationIsSkipped()
    {
        // No MenuText and no resolvable type name: the native menu hides
        // the row entirely rather than showing a bare extension.
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".zzz", null, null, null, "", null, null, null),
        ]);

        Assert.Empty(entries);
    }

    [Fact]
    public void BuildEntries_FirstUsableShapePerExtensionWins()
    {
        // An unsupported first shape (Command-only) must not shadow the
        // usable ProgId-laid-out second shape for the same extension.
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".aaa", null, "Alpha", null, null, null, "run", null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".aaa", null, "Alpha", null, "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".aaa", null, "Beta", null, "", null, null, null),
        ]);

        ShellNewEntry entry = Assert.Single(entries);
        Assert.Equal("Alpha", entry.DisplayName);
    }

    [Fact]
    public void BuildEntries_SortsByExtensionLikeTheNativeMenu()
    {
        // Explorer orders the New submenu by extension, not display name
        // (.docx before .md before .zip even though the labels reorder).
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".zip", null, "坚果云绘图", null, "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".md", null, "WorkBuddy Document", null, "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".docx", null, "Microsoft Word 文档", null, "", null, null, null),
        ]);

        Assert.Equal(
            [".docx", ".md", ".zip"],
            entries.Select(e => e.Extension).ToArray());
    }

    [Fact]
    public void BuildEntries_ItemNameRidesAsFileNameBase()
    {
        var entries = ShellNewMenuHelper.BuildEntries(
        [
            new ShellNewMenuHelper.ShellNewKeyData(
                ".zip", null, "ZIP 压缩文件", "新建压缩(zipped)文件夹",
                "", null, null, null),
            new ShellNewMenuHelper.ShellNewKeyData(
                ".txt", null, "文本文档", null, "", null, null, null),
        ]);

        Assert.Equal(
            "新建压缩(zipped)文件夹",
            entries.Single(e => e.Extension == ".zip").FileNameBase);
        Assert.Null(entries.Single(e => e.Extension == ".txt").FileNameBase);
    }

    [Fact]
    public void StripMenuMnemonic_RemovesAcceleratorGroups()
    {
        Assert.Equal(
            "快捷方式",
            ShellNewMenuHelper.StripMenuMnemonic("快捷方式(&S)"));
        Assert.Equal(
            "Shortcut",
            ShellNewMenuHelper.StripMenuMnemonic("&Shortcut"));
        Assert.Equal(
            "A",
            ShellNewMenuHelper.StripMenuMnemonic("A(&B)(&C)"));
        // Prose parentheses survive.
        Assert.Equal(
            "备注(不属于加速键)",
            ShellNewMenuHelper.StripMenuMnemonic("备注(不属于加速键)"));
        Assert.Null(ShellNewMenuHelper.StripMenuMnemonic(null));
    }

    [Fact]
    public void CreateFile_EmptyFileEntry_UsesFormatterAndExtension()
    {
        var entry = new ShellNewEntry(".txt", "文本文档", null);

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            name => $"新建{name}");

        Assert.NotNull(created);
        Assert.EndsWith("新建文本文档.txt", created, StringComparison.Ordinal);
        Assert.True(File.Exists(created));
        Assert.Empty(File.ReadAllText(created!));
    }

    [Fact]
    public void CreateFile_DataBlobSeedsFileContent()
    {
        var entry = new ShellNewEntry(".zip", "zip", null, [1, 2, 3]);

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            name => name);

        Assert.NotNull(created);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(created!));
    }

    [Fact]
    public void CreateFile_FileNameBaseIsUsedVerbatim()
    {
        // ItemName already carries the OS-local "新建…" wording, so the
        // formatter must not be applied on top of it.
        var entry = new ShellNewEntry(
            ".zip", "ZIP 压缩文件", null,
            [0x50, 0x4B, 0x05, 0x06],
            "新建压缩(zipped)文件夹");

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            _ => throw new InvalidOperationException("formatter must not run"));

        Assert.NotNull(created);
        Assert.EndsWith(
            "新建压缩(zipped)文件夹.zip",
            created,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFile_TemplateEntry_CopiesTemplate()
    {
        string template = Path.Combine(_root, "tpl2.docx");
        File.WriteAllText(template, "payload");
        var entry = new ShellNewEntry(".docx", "Word", template);

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            name => name);

        Assert.NotNull(created);
        Assert.Equal("payload", File.ReadAllText(created!));
    }

    [Fact]
    public void CreateFile_Collision_GetsSuffix()
    {
        var entry = new ShellNewEntry(".txt", "Text", null);
        File.WriteAllText(Path.Combine(_root, "new text.txt"), "busy");

        string? created = ShellNewMenuHelper.CreateFile(
            entry,
            _root,
            _ => "new text");

        Assert.NotNull(created);
        Assert.EndsWith("new text (2).txt", created, StringComparison.Ordinal);
    }
}
