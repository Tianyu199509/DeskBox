using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class P1Batch2RegressionTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"DeskBox-P1Batch2-{Guid.NewGuid():N}");

    public P1Batch2RegressionTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void WidgetPathRebaseHelper_RebasesItemsStacksOrderAndAddedAtAtomically()
    {
        string oldFolder = Path.Combine(_tempRoot, "OldRoot", "Docs");
        string newFolder = Path.Combine(_tempRoot, "NewRoot", "Docs");
        string oldFileA = Path.Combine(oldFolder, "alpha.pdf");
        string oldFileB = Path.Combine(oldFolder, "sub", "beta.docx");
        string externalFile = Path.Combine(_tempRoot, "External", "keep.txt");
        var addedTimestamp = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

        var config = new WidgetConfig
        {
            Id = "file-widget-1",
            MappedFolderPath = oldFolder,
            Items =
            [
                new WidgetItemConfig { Path = oldFileA, SortOrder = 0 },
                new WidgetItemConfig { Path = oldFileB, SortOrder = 1 },
                new WidgetItemConfig { Path = externalFile, SortOrder = 2 }
            ],
            FileAddedAtByPath = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
            {
                [oldFileA] = addedTimestamp,
                [externalFile] = addedTimestamp.AddMinutes(5)
            }
        };

        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Manual:stack1"] = [oldFileA, oldFileB]
            });
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                "Manual:stack1",
                $"Item:{Path.GetFullPath(oldFileA).ToUpperInvariant()}",
                $"Item:{Path.GetFullPath(externalFile).ToUpperInvariant()}"
            ]);

        bool changed = WidgetPathRebaseHelper.RebaseFolderTree(config, oldFolder, newFolder);

        Assert.True(changed);
        Assert.Equal(Path.GetFullPath(newFolder), config.MappedFolderPath);
        string expectedNewA = Path.Combine(Path.GetFullPath(newFolder), "alpha.pdf");
        string expectedNewB = Path.Combine(Path.GetFullPath(newFolder), "sub", "beta.docx");
        Assert.Equal(expectedNewA, config.Items[0].Path);
        Assert.Equal(expectedNewB, config.Items[1].Path);
        Assert.Equal(Path.GetFullPath(externalFile), Path.GetFullPath(config.Items[2].Path));

        Dictionary<string, List<string>> members = WidgetFileStackSettings.GetStackMemberOverrides(config);
        Assert.Equal([expectedNewA, expectedNewB], members["Manual:stack1"]);

        List<string> order = WidgetFileStackSettings.GetStackOrder(config);
        Assert.Contains($"Item:{expectedNewA.ToUpperInvariant()}", order);
        Assert.Contains($"Item:{Path.GetFullPath(externalFile).ToUpperInvariant()}", order);
        Assert.True(config.FileAddedAtByPath.ContainsKey(expectedNewA));
        Assert.False(config.FileAddedAtByPath.ContainsKey(oldFileA));
    }

    [Fact]
    public void StorageMigrationNativeMethods_AllowsHydratedCloudReparseAndRejectsSymlinksAndPlaceholders()
    {
        const uint IoReparseTagCloud = 0x9000001Au;
        const uint IoReparseTagCloud3 = 0x9000301Au;
        const uint IoReparseTagMountPoint = 0xA0000003u;
        const uint IoReparseTagSymlink = 0xA000000Cu;
        const uint FileAttributeReparsePoint = 0x00000400u;
        const uint FileAttributeRecallOnDataAccess = 0x00400000u;
        const uint FileAttributeOffline = 0x00001000u;

        Assert.True(StorageMigrationNativeMethods.IsAllowedStorageReparseAttributes(
            FileAttributeReparsePoint,
            IoReparseTagCloud,
            out bool unhydrated1));
        Assert.False(unhydrated1);

        Assert.True(StorageMigrationNativeMethods.IsAllowedStorageReparseAttributes(
            FileAttributeReparsePoint,
            IoReparseTagCloud3,
            out bool unhydrated2));
        Assert.False(unhydrated2);

        Assert.False(StorageMigrationNativeMethods.IsAllowedStorageReparseAttributes(
            FileAttributeReparsePoint | FileAttributeRecallOnDataAccess,
            IoReparseTagCloud,
            out bool unhydrated3));
        Assert.True(unhydrated3);

        Assert.False(StorageMigrationNativeMethods.IsAllowedStorageReparseAttributes(
            FileAttributeReparsePoint | FileAttributeOffline,
            IoReparseTagCloud,
            out bool unhydrated4));
        Assert.True(unhydrated4);

        Assert.False(StorageMigrationNativeMethods.IsAllowedStorageReparseAttributes(
            FileAttributeReparsePoint,
            IoReparseTagMountPoint,
            out bool junctionUnhydrated));
        Assert.False(junctionUnhydrated);

        Assert.False(StorageMigrationNativeMethods.IsAllowedStorageReparseAttributes(
            FileAttributeReparsePoint,
            IoReparseTagSymlink,
            out _));
    }

    [Fact]
    public void VirtualDropDestinationPath_PreservesNestedTreeAndBlocksTraversalOrAds()
    {
        string stagingDir = Path.Combine(_tempRoot, "virtual-batch");
        Directory.CreateDirectory(stagingDir);

        Assert.True(DropStagingFileOps.TryBuildVirtualDropDestinationPath(
            stagingDir,
            @"FolderA\SubB\report.txt",
            isDirectory: false,
            index: 0,
            out string nestedDest,
            out string topLevel));
        Assert.Equal(Path.Combine(stagingDir, "FolderA", "SubB", "report.txt"), nestedDest);
        Assert.Equal(Path.Combine(stagingDir, "FolderA"), topLevel);

        Assert.True(DropStagingFileOps.TryBuildVirtualDropDestinationPath(
            stagingDir,
            @"EmptyDir\InnerEmpty",
            isDirectory: true,
            index: 1,
            out string emptyDirDest,
            out string emptyDirTop));
        Assert.True(Directory.Exists(emptyDirDest));
        Assert.Equal(Path.Combine(stagingDir, "EmptyDir"), emptyDirTop);

        Assert.False(DropStagingFileOps.TryBuildVirtualDropDestinationPath(
            stagingDir,
            @"..\..\escape.exe",
            isDirectory: false,
            index: 2,
            out _,
            out _));
        Assert.False(DropStagingFileOps.TryBuildVirtualDropDestinationPath(
            stagingDir,
            @"C:\Windows\System32\cmd.exe",
            isDirectory: false,
            index: 3,
            out _,
            out _));
        Assert.False(DropStagingFileOps.TryBuildVirtualDropDestinationPath(
            stagingDir,
            @"innocent.txt:Zone.Identifier",
            isDirectory: false,
            index: 4,
            out _,
            out _));
        Assert.False(DropStagingFileOps.TryBuildVirtualDropDestinationPath(
            stagingDir,
            @"\rooted\file.txt",
            isDirectory: false,
            index: 5,
            out _,
            out _));
    }

    [Fact]
    public void ResolveSafeDropCompletionOperation_DowngradesExternalMoveAndReportsNoneForInternalMove()
    {
        Assert.Equal(
            DataPackageOperation.None,
            FileSurfaceContent.ResolveSafeDropCompletionOperation(
                DataPackageOperation.Move,
                isDeskBoxFileDrag: true,
                requestedMoveCount: 3,
                completedMoveCount: 3,
                canReportCopy: true));

        Assert.Equal(
            DataPackageOperation.Copy,
            FileSurfaceContent.ResolveSafeDropCompletionOperation(
                DataPackageOperation.Move,
                isDeskBoxFileDrag: false,
                requestedMoveCount: 3,
                completedMoveCount: 3,
                canReportCopy: true));

        Assert.Equal(
            DataPackageOperation.None,
            FileSurfaceContent.ResolveSafeDropCompletionOperation(
                DataPackageOperation.Move,
                isDeskBoxFileDrag: false,
                requestedMoveCount: 3,
                completedMoveCount: 3,
                canReportCopy: false));

        Assert.Equal(
            DataPackageOperation.None,
            FileSurfaceContent.ResolveSafeDropCompletionOperation(
                DataPackageOperation.Move,
                isDeskBoxFileDrag: false,
                requestedMoveCount: 3,
                completedMoveCount: 2,
                canReportCopy: true));
    }

    [Fact]
    public void PackagedUnelevatedRelaunch_FormatsAppsFolderTargetAndRewritesAwaitParentExitPid()
    {
        Assert.True(PackagedUnelevatedRelaunch.TryFormatAppsFolderTarget(
            "Simon.DeskBox_8wekyb3d8bbwe!App",
            out string target));
        Assert.Equal(@"shell:AppsFolder\Simon.DeskBox_8wekyb3d8bbwe!App", target);

        Assert.False(PackagedUnelevatedRelaunch.TryFormatAppsFolderTarget("DeskBox.DeskBox", out _));
        Assert.False(PackagedUnelevatedRelaunch.TryFormatAppsFolderTarget(@"Bad\Aumid!App", out _));

        string normalized = InProcessSettingsAppActions.NormalizeAwaitParentExitArguments(
            "--await-parent-exit 99999 --post-elevation-notice",
            4242);
        Assert.Equal("--await-parent-exit 4242 --post-elevation-notice", normalized);
    }

    [Fact]
    public async Task QuickCaptureDragPackage_TryPrepare_SucceedsForLocalImageFileWithoutArgumentException()
    {
        string pngPath = Path.Combine(_tempRoot, "capture-drag.png");
        byte[] validPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGNkYPj/nwEJMDGgAQAAMQIEA3mLB4MAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(pngPath, validPng);

        var model = new QuickCaptureItem
        {
            Id = "img-1",
            Type = QuickCaptureItemType.Image,
            Body = "Image",
            ImagePath = pngPath,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var itemVm = new QuickCaptureItemViewModel(
            model,
            TestServices.CreateLocalizationService(),
            textSize: 14,
            iconSize: 16,
            searchText: null);
        var dataPackage = new DataPackage();

        bool prepared = QuickCaptureDragPackage.TryPrepare(
            dataPackage,
            [itemVm],
            TestServices.CreateLocalizationService());

        Assert.True(prepared);
        DataPackageView view = dataPackage.GetView();
        Assert.True(view.Contains(StandardDataFormats.StorageItems));
        Assert.True(view.Contains(StandardDataFormats.Bitmap));

        var storageItems = await view.GetStorageItemsAsync();
        Assert.Single(storageItems);
        Assert.Equal(Path.GetFullPath(pngPath), storageItems[0].Path, ignoreCase: true);

        var bitmapRef = await view.GetBitmapAsync();
        Assert.NotNull(bitmapRef);
        using var stream = await bitmapRef.OpenReadAsync();
        Assert.True(stream.Size > 0);
    }

    [Fact]
    public async Task TodoAndQuickCapture_ReclaimManagedAttachmentsAfterUndoExpiresAndRecycleOnClear()
    {
        var recycledPaths = new List<string>();
        Task<bool> FakeRecycleKernelAsync(string path)
        {
            recycledPaths.Add(Path.GetFullPath(path));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.FromResult(true);
        }

        // 1. TodoWidget: preserve managed attachment while Undo is active, then recycle once Undo is dismissed.
        string widgetsRoot = Path.Combine(_tempRoot, "widgets");
        var todoStore = new TodoWidgetStore(widgetsRoot, "todo-p1");
        var todoConfig = new WidgetConfig { Id = "todo-p1", WidgetKind = WidgetKind.Todo };
        using var todoVm = new TodoWidgetViewModel(
            todoStore,
            TestServices.CreateLocalizationService(),
            todoConfig,
            recycleAttachmentFileAsync: FakeRecycleKernelAsync);
        await todoVm.InitializeAsync();

        TodoItemViewModel? created = await todoVm.AddItemAsync("Task with attachment");
        Assert.NotNull(created);
        string sourceDoc = Path.Combine(_tempRoot, "spec.txt");
        await File.WriteAllTextAsync(sourceDoc, "managed-attachment-body");
        TodoAttachmentViewModel? addedAttachment = await todoVm.AddAttachmentPathAsync(
            created!.Id,
            sourceDoc,
            copyToManagedStorageOverride: true);
        Assert.NotNull(addedAttachment);
        string managedTodoAttachmentPath = addedAttachment!.FilePath;
        Assert.True(File.Exists(managedTodoAttachmentPath));

        Assert.True(await todoVm.DeleteItemAsync(created.Id));
        // Still on disk while Undo is available!
        Assert.True(File.Exists(managedTodoAttachmentPath));
        Assert.DoesNotContain(Path.GetFullPath(managedTodoAttachmentPath), recycledPaths);

        await todoVm.DismissUndoAsync();
        Assert.Contains(Path.GetFullPath(managedTodoAttachmentPath), recycledPaths);
        Assert.False(File.Exists(managedTodoAttachmentPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(managedTodoAttachmentPath)!));

        // 2. QuickCaptureService: ClearAsync recycles managed attachments instead of bypassing the Recycle Bin.
        recycledPaths.Clear();
        string qcRoot = Path.Combine(_tempRoot, "quick-capture");
        var qcStore = new QuickCaptureStore(qcRoot);
        var qcService = new QuickCaptureService(qcStore, FakeRecycleKernelAsync);
        QuickCaptureItem qcItem = await qcService.AddItemAsync("Note with managed file");

        string qcItemAttachmentDir = Path.Combine(qcStore.AttachmentDirectory, qcItem.Id);
        TodoAttachment? qcManaged = await AttachmentStorageService.ImportPathAsync(
            sourceDoc,
            qcItemAttachmentDir,
            copyToManagedStorage: true);
        Assert.NotNull(qcManaged);

        QuickCaptureStoreData qcData = await qcStore.LoadAsync();
        qcData.Items.Single(entry => entry.Id == qcItem.Id).Attachments.Add(qcManaged!);
        await qcStore.SaveAsync(qcData);

        var reloadedQcService = new QuickCaptureService(qcStore, FakeRecycleKernelAsync);
        await reloadedQcService.ClearAsync();

        Assert.Contains(Path.GetFullPath(qcManaged!.FilePath), recycledPaths);
        Assert.False(File.Exists(qcManaged.FilePath));
    }

    [Fact]
    public async Task ArchiveZipService_PreservesDottedFolderNamesReadsSharedLockedFilesSkipsJunctionLoopsAndBlocksZipBombs()
    {
        // 1. Dotted directory name & shared-write locked source file & junction cycle safety.
        string projectDir = Path.Combine(_tempRoot, "Project.v2.1");
        Directory.CreateDirectory(Path.Combine(projectDir, "empty-sub"));
        string liveLogFile = Path.Combine(projectDir, "active.log");
        await File.WriteAllTextAsync(liveLogFile, "line-while-open", Encoding.UTF8);

        string junctionLink = Path.Combine(projectDir, "loop-back");
        CreateDirectoryJunctionIfSupported(junctionLink, projectDir);

        string zipPath;
        using (var writerLock = new FileStream(
                   liveLogFile,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.ReadWrite))
        {
            zipPath = await ArchiveZipService.CompressToZipAsync([projectDir]);
        }

        Assert.Equal(Path.Combine(_tempRoot, "Project.v2.1.zip"), zipPath);
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.partial"));

        string extracted = await ArchiveZipService.ExtractAllAsync(zipPath);
        Assert.Equal(
            "line-while-open",
            await File.ReadAllTextAsync(Path.Combine(extracted, "Project.v2.1", "active.log")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "Project.v2.1", "empty-sub")));
        Assert.False(Directory.Exists(Path.Combine(extracted, "Project.v2.1", "loop-back")));

        // 2. Backslash directory entry + Zip Bomb budget enforcement & partial folder cleanup.
        string bombZip = Path.Combine(_tempRoot, "oversized.zip");
        using (var fs = new FileStream(bombZip, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            archive.CreateEntry(@"winstyle\dir\");
            ZipArchiveEntry bigEntry = archive.CreateEntry(@"winstyle\dir\payload.bin");
            using Stream s = bigEntry.Open();
            s.Write(new byte[4096]);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ArchiveZipService.ExtractAllCoreAsync(bombZip, maxExtractedBytes: 1024));
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "oversized")));
    }

    private static void CreateDirectoryJunctionIfSupported(string junctionPath, string targetPath)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        proc?.WaitForExit(5000);
    }
}
