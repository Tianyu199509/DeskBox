using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FileServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public FileServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Theory]
    [InlineData("  name  ", "name")]
    [InlineData("trailing.", "trailing")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SanitizeFileSystemName_NormalizesBasicInput(string input, string expected)
    {
        Assert.Equal(expected, FileService.SanitizeFileSystemName(input));
    }

    [Fact]
    public void SanitizeFileSystemName_ReplacesInvalidFileNameChars()
    {
        char invalidChar = Path.GetInvalidFileNameChars().First();

        string result = FileService.SanitizeFileSystemName($"left{invalidChar}right");

        Assert.Equal("left-right", result);
    }

    [Fact]
    public void GetAvailablePath_ReturnsDesiredPathWhenUnused()
    {
        string desiredPath = Path.Combine(_tempRoot, "item.txt");

        string result = FileService.GetAvailablePath(desiredPath);

        Assert.Equal(Path.GetFullPath(desiredPath), result);
    }

    [Fact]
    public void GetAvailablePath_AppendsIndexWhenPathExistsOrReserved()
    {
        string desiredPath = Path.Combine(_tempRoot, "item.txt");
        File.WriteAllText(desiredPath, "existing");
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_tempRoot, "item (2).txt")
        };

        string result = FileService.GetAvailablePath(desiredPath, reservedPaths);

        Assert.Equal(Path.Combine(_tempRoot, "item (3).txt"), result);
        Assert.Contains(result, reservedPaths);
    }

    [Fact]
    public void IsPathUnderDirectory_MatchesSelfAndChildrenOnly()
    {
        string root = Path.Combine(_tempRoot, "root");
        string child = Path.Combine(root, "child", "file.txt");
        string sibling = Path.Combine(_tempRoot, "root-other", "file.txt");

        Assert.True(FileService.IsPathUnderDirectory(root, root));
        Assert.True(FileService.IsPathUnderDirectory(child, root));
        Assert.False(FileService.IsPathUnderDirectory(sibling, root));
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_MovesFilesToAvailableNames()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string destinationDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "destination")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        string existingDestinationPath = Path.Combine(destinationDirectory, "note.txt");
        File.WriteAllText(sourcePath, "source");
        File.WriteAllText(existingDestinationPath, "existing");

        var results = await service.TransferItemsWithResultAsync([sourcePath], destinationDirectory, move: true);

        var result = Assert.Single(results);
        Assert.Equal(sourcePath, result.SourcePath);
        Assert.Equal(Path.Combine(destinationDirectory, "note (2).txt"), result.DestinationPath);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal("source", File.ReadAllText(result.DestinationPath));
        Assert.Equal("existing", File.ReadAllText(existingDestinationPath));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_CopiesDirectoryRecursively()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        string nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested")).FullName;
        string sourceFile = Path.Combine(nestedDirectory, "file.txt");
        File.WriteAllText(sourceFile, "content");

        string destinationDirectory = Path.Combine(_tempRoot, "destination-folder");
        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
            move: false);

        var result = Assert.Single(results);
        Assert.Equal(sourceDirectory, result.SourcePath);
        Assert.Equal(destinationDirectory, result.DestinationPath);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationDirectory, "nested", "file.txt")));
    }

    [Fact]
    public async Task ExecuteTransferPlanAsync_MovesDeepDirectoryWithoutMissingOrDuplicatingFiles()
    {
        var service = new FileService();
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-folder")).FullName;
        string level1 = Directory.CreateDirectory(Path.Combine(sourceDirectory, "level1")).FullName;
        string level2 = Directory.CreateDirectory(Path.Combine(level1, "level2")).FullName;
        string level3 = Directory.CreateDirectory(Path.Combine(level2, "level3")).FullName;
        File.WriteAllText(Path.Combine(sourceDirectory, "root.txt"), "root");
        File.WriteAllText(Path.Combine(level1, "one.txt"), "one");
        File.WriteAllText(Path.Combine(level2, "two.txt"), "two");
        File.WriteAllText(Path.Combine(level3, "three.txt"), "three");

        string destinationDirectory = Path.Combine(_tempRoot, "destination-folder");
        var results = await service.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceDirectory, destinationDirectory)],
            move: true);

        var result = Assert.Single(results);
        Assert.Equal(sourceDirectory, result.SourcePath);
        Assert.Equal(destinationDirectory, result.DestinationPath);
        Assert.False(Directory.Exists(sourceDirectory));
        Assert.Equal(4, Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories).Count());
        Assert.Equal("root", File.ReadAllText(Path.Combine(destinationDirectory, "root.txt")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(destinationDirectory, "level1", "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(destinationDirectory, "level1", "level2", "two.txt")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(destinationDirectory, "level1", "level2", "level3", "three.txt")));
    }

    [Fact]
    public async Task TransferItemsWithResultAsync_MovesDeepDirectoryToAvailableNameWhenDestinationExists()
    {
        var service = new FileService();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string destinationRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "destination")).FullName;
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "project")).FullName;
        string nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested", "child")).FullName;
        File.WriteAllText(Path.Combine(nestedDirectory, "file.txt"), "content");
        Directory.CreateDirectory(Path.Combine(destinationRoot, "project"));

        var results = await service.TransferItemsWithResultAsync([sourceDirectory], destinationRoot, move: true);

        var result = Assert.Single(results);
        string expectedDestination = Path.Combine(destinationRoot, "project (2)");
        Assert.Equal(expectedDestination, result.DestinationPath);
        Assert.False(Directory.Exists(sourceDirectory));
        Assert.True(Directory.Exists(Path.Combine(destinationRoot, "project")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(expectedDestination, "nested", "child", "file.txt")));
        Assert.Single(Directory.EnumerateFiles(expectedDestination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_SkipsHiddenAndDesktopIniEntries()
    {
        var service = new FileService();
        string visibleFile = Path.Combine(_tempRoot, "visible.txt");
        string desktopIni = Path.Combine(_tempRoot, "desktop.ini");
        string hiddenFile = Path.Combine(_tempRoot, "hidden.txt");
        File.WriteAllText(visibleFile, "visible");
        File.WriteAllText(desktopIni, "desktop");
        File.WriteAllText(hiddenFile, "hidden");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        var items = await service.EnumerateDirectoryAsync(_tempRoot);

        var item = Assert.Single(items);
        Assert.Equal("visible", item.Name);
        Assert.Equal(visibleFile, item.Path);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_CanShowFileExtensions()
    {
        var service = new FileService();
        string visibleFile = Path.Combine(_tempRoot, "visible.txt");
        File.WriteAllText(visibleFile, "visible");

        var items = await service.EnumerateDirectoryAsync(_tempRoot, showFileExtensions: true);

        var item = Assert.Single(items);
        Assert.Equal("visible.txt", item.Name);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_ExcludesShortcutExtensionByDefaultWhenShowingFileExtensions()
    {
        var service = new FileService();
        string shortcutFile = Path.Combine(_tempRoot, "app.lnk");
        File.WriteAllText(shortcutFile, "shortcut");

        var items = await service.EnumerateDirectoryAsync(_tempRoot, showFileExtensions: true);

        var item = Assert.Single(items);
        Assert.Equal("app", item.Name);
    }

    [Fact]
    public async Task EnumerateDirectoryAsync_CanShowShortcutExtension()
    {
        var service = new FileService();
        string shortcutFile = Path.Combine(_tempRoot, "app.lnk");
        File.WriteAllText(shortcutFile, "shortcut");

        var items = await service.EnumerateDirectoryAsync(
            _tempRoot,
            showFileExtensions: true,
            hideShortcutExtensionWhenShowingFileExtensions: false);

        var item = Assert.Single(items);
        Assert.Equal("app.lnk", item.Name);
    }

    [Fact]
    public async Task CreateWidgetItemAsync_FolderSecondaryInfoShowsVisibleItemCountOnly()
    {
        var service = new FileService();
        string folder = Directory.CreateDirectory(Path.Combine(_tempRoot, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "first.txt"), "first");
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "desktop");
        string hiddenFile = Path.Combine(folder, "hidden.txt");
        File.WriteAllText(hiddenFile, "hidden");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        var item = await service.CreateWidgetItemAsync(folder);

        Assert.True(item.IsFolder);
        Assert.Equal(1, item.FolderItemCount);
        Assert.Equal("1 项", item.SecondaryInfo);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            File.SetAttributes(_tempRoot, FileAttributes.Normal);
            foreach (string path in Directory.EnumerateFileSystemEntries(_tempRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
