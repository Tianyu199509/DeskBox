using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetManagerStorageCleanupTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _storageRoot;
    private readonly string _desktopRoot;
    private readonly SettingsService _settingsService;
    private readonly WidgetManager _widgetManager;

    public WidgetManagerStorageCleanupTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "storage")).FullName;
        _desktopRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;

        _settingsService = new SettingsService(Path.Combine(_tempRoot, "settings"));
        _settingsService.Settings.DefaultManagedStorageRootPath = _storageRoot;

        var fileService = new FileService();
        var organizerService = new OrganizerService(_settingsService, fileService);
        var themeService = new ThemeService(_settingsService);
        _widgetManager = new WidgetManager(
            _settingsService,
            fileService,
            organizerService,
            themeService,
            new QuickCaptureService(new QuickCaptureStore(Path.Combine(_tempRoot, "quick-capture"))),
            () => _desktopRoot,
            recycleManagedFolderDeletes: false);
    }

    [Fact]
    public void GetOrphanManagedStorageFolders_ReturnsOnlyUntrackedRootChildren()
    {
        string activeFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Active")).FullName;
        string orphanFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Orphan")).FullName;
        string mappedFolderOutsideRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped")).FullName;
        File.WriteAllText(Path.Combine(orphanFolder, "note.txt"), "orphan");

        _settingsService.Settings.Widgets.Add(CreateManagedWidget("Active", activeFolder));
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "Mapped",
            MappedFolderPath = mappedFolderOutsideRoot,
            FollowsDefaultStoragePath = false
        });

        var candidates = _widgetManager.GetOrphanManagedStorageFolders();

        var candidate = Assert.Single(candidates);
        Assert.Equal("Orphan", candidate.Name);
        Assert.Equal(orphanFolder, candidate.Path);
        Assert.Equal(1, candidate.ItemCount);
    }

    [Fact]
    public async Task MoveOrphanManagedStorageFolderContentsToDesktopAsync_MovesContentsAndDeletesEmptyFolder()
    {
        string orphanFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Orphan")).FullName;
        string sourcePath = Path.Combine(orphanFolder, "note.txt");
        string existingDesktopPath = Path.Combine(_desktopRoot, "note.txt");
        File.WriteAllText(sourcePath, "orphan");
        File.WriteAllText(existingDesktopPath, "existing");

        await _widgetManager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(orphanFolder);

        Assert.False(Directory.Exists(orphanFolder));
        Assert.Equal("existing", File.ReadAllText(existingDesktopPath));
        Assert.Equal("orphan", File.ReadAllText(Path.Combine(_desktopRoot, "note (2).txt")));
    }

    [Fact]
    public async Task MoveOrphanManagedStorageFolderContentsToDesktopAsync_RejectsActiveManagedFolder()
    {
        string activeFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Active")).FullName;
        string activeFile = Path.Combine(activeFolder, "note.txt");
        File.WriteAllText(activeFile, "active");
        _settingsService.Settings.Widgets.Add(CreateManagedWidget("Active", activeFolder));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _widgetManager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(activeFolder));

        Assert.True(Directory.Exists(activeFolder));
        Assert.Equal("active", File.ReadAllText(activeFile));
    }

    [Fact]
    public async Task DeleteOrphanManagedStorageFolderAsync_DeletesValidatedOrphanFolder()
    {
        string orphanFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Orphan")).FullName;
        File.WriteAllText(Path.Combine(orphanFolder, "note.txt"), "orphan");

        await _widgetManager.DeleteOrphanManagedStorageFolderAsync(orphanFolder);

        Assert.False(Directory.Exists(orphanFolder));
    }

    [Fact]
    public async Task RestoreOrphanManagedStorageFoldersAsync_CreatesManagedWidgetsForExistingFolders()
    {
        string orphanFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Orphan")).FullName;
        File.WriteAllText(Path.Combine(orphanFolder, "note.txt"), "orphan");

        int restored = await _widgetManager.RestoreOrphanManagedStorageFoldersAsync([orphanFolder]);

        Assert.Equal(1, restored);
        var widget = Assert.Single(_settingsService.Settings.Widgets);
        Assert.Equal("Orphan", widget.Name);
        Assert.Equal(WidgetKind.File, widget.WidgetKind);
        Assert.True(widget.FollowsDefaultStoragePath);
        Assert.Equal("Orphan", widget.ManagedFolderName);
        Assert.Equal(orphanFolder, widget.MappedFolderPath);
        Assert.True(widget.IsVisible);
        Assert.False(widget.IsDisabled);
        Assert.True(Directory.Exists(orphanFolder));
        Assert.Empty(_widgetManager.GetOrphanManagedStorageFolders());
    }

    [Fact]
    public async Task RemoveWidgetAsync_MoveManagedFolderContentsToDesktop_RemovesConfigAndMovesFiles()
    {
        string managedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Managed")).FullName;
        string sourcePath = Path.Combine(managedFolder, "note.txt");
        File.WriteAllText(sourcePath, "managed");
        var widget = CreateManagedWidget("Managed", managedFolder);
        _settingsService.Settings.Widgets.Add(widget);

        await _widgetManager.RemoveWidgetAsync(widget.Id, WidgetRemovalAction.MoveManagedFolderContentsToDesktop);

        Assert.DoesNotContain(_settingsService.Settings.Widgets, item => item.Id == widget.Id);
        Assert.Contains(widget.Id, _settingsService.Settings.DeletedWidgetIds);
        Assert.False(Directory.Exists(managedFolder));
        Assert.Equal("managed", File.ReadAllText(Path.Combine(_desktopRoot, "note.txt")));
    }

    [Fact]
    public async Task RemoveWidgetAsync_RejectsFolderCleanupForMappedFolder()
    {
        string mappedFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped")).FullName;
        string mappedFile = Path.Combine(mappedFolder, "note.txt");
        File.WriteAllText(mappedFile, "mapped");
        var widget = new WidgetConfig
        {
            Name = "Mapped",
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        };
        _settingsService.Settings.Widgets.Add(widget);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _widgetManager.RemoveWidgetAsync(widget.Id, WidgetRemovalAction.DeleteManagedFolder));

        Assert.Contains(_settingsService.Settings.Widgets, item => item.Id == widget.Id);
        Assert.DoesNotContain(widget.Id, _settingsService.Settings.DeletedWidgetIds);
        Assert.True(Directory.Exists(mappedFolder));
        Assert.Equal("mapped", File.ReadAllText(mappedFile));
    }

    [Fact]
    public async Task SaveQuickCaptureItemToFileWidgetAsync_WritesRealFiles()
    {
        string managedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Target")).FullName;
        var widget = CreateManagedWidget("Target", managedFolder);
        _settingsService.Settings.Widgets.Add(widget);

        string? textPath = await _widgetManager.SaveQuickCaptureItemToFileWidgetAsync(
            new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Text,
                Body = "hello world"
            },
            widget.Id,
            "Capture");

        string? linkPath = await _widgetManager.SaveQuickCaptureItemToFileWidgetAsync(
            new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Link,
                Body = "https://example.com/docs",
                Url = "https://example.com/docs"
            },
            widget.Id,
            "Capture");

        string sourceImagePath = Path.Combine(_tempRoot, "source.png");
        await File.WriteAllBytesAsync(sourceImagePath, [1, 2, 3, 4]);
        string? imagePath = await _widgetManager.SaveQuickCaptureItemToFileWidgetAsync(
            new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Image,
                Body = "Image",
                ImagePath = sourceImagePath,
                UpdatedAt = new DateTimeOffset(2026, 6, 21, 14, 32, 0, TimeSpan.Zero)
            },
            widget.Id,
            "Capture");

        Assert.NotNull(textPath);
        Assert.Equal("hello world", await File.ReadAllTextAsync(textPath));
        Assert.EndsWith(".txt", textPath, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(linkPath);
        Assert.Contains("URL=https://example.com/docs", await File.ReadAllTextAsync(linkPath));
        Assert.EndsWith(".url", linkPath, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(imagePath);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(imagePath));
        Assert.StartsWith("Capture ", Path.GetFileName(imagePath), StringComparison.Ordinal);
        Assert.EndsWith(".png", imagePath, StringComparison.OrdinalIgnoreCase);

        var target = Assert.Single(_widgetManager.GetQuickCaptureFileWidgetTargets());
        Assert.Equal(widget.Id, target.WidgetId);
        Assert.Equal(managedFolder, target.FolderPath);
        Assert.Equal(widget.Id, _settingsService.Settings.LastQuickCaptureFileWidgetId);

        var lastTarget = _widgetManager.GetLastQuickCaptureFileWidgetTarget();
        Assert.NotNull(lastTarget);
        Assert.Equal(widget.Id, lastTarget.WidgetId);
    }

    private static WidgetConfig CreateManagedWidget(string name, string folderPath)
    {
        return new WidgetConfig
        {
            Name = name,
            WidgetKind = WidgetKind.File,
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = true,
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
