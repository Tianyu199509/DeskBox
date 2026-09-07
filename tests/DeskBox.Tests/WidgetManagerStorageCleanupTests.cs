using System.Diagnostics;
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
    public void EnsureFileWidgetPathAvailable_AllowsStrictlyNestedExternalWidgetPaths()
    {
        string mappedFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped", "projects")).FullName;
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "Projects",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        });

        Assert.Throws<InvalidOperationException>(() =>
            _widgetManager.EnsureFileWidgetPathAvailable(mappedFolder));
        _widgetManager.EnsureFileWidgetPathAvailable(
            Path.Combine(mappedFolder, "nested"));
        _widgetManager.EnsureFileWidgetPathAvailable(
            Path.GetDirectoryName(mappedFolder)!);

        _widgetManager.EnsureFileWidgetPathAvailable(Path.Combine(_tempRoot, "mapped", "sibling"));
    }

    [Fact]
    public void EnsureFileWidgetPathAvailable_RejectsOverlapWithManagedWidget()
    {
        string managedFolder = Directory.CreateDirectory(
            Path.Combine(_storageRoot, "Managed")).FullName;
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "Managed",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = managedFolder,
            FollowsDefaultStoragePath = true,
            ManagedFolderName = "Managed"
        });

        Assert.Throws<InvalidOperationException>(() =>
            _widgetManager.EnsureFileWidgetPathAvailable(
                Path.Combine(managedFolder, "nested")));
        Assert.Throws<InvalidOperationException>(() =>
            _widgetManager.EnsureFileWidgetPathAvailable(
                Path.GetDirectoryName(managedFolder)!));
    }

    [Fact]
    public void EnsureFileWidgetPathAvailable_RejectsManagedCandidateOverlappingExternalWidget()
    {
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "mapped", "projects")).FullName;
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "External",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        });

        Assert.Throws<InvalidOperationException>(() =>
            _widgetManager.EnsureFileWidgetPathAvailable(
                Path.Combine(mappedFolder, "managed"),
                candidateFollowsDefaultStoragePath: true));
    }

    [Fact]
    public void EnsureFileWidgetPathAvailable_RejectsExternalMappingOverlappingManagedRoot()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _widgetManager.EnsureFileWidgetPathAvailable(
                Path.Combine(_storageRoot, "external")));
        Assert.Throws<InvalidOperationException>(() =>
            _widgetManager.EnsureFileWidgetPathAvailable(
                Path.GetDirectoryName(_storageRoot)!));
    }

    [Fact]
    public void EnsureFileWidgetPathAvailable_RejectsAliasOfExistingExternalWidget()
    {
        string mappedFolder = Directory.CreateDirectory(
            Path.Combine(_tempRoot, "mapped-alias-target")).FullName;
        string mappedAlias = Path.Combine(_tempRoot, "mapped-alias");
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "External",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        });

        Assert.True(
            TryCreateDirectoryJunction(mappedAlias, mappedFolder),
            "The Windows test host must support creating a directory junction.");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                _widgetManager.EnsureFileWidgetPathAvailable(mappedAlias));
        }
        finally
        {
            TryDeleteDirectoryJunction(mappedAlias);
        }
    }

    [Fact]
    public void EnsureFileWidgetPathAvailable_AllowsCurrentWidgetWhenEditing()
    {
        string mappedFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped")).FullName;
        var widget = new WidgetConfig
        {
            Name = "Mapped",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        };
        _settingsService.Settings.Widgets.Add(widget);

        _widgetManager.EnsureFileWidgetPathAvailable(mappedFolder, widget.Id);
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
    public async Task RestoreWidgetsAsync_SkipsFutureContentWidgetsWhileRegistryIsClosed()
    {
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Id = "tags-hidden",
            Name = "Tags",
            WidgetKind = WidgetKind.Tags,
            IsVisible = true
        });

        await _widgetManager.RestoreWidgetsAsync();

        Assert.Empty(_widgetManager.ContentWidgets);
        Assert.False(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Tags));
    }

    [Fact]
    public void RepairLegacyContentFeatureFileShells_RemovesOnlyEmptyMusicFileShells()
    {
        var musicConfig = new WidgetConfig
        {
            Id = "music-real",
            Name = "Music",
            WidgetKind = WidgetKind.Music,
            IsVisible = true
        };
        var legacyShell = new WidgetConfig
        {
            Id = "music-shell",
            Name = "\u97F3\u4E50",
            WidgetKind = WidgetKind.File,
            IsVisible = true
        };
        var userFileWidget = new WidgetConfig
        {
            Id = "music-user-file",
            Name = "Music",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = _desktopRoot,
            IsVisible = true
        };
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Music, true);
        _settingsService.Settings.Widgets.Add(musicConfig);
        _settingsService.Settings.Widgets.Add(legacyShell);
        _settingsService.Settings.Widgets.Add(userFileWidget);

        int repaired = _widgetManager.RepairLegacyContentFeatureFileShells();

        Assert.Equal(1, repaired);
        Assert.DoesNotContain(_settingsService.Settings.Widgets, widget => widget.Id == legacyShell.Id);
        Assert.Contains(_settingsService.Settings.Widgets, widget => widget.Id == musicConfig.Id);
        Assert.Contains(_settingsService.Settings.Widgets, widget => widget.Id == userFileWidget.Id);
        Assert.Contains(legacyShell.Id, _settingsService.Settings.DeletedWidgetIds);
    }

    [Fact]
    public async Task CreateWidgetFromConfigAsync_RejectsContentFeatureConfigBeforeMutatingKind()
    {
        var musicConfig = new WidgetConfig
        {
            Id = "music-window",
            Name = "Music",
            WidgetKind = WidgetKind.Music
        };
        var method = typeof(WidgetManager).GetMethod(
            "CreateWidgetFromConfigAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            _widgetManager,
            [musicConfig, false, false, false]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        Assert.Contains("File config", exception.Message);
        Assert.Equal(WidgetKind.Music, musicConfig.WidgetKind);
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
    public async Task RemoveWidgetAsync_MissingManagedFolder_RemovesConfigWithoutCreatingFolder()
    {
        string missingFolder = Path.Combine(_storageRoot, "Missing");
        var widget = CreateManagedWidget("Missing", missingFolder);
        _settingsService.Settings.Widgets.Add(widget);

        Assert.False(_widgetManager.CanCleanupManagedStorageForWidget(widget.Id));

        await _widgetManager.RemoveWidgetAsync(widget.Id, WidgetRemovalAction.DeleteManagedFolder);

        Assert.DoesNotContain(_settingsService.Settings.Widgets, item => item.Id == widget.Id);
        Assert.Contains(widget.Id, _settingsService.Settings.DeletedWidgetIds);
        Assert.False(Directory.Exists(missingFolder));
    }

    [Fact]
    public async Task RemoveWidgetAsync_InvalidManagedCleanup_RemovesConfigAndKeepsFolder()
    {
        string mappedFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped")).FullName;
        var widget = new WidgetConfig
        {
            Name = "Mapped",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        };
        _settingsService.Settings.Widgets.Add(widget);

        await _widgetManager.RemoveWidgetAsync(widget.Id, WidgetRemovalAction.DeleteManagedFolder);

        Assert.DoesNotContain(_settingsService.Settings.Widgets, item => item.Id == widget.Id);
        Assert.Contains(widget.Id, _settingsService.Settings.DeletedWidgetIds);
        Assert.True(Directory.Exists(mappedFolder));
    }

    [Fact]
    public async Task RemoveWidgetAsync_MappedFolderCleanupRequest_RemovesConfigAndKeepsFolder()
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

        await _widgetManager.RemoveWidgetAsync(widget.Id, WidgetRemovalAction.DeleteManagedFolder);

        Assert.DoesNotContain(_settingsService.Settings.Widgets, item => item.Id == widget.Id);
        Assert.Contains(widget.Id, _settingsService.Settings.DeletedWidgetIds);
        Assert.True(Directory.Exists(mappedFolder));
        Assert.Equal("mapped", File.ReadAllText(mappedFile));
    }

    [Fact]
    public async Task RenameWidgetAsync_ManagedWidgetRejectsDuplicateNameWithoutCreatingFolder()
    {
        string existingFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "AI")).FullName;
        string targetFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        var existingWidget = CreateManagedWidget("AI", existingFolder);
        var targetWidget = CreateManagedWidget("Work", targetFolder);
        _settingsService.Settings.Widgets.Add(existingWidget);
        _settingsService.Settings.Widgets.Add(targetWidget);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _widgetManager.RenameWidgetAsync(targetWidget.Id, "AI"));

        Assert.Equal("Work", targetWidget.Name);
        Assert.Equal("Work", targetWidget.ManagedFolderName);
        Assert.Equal(targetFolder, targetWidget.MappedFolderPath);
        Assert.True(Directory.Exists(targetFolder));
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "AI (2)")));
    }

    [Fact]
    public async Task RenameWidgetAsync_ManagedWidgetMovesFolderAfterValidation()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        File.WriteAllText(Path.Combine(sourceFolder, "note.txt"), "content");
        var widget = CreateManagedWidget("Work", sourceFolder);
        _settingsService.Settings.Widgets.Add(widget);

        await _widgetManager.RenameWidgetAsync(widget.Id, "AI");

        string destinationFolder = Path.Combine(_storageRoot, "AI");
        Assert.Equal("AI", widget.Name);
        Assert.Equal("AI", widget.ManagedFolderName);
        Assert.Equal(destinationFolder, widget.MappedFolderPath);
        Assert.False(Directory.Exists(sourceFolder));
        Assert.Equal("content", File.ReadAllText(Path.Combine(destinationFolder, "note.txt")));
    }

    [Fact]
    public async Task RenameWidgetAsync_ConcurrentManagedRenamesDoNotCreateDuplicateFolders()
    {
        string sourceFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        var widget = CreateManagedWidget("Work", sourceFolder);
        _settingsService.Settings.Widgets.Add(widget);

        var firstRename = _widgetManager.RenameWidgetAsync(widget.Id, "AI");
        var secondRename = _widgetManager.RenameWidgetAsync(widget.Id, "AI");
        await Task.WhenAll(firstRename, secondRename);

        string destinationFolder = Path.Combine(_storageRoot, "AI");
        Assert.Equal("AI", widget.Name);
        Assert.Equal("AI", widget.ManagedFolderName);
        Assert.Equal(destinationFolder, widget.MappedFolderPath);
        Assert.True(Directory.Exists(destinationFolder));
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "AI (2)")));
    }

    [Fact]
    public async Task RenameWidgetAsync_ConcurrentManagedRenamesRespectDuplicateNameGuard()
    {
        string existingFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "AI")).FullName;
        string targetFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        var existingWidget = CreateManagedWidget("AI", existingFolder);
        var targetWidget = CreateManagedWidget("Work", targetFolder);
        _settingsService.Settings.Widgets.Add(existingWidget);
        _settingsService.Settings.Widgets.Add(targetWidget);

        var firstRename = _widgetManager.RenameWidgetAsync(targetWidget.Id, "AI");
        var secondRename = _widgetManager.RenameWidgetAsync(targetWidget.Id, "AI");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await Task.WhenAll(firstRename, secondRename));

        Assert.Equal("Work", targetWidget.Name);
        Assert.Equal("Work", targetWidget.ManagedFolderName);
        Assert.Equal(targetFolder, targetWidget.MappedFolderPath);
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "AI (2)")));
    }

    [Fact]
    public async Task RenameWidgetAsync_ManagedWidgetMissingSourceDoesNotCreateTargetFolder()
    {
        string missingFolder = Path.Combine(_storageRoot, "Missing");
        var widget = CreateManagedWidget("Missing", missingFolder);
        _settingsService.Settings.Widgets.Add(widget);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _widgetManager.RenameWidgetAsync(widget.Id, "AI"));

        Assert.Equal("Missing", widget.Name);
        Assert.Equal("Missing", widget.ManagedFolderName);
        Assert.Equal(missingFolder, widget.MappedFolderPath);
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "AI")));
    }

    [Fact]
    public async Task FileImport_GoesThroughThePortAndWritesRealFiles()
    {
        string managedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Target")).FullName;
        var widget = CreateManagedWidget("Target", managedFolder);
        _settingsService.Settings.Widgets.Add(widget);

        // Stage 3b wiring: the producer-side translation (QuickCaptureService)
        // plans the import; the IFileWidgetImportTarget implementation owns
        // folders and writes. Same flow production uses.
        var textPlan = QuickCaptureService.BuildFileImportPlan(
            new QuickCaptureItem { Type = QuickCaptureItemType.Text, Body = "hello world" },
            imageFileNamePrefix: "Capture",
            textFileNamePrefix: "Capture",
            linkFileNamePrefix: "Capture");
        Assert.NotNull(textPlan);
        Assert.Null(textPlan.SourceFilePath);
        string? textPath = await _widgetManager.TryImportTextAsync(
            textPlan.Text!,
            textPlan.FileName,
            widget.Id);

        var linkPlan = QuickCaptureService.BuildFileImportPlan(
            new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Link,
                Body = "https://example.com/docs",
                Url = "https://example.com/docs"
            },
            imageFileNamePrefix: "Capture",
            textFileNamePrefix: "Capture",
            linkFileNamePrefix: "Capture");
        Assert.NotNull(linkPlan);
        string? linkPath = await _widgetManager.TryImportTextAsync(
            linkPlan.Text!,
            linkPlan.FileName,
            widget.Id);

        string sourceImagePath = Path.Combine(_tempRoot, "source.png");
        await File.WriteAllBytesAsync(sourceImagePath, [1, 2, 3, 4]);
        var imagePlan = QuickCaptureService.BuildFileImportPlan(
            new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Image,
                Body = "Image",
                ImagePath = sourceImagePath,
                UpdatedAt = new DateTimeOffset(2026, 6, 21, 14, 32, 0, TimeSpan.Zero)
            },
            imageFileNamePrefix: "Capture",
            textFileNamePrefix: "Capture",
            linkFileNamePrefix: "Capture");
        Assert.NotNull(imagePlan);
        Assert.Null(imagePlan.Text);
        string? imagePath = await _widgetManager.TryImportFileAsync(
            imagePlan.SourceFilePath!,
            widget.Id,
            imagePlan.FileName);

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

        var target = Assert.Single(_widgetManager.GetImportTargets());
        Assert.Equal(widget.Id, target.WidgetId);
        Assert.Equal(widget.Name, target.Name);
        Assert.Equal(widget.Id, _settingsService.Settings.LastQuickCaptureFileWidgetId);

        var lastTarget = _widgetManager.GetLastImportTarget();
        Assert.NotNull(lastTarget);
        Assert.Equal(widget.Id, lastTarget.WidgetId);
    }

    [Fact]
    public void BuildFileImportPlan_ReturnsNullForNothingImportable()
    {
        // An image whose file vanished plans nothing (import returns null,
        // matching the pre-wiring behavior).
        Assert.Null(QuickCaptureService.BuildFileImportPlan(
            new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Image,
                ImagePath = Path.Combine(_tempRoot, "missing.png")
            },
            imageFileNamePrefix: "Capture",
            textFileNamePrefix: "Capture",
            linkFileNamePrefix: "Capture"));

        // Empty body text plans nothing.
        Assert.Null(QuickCaptureService.BuildFileImportPlan(
            new QuickCaptureItem { Type = QuickCaptureItemType.Text, Body = "   " },
            imageFileNamePrefix: "Capture",
            textFileNamePrefix: "Capture",
            linkFileNamePrefix: "Capture"));

        // A link whose URL is not absolute falls back to the text plan.
        var fallbackPlan = QuickCaptureService.BuildFileImportPlan(
            new QuickCaptureItem { Type = QuickCaptureItemType.Link, Body = "not a url" },
            imageFileNamePrefix: "Capture",
            textFileNamePrefix: "Capture",
            linkFileNamePrefix: "Capture");
        Assert.NotNull(fallbackPlan);
        Assert.Equal("not a url", fallbackPlan.Text);
        Assert.EndsWith(".txt", fallbackPlan.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportSink_ConfinesProducerSuppliedFileNamesToTheWidgetFolder()
    {
        // Producers cannot be trusted to sanitize: traversal, rooted and
        // absolute-path names must be reduced to a harmless in-folder name
        // (or rejected), never escape the sink.
        string managedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Confined")).FullName;
        var widget = CreateManagedWidget("Confined", managedFolder);
        _settingsService.Settings.Widgets.Add(widget);

        Assert.Null(await _widgetManager.TryImportTextAsync(
            "escape", "..\\..\\escaped.txt", widget.Id));
        Assert.Null(await _widgetManager.TryImportTextAsync(
            "escape", "../escaped.txt", widget.Id));
        Assert.Null(await _widgetManager.TryImportTextAsync(
            "escape", Path.Combine(_tempRoot, "absolute-escape.txt"), widget.Id));

        Assert.False(File.Exists(Path.Combine(_storageRoot, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "absolute-escape.txt")));
        Assert.Empty(Directory.GetFiles(managedFolder));
    }

    [Fact]
    public async Task ImportSink_LeavesNoPartialFileWhenCancelledOrFailed()
    {
        string managedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Atomic")).FullName;
        var widget = CreateManagedWidget("Atomic", managedFolder);
        _settingsService.Settings.Widgets.Add(widget);

        string sourcePath = Path.Combine(_tempRoot, "cancel-source.bin");
        await File.WriteAllBytesAsync(sourcePath, new byte[64 * 1024]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _widgetManager.TryImportFileAsync(sourcePath, widget.Id, "partial.bin", cts.Token));

        Assert.False(File.Exists(Path.Combine(managedFolder, "partial.bin")));
        Assert.Empty(Directory.GetFiles(managedFolder, ".import-*.tmp"));
    }

    [Fact]
    public void ImportDestination_DriveRootMappedFolderStaysImportable()
    {
        // A File widget mapped to a drive root used to be wrongly rejected:
        // folder + separator ("C:\" + "\") can never prefix-match a child
        // ("C:\abc.txt"). GetRelativePath-based containment handles roots.
        bool resolved = WidgetManager.TryResolveImportDestination(
            @"C:\",
            "abc.txt",
            out string destinationPath,
            out string baseCandidatePath);

        Assert.True(resolved);
        Assert.Equal(Path.GetFullPath(@"C:\abc.txt"), baseCandidatePath);
        Assert.Equal(baseCandidatePath, destinationPath);
    }

    [Fact]
    public void Import_NeverOverwritesAFileCreatedAfterPathResolution()
    {
        // The destination was taken between GetAvailablePath and the rename:
        // the import must land on the next free variant, never clobber.
        string folder = Directory.CreateDirectory(Path.Combine(_tempRoot, "NoClobber")).FullName;
        string baseCandidate = Path.Combine(folder, "race.txt");
        string occupied = Path.Combine(folder, "race.txt");
        File.WriteAllText(occupied, "someone else");
        string tempPath = Path.Combine(folder, ".import-test.tmp");
        File.WriteAllText(tempPath, "mine");

        string finalPath = WidgetManager.MoveImportIntoPlace(tempPath, baseCandidate, occupied);

        Assert.NotEqual(occupied, finalPath);
        Assert.Equal("someone else", File.ReadAllText(occupied));
        Assert.Equal("mine", File.ReadAllText(finalPath));
        Assert.StartsWith("race", Path.GetFileName(finalPath), StringComparison.Ordinal);
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

    private static bool TryCreateDirectoryJunction(string junction, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(junction);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryJunction(string junction)
    {
        try
        {
            Directory.Delete(junction, recursive: false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
