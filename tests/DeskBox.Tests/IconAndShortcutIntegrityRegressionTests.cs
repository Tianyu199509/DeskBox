using System.Reflection;
using System.Text;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class IconAndShortcutIntegrityRegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        $"icon-shortcut-integrity-{Guid.NewGuid():N}");

    public IconAndShortcutIntegrityRegressionTests()
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
        }
    }

    [Fact]
    public void ProxyFailureWindow_UsesShortRetryWindowForIconsAndLongWindowForThumbnails()
    {
        Assert.Equal(
            30_000L,
            ShellThumbnailProxy.GetFailureRetryDelayMilliseconds(
                ShellThumbnailProxy.ShellImageMode.Thumbnail));
        Assert.True(
            ShellThumbnailProxy.GetFailureRetryDelayMilliseconds(
                ShellThumbnailProxy.ShellImageMode.Icon) <= 1_000L);
        Assert.True(
            ShellThumbnailProxy.GetFailureRetryDelayMilliseconds(
                ShellThumbnailProxy.ShellImageMode.IconWithOverlays) <= 1_000L);
    }

    [Fact]
    public async Task BatchFrameReader_SlidingPerFrameTimeoutKeepsCompletedFramesWhenLastItemHangs()
    {
        byte[] okPayload = new byte[138];
        okPayload[0] = (byte)'B';
        okPayload[1] = (byte)'M';

        byte[] frameZero = BuildBatchFrame(0, 0, okPayload);
        byte[] frameOne = BuildBatchFrame(1, 0, okPayload);
        var requests = new[]
        {
            new ShellThumbnailProxy.ProxyBatchRequest(
                ShellThumbnailProxy.ShellImageMode.Icon,
                @"C:\fast0.lnk",
                48,
                new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously)),
            new ShellThumbnailProxy.ProxyBatchRequest(
                ShellThumbnailProxy.ShellImageMode.Icon,
                @"C:\fast1.lnk",
                48,
                new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously)),
            new ShellThumbnailProxy.ProxyBatchRequest(
                ShellThumbnailProxy.ShellImageMode.Icon,
                @"C:\hung2.lnk",
                48,
                new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously)),
        };

        // Frame 0 arrives after 80 ms, frame 1 arrives 80 ms after frame 0
        // (total 160 ms > 130 ms per-frame timeout!), and frame 2 hangs.
        await using var stream = new StaggeredThenHangingFrameStream(
            [
                (TimeSpan.FromMilliseconds(70), frameZero),
                (TimeSpan.FromMilliseconds(70), frameOne),
            ]);
        var fulfilled = new bool[requests.Length];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ShellThumbnailProxy.ReadBatchFramesAsync(
                stream,
                requests,
                fulfilled,
                CancellationToken.None,
                perFrameTimeout: TimeSpan.FromMilliseconds(140)));

        Assert.True(fulfilled[0]);
        Assert.True(fulfilled[1]);
        Assert.False(fulfilled[2]);
        Assert.Equal(okPayload, await requests[0].Completion.Task);
        Assert.Equal(okPayload, await requests[1].Completion.Task);
        Assert.False(requests[2].Completion.Task.IsCompleted);
    }

    [Fact]
    public void LoadIconBytes_StrictModeRefusesBlankSheetFallbackForPerInstanceItemsAndResolvesFolders()
    {
        string lnkPath = Path.Combine(_tempRoot, "app.lnk");
        string urlPath = Path.Combine(_tempRoot, "site.url");
        string emptyExePath = Path.Combine(_tempRoot, "alias.exe");
        string plainFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "PlainFolder")).FullName;
        string customFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "CustomFolder")).FullName;

        File.WriteAllBytes(lnkPath, [0x4C, 0x00, 0x00, 0x00]);
        File.WriteAllText(urlPath, "[InternetShortcut]\r\nURL=https://example.com\r\n");
        File.WriteAllBytes(emptyExePath, []); // 0-byte WindowsApps AppExecutionAlias shape
        File.WriteAllText(Path.Combine(customFolder, "desktop.ini"), "[.ShellClassInfo]\r\n");

        Assert.Null(InvokeStrictLoadIconBytes(CreateIconSource(lnkPath, 0, explicitIndex: false, shellItem: true)));
        Assert.Null(InvokeStrictLoadIconBytes(CreateIconSource(urlPath, 0, explicitIndex: false, shellItem: false)));
        Assert.Null(InvokeStrictLoadIconBytes(CreateIconSource(emptyExePath, 0, explicitIndex: false, shellItem: false)));
        Assert.Null(InvokeStrictLoadIconBytes(CreateIconSource(emptyExePath, 99, explicitIndex: true, shellItem: false)));
        Assert.Null(InvokeStrictLoadIconBytes(CreateIconSource(customFolder, 0, explicitIndex: false, shellItem: false)));

        byte[]? plainFolderBytes = InvokeStrictLoadIconBytes(
            CreateIconSource(plainFolder, 0, explicitIndex: false, shellItem: false));
        Assert.NotNull(plainFolderBytes);
        Assert.True(IconPngEncoder.TryDecodePngHasVisiblePixels(plainFolderBytes!));
    }

    [Fact]
    public void LoadingPlaceholderAndHydrationRetry_KeepVisibleVectorGlyphAndResetTransientFailures()
    {
        string surfaceXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string hydrationSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs"));

        Assert.Contains("x:Key=\"FileIconLoadingPlaceholderStyle\"", surfaceXaml, StringComparison.Ordinal);
        Assert.Contains("<Border", surfaceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Path", surfaceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<FontIcon", surfaceXaml, StringComparison.Ordinal);
        Assert.Contains("EnsureProjectionAndRenderWindowSynchronizedForHydration()", hydrationSource, StringComparison.Ordinal);
        Assert.Contains("resetTransientFailures: true", hydrationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutCloning_PreservesUrlExtensionFaviconStreamAndBinaryLnkWithoutNesting()
    {
        Assert.Equal(
            "GitHub - Shortcut.url",
            FileSurfaceContent.GetShortcutDisplayName(@"C:\Links\GitHub.url", isDirectory: false));
        Assert.Equal(
            "MyApp - Shortcut.lnk",
            FileSurfaceContent.GetShortcutDisplayName(@"C:\Links\MyApp.lnk", isDirectory: false));
        Assert.Equal(
            "v1.2.3 - Shortcut.lnk",
            FileSurfaceContent.GetShortcutDisplayName(@"C:\Releases\v1.2.3", isDirectory: true));

        string sourceUrl = Path.Combine(_tempRoot, "BrowserSite.url");
        string clonedUrl = Path.Combine(_tempRoot, "Cloned", "BrowserSite - Shortcut.url");
        byte[] faviconPayload = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        File.WriteAllText(sourceUrl, "[InternetShortcut]\r\nURL=https://deskbox.example\r\n");
        WriteAlternateDataStream(sourceUrl, ":favicon:$DATA", faviconPayload);

        DropStagingFileOps.CopyShortcutFilePreservingStreams(sourceUrl, clonedUrl);

        Assert.True(File.Exists(clonedUrl));
        Assert.Equal(
            File.ReadAllText(sourceUrl),
            File.ReadAllText(clonedUrl));
        Assert.Equal(
            faviconPayload,
            ReadAlternateDataStream(clonedUrl, ":favicon:$DATA"));

        string sourceLnk = Path.Combine(_tempRoot, "RecycleBin.lnk");
        string clonedLnk = Path.Combine(_tempRoot, "Cloned", "RecycleBin - Shortcut.lnk");
        ShortcutHelper.CreateShellNamespaceShortcut(
            sourceLnk,
            "shell:RecycleBinFolder",
            "Recycle Bin");

        DropStagingFileOps.CopyShortcutFilePreservingStreams(sourceLnk, clonedLnk);

        Assert.Equal(
            "::{645FF040-5081-101B-9F08-00AA002F954E}",
            ShellLinkVtableInterop.TryReadTargetParsingName(clonedLnk),
            ignoreCase: true);
    }

    [Fact]
    public void RawPidlShortcut_RoundTripsExactCustomItemIdListBytes()
    {
        // Construct a valid 2-segment ITEMIDLIST carrying custom property-store-like
        // payload bytes that string re-parsing would never preserve.
        byte[] item1Data = [0x1F, 0x50, 0xE0, 0x4F, 0xD0, 0x20, 0xEA, 0x3A, 0x69, 0x10, 0xA2, 0xD8, 0x08, 0x00, 0x2B, 0x30, 0x30, 0x9D];
        byte[] item2Data = Encoding.Unicode.GetBytes("AppsFolderCustomPropertyPayload!App");
        ushort cb1 = (ushort)(2 + item1Data.Length);
        ushort cb2 = (ushort)(2 + item2Data.Length);
        byte[] rawPidl = new byte[cb1 + cb2 + 2];
        BitConverter.GetBytes(cb1).CopyTo(rawPidl, 0);
        item1Data.CopyTo(rawPidl, 2);
        BitConverter.GetBytes(cb2).CopyTo(rawPidl, cb1);
        item2Data.CopyTo(rawPidl, cb1 + 2);

        Assert.True(ShellLinkVtableInterop.IsValidPidlBytes(rawPidl));

        string shortcutPath = Path.Combine(_tempRoot, "RawPidlApp.lnk");
        Assert.True(ShortcutHelper.TryCreateShortcutFromRawPidl(
            shortcutPath,
            rawPidl,
            "Raw PIDL regression"));
        Assert.True(ShellLinkVtableInterop.TryReadRawPidlBytes(
            shortcutPath,
            out byte[] roundTripped));
        Assert.Equal(rawPidl, roundTripped);
    }

    [Fact]
    public async Task ManagedTransferAndOpenDestinationHandle_PreserveNtfsAlternateDataStreams()
    {
        string sourceDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "ads-src")).FullName;
        string copyDestDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "ads-copy-dst")).FullName;
        string lockedDestDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "ads-locked-dst")).FullName;

        string sourceUrl = Path.Combine(sourceDir, "WebApp.url");
        byte[] faviconBytes = [0x00, 0x00, 0x01, 0x00, 0x10, 0x10, 0xDE, 0xAD, 0xBE, 0xEF];
        DateTime fixedWriteUtc = new(2026, 5, 12, 8, 30, 0, DateTimeKind.Utc);

        await File.WriteAllTextAsync(sourceUrl, "[InternetShortcut]\r\nURL=https://app.example\r\n");
        WriteAlternateDataStream(sourceUrl, ":favicon:$DATA", faviconBytes);
        File.SetLastWriteTimeUtc(sourceUrl, fixedWriteUtc);

        // 1. End-to-end FileService copy (exercises CopyFileCoreAsync + CopyFileMetadata).
        var fileService = new FileService();
        string copiedUrl = Path.Combine(copyDestDir, "WebApp.url");
        await fileService.ExecuteTransferPlanAsync(
            [new FileService.FileTransferPlan(sourceUrl, copiedUrl)],
            move: false);

        Assert.Equal(faviconBytes, ReadAlternateDataStream(copiedUrl, ":favicon:$DATA"));
        Assert.Equal(fixedWriteUtc, File.GetLastWriteTimeUtc(copiedUrl));

        // 2. Directly exercise coexistence with CreateTransferDestinationStream's
        //    ShareNone + DeleteAccess handle (the chunked cross-volume move shape).
        string lockedUrl = Path.Combine(lockedDestDir, "WebApp.url");
        await using (FileStream heldDestination = FileService.CreateTransferDestinationStream(lockedUrl))
        {
            byte[] mainBody = await File.ReadAllBytesAsync(sourceUrl);
            await heldDestination.WriteAsync(mainBody);
            await heldDestination.FlushAsync();

            await StorageMigrationNativeMethods.CopyAlternateDataStreamsAsync(
                sourceUrl,
                lockedUrl,
                CancellationToken.None);
        }

        Assert.Equal(faviconBytes, ReadAlternateDataStream(lockedUrl, ":favicon:$DATA"));
    }

    [Fact]
    public async Task OrganizerService_RebasesCoMovedShortcutTargetWorkingDirAndIconAndRestoresOnUndo()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_tempRoot, "org-desktop")).FullName;
        string boxFolder = Directory.CreateDirectory(Path.Combine(_tempRoot, "org-box")).FullName;

        string desktopAppDir = Directory.CreateDirectory(Path.Combine(desktop, "MyPortableApp")).FullName;
        string desktopExe = Path.Combine(desktopAppDir, "launcher.exe");
        string desktopIco = Path.Combine(desktopAppDir, "brand.ico");
        await File.WriteAllBytesAsync(desktopExe, [0x4D, 0x5A]);
        await File.WriteAllBytesAsync(desktopIco, [0x00, 0x00, 0x01, 0x00]);

        string desktopLnk = Path.Combine(desktop, "MyPortableApp.lnk");
        CreateTestShortcut(
            desktopLnk,
            targetPath: desktopExe,
            workingDirectory: desktopAppDir,
            iconLocation: desktopIco,
            iconIndex: 0);

        var settings = new SettingsService(Path.Combine(_tempRoot, "org-settings"));
        var widget = new WidgetConfig
        {
            Id = "box-1",
            Name = "Apps",
            MappedFolderPath = boxFolder,
            WidgetKind = WidgetKind.File,
        };
        settings.Settings.Widgets.Add(widget);
        var fileService = new FileService();
        var organizer = TestOrganizerServices.Create(settings, fileService, () => desktop);

        OrganizerOperationResult importResult = await organizer.OrganizeDropAsync(
            widget,
            widget.Name,
            [desktopAppDir, desktopLnk],
            move: true);

        string movedAppDir = Path.Combine(boxFolder, "MyPortableApp");
        string movedExe = Path.Combine(movedAppDir, "launcher.exe");
        string movedIco = Path.Combine(movedAppDir, "brand.ico");
        string movedLnk = Path.Combine(boxFolder, "MyPortableApp.lnk");

        ShortcutInfo? inBoxMeta = ShortcutHelper.ReadStoredMetadata(movedLnk);
        Assert.NotNull(inBoxMeta);
        Assert.Equal(movedExe, Path.GetFullPath(inBoxMeta!.TargetPath), ignoreCase: true);
        Assert.Equal(movedAppDir, Path.GetFullPath(inBoxMeta.WorkingDirectory), ignoreCase: true);
        Assert.Equal(movedIco, Path.GetFullPath(inBoxMeta.IconLocation), ignoreCase: true);

        // Undo must verify the post-rebase receipt without throwing "changed on disk"
        // and must rebase the .lnk back to the Desktop folder!
        await organizer.UndoAsync(importResult.History.Id);

        Assert.True(File.Exists(desktopLnk));
        Assert.True(Directory.Exists(desktopAppDir));
        ShortcutInfo? restoredMeta = ShortcutHelper.ReadStoredMetadata(desktopLnk);
        Assert.NotNull(restoredMeta);
        Assert.Equal(desktopExe, Path.GetFullPath(restoredMeta!.TargetPath), ignoreCase: true);
        Assert.Equal(desktopAppDir, Path.GetFullPath(restoredMeta.WorkingDirectory), ignoreCase: true);
        Assert.Equal(desktopIco, Path.GetFullPath(restoredMeta.IconLocation), ignoreCase: true);
    }

    [Fact]
    public async Task DesktopOrganizationTransaction_RebasesCoMovedShortcutRefreshesSnapshotAndUndoesCleanly()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_tempRoot, "tx-desktop")).FullName;
        string storage = Directory.CreateDirectory(Path.Combine(_tempRoot, "tx-storage")).FullName;

        string desktopAppDir = Directory.CreateDirectory(Path.Combine(desktop, "StudioSuite")).FullName;
        string desktopExe = Path.Combine(desktopAppDir, "studio.exe");
        string desktopIco = Path.Combine(desktopAppDir, "studio.ico");
        await File.WriteAllBytesAsync(desktopExe, [0x4D, 0x5A, 0x90, 0x00]);
        await File.WriteAllBytesAsync(desktopIco, [0x00, 0x00, 0x01, 0x00]);

        string desktopLnk = Path.Combine(desktop, "StudioSuite.lnk");
        CreateTestShortcut(
            desktopLnk,
            targetPath: desktopExe,
            workingDirectory: desktopAppDir,
            iconLocation: desktopIco,
            iconIndex: 0);

        var dirInfo = new DirectoryInfo(desktopAppDir);
        var lnkInfo = new FileInfo(desktopLnk);
        string targetDir = Path.Combine(storage, "Apps");
        var plan = new DesktopOrganizationPlan
        {
            Id = Guid.NewGuid().ToString("N"),
            DesktopPath = desktop,
            StorageRootPath = storage,
            Targets =
            [
                new DesktopOrganizationTargetPlan
                {
                    TargetWidgetId = "tx-apps",
                    CategoryId = DesktopOrganizationCategoryIds.Shortcuts,
                    SuggestedDisplayName = "Apps",
                    TargetDirectoryPath = targetDir,
                    CreatesWidget = true,
                    Items =
                    [
                        new DesktopOrganizationFileSnapshot(
                            SourcePath: desktopAppDir,
                            Name: "StudioSuite",
                            Extension: string.Empty,
                            Size: 0,
                            LastWriteTimeUtc: dirInfo.LastWriteTimeUtc,
                            CategoryId: DesktopOrganizationCategoryIds.Other,
                            SubtypeId: null,
                            ExclusionReason: DesktopOrganizationExclusionReason.None,
                            IsDirectory: true,
                            SourceScope: DesktopOrganizationSourceScope.Personal),
                        new DesktopOrganizationFileSnapshot(
                            SourcePath: desktopLnk,
                            Name: "StudioSuite.lnk",
                            Extension: ".lnk",
                            Size: lnkInfo.Length,
                            LastWriteTimeUtc: lnkInfo.LastWriteTimeUtc,
                            CategoryId: DesktopOrganizationCategoryIds.Shortcuts,
                            SubtypeId: null,
                            ExclusionReason: DesktopOrganizationExclusionReason.None,
                            IsDirectory: false,
                            SourceScope: DesktopOrganizationSourceScope.Personal)
                    ]
                }
            ]
        };

        var settings = new SettingsService(Path.Combine(_tempRoot, "tx-settings"));
        var fileService = new FileService();
        var recovery = new DesktopOrganizationRecoveryStore(
            Path.Combine(_tempRoot, "tx-recovery.json"));
        var transaction = new DesktopOrganizationTransaction(settings, fileService, recovery);

        DesktopOrganizationExecutionResult execResult = await transaction.ExecuteAsync(plan);
        Assert.Equal(2, execResult.History.Items.Count);

        string movedAppDir = Path.Combine(targetDir, "StudioSuite");
        string movedExe = Path.Combine(movedAppDir, "studio.exe");
        string movedIco = Path.Combine(movedAppDir, "studio.ico");
        string movedLnk = Path.Combine(targetDir, "StudioSuite.lnk");

        ShortcutInfo? movedMeta = ShortcutHelper.ReadStoredMetadata(movedLnk);
        Assert.NotNull(movedMeta);
        Assert.Equal(movedExe, Path.GetFullPath(movedMeta!.TargetPath), ignoreCase: true);
        Assert.Equal(movedIco, Path.GetFullPath(movedMeta.IconLocation), ignoreCase: true);

        await transaction.UndoAsync(execResult.History.Id);

        Assert.True(File.Exists(desktopLnk));
        Assert.True(Directory.Exists(desktopAppDir));
        ShortcutInfo? restoredMeta = ShortcutHelper.ReadStoredMetadata(desktopLnk);
        Assert.NotNull(restoredMeta);
        Assert.Equal(desktopExe, Path.GetFullPath(restoredMeta!.TargetPath), ignoreCase: true);
        Assert.Equal(desktopIco, Path.GetFullPath(restoredMeta.IconLocation), ignoreCase: true);
    }

    [Fact]
    public void MovingShortcutAlone_AnchorsRelativeIconLocationToOriginalDirectory()
    {
        string desktop = Directory.CreateDirectory(Path.Combine(_tempRoot, "rel-desktop")).FullName;
        string box = Directory.CreateDirectory(Path.Combine(_tempRoot, "rel-box")).FullName;
        string assetsDir = Directory.CreateDirectory(Path.Combine(desktop, "Assets")).FullName;
        string iconFile = Path.Combine(assetsDir, "custom.ico");
        File.WriteAllBytes(iconFile, [0x00, 0x00, 0x01, 0x00]);

        string oldShortcutPath = Path.Combine(desktop, "Portal.lnk");
        string newShortcutPath = Path.Combine(box, "Portal.lnk");
        CreateTestShortcut(
            oldShortcutPath,
            targetPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            workingDirectory: string.Empty,
            iconLocation: @"Assets\custom.ico",
            iconIndex: 0);
        File.Move(oldShortcutPath, newShortcutPath);

        IReadOnlySet<string> rewritten = ShortcutHelper.RebaseMovedShortcuts(
            [(oldShortcutPath, newShortcutPath)]);

        Assert.Contains(newShortcutPath, rewritten);
        ShortcutInfo? updated = ShortcutHelper.ReadStoredMetadata(newShortcutPath);
        Assert.NotNull(updated);
        Assert.Equal(iconFile, Path.GetFullPath(updated!.IconLocation), ignoreCase: true);
    }

    private static object CreateIconSource(
        string path,
        int iconIndex,
        bool explicitIndex,
        bool shellItem)
    {
        Type iconSourceType = typeof(IconHelper).GetNestedType(
            "IconSource",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return Activator.CreateInstance(
            iconSourceType,
            path,
            iconIndex,
            explicitIndex,
            shellItem)!;
    }

    private static byte[]? InvokeStrictLoadIconBytes(object iconSource)
    {
        MethodInfo method = typeof(IconHelper)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == "LoadIconBytes" &&
                candidate.GetParameters().Length == 3);
        return (byte[]?)method.Invoke(null, [iconSource, false, false]);
    }

    private static void CreateTestShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconLocation,
        int iconIndex)
    {
        var metadata = new ShortcutInfo(
            TargetPath: targetPath,
            Description: "test",
            Arguments: string.Empty,
            WorkingDirectory: workingDirectory,
            IconLocation: iconLocation,
            IconIndex: iconIndex);
        ShortcutNativeWriteCallResult result =
            ShortcutNativeBackend.WriteShortcut(shortcutPath, metadata);
        Assert.True(result.Success, $"WriteShortcut failed: {result.Failure}; {result.Detail}");
        ShortcutHelper.InvalidateStoredMetadataCache(shortcutPath);
    }

    private static void WriteAlternateDataStream(string filePath, string streamSuffix, byte[] data)
    {
        string streamPath = StorageMigrationNativeMethods.Extended(Path.GetFullPath(filePath)) + streamSuffix;
        using var handle = FileTransferNativeMethods.CreateFileW(
            streamPath,
            0x4000_0000,
            0x0000_0001 | 0x0000_0002 | 0x0000_0004,
            IntPtr.Zero,
            2,
            0,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"Failed to open ADS for write: {streamPath}");
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.Write(data, 0, data.Length);
        stream.Flush();
    }

    private static byte[] ReadAlternateDataStream(string filePath, string streamSuffix)
    {
        string streamPath = StorageMigrationNativeMethods.Extended(Path.GetFullPath(filePath)) + streamSuffix;
        using var handle = FileTransferNativeMethods.CreateFileW(
            streamPath,
            0x8000_0000,
            0x0000_0001 | 0x0000_0002 | 0x0000_0004,
            IntPtr.Zero,
            3,
            0,
            IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"Expected ADS stream '{streamSuffix}' to exist on '{filePath}'.");
        using var stream = new FileStream(handle, FileAccess.Read);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] BuildBatchFrame(uint index, uint status, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(index);
        writer.Write(status);
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class StaggeredThenHangingFrameStream(
        IReadOnlyList<(TimeSpan Delay, byte[] Bytes)> chunks) : Stream
    {
        private int _chunkIndex;
        private int _offsetInChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_chunkIndex >= chunks.Count)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return 0;
            }

            (TimeSpan delay, byte[] bytes) = chunks[_chunkIndex];
            if (_offsetInChunk == 0 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            int count = Math.Min(buffer.Length, bytes.Length - _offsetInChunk);
            bytes.AsMemory(_offsetInChunk, count).CopyTo(buffer);
            _offsetInChunk += count;
            if (_offsetInChunk >= bytes.Length)
            {
                _chunkIndex++;
                _offsetInChunk = 0;
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
