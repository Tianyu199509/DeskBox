using System.Diagnostics;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetManagerStorageCleanupTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _storageRoot;
    private readonly string _desktopRoot;
    private readonly SettingsService _settingsService;
    private readonly SearchFeatureSettingsStub _searchSettings;
    private readonly WidgetManager _widgetManager;

    public WidgetManagerStorageCleanupTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "storage")).FullName;
        _desktopRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;

        _settingsService = new SettingsService(Path.Combine(_tempRoot, "settings"));
        _settingsService.Settings.DefaultManagedStorageRootPath = _storageRoot;
        _searchSettings = new SearchFeatureSettingsStub(_settingsService);

        var fileService = new FileService();
        var organizerService = new OrganizerService(_settingsService, fileService);
        var themeService = new ThemeService(_settingsService);
        _widgetManager = new WidgetManager(
            _settingsService,
            fileService,
            organizerService,
            themeService,
            new QuickCaptureService(new QuickCaptureStore(Path.Combine(_tempRoot, "quick-capture"))),
            localizationService: null,
            desktopPathProvider: () => _desktopRoot,
            recycleManagedFolderDeletes: false,
            searchFeatureSettings: _searchSettings);
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
    public async Task RemoveWidgetAsync_SearchAwaitsRuntimeCommitBeforeFinishingDeletion()
    {
        var search = new WidgetConfig { Name = "Search", WidgetKind = WidgetKind.Search };
        _settingsService.Settings.Widgets.Add(search);
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Search, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _searchSettings.BeforeCommit = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        Task deletion = _widgetManager.RemoveWidgetAsync(search.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(deletion.IsCompleted);
        release.TrySetResult();
        await deletion;

        Assert.Equal(1, _searchSettings.CommitCount);
        Assert.False(_searchSettings.Enabled);
        Assert.DoesNotContain(_settingsService.Settings.Widgets, item => item.Id == search.Id);
        Assert.Contains(search.Id, _settingsService.Settings.DeletedWidgetIds);
        var reloaded = new SettingsService(Path.Combine(_tempRoot, "settings"));
        Assert.False(FeatureWidgetSettings.IsEnabled(reloaded.Settings, WidgetKind.Search));
        Assert.DoesNotContain(reloaded.Settings.Widgets, item => item.Id == search.Id);
    }

    [Fact]
    public async Task TodoMenuToggle_AndSettingsToggle_UseTheSameTransitionOrder()
    {
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Todo, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = new List<bool>();
        TodoSettingsCoordinator? coordinator = null;
        coordinator = new TodoSettingsCoordinator(_settingsService, async enabled =>
        {
            transitions.Add(enabled);
            coordinator!.CommitEnabledState(enabled);
            if (!enabled)
            {
                entered.TrySetResult();
                await release.Task;
            }
        });
        var fileService = new FileService();
        var manager = new WidgetManager(
            _settingsService,
            fileService,
            new OrganizerService(_settingsService, fileService),
            new ThemeService(_settingsService),
            new QuickCaptureService(new QuickCaptureStore(Path.Combine(_tempRoot, "todo-toggle-capture"))),
            localizationService: null,
            desktopPathProvider: () => _desktopRoot,
            recycleManagedFolderDeletes: false,
            todoSettings: coordinator);

        Task menuDisable = manager.SetFeatureWidgetEnabledAsync(
            WidgetKind.Todo, enabled: false, reveal: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task settingsEnable = coordinator.SetEnabledAsync(true);
        Assert.False(settingsEnable.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(menuDisable, settingsEnable);

        Assert.Equal([false, true], transitions);
        Assert.True(FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo));
        await coordinator.StopAsync();
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
    public async Task RenameWidgetAsync_EmptyWidgetAdoptsClosedWidgetResidueFolder()
    {
        // A closed widget kept its managed folder behind by design; a fresh
        // empty widget renamed to the same name must adopt it instead of
        // dead-ending on a misleading duplicate-name error (#113).
        string residueFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Sysinternals")).FullName;
        File.WriteAllText(Path.Combine(residueFolder, "procmon.lnk"), "old shortcut");
        string emptyFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        var widget = CreateManagedWidget("Work", emptyFolder);
        _settingsService.Settings.Widgets.Add(widget);

        await _widgetManager.RenameWidgetAsync(widget.Id, "Sysinternals");

        Assert.Equal("Sysinternals", widget.Name);
        Assert.Equal("Sysinternals", widget.ManagedFolderName);
        Assert.Equal(residueFolder, widget.MappedFolderPath, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(residueFolder, "procmon.lnk")),
            "The adopted folder's contents must stay untouched.");
        Assert.False(Directory.Exists(emptyFolder),
            "The fresh widget's empty default folder must be cleaned up.");
        Assert.False(Directory.Exists(Path.Combine(_storageRoot, "Sysinternals (2)")));
    }

    [Fact]
    public async Task RenameWidgetAsync_RejectsAdoptionWhenFolderClaimedByLiveWidget()
    {
        // A mapped widget's folder under the managed root must never be
        // adopted — two widgets sharing one directory means the first
        // "close and delete files" wipes the other's contents.
        string claimedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "AI")).FullName;
        File.WriteAllText(Path.Combine(claimedFolder, "theirs.txt"), "mapped content");
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "Mapped",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = claimedFolder,
            FollowsDefaultStoragePath = false
        });
        string emptyFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        var widget = CreateManagedWidget("Work", emptyFolder);
        _settingsService.Settings.Widgets.Add(widget);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _widgetManager.RenameWidgetAsync(widget.Id, "AI"));

        Assert.Equal("Work", widget.ManagedFolderName);
        Assert.Equal(emptyFolder, widget.MappedFolderPath, ignoreCase: true);
        Assert.True(Directory.Exists(emptyFolder));
        Assert.True(File.Exists(Path.Combine(claimedFolder, "theirs.txt")));
    }

    [Fact]
    public async Task RenameWidgetAsync_RejectsAdoptionThroughJunctionAlias()
    {
        // "OldA" is a junction onto the live widget's real folder: a
        // lexical path compare sees two different strings, but resolved
        // they are the same physical directory. Adopting it would leave
        // both widgets sharing one tree — the first "close and delete
        // files" would wipe the other's contents.
        string claimedFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "AI")).FullName;
        File.WriteAllText(Path.Combine(claimedFolder, "theirs.txt"), "mapped content");
        _settingsService.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "Mapped",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = claimedFolder,
            FollowsDefaultStoragePath = false
        });
        string junctionFolder = Path.Combine(_storageRoot, "OldA");
        Assert.True(
            TryCreateDirectoryJunction(junctionFolder, claimedFolder),
            "The Windows test host must support creating a directory junction.");
        try
        {
            string emptyFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
            var widget = CreateManagedWidget("Work", emptyFolder);
            _settingsService.Settings.Widgets.Add(widget);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _widgetManager.RenameWidgetAsync(widget.Id, "OldA"));

            Assert.Equal("Work", widget.ManagedFolderName);
            Assert.Equal(emptyFolder, widget.MappedFolderPath, ignoreCase: true);
            Assert.True(Directory.Exists(emptyFolder),
                "A rejected adoption must keep the widget's current folder.");
            Assert.True(File.Exists(Path.Combine(claimedFolder, "theirs.txt")));
            Assert.True(Directory.Exists(junctionFolder),
                "The foreign junction is never adopted or removed.");
        }
        finally
        {
            TryDeleteDirectoryJunction(junctionFolder);
        }
    }

    [Fact]
    public async Task RenameWidgetAsync_RejectsNameWhenBothFoldersHoldFiles()
    {
        string residueFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "AI")).FullName;
        File.WriteAllText(Path.Combine(residueFolder, "old.txt"), "residue content");
        string targetFolder = Directory.CreateDirectory(Path.Combine(_storageRoot, "Work")).FullName;
        File.WriteAllText(Path.Combine(targetFolder, "mine.txt"), "current content");
        var widget = CreateManagedWidget("Work", targetFolder);
        _settingsService.Settings.Widgets.Add(widget);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _widgetManager.RenameWidgetAsync(widget.Id, "AI"));

        Assert.Equal("Work", widget.Name);
        Assert.Equal("Work", widget.ManagedFolderName);
        Assert.Equal(targetFolder, widget.MappedFolderPath, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(residueFolder, "old.txt")));
        Assert.True(File.Exists(Path.Combine(targetFolder, "mine.txt")),
            "A rejected rename must not move, merge, or delete either side.");
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

    [Fact]
    public void TryGetFileWidgetPathConflict_ReportsManagedStorageRootOverlap()
    {
        Assert.True(_widgetManager.TryGetFileWidgetPathConflict(
            Path.Combine(_storageRoot, "new-folder"),
            out FileWidgetPathConflict? rootConflict));
        Assert.Equal(FileWidgetPathConflictKind.ManagedStorageRoot, rootConflict!.Kind);
        Assert.Null(rootConflict.ConflictingWidget);
    }

    [Fact]
    public void TryGetFileWidgetPathConflict_ReportsTheConflictingWidget()
    {
        string mappedFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped-conflict")).FullName;
        var widget = new WidgetConfig
        {
            Id = "mapped-widget",
            Name = "Mapped",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        };
        _settingsService.Settings.Widgets.Add(widget);

        Assert.True(_widgetManager.TryGetFileWidgetPathConflict(
            mappedFolder,
            out FileWidgetPathConflict? conflict));
        Assert.Equal(FileWidgetPathConflictKind.ExistingWidget, conflict!.Kind);
        Assert.Equal("mapped-widget", conflict.ConflictingWidget!.Id);
    }

    [Fact]
    public void TryGetFileWidgetPathConflict_HonorsExclusionAndReportsNoConflict()
    {
        string mappedFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "mapped-excluded")).FullName;
        var widget = new WidgetConfig
        {
            Id = "excluded-widget",
            Name = "Mapped",
            WidgetKind = WidgetKind.File,
            MappedFolderPath = mappedFolder,
            FollowsDefaultStoragePath = false
        };
        _settingsService.Settings.Widgets.Add(widget);
        string freeFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "free")).FullName;

        Assert.True(_widgetManager.TryGetFileWidgetPathConflict(
            mappedFolder,
            out _,
            excludedWidgetId: "another-widget"));
        Assert.True(_widgetManager.TryGetFileWidgetPathConflict(mappedFolder, out _));
        Assert.False(_widgetManager.TryGetFileWidgetPathConflict(
            mappedFolder,
            out FileWidgetPathConflict? excluded,
            excludedWidgetId: widget.Id));
        Assert.Null(excluded);
        Assert.False(_widgetManager.TryGetFileWidgetPathConflict(
            freeFolder,
            out FileWidgetPathConflict? none));
        Assert.Null(none);
    }

    [Fact]
    public void DescribeFileWidgetPathRelation_DistinguishesSameInsideAndContains()
    {
        string root = Directory.CreateDirectory(Path.Combine(_tempRoot, "relation")).FullName;
        string parent = Directory.CreateDirectory(Path.Combine(root, "parent")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
        string elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;

        Assert.Equal(
            FileWidgetPathRelation.SameDirectory,
            WidgetManager.DescribeFileWidgetPathRelation(parent, parent));
        Assert.Equal(
            FileWidgetPathRelation.CandidateInsideOther,
            WidgetManager.DescribeFileWidgetPathRelation(child, parent));
        Assert.Equal(
            FileWidgetPathRelation.CandidateContainsOther,
            WidgetManager.DescribeFileWidgetPathRelation(parent, child));
        // Unrelated paths never reach the conflict formatter, but the
        // classifier still answers from the neutral "cannot verify" bucket.
        Assert.Equal(
            FileWidgetPathRelation.UnresolvableOverlap,
            WidgetManager.DescribeFileWidgetPathRelation(parent, elsewhere));
    }

    [Fact]
    public void FormatFileWidgetPathConflictMessage_NamesTheOtherPath()
    {
        string parent = Directory.CreateDirectory(Path.Combine(_tempRoot, "conflict-parent")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(parent, "conflict-child")).FullName;

        string message = _widgetManager.FormatFileWidgetPathConflictMessage(child, "配置", parent);

        Assert.Contains("配置", message);
        Assert.Contains(parent, message);
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

    private sealed class SearchFeatureSettingsStub(SettingsService settings) : ISearchFeatureSettings
    {
        public bool Enabled => FeatureWidgetSettings.IsEnabled(settings.Settings, WidgetKind.Search);
        public event Action? FeatureChanged { add { } remove { } }
        public Func<bool, Task>? BeforeCommit { get; set; }
        public int CommitCount { get; private set; }

        public Task SetEnabledAsync(bool enabled, bool reveal = true,
            CancellationToken cancellationToken = default) =>
            CommitEnabledStateAsync(enabled, cancellationToken);

        public async Task CommitEnabledStateAsync(bool enabled,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeCommit is not null) await BeforeCommit(enabled);
            FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Search, enabled);
            CommitCount++;
        }

        public Task StopAsync() => Task.CompletedTask;
    }
}
