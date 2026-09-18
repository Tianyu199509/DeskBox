using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class OrganizerServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _desktopRoot;
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
    private readonly OrganizerService _organizerService;

    public OrganizerServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _desktopRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;

        _settingsService = new SettingsService(Path.Combine(_tempRoot, "settings"));
        _fileService = new FileService();
        _organizerService = new OrganizerService(_settingsService, _fileService, () => _desktopRoot);
    }

    [Fact]
    public async Task OrganizeDropAsync_Move_RecordsUndoableHistoryAndMovesFile()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);

        var operation = await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: true);

        string destinationPath = Path.Combine(targetDirectory, "note.txt");
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.True(operation.History.CanUndo);
        Assert.False(operation.History.IsFailed);
        Assert.Equal(OrganizationActionType.ManagedDrop, operation.History.ActionType);
        Assert.Equal("Move", operation.History.TransferMode);
        var item = Assert.Single(operation.CompletedItems);
        Assert.Equal(sourcePath, item.SourcePath);
        Assert.Equal(destinationPath, item.DestinationPath);
        Assert.Same(operation.History, Assert.Single(_settingsService.OrganizationHistory.Entries));
    }

    [Fact]
    public async Task OrganizeDropAsync_Copy_RecordsNonUndoableHistoryAndKeepsSourceFile()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);

        var operation = await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: false);

        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(Path.Combine(targetDirectory, "note.txt")));
        Assert.False(operation.History.CanUndo);
        Assert.Equal("Copy", operation.History.TransferMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrganizeDropAsync_SameDirectoryIsNoOpWithoutNumberedDuplicate(
        bool move)
    {
        string targetDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "same-directory")).FullName;
        string sourcePath = Path.Combine(targetDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        WidgetConfig widget = CreateWidget(targetDirectory);

        var operation =
            await _organizerService.OrganizeDropAsync(
                widget,
                "Widget",
                [sourcePath],
                move);

        Assert.Empty(operation.CompletedItems);
        Assert.False(operation.History.CanUndo);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(targetDirectory, "note (2).txt")));
        Assert.Empty(_settingsService.OrganizationHistory.Entries);
    }

    [Fact]
    public async Task OrganizeDropAsync_DoesNotBroadcastGlobalSettingsChanged()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-notify")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget-notify")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);
        int settingsChangedCount = 0;
        _settingsService.SettingsChanged += () => settingsChangedCount++;

        await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: false);

        Assert.Equal(0, settingsChangedCount);
        Assert.Single(_settingsService.OrganizationHistory.Entries);
    }

    [Fact]
    public async Task OrganizeDropAsync_UsesCurrentChildFolderDestination()
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "source-child-target")).FullName;
        string mappedRoot = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "widget-child-target")).FullName;
        string childFolder = Directory.CreateDirectory(
            Path.Combine(mappedRoot, "child")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        WidgetConfig widget = CreateWidget(mappedRoot);

        var operation =
            await _organizerService.OrganizeDropAsync(
                widget,
                "Widget",
                [sourcePath],
                move: false,
                destinationFolderPath: childFolder);

        Assert.True(File.Exists(Path.Combine(childFolder, "note.txt")));
        Assert.Equal(
            childFolder,
            Path.GetDirectoryName(operation.CompletedItems.Single().DestinationPath));
    }

    [Fact]
    public async Task OrganizeDropAsync_RejectsDestinationOutsideMappedRoot()
    {
        string sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "source-outside-target")).FullName;
        string mappedRoot = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "widget-outside-target")).FullName;
        string outsideFolder = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "outside-target")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        WidgetConfig widget = CreateWidget(mappedRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _organizerService.OrganizeDropAsync(
                widget,
                "Widget",
                [sourcePath],
                move: false,
                destinationFolderPath: outsideFolder));
    }

    [Fact]
    public async Task UndoLatestAsync_RestoresMovedFileAndMarksHistoryUndone()
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "source")).FullName;
        string targetDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "widget")).FullName;
        string sourcePath = Path.Combine(sourceDirectory, "note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(targetDirectory);
        var operation = await _organizerService.OrganizeDropAsync(widget, "Widget", [sourcePath], move: true);

        bool undone = await _organizerService.UndoLatestAsync();

        Assert.True(undone);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(targetDirectory, "note.txt")));
        Assert.True(operation.History.IsUndone);
        Assert.False(operation.History.CanUndo);
    }

    [Fact]
    public async Task MoveItemBackToDesktopAsync_RejectsWidgetsWithoutMappedFolder()
    {
        var widget = CreateWidget(string.Empty, followsDefaultStoragePath: false);
        var item = new WidgetItem
        {
            Path = Path.Combine(_tempRoot, "missing.txt"),
            Name = "missing"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _organizerService.MoveItemBackToDesktopAsync(widget, "Widget", item));
    }

    [Fact]
    public async Task MoveItemBackToDesktopAsync_AllowsMappedFolderWidgets()
    {
        string widgetFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped")).FullName;
        string sourcePath = Path.Combine(widgetFolder, "mapped-note.txt");
        File.WriteAllText(sourcePath, "content");
        var widget = CreateWidget(widgetFolder, followsDefaultStoragePath: false);
        var item = new WidgetItem
        {
            Path = sourcePath,
            Name = "mapped-note"
        };

        var operation = await _organizerService.MoveItemBackToDesktopAsync(widget, "Widget", item);

        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(operation.CompletedItems.Single().DestinationPath));
        Assert.Equal(_desktopRoot, Path.GetDirectoryName(operation.CompletedItems.Single().DestinationPath));
        Assert.True(_organizerService.AutoOrganizationSuppressions.TryConsume(
            operation.CompletedItems.Single().DestinationPath));
    }

    private static WidgetConfig CreateWidget(string folderPath, bool followsDefaultStoragePath = true)
    {
        return new WidgetConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Widget",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = followsDefaultStoragePath,
            ManagedFolderName = Path.GetFileName(folderPath)
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
