using System.Runtime.CompilerServices;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class DragDropSecurityAndCompatibilityTests : IDisposable
{
    private readonly string _tempRoot;

    public DragDropSecurityAndCompatibilityTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBox.DragSecurityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                DropStagingFileOps.DeleteStagingTreeNonReparse(
                    new DirectoryInfo(_tempRoot),
                    isBatchRoot: false);
            }
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData(false, false, true, false, true, false)]
    [InlineData(false, false, false, true, false, true)]
    [InlineData(true, false, false, true, true, false)]
    [InlineData(true, false, true, false, false, true)]
    [InlineData(false, true, false, true, true, false)]
    [InlineData(false, true, true, false, false, true)]
    [InlineData(true, true, true, false, true, false)]
    [InlineData(true, true, false, true, false, true)]
    public void EvaluateLogicalDragButtons_RespectsSwappedMouseButtons(
        bool useSecondaryDragButton,
        bool swappedButtons,
        bool vkLButtonDown,
        bool vkRButtonDown,
        bool expectedDragDown,
        bool expectedAbortDown)
    {
        (bool dragButtonDown, bool abortButtonDown) = Win32Helper.EvaluateLogicalDragButtons(
            useSecondaryDragButton,
            swappedButtons,
            vkLButtonDown,
            vkRButtonDown);

        Assert.Equal(expectedDragDown, dragButtonDown);
        Assert.Equal(expectedAbortDown, abortButtonDown);
    }

    [Fact]
    public void DeleteStagingTreeNonReparse_DeletesStagingFilesWithoutDeletingJunctionTargetContents()
    {
        string stagingDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "staging")).FullName;
        string externalTargetDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "external-target")).FullName;
        string stagedFile = Path.Combine(stagingDir, "staged.txt");
        string protectedExternalFile = Path.Combine(externalTargetDir, "secret.txt");
        File.WriteAllText(stagedFile, "staged");
        File.WriteAllText(protectedExternalFile, "do-not-delete");

        string junctionPath = Path.Combine(stagingDir, "escape-junction");
        Assert.True(TestJunction.TryCreate(junctionPath, externalTargetDir));

        DropStagingFileOps.DeleteStagingTreeNonReparse(new DirectoryInfo(stagingDir));

        Assert.False(Directory.Exists(stagingDir));
        Assert.False(File.Exists(stagedFile));
        Assert.True(Directory.Exists(externalTargetDir));
        Assert.True(File.Exists(protectedExternalFile));
        Assert.Equal("do-not-delete", File.ReadAllText(protectedExternalFile));
    }

    [Fact]
    public async Task SuppressLayoutUndoScope_SuppressesUndoHistoryAcrossAsyncBoundaryAndRestoresAfterDispose()
    {
        string settingsRoot = Path.Combine(_tempRoot, "settings", Guid.NewGuid().ToString("N"));
        string folderPath = Path.Combine(_tempRoot, "widget-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderPath);
        foreach (string name in new[] { "1.txt", "2.txt", "3.txt" })
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, name), name);
        }

        var settings = new SettingsService(settingsRoot);
        await settings.LoadAsync();
        settings.Settings.FileStacksEnabled = false;
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "SuppressScopeTest",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name,
        };
        settings.Settings.Widgets.Add(config);

        using var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        using (vm.SuppressLayoutUndoScope())
        {
            await Task.Yield();
            vm.SetSortMode(WidgetSortMode.Manual);
            WidgetItem item3 = vm.Items.Single(item => item.Name == "3");
            Assert.True(vm.MoveItemsForReorder([item3], targetIndex: 0));
            Assert.False(vm.HasLayoutUndoHistory);
        }

        WidgetItem item2 = vm.Items.Single(item => item.Name == "2");
        Assert.True(vm.MoveItemsForReorder([item2], targetIndex: 0));
        Assert.True(vm.HasLayoutUndoHistory);
    }

    [Fact]
    public void TransferIntentEngineAndSnapshot_EnforceAppExecutionAliasAndCtrlShiftShortcut()
    {
        SourceOriginSnapshot aliasOrigin = SourceOriginSnapshot.Empty with
        {
            HasAppExecutionAliasSource = true,
        };
        TransferIntentRequest baseAliasRequest = new(
            TransferDomain.ExternalInbound,
            TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: false,
            ShiftDown: false,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
            NoOperationMetadata: false,
            DefaultMove: false,
            FollowWindows: true,
            SameVolume: true,
            CanLink: true,
            SourceOrigin: aliasOrigin);

        Assert.Equal(FileDropIntent.Shortcut, TransferIntentEngine.Evaluate(baseAliasRequest).Intent);
        Assert.Equal(
            FileDropIntent.Shortcut,
            TransferIntentEngine.Evaluate(baseAliasRequest with { ControlDown = true }).Intent);
        Assert.Equal(
            FileDropIntent.None,
            TransferIntentEngine.Evaluate(baseAliasRequest with { ShiftDown = true }).Intent);

        string normalFile = Path.Combine(_tempRoot, "doc.txt");
        File.WriteAllText(normalFile, "doc");
        FileDropIntent? ctrlShiftForced = ContentWidgetWindow.ResolveSnapshotForcedIntent(
            ctrlDown: true,
            shiftDown: true,
            altDown: false,
            allowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
            containsTemporaryFiles: false,
            sourcePaths: [normalFile]);
        Assert.Equal(FileDropIntent.Shortcut, ctrlShiftForced);
    }

    [Fact]
    public void ActiveDeskBoxDragRegistry_TracksNestedPointerDragSessionsSafely()
    {
        int begun = 0;
        try
        {
            ActiveDeskBoxDragRegistry.BeginPointerDragSession();
            begun++;
            Assert.True(ActiveDeskBoxDragRegistry.HasActivePointerDragSession);

            ActiveDeskBoxDragRegistry.BeginPointerDragSession();
            begun++;
            Assert.True(ActiveDeskBoxDragRegistry.HasActivePointerDragSession);

            ActiveDeskBoxDragRegistry.EndPointerDragSession();
            begun--;
            Assert.True(ActiveDeskBoxDragRegistry.HasActivePointerDragSession);

            ActiveDeskBoxDragRegistry.EndPointerDragSession();
            begun--;
        }
        finally
        {
            while (begun > 0)
            {
                ActiveDeskBoxDragRegistry.EndPointerDragSession();
                begun--;
            }
        }
    }

    [Fact]
    public void GetArrivalNameCandidates_PreservesVersionedDirectoryNameWithoutSplittingDots()
    {
        string[] dirCandidates = DesktopOutboundDropCoordinator
            .GetArrivalNameCandidates("DeskBox.v2.1", isDirectory: true)
            .ToArray();

        Assert.Contains("DeskBox.v2.1", dirCandidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("DeskBox.v2.1 - Copy", dirCandidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("DeskBox.v2.1 (2)", dirCandidates, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeskBox.v2 - Copy.1", dirCandidates, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeskBox.v2 (2).1", dirCandidates, StringComparer.OrdinalIgnoreCase);

        string[] fileCandidates = DesktopOutboundDropCoordinator
            .GetArrivalNameCandidates("DeskBox.v2.txt", isDirectory: false)
            .ToArray();
        Assert.Contains("DeskBox.v2 - Copy.txt", fileCandidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("DeskBox.v2 (2).txt", fileCandidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAnyDeskBoxStagingPath_RecognizesBothStoreLocalLowAndTempRoots()
    {
        string storeRoot = DropStagingFileOps.GetStagingRootDirectory(isMicrosoftStoreOverride: true);
        string unpackagedRoot = DropStagingFileOps.GetStagingRootDirectory(isMicrosoftStoreOverride: false);
        string storeChild = Path.Combine(storeRoot, "VirtualDrops", "batch-1", "sample.txt");
        string unpackagedChild = Path.Combine(unpackagedRoot, "VirtualDrops", "batch-2", "sample.txt");
        string unrelatedPath = Path.Combine(_tempRoot, "user-folder", "sample.txt");

        Assert.True(DropStagingFileOps.IsAnyDeskBoxStagingPath(storeChild));
        Assert.True(DropStagingFileOps.IsAnyDeskBoxStagingPath(unpackagedChild));
        Assert.False(DropStagingFileOps.IsAnyDeskBoxStagingPath(unrelatedPath));
    }

    [Fact]
    public void DeleteStagingTreeNonReparse_DeletesUnlockedSiblingsWhenOneFileIsLocked()
    {
        string stagingDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "locked-staging")).FullName;
        string subDir = Directory.CreateDirectory(Path.Combine(stagingDir, "sub")).FullName;
        string lockedFile = Path.Combine(stagingDir, "locked.txt");
        string unlockedSibling = Path.Combine(stagingDir, "unlocked.txt");
        string unlockedNested = Path.Combine(subDir, "nested.txt");

        File.WriteAllText(lockedFile, "locked");
        File.WriteAllText(unlockedSibling, "unlocked");
        File.WriteAllText(unlockedNested, "nested");

        using (var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            DropStagingFileOps.DeleteStagingTreeNonReparse(new DirectoryInfo(stagingDir));

            Assert.True(File.Exists(lockedFile));
            Assert.False(File.Exists(unlockedSibling));
            Assert.False(File.Exists(unlockedNested));
            Assert.False(Directory.Exists(subDir));
        }
    }

    [Fact]
    public void ShortcutHelper_ReadsTargetPathExceedingMaxPath()
    {
        string segment = new('d', 95);
        string longTargetDir = Path.Combine(_tempRoot, segment, segment, segment + "_target");
        Assert.True(longTargetDir.Length > 270);
        Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(longTargetDir));

        string shortcutPath = Path.Combine(_tempRoot, "long-folder.lnk");
        ShortcutHelper.CreateOrUpdateFolderShortcut(shortcutPath, longTargetDir, "Long target folder");
        ShortcutHelper.InvalidateStoredMetadataCache(shortcutPath);

        string expectedTarget = Path.GetFullPath(longTargetDir);
        ShortcutInfo? stored = ShortcutHelper.ReadStoredMetadata(shortcutPath);
        string? effective = ShortcutHelper.TryReadEffectiveTargetPath(shortcutPath);

        Assert.NotNull(stored);
        Assert.Equal(expectedTarget, stored!.TargetPath, ignoreCase: true);
        Assert.Equal(expectedTarget, effective, ignoreCase: true);
    }

    [Fact]
    public void ResolvePendingPointerDragItemsOnPress_PreservesSelectedStacksForMultiDrag()
    {
        var stackOne = new WidgetStackItem
        {
            Category = WidgetStackCategory.Other,
            StackKey = "Manual:stack-1",
            Name = "Stack One"
        };
        var stackTwo = new WidgetStackItem
        {
            Category = WidgetStackCategory.Other,
            StackKey = "Manual:stack-2",
            Name = "Stack Two"
        };

        WidgetItem[] pending = FileSurfaceContent.ResolvePendingPointerDragItemsOnPress(
            stackOne,
            [stackOne, stackTwo],
            shiftPressed: false);

        Assert.Equal(2, pending.Length);
        Assert.Contains(stackOne, pending);
        Assert.Contains(stackTwo, pending);
    }

    [Fact]
    public async Task RestoreDepartedStackMemberships_RebuildsDissolvedTwoItemStackAfterMoveUndo()
    {
        string settingsRoot = Path.Combine(_tempRoot, "stack-undo-settings");
        string folderPath = Path.Combine(_tempRoot, "stack-undo-files");
        Directory.CreateDirectory(folderPath);
        string fileA = Path.Combine(folderPath, "alpha.txt");
        string fileB = Path.Combine(folderPath, "beta.txt");
        await File.WriteAllTextAsync(fileA, "alpha");
        await File.WriteAllTextAsync(fileB, "beta");

        var settings = new SettingsService(settingsRoot);
        await settings.LoadAsync();
        settings.Settings.FileStacksEnabled = true;
        settings.Settings.FileStackAutoStacking = false;
        settings.Settings.FileStackOpenMode = SettingsService.FileStackOpenModePopover;

        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService);
        var config = new WidgetConfig
        {
            Name = "StackUndoTest",
            MappedFolderPath = folderPath,
            FollowsDefaultStoragePath = false,
            SortMode = WidgetSortMode.Name
        };
        settings.Settings.Widgets.Add(config);

        var vm = new WidgetViewModel(
            config,
            fileService,
            organizer,
            settings,
            dispatcherQueue: null);
        await vm.InitializeAsync();

        Assert.True(vm.CreateManualStack(vm.Items.ToArray()));
        WidgetStackItem initialStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal(2, initialStack.Members.Count);

        File.Delete(fileA);
        await vm.HandleItemsMovedOutAsync([fileA]);

        Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Single(vm.VisibleItems);

        await File.WriteAllTextAsync(fileA, "alpha");
        await vm.RefreshFolderContentsAsync();

        Assert.Empty(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.True(vm.RestoreDepartedStackMemberships([fileA]));

        WidgetStackItem restoredStack = Assert.Single(vm.VisibleItems.OfType<WidgetStackItem>());
        Assert.Equal(2, restoredStack.Members.Count);
        Assert.Contains(restoredStack.Members, item => string.Equals(item.Path, fileA, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(restoredStack.Members, item => string.Equals(item.Path, fileB, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetAllArrivalNameCandidates_IncludesLocalizedCopySuffixes()
    {
        string[] candidates = DesktopOutboundDropCoordinator
            .GetAllArrivalNameCandidates("notes.txt")
            .ToArray();

        Assert.Contains("notes - Copy.txt", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notes - Copie.txt", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notes - Kopie.txt", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notes - Copia.txt", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveFileDropCompletionFeedbackOperation_PreservesWin10LinkWithoutReportingMove()
    {
        var internalPackage = new DataPackage();
        internalPackage.Properties[DeskBoxDragData.SourceWidgetIdProperty] = "widget-source";
        internalPackage.Properties[DeskBoxDragData.SourcePathsProperty] = new[] { @"C:\Users\Test\AppData\Local\Microsoft\WindowsApps\wt.exe" };
        internalPackage.Properties[DeskBoxDragData.InternalFileDragTokenProperty] = DeskBoxDragData.InternalFileDragToken;
        DataPackageView internalView = internalPackage.GetView();

        Assert.Equal(
            DataPackageOperation.Link,
            DeskBoxDragData.ResolveFileDropCompletionFeedbackOperation(
                internalView,
                DataPackageOperation.Link,
                DataPackageOperation.Link));

        Assert.Equal(
            DataPackageOperation.Copy,
            DeskBoxDragData.ResolveFileDropCompletionFeedbackOperation(
                internalView,
                DataPackageOperation.Link,
                DataPackageOperation.Copy | DataPackageOperation.Link));

        Assert.Equal(
            DataPackageOperation.Copy,
            DeskBoxDragData.ResolveFileDropCompletionFeedbackOperation(
                internalView,
                DataPackageOperation.Copy,
                DataPackageOperation.Move));

        Assert.Equal(
            DataPackageOperation.None,
            DeskBoxDragData.ResolveFileDropCompletionFeedbackOperation(
                internalView,
                DataPackageOperation.None,
                DataPackageOperation.Move));
    }

    [Fact]
    public void ResolveCompletionEffect_ReturnsLinkForSingleBitLinkOfferAndNeverMove()
    {
        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: NativeDropEffectPolicy.Link));

        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: NativeDropEffectPolicy.Move));
    }
}
