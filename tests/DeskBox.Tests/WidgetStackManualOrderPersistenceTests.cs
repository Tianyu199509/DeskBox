using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class WidgetStackManualOrderPersistenceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _junctions = [];

    public void Dispose()
    {
        foreach (string junction in _junctions)
        {
            try
            {
                if (Directory.Exists(junction))
                {
                    Directory.Delete(junction, recursive: false);
                }
            }
            catch
            {
            }
        }

        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ManualSortOrder_SurvivesFeedback944StackDissolveRestackAndColdRestart()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings");
        string folderPath = Path.Combine(_tempRoot, "files");
        Directory.CreateDirectory(folderPath);
        string[] initialNames = ["1.txt", "2.txt", "3.txt", "4.txt", "5.txt"];
        foreach (string name in initialNames)
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        string widgetId;
        {
            var settings = await CreateManualStackSettingsAsync(settingsRoot);
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settings, fileService);
            var config = new WidgetConfig
            {
                Name = "ManualStackTest",
                MappedFolderPath = folderPath,
                FollowsDefaultStoragePath = false,
                SortMode = WidgetSortMode.Name
            };
            settings.Settings.Widgets.Add(config);
            widgetId = config.Id;

            using var vm = new WidgetViewModel(
                config,
                fileService,
                organizer,
                settings,
                dispatcherQueue: null);
            await vm.InitializeAsync();
            Assert.Equal(["1", "2", "3", "4", "5"], GetVisibleNames(vm));

            // Drag "5.txt" to the front in manual sort mode -> [5, 1, 2, 3, 4]
            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem item5 = vm.VisibleItems.Single(item => item.Name == "5");
            Assert.True(vm.MoveVisibleItemForReorder(item5, visibleInsertionIndex: 0));
            vm.PersistManualOrder();
            Assert.Equal(["5", "1", "2", "3", "4"], GetVisibleNames(vm));
            Assert.Equal(["5", "1", "2", "3", "4"], vm.Items.Select(item => item.Name));

            // Feedback #944 sequence:
            // 1. Select last 3 contiguous files [2, 3, 4] and create manual stack
            WidgetItem[] lastThree = vm.VisibleItems.Skip(2).Take(3).ToArray();
            Assert.True(vm.CreateManualStack(lastThree));
            WidgetStackItem firstStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(["2", "3", "4"], firstStack.Members.Select(member => member.Name));

            // 2. Dissolve that stack -> [5, 1, 2, 3, 4] must remain intact
            Assert.True(vm.DissolveStack(firstStack));
            Assert.Equal(["5", "1", "2", "3", "4"], GetVisibleNames(vm));

            // 3. Select 3 files starting from second-to-last ([1, 2, 3]) and stack
            WidgetItem[] shiftedThree = vm.VisibleItems.Skip(1).Take(3).ToArray();
            Assert.Equal(["1", "2", "3"], shiftedThree.Select(item => item.Name));
            Assert.True(vm.CreateManualStack(shiftedThree));
            WidgetStackItem secondStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.NotEqual(firstStack.StackKey, secondStack.StackKey);
            Assert.Equal(["1", "2", "3"], secondStack.Members.Select(member => member.Name));
            Assert.Same(secondStack, vm.FindStackByKey(secondStack.StackKey));

            // 4. Dissolve the second stack as well -> [5, 1, 2, 3, 4] must still hold
            Assert.True(vm.DissolveStack(secondStack));
            Assert.Equal(["5", "1", "2", "3", "4"], GetVisibleNames(vm));

            await settings.SaveAsync(notifySubscribers: false);
        }

        // Cold restart from persisted settings.json
        {
            var reloadedSettings = new SettingsService(settingsRoot);
            await reloadedSettings.LoadAsync();
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(reloadedSettings, fileService);
            WidgetConfig reloadedConfig = Assert.Single(
                reloadedSettings.Settings.Widgets,
                widget => widget.Id == widgetId);

            using var restartedVm = new WidgetViewModel(
                reloadedConfig,
                fileService,
                organizer,
                reloadedSettings,
                dispatcherQueue: null);
            await restartedVm.InitializeAsync();

            Assert.Equal(["5", "1", "2", "3", "4"], GetVisibleNames(restartedVm));
            Assert.Equal(["5", "1", "2", "3", "4"], restartedVm.Items.Select(item => item.Name));
        }
    }

    [Fact]
    public async Task ManualSortOrder_PreservesPopoverMemberReorderAfterDissolveAndColdRestart()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-popover");
        string folderPath = Path.Combine(_tempRoot, "files-popover");
        Directory.CreateDirectory(folderPath);
        foreach (string name in new[] { "1.txt", "2.txt", "3.txt", "4.txt", "5.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        string widgetId;
        {
            var settings = await CreateManualStackSettingsAsync(settingsRoot);
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settings, fileService);
            var config = new WidgetConfig
            {
                Name = "PopoverMemberOrderTest",
                MappedFolderPath = folderPath,
                FollowsDefaultStoragePath = false,
                SortMode = WidgetSortMode.Manual
            };
            settings.Settings.Widgets.Add(config);
            widgetId = config.Id;

            using var vm = new WidgetViewModel(
                config,
                fileService,
                organizer,
                settings,
                dispatcherQueue: null);
            await vm.InitializeAsync();

            // Reorder to [5, 1, 2, 3, 4]
            WidgetItem item5 = vm.VisibleItems.Single(item => item.Name == "5");
            Assert.True(vm.MoveVisibleItemForReorder(item5, visibleInsertionIndex: 0));
            vm.PersistManualOrder();

            // Stack [2, 3, 4] -> reorder members inside popover to [4, 2, 3]
            Assert.True(vm.CreateManualStack(vm.VisibleItems.Skip(2).Take(3).ToArray()));
            WidgetStackItem stack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            WidgetItem member4 = stack.Members.Single(member => member.Name == "4");
            Assert.True(vm.MoveStackMembersForReorder(stack.StackKey, [member4], memberInsertionIndex: 0));
            WidgetStackItem? refreshedStack = vm.FindStackByKey(stack.StackKey);
            Assert.NotNull(refreshedStack);
            Assert.Equal(["4", "2", "3"], refreshedStack.Members.Select(member => member.Name));

            // Dissolve stack -> [4, 2, 3] must splice in place -> [5, 1, 4, 2, 3]
            Assert.True(vm.DissolveStack(refreshedStack));
            Assert.Equal(["5", "1", "4", "2", "3"], GetVisibleNames(vm));
            Assert.Equal(["5", "1", "4", "2", "3"], vm.Items.Select(item => item.Name));

            await settings.SaveAsync(notifySubscribers: false);
        }

        {
            var reloadedSettings = new SettingsService(settingsRoot);
            await reloadedSettings.LoadAsync();
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(reloadedSettings, fileService);
            WidgetConfig reloadedConfig = Assert.Single(
                reloadedSettings.Settings.Widgets,
                widget => widget.Id == widgetId);

            using var restartedVm = new WidgetViewModel(
                reloadedConfig,
                fileService,
                organizer,
                reloadedSettings,
                dispatcherQueue: null);
            await restartedVm.InitializeAsync();

            Assert.Equal(["5", "1", "4", "2", "3"], GetVisibleNames(restartedVm));
            Assert.Equal(["5", "1", "4", "2", "3"], restartedVm.Items.Select(item => item.Name));
        }
    }

    [Fact]
    public async Task ManualSortOrder_SelfHealsLegacyDesyncedConfigItemsOnColdRestart()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-heal");
        string folderPath = Path.Combine(_tempRoot, "files-heal");
        Directory.CreateDirectory(folderPath);
        string p1 = Path.Combine(folderPath, "1.txt");
        string p2 = Path.Combine(folderPath, "2.txt");
        string p3 = Path.Combine(folderPath, "3.txt");
        string p4 = Path.Combine(folderPath, "4.txt");
        foreach (string path in new[] { p1, p2, p3, p4 })
        {
            await File.WriteAllTextAsync(path, path);
        }

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        var config = new WidgetConfig
        {
            Name = "LegacyDesyncWidget",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual,
            // Simulate v1.5.9 stale Config.Items order [1, 2, 3, 4] while
            // FileStackGroupOrder had already reordered the projection to [4, Stack(3,2), 1]
            Items =
            [
                new WidgetItemConfig { Path = p1, SortOrder = 0 },
                new WidgetItemConfig { Path = p2, SortOrder = 1 },
                new WidgetItemConfig { Path = p3, SortOrder = 2 },
                new WidgetItemConfig { Path = p4, SortOrder = 3 }
            ]
        };
        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>
            {
                ["Manual:legacy"] = [p3, p2]
            });
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                "Item:" + Path.GetFullPath(p4).ToUpperInvariant(),
                "Manual:legacy",
                "Item:" + Path.GetFullPath(p1).ToUpperInvariant()
            ]);
        settings.Settings.Widgets.Add(config);
        await settings.SaveAsync(notifySubscribers: false);

        var reloadedSettings = new SettingsService(settingsRoot);
        await reloadedSettings.LoadAsync();
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(reloadedSettings, fileService);
        WidgetConfig reloadedConfig = Assert.Single(reloadedSettings.Settings.Widgets);

        using var vm = new WidgetViewModel(
            reloadedConfig,
            fileService,
            organizer,
            reloadedSettings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        // Projection build heals underlying Items and persisted Config.Items immediately
        Assert.Equal(["4", "3", "2", "1"], vm.Items.Select(item => item.Name));
        Assert.Equal(
            [p4, p3, p2, p1],
            reloadedConfig.Items.OrderBy(item => item.SortOrder).Select(item => item.Path));
    }

    [Fact]
    public async Task AutoSortMode_CreatingManualStackDoesNotFreezeLooseItemOrderForNewFiles()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-autosort");
        string folderPath = Path.Combine(_tempRoot, "files-autosort");
        Directory.CreateDirectory(folderPath);
        foreach (string name in new[] { "a.txt", "c.txt", "d.txt", "e.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "AutoSortWithManualStack",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name
        };
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        WidgetItem[] stackTargets = vm.VisibleItems
            .Where(item => item.Name is "c" or "d")
            .ToArray();
        Assert.True(vm.CreateManualStack(stackTargets));
        WidgetStackItem stack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());

        // Stack sits in place between 'a' and 'e' without freezing loose "Item:" keys
        Assert.Empty(WidgetFileStackSettings.GetStackOrder(vm.Config));
        Assert.Equal(["a", stack.Name, "e"], GetVisibleNames(vm));

        // Adding 'b.txt' naturally sorts it between 'a' and Stack(c, d)
        await File.WriteAllTextAsync(Path.Combine(folderPath, "b.txt"), "b");
        await vm.RefreshFolderContentsAsync();
        Assert.Equal(["a", "b", stack.Name, "e"], GetVisibleNames(vm));
    }

    [Fact]
    public async Task JunctionMappedFolder_ColdRestartRebasesPersistedManualOrderToTraversalTarget()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-junction");
        string physicalFolder = Path.Combine(_tempRoot, "physical");
        string junctionFolder = Path.Combine(_tempRoot, "junction");
        Directory.CreateDirectory(physicalFolder);
        if (!TestJunction.TryCreate(junctionFolder, physicalFolder))
        {
            return;
        }

        _junctions.Add(junctionFolder);
        foreach (string name in new[] { "alpha.txt", "beta.txt", "gamma.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(physicalFolder, name), name);
        }

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        // Simulate legacy config storing paths under logical junction prefix instead of physical target
        var config = new WidgetConfig
        {
            Name = "JunctionWidget",
            MappedFolderPath = junctionFolder,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual,
            Items =
            [
                new WidgetItemConfig { Path = Path.Combine(junctionFolder, "gamma.txt"), SortOrder = 0 },
                new WidgetItemConfig { Path = Path.Combine(junctionFolder, "alpha.txt"), SortOrder = 1 },
                new WidgetItemConfig { Path = Path.Combine(junctionFolder, "beta.txt"), SortOrder = 2 }
            ]
        };
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                "Item:" + Path.Combine(junctionFolder, "gamma.txt").ToUpperInvariant(),
                "Item:" + Path.Combine(junctionFolder, "alpha.txt").ToUpperInvariant(),
                "Item:" + Path.Combine(junctionFolder, "beta.txt").ToUpperInvariant()
            ]);
        settings.Settings.Widgets.Add(config);

        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        Assert.Equal(["gamma", "alpha", "beta"], GetVisibleNames(vm));
        Assert.Equal(["gamma", "alpha", "beta"], vm.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task RemoveItemFromStack_TwoMemberManualStack_KeepsSurvivorAndDetachedItemInPlaceAcrossRestart()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-remove-two");
        string folderPath = Path.Combine(_tempRoot, "files-remove-two");
        Directory.CreateDirectory(folderPath);
        foreach (string name in new[] { "1.txt", "2.txt", "3.txt", "4.txt", "5.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileStackOpenMode = SettingsService.FileStackOpenModeInline;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "RemoveTwoMemberStack",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual
        };
        settings.Settings.Widgets.Add(config);

        using (var vm = new WidgetViewModel(
                   config,
                   fileService,
                   organizer,
                   settings,
                   dispatcherQueue: null))
        {
            await vm.InitializeAsync();

            WidgetItem item5 = vm.VisibleItems.Single(item => item.Name == "5");
            Assert.True(vm.MoveVisibleItemForReorder(item5, 0));
            Assert.Equal(["5", "1", "2", "3", "4"], GetVisibleNames(vm));

            WidgetItem[] pair = vm.VisibleItems
                .Where(item => item.Name is "2" or "3")
                .ToArray();
            Assert.True(vm.CreateManualStack(pair));
            WidgetStackItem stack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
            vm.SetStackExpanded(stack, expanded: true);

            WidgetItem member2 = stack.Members.Single(item => item.Name == "2");
            Assert.True(vm.RemoveItemFromStack(member2));
            Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
            Assert.Equal(["5", "1", "3", "2", "4"], GetVisibleNames(vm));
            await settings.SaveAsync(notifySubscribers: false);
        }

        var reloadedSettings = new SettingsService(settingsRoot);
        await reloadedSettings.LoadAsync();
        var reloadedFileService = new FileService();
        var reloadedOrganizer = TestOrganizerServices.Create(reloadedSettings, reloadedFileService);
        WidgetConfig reloadedConfig = Assert.Single(reloadedSettings.Settings.Widgets);

        using var reloadedVm = new WidgetViewModel(
            reloadedConfig,
            reloadedFileService,
            reloadedOrganizer,
            reloadedSettings,
            dispatcherQueue: null);
        await reloadedVm.InitializeAsync();
        Assert.Equal(["5", "1", "3", "2", "4"], GetVisibleNames(reloadedVm));
    }

    [Fact]
    public async Task InlineExpandedStack_BlockReorderDetachingAllMembersDownward_LandsAtExactInsertionSlot()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-block-detach");
        string folderPath = Path.Combine(_tempRoot, "files-block-detach");
        Directory.CreateDirectory(folderPath);
        foreach (string name in new[] { "1.txt", "2.txt", "3.txt", "4.txt", "5.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileStackOpenMode = SettingsService.FileStackOpenModeInline;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "InlineBlockDetach",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual
        };
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        WidgetItem[] pair = vm.VisibleItems
            .Where(item => item.Name is "2" or "3")
            .ToArray();
        Assert.True(vm.CreateManualStack(pair));
        WidgetStackItem stack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        vm.SetStackExpanded(stack, expanded: true);

        // Visible items: [0]="1", [1]=Stack, [2]="2"(child), [3]="3"(child), [4]="4", [5]="5"
        // Insertion index 5 is right between "4" and "5".
        WidgetItem[] allExpandedMembers = stack.Members.ToArray();
        Assert.True(vm.MoveVisibleItemsForReorder(allExpandedMembers, visibleInsertionIndex: 5));
        Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal(["1", "4", "2", "3", "5"], GetVisibleNames(vm));
    }

    [Fact]
    public async Task DissolveStack_WhenStaleLooseItemKeyPrecedesStackInOrder_UsesCurrentStackSlot()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-stale-key");
        string folderPath = Path.Combine(_tempRoot, "files-stale-key");
        Directory.CreateDirectory(folderPath);
        string p1 = Path.Combine(folderPath, "1.txt");
        string p2 = Path.Combine(folderPath, "2.txt");
        string p3 = Path.Combine(folderPath, "3.txt");
        string p4 = Path.Combine(folderPath, "4.txt");
        foreach (string path in new[] { p1, p2, p3, p4 })
        {
            await File.WriteAllTextAsync(path, path);
        }

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        var config = new WidgetConfig
        {
            Name = "StaleKeyPrecedence",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual
        };
        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Manual:test"] = [p2, p3]
            });
        // Stale "Item:<P2>" sits ahead of "Item:<P4>" and "Manual:test"
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                "Item:" + Path.GetFullPath(p2).ToUpperInvariant(),
                "Item:" + Path.GetFullPath(p4).ToUpperInvariant(),
                "Manual:test",
                "Item:" + Path.GetFullPath(p1).ToUpperInvariant()
            ]);
        settings.Settings.Widgets.Add(config);

        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        WidgetStackItem stack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal(["4", stack.Name, "1"], GetVisibleNames(vm));

        Assert.True(vm.DissolveStack(stack));
        Assert.Equal(["4", "2", "3", "1"], GetVisibleNames(vm));
    }

    [Fact]
    public async Task EmbeddedSubfolderNavigation_ReorderingSubfolderPreservesMappedRootStackAndFileOrder()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-subfolder-order");
        string folderPath = Path.Combine(_tempRoot, "files-subfolder-order");
        string subFolder = Path.Combine(folderPath, "Sub");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "c.txt"), "c");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "x.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "y.txt"), "y");

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "EmbeddedNavManualOrder",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual
        };
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        // Arrange custom root order: ["c", Stack("b", "a"), "Sub"]
        WidgetItem itemC = vm.VisibleItems.Single(item => item.Name == "c");
        Assert.True(vm.MoveVisibleItemForReorder(itemC, 0));
        WidgetItem itemB = vm.VisibleItems.Single(item => item.Name == "b");
        WidgetItem itemA = vm.VisibleItems.Single(item => item.Name == "a");
        Assert.True(vm.CreateManualStack([itemB, itemA]));
        WidgetStackItem rootStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        WidgetItem subItem = vm.VisibleItems.Single(item => item.Name == "Sub");
        Assert.True(vm.MoveVisibleItemForReorder(subItem, 3));
        Assert.Equal(["c", rootStack.Name, "Sub"], GetVisibleNames(vm));

        // Navigate into Sub and reorder [x, y] -> [y, x]
        Assert.True(await vm.NavigateIntoFolderAsync(subItem));
        Assert.False(vm.IsAtMappedRoot);
        WidgetItem itemY = vm.VisibleItems.Single(item => item.Name == "y");
        Assert.True(vm.MoveVisibleItemForReorder(itemY, 0));
        Assert.Equal(["y", "x"], GetVisibleNames(vm));

        // Navigate back up to mapped root: root stack and file order must still be intact
        Assert.True(await vm.NavigateUpAsync());
        Assert.True(vm.IsAtMappedRoot);
        WidgetStackItem restoredStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal(["c", restoredStack.Name, "Sub"], GetVisibleNames(vm));
        Assert.Equal(["b", "a"], restoredStack.Members.Select(member => member.Name));
    }

    [Fact]
    public async Task EmbeddedSubfolderNavigation_BidirectionalReorderAndSortModeChangesRemainIndependentAcrossRestart()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-per-folder-bidirectional");
        string folderPath = Path.Combine(_tempRoot, "files-per-folder-bidirectional");
        string subFolder = Path.Combine(folderPath, "Sub");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "c.txt"), "c");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "x.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "y.txt"), "y");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "z.txt"), "z");

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "PerFolderIndependentSort",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name
        };
        settings.Settings.Widgets.Add(config);

        using (var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null))
        {
            await vm.InitializeAsync();

            Assert.Equal(WidgetSortMode.Name, vm.EffectiveSortMode);
            Assert.Equal(["Sub", "a", "b", "c"], GetVisibleNames(vm));

            // 1. Enter Sub: starts inheriting Name, then user manually reorders [x, y, z] -> [z, x, y]
            WidgetItem subItem = vm.VisibleItems.Single(item => item.Name == "Sub");
            Assert.True(await vm.NavigateIntoFolderAsync(subItem));
            Assert.Equal(WidgetSortMode.Name, vm.EffectiveSortMode);
            Assert.Equal(["x", "y", "z"], GetVisibleNames(vm));

            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem itemZ = vm.VisibleItems.Single(item => item.Name == "z");
            Assert.True(vm.MoveVisibleItemForReorder(itemZ, 0));
            Assert.Equal(WidgetSortMode.Manual, vm.EffectiveSortMode);
            Assert.Equal(WidgetSortMode.Name, config.SortMode);
            Assert.Equal(["z", "x", "y"], GetVisibleNames(vm));

            // 2. Return to Root: Root must STILL be in Name auto-sort mode and sort new files automatically
            await File.WriteAllTextAsync(Path.Combine(folderPath, "ab.txt"), "ab");
            Assert.True(await vm.NavigateUpAsync());
            Assert.Equal(WidgetSortMode.Name, vm.EffectiveSortMode);
            Assert.Equal(["Sub", "a", "ab", "b", "c"], GetVisibleNames(vm));

            // 3. Reorder external items in Root -> must NOT reset internal Sub order
            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem itemC = vm.VisibleItems.Single(item => item.Name == "c");
            Assert.True(vm.MoveVisibleItemForReorder(itemC, 0));
            Assert.Equal(["c", "Sub", "a", "ab", "b"], GetVisibleNames(vm));

            subItem = vm.VisibleItems.Single(item => item.Name == "Sub");
            Assert.True(await vm.NavigateIntoFolderAsync(subItem));
            Assert.Equal(WidgetSortMode.Manual, vm.EffectiveSortMode);
            Assert.Equal(["z", "x", "y"], GetVisibleNames(vm));

            // 4. Switch Root back to Name auto-sort -> Sub must STILL remain Manual with [z, x, y]
            Assert.True(await vm.NavigateUpAsync());
            vm.ClearStackDisplayOrderOverride();
            vm.SetSortMode(WidgetSortMode.Name);
            Assert.Equal(WidgetSortMode.Name, vm.EffectiveSortMode);
            Assert.Equal(["Sub", "a", "ab", "b", "c"], GetVisibleNames(vm));

            subItem = vm.VisibleItems.Single(item => item.Name == "Sub");
            Assert.True(await vm.NavigateIntoFolderAsync(subItem));
            Assert.Equal(WidgetSortMode.Manual, vm.EffectiveSortMode);
            Assert.Equal(["z", "x", "y"], GetVisibleNames(vm));

            Assert.True(await vm.NavigateUpAsync());
            await settings.SaveAsync();
        }

        // 5. Cold restart from persisted settings
        var reloadedSettings = await CreateManualStackSettingsAsync(settingsRoot);
        reloadedSettings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        WidgetConfig reloadedConfig = Assert.Single(reloadedSettings.Settings.Widgets);
        var reloadedOrganizer = TestOrganizerServices.Create(reloadedSettings, fileService);
        using var restartedVm = new WidgetViewModel(
            reloadedConfig,
            fileService,
            reloadedOrganizer,
            reloadedSettings,
            dispatcherQueue: null);
        await restartedVm.InitializeAsync();

        Assert.Equal(WidgetSortMode.Name, restartedVm.EffectiveSortMode);
        Assert.Equal(["Sub", "a", "ab", "b", "c"], GetVisibleNames(restartedVm));

        WidgetItem restartedSub = restartedVm.VisibleItems.Single(item => item.Name == "Sub");
        Assert.True(await restartedVm.NavigateIntoFolderAsync(restartedSub));
        Assert.Equal(WidgetSortMode.Manual, restartedVm.EffectiveSortMode);
        Assert.Equal(["z", "x", "y"], GetVisibleNames(restartedVm));
    }

    [Fact]
    public async Task EmbeddedSubfolderNavigation_FlatModeManualOrder_PersistsIndependentlyInRootAndSubfolder()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-flat-subfolder");
        string folderPath = Path.Combine(_tempRoot, "files-flat-subfolder");
        string subFolder = Path.Combine(folderPath, "Sub");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "c.txt"), "c");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "1.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "2.txt"), "2");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "3.txt"), "3");

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileStacksEnabled = false;
        settings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "FlatModePerFolder",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name
        };
        settings.Settings.Widgets.Add(config);

        using (var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null))
        {
            await vm.InitializeAsync();
            Assert.False(vm.UsesStackProjection);

            // Reorder Root in flat mode: [Sub, a, b, c] -> [c, Sub, a, b]
            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem itemC = vm.Items.Single(item => item.Name == "c");
            Assert.True(vm.MoveItemsForReorder([itemC], 0));
            vm.PersistManualOrder();
            Assert.Equal(["c", "Sub", "a", "b"], vm.Items.Select(item => item.Name));

            // Navigate into Sub and reorder in flat mode: [1, 2, 3] -> [3, 1, 2]
            WidgetItem subItem = vm.Items.Single(item => item.Name == "Sub");
            Assert.True(await vm.NavigateIntoFolderAsync(subItem));
            Assert.Equal(WidgetSortMode.Name, vm.EffectiveSortMode);
            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem item3 = vm.Items.Single(item => item.Name == "3");
            Assert.True(vm.MoveItemsForReorder([item3], 0));
            vm.PersistManualOrder();
            Assert.Equal(["3", "1", "2"], vm.Items.Select(item => item.Name));

            // Return to Root, reorder Root again -> [b, c, Sub, a], then verify Sub is untouched
            Assert.True(await vm.NavigateUpAsync());
            Assert.Equal(["c", "Sub", "a", "b"], vm.Items.Select(item => item.Name));
            WidgetItem itemB = vm.Items.Single(item => item.Name == "b");
            Assert.True(vm.MoveItemsForReorder([itemB], 0));
            vm.PersistManualOrder();
            Assert.Equal(["b", "c", "Sub", "a"], vm.Items.Select(item => item.Name));

            subItem = vm.Items.Single(item => item.Name == "Sub");
            Assert.True(await vm.NavigateIntoFolderAsync(subItem));
            Assert.Equal(["3", "1", "2"], vm.Items.Select(item => item.Name));

            await settings.SaveAsync();
        }

        // Cold restart in flat mode
        var reloadedSettings = await CreateManualStackSettingsAsync(settingsRoot);
        reloadedSettings.Settings.FileStacksEnabled = false;
        reloadedSettings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        WidgetConfig reloadedConfig = Assert.Single(reloadedSettings.Settings.Widgets);
        var reloadedOrganizer = TestOrganizerServices.Create(reloadedSettings, fileService);
        using var restartedVm = new WidgetViewModel(
            reloadedConfig,
            fileService,
            reloadedOrganizer,
            reloadedSettings,
            dispatcherQueue: null);
        await restartedVm.InitializeAsync();

        Assert.Equal(["b", "c", "Sub", "a"], restartedVm.Items.Select(item => item.Name));
        WidgetItem restartedSub = restartedVm.Items.Single(item => item.Name == "Sub");
        Assert.True(await restartedVm.NavigateIntoFolderAsync(restartedSub));
        Assert.Equal(["3", "1", "2"], restartedVm.Items.Select(item => item.Name));

        // Enabling stack projection inside Sub keeps the flat manual order intact
        restartedVm.SetFileStacksEnabledOverride(true);
        Assert.True(restartedVm.UsesStackProjection);
        Assert.Equal(["3", "1", "2"], GetVisibleNames(restartedVm));
    }

    [Fact]
    public async Task EmbeddedSubfolderNavigation_AutoCategoryStacksInRootAndSubfolder_KeepIndependentPositions()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-auto-category-isolation");
        string folderPath = Path.Combine(_tempRoot, "files-auto-category-isolation");
        string subFolder = Path.Combine(folderPath, "Sub");
        string childFolder = Path.Combine(subFolder, "Child");
        Directory.CreateDirectory(childFolder);

        // Root: 1 folder ("Sub"), 1 image ("cover.png"), 2 documents ("doc1.docx", "doc2.docx" -> "Documents" stack)
        await File.WriteAllTextAsync(Path.Combine(folderPath, "cover.png"), "img");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "doc1.docx"), "d1");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "doc2.docx"), "d2");

        // Sub: 1 folder ("Child"), 1 image ("banner.png"), 2 documents ("s1.docx", "s2.docx" -> "Documents" stack)
        await File.WriteAllTextAsync(Path.Combine(subFolder, "banner.png"), "img");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "s1.docx"), "s1");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "s2.docx"), "s2");

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileStackAutoStacking = true;
        settings.Settings.FileStackGroupBy = SettingsService.FileStackGroupByKind;
        settings.Settings.FileStackThreshold = 2;
        settings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;

        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "AutoStackIsolation",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name
        };
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        // Place Root's "Documents" stack first, followed by "cover" then "Sub"
        vm.SetSortMode(WidgetSortMode.Manual);
        WidgetStackItem rootDocs = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal("Documents", rootDocs.StackKey);
        WidgetItem rootCover = vm.VisibleItems.Single(item => item.Name == "cover");
        Assert.True(vm.MoveVisibleItemForReorder(rootCover, 0));
        Assert.True(vm.MoveStackForReorder(rootDocs.StackKey, 0));
        Assert.Equal([rootDocs.Name, "cover", "Sub"], GetVisibleNames(vm));

        // Enter Sub and place Sub's "Documents" stack LAST: [Child, banner, Documents]
        WidgetItem rootSub = vm.VisibleItems.Single(item => item.Name == "Sub");
        Assert.True(await vm.NavigateIntoFolderAsync(rootSub));
        vm.SetSortMode(WidgetSortMode.Manual);
        WidgetStackItem subDocs = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal("Documents", subDocs.StackKey);
        Assert.True(vm.MoveStackForReorder(subDocs.StackKey, 3));
        Assert.Equal(["Child", "banner", subDocs.Name], GetVisibleNames(vm));

        // Return to Root: Root's "Documents" stack must still be at index 0
        Assert.True(await vm.NavigateUpAsync());
        WidgetStackItem rootDocsAfter = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal([rootDocsAfter.Name, "cover", "Sub"], GetVisibleNames(vm));

        // Re-enter Sub: Sub's "Documents" stack must still be at index 2
        rootSub = vm.VisibleItems.Single(item => item.Name == "Sub");
        Assert.True(await vm.NavigateIntoFolderAsync(rootSub));
        WidgetStackItem subDocsAfter = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal(["Child", "banner", subDocsAfter.Name], GetVisibleNames(vm));
    }

    [Fact]
    public async Task EmbeddedSubfolderNavigation_RenamingSubfolder_TransfersSortOverrideStacksAndManualOrder()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-subfolder-rename");
        string folderPath = Path.Combine(_tempRoot, "files-subfolder-rename");
        string subFolder = Path.Combine(folderPath, "Sub");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(subFolder, "x.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "y.txt"), "y");
        await File.WriteAllTextAsync(Path.Combine(subFolder, "z.txt"), "z");

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "SubfolderRenameRebase",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name
        };
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        WidgetItem subItem = Assert.Single(vm.VisibleItems);
        Assert.True(await vm.NavigateIntoFolderAsync(subItem));
        vm.SetSortMode(WidgetSortMode.Manual);
        WidgetItem itemZ = vm.VisibleItems.Single(item => item.Name == "z");
        WidgetItem itemY = vm.VisibleItems.Single(item => item.Name == "y");
        Assert.True(vm.CreateManualStack([itemZ, itemY]));
        WidgetStackItem subStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        vm.MoveStackForReorder(subStack.StackKey, 0);
        Assert.Equal([subStack.Name, "x"], GetVisibleNames(vm));

        Assert.True(await vm.NavigateUpAsync());
        subItem = Assert.Single(vm.VisibleItems);
        await vm.RenameItemAsync(subItem, "SubRenamed");


        WidgetItem renamedSubItem = Assert.Single(vm.VisibleItems);
        Assert.Equal("SubRenamed", renamedSubItem.Name);
        Assert.True(await vm.NavigateIntoFolderAsync(renamedSubItem));
        Assert.Equal(WidgetSortMode.Manual, vm.EffectiveSortMode);
        WidgetStackItem renamedSubStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal([renamedSubStack.Name, "x"], GetVisibleNames(vm));
        Assert.Equal(["z", "y"], renamedSubStack.Members.Select(member => member.Name));
    }

    [Fact]
    public async Task EmbeddedSubfolderNavigation_LegacySubfolderKeysInRootStackOrder_MigrateCleanly()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings-legacy-sub-migration");
        string folderPath = Path.Combine(_tempRoot, "files-legacy-sub-migration");
        string subFolder = Path.Combine(folderPath, "Sub");
        Directory.CreateDirectory(subFolder);
        string aPath = Path.Combine(folderPath, "a.txt");
        string cPath = Path.Combine(folderPath, "c.txt");
        string xPath = Path.Combine(subFolder, "x.txt");
        string yPath = Path.Combine(subFolder, "y.txt");
        await File.WriteAllTextAsync(aPath, "a");
        await File.WriteAllTextAsync(cPath, "c");
        await File.WriteAllTextAsync(xPath, "x");
        await File.WriteAllTextAsync(yPath, "y");

        var settings = await CreateManualStackSettingsAsync(settingsRoot);
        settings.Settings.FileWidget.FileWidgetFolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.Embedded;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "LegacySubfolderOrderMigration",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Manual
        };
        // Pre-isolation format: root FileStackGroupOrder mixed root and subfolder Item: keys together
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                "Item:" + Path.GetFullPath(cPath).ToUpperInvariant(),
                "Item:" + Path.GetFullPath(subFolder).ToUpperInvariant(),
                "Item:" + Path.GetFullPath(aPath).ToUpperInvariant(),
                "Item:" + Path.GetFullPath(yPath).ToUpperInvariant(),
                "Item:" + Path.GetFullPath(xPath).ToUpperInvariant()
            ]);
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        Assert.Equal(["c", "Sub", "a"], GetVisibleNames(vm));

        // Reorder Root and verify migrated Sub order [y, x] is preserved independently
        WidgetItem itemA = vm.VisibleItems.Single(item => item.Name == "a");
        Assert.True(vm.MoveVisibleItemForReorder(itemA, 0));
        Assert.Equal(["a", "c", "Sub"], GetVisibleNames(vm));

        WidgetItem subItem = vm.VisibleItems.Single(item => item.Name == "Sub");
        Assert.True(await vm.NavigateIntoFolderAsync(subItem));
        Assert.Equal(["y", "x"], GetVisibleNames(vm));
    }

    private static async Task<SettingsService> CreateManualStackSettingsAsync(string settingsRoot)
    {
        var settings = new SettingsService(settingsRoot);
        await settings.LoadAsync();
        settings.Settings.FileStacksEnabled = true;
        settings.Settings.FileStackAutoStacking = false;
        settings.Settings.FileStackOpenMode = SettingsService.FileStackOpenModePopover;
        settings.Settings.ShowFileExtensions = false;
        return settings;
    }

    private static string[] GetVisibleNames(WidgetViewModel vm) =>
        vm.VisibleItems.Select(item => item.Name).ToArray();
}
