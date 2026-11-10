using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class Phase1IntentAndOriginTests : IDisposable
{
    private readonly string _tempRoot;

    public Phase1IntentAndOriginTests()
    {
        PathOriginClassifier.ResetCachesForTests();
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "DeskBoxTests",
            "Phase1IntentOrigin",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        PathOriginClassifier.ResetCachesForTests();
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
    public void PathOriginClassifier_ResolveSubstPath_ResolvesLocalDriveAndUncSubstAndPreventsLoops()
    {
        string localResolved = PathOriginClassifier.ResolveSubstPath(
            @"Z:\Sub\report.pdf",
            drive => drive == 'Z' ? @"\??\D:\Projects\Work" : null);
        Assert.Equal(@"D:\Projects\Work\Sub\report.pdf", localResolved);

        string uncResolved = PathOriginClassifier.ResolveSubstPath(
            @"Y:\Docs\spec.docx",
            drive => drive == 'Y' ? @"\??\UNC\CorpServer\TeamShare\TeamA" : null);
        Assert.Equal(@"\\CorpServer\TeamShare\TeamA\Docs\spec.docx", uncResolved);

        // Cyclic subst mapping (X -> W -> X) terminates cleanly within bounded hops.
        string cyclic = PathOriginClassifier.ResolveSubstPath(
            @"X:\file.txt",
            drive => drive switch
            {
                'X' => @"\??\W:\LoopA",
                'W' => @"\??\X:\LoopB",
                _ => null,
            });
        Assert.False(string.IsNullOrWhiteSpace(cyclic));
    }

    [Fact]
    public void PathOriginClassifier_AreSameVolume_DistinguishesNtfsDirectoryMountPointsAndSubstAliases()
    {
        Func<string, string?> mountProbe = fullPath =>
            fullPath.StartsWith(@"C:\Mount\UsbCard\", StringComparison.OrdinalIgnoreCase)
                ? @"C:\Mount\UsbCard\"
                : @"C:\";
        Func<string, string?> guidProbe = mount =>
            string.Equals(mount, @"C:\Mount\UsbCard\", StringComparison.OrdinalIgnoreCase)
                ? @"\\?\Volume{22222222-2222-2222-2222-222222222222}\"
                : @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

        // Same drive letter C:\, but C:\Mount\UsbCard is a mounted separate volume -> cross-volume!
        Assert.False(PathOriginClassifier.AreSameVolume(
            @"C:\Users\simon\Desktop\photo.jpg",
            @"C:\Mount\UsbCard\Inbox",
            dosDeviceProbe: _ => null,
            volumeMountPointProbe: mountProbe,
            volumeGuidProbe: guidProbe));

        // Different drive letter Z:\ subst-aliased to C:\Work -> same volume as C:\Users\simon\Desktop!
        Assert.True(PathOriginClassifier.AreSameVolume(
            @"Z:\draft.md",
            @"C:\Users\simon\Desktop",
            dosDeviceProbe: drive => drive == 'Z' ? @"\??\C:\Work" : null,
            volumeMountPointProbe: mountProbe,
            volumeGuidProbe: guidProbe));

        // Mixed batch where one item lives on the mounted USB card -> AreAllOnSameVolume returns false.
        Assert.False(PathOriginClassifier.AreAllOnSameVolume(
            [@"C:\Users\simon\Desktop\a.txt", @"C:\Mount\UsbCard\b.txt"],
            @"C:\Users\simon\DeskBox\Managed",
            dosDeviceProbe: _ => null,
            volumeMountPointProbe: mountProbe,
            volumeGuidProbe: guidProbe));

        // UNC share root comparison stays purely lexical and matches case-insensitively.
        Assert.True(PathOriginClassifier.AreSameVolume(
            @"\\Nas01\Media\Raw\clip.mp4",
            @"\\nas01\media\Edited"));
        Assert.False(PathOriginClassifier.AreSameVolume(
            @"\\Nas01\Media\Raw\clip.mp4",
            @"\\Nas01\Archive\Edited"));
    }

    [Fact]
    public void PathOriginClassifier_BoundedHoverTimeout_FailsSafeWithoutBlockingUiCaller()
    {
        var sw = Stopwatch.StartNew();
        bool sameVolume = PathOriginClassifier.AreSameVolume(
            @"S:\SlowShare\video.mp4",
            @"C:\DeskBox\Files",
            probeTimeout: TimeSpan.FromMilliseconds(50),
            dosDeviceProbe: _ => null,
            volumeMountPointProbe: _ =>
            {
                Thread.Sleep(300);
                return @"S:\";
            },
            volumeGuidProbe: _ => @"\\?\Volume{33333333-3333-3333-3333-333333333333}\");
        sw.Stop();

        Assert.False(sameVolume);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Expected bounded wait, took {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        SourceOriginSnapshot timedOutOrigin = PathOriginClassifier.ClassifySources(
            [@"S:\SlowShare\video.mp4"],
            probeTimeout: TimeSpan.FromMilliseconds(50),
            driveTypeProbe: _ =>
            {
                Thread.Sleep(300);
                return DriveType.Fixed;
            },
            busProbe: _ => (StorageBusKind.Nvme, false));
        sw.Stop();

        Assert.True(timedOutOrigin.IsProtectedCopyByDefault);
        Assert.Equal(InboundSourceProtectionKind.ExternalRemovableOrNetwork, timedOutOrigin.InboundProtection);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Expected bounded classify wait, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void PathOriginClassifier_ClassifySources_DetectsChatCachesCloudPlaceholdersAndPublicDesktop()
    {
        SourceOriginSnapshot wechat = PathOriginClassifier.ClassifySources(
            [@"C:\Users\simon\Documents\WeChat Files\wxid_123\FileStorage\File\2026-11\contract.pdf"],
            driveTypeProbe: _ => DriveType.Fixed,
            busProbe: _ => (StorageBusKind.Nvme, false),
            cloudPlaceholderProbe: _ => false);
        Assert.True(wechat.HasApplicationCacheSource);
        Assert.True(wechat.IsProtectedCopyByDefault);
        Assert.False(wechat.IsMoveProtectedSystemOrSharedSource);

        SourceOriginSnapshot cloudPlaceholder = PathOriginClassifier.ClassifySources(
            [@"C:\Users\simon\OneDrive\Specs\roadmap.pptx"],
            driveTypeProbe: _ => DriveType.Fixed,
            busProbe: _ => (StorageBusKind.Nvme, false),
            cloudPlaceholderProbe: _ => true);
        Assert.True(cloudPlaceholder.HasUnhydratedCloudPlaceholder);
        Assert.True(cloudPlaceholder.IsProtectedCopyByDefault);
        Assert.False(cloudPlaceholder.IsMoveProtectedSystemOrSharedSource);

        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            SourceOriginSnapshot sharedDesktop = PathOriginClassifier.ClassifySources(
                [Path.Combine(publicDesktop, "Edge.lnk")],
                driveTypeProbe: _ => DriveType.Fixed,
                busProbe: _ => (StorageBusKind.Nvme, false),
                cloudPlaceholderProbe: _ => false);
            Assert.True(sharedDesktop.IsMoveProtectedSystemOrSharedSource);
            Assert.True(sharedDesktop.IsProtectedCopyByDefault);
        }
    }

    [Fact]
    public void TransferIntentEngine_TargetKindDecoupling_AllowsRecycleBinAndAppLaunchForProtectedSources()
    {
        var publicDesktopOrigin = new SourceOriginSnapshot(
            InboundSourceProtectionKind.SystemOrSharedProtected,
            HasMoveProtectedRoot: true,
            HasUnhydratedCloudPlaceholder: false,
            HasApplicationCacheSource: false,
            HasTransientTempOrMsixCacheSource: false);

        // 1. Over DirectorySurface: Public Desktop forces Copy and forbids Shift+Move.
        TransferIntentDecision dirDefault = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.DirectorySurface,
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
            DefaultMove: true,
            FollowWindows: false,
            SameVolume: true,
            SourceOrigin: publicDesktopOrigin));
        Assert.Equal(FileDropIntent.Copy, dirDefault.Intent);
        Assert.Equal(NativeDropEffectPolicy.Copy, dirDefault.FeedbackEffect);

        TransferIntentDecision dirShift = TransferIntentEngine.Evaluate(
            dirDefault is var _ ? new TransferIntentRequest(
                Domain: TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: true,
                ControlDown: false,
                ShiftDown: true,
                AltDown: false,
                AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
                NoOperationMetadata: false,
                DefaultMove: true,
                FollowWindows: false,
                SameVolume: true,
                SourceOrigin: publicDesktopOrigin) : default);
        Assert.Equal(FileDropIntent.None, dirShift.Intent);
        Assert.Equal(NativeDropEffectPolicy.None, dirShift.FeedbackEffect);

        // 2. Over RecycleBinTile: Public Desktop / USB still resolves Move (deletion via Shell IFileOperation).
        TransferIntentDecision binDecision = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.RecycleBinTile,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: false,
            ShiftDown: false,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move,
            NoOperationMetadata: false,
            DefaultMove: false,
            FollowWindows: true,
            SameVolume: false,
            SourceOrigin: publicDesktopOrigin));
        Assert.Equal(FileDropIntent.Move, binDecision.Intent);
        Assert.Equal(NativeDropEffectPolicy.Move, binDecision.FeedbackEffect);

        // 3. Over ApplicationShortcutTile: resolves Shortcut/Link launch regardless of source protection.
        TransferIntentDecision launchDecision = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.ApplicationShortcutTile,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: false,
            ShiftDown: true,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link,
            NoOperationMetadata: false,
            DefaultMove: true,
            FollowWindows: false,
            SameVolume: true,
            SourceOrigin: publicDesktopOrigin));
        Assert.Equal(FileDropIntent.Shortcut, launchDecision.Intent);
        Assert.Equal(NativeDropEffectPolicy.Link, launchDecision.FeedbackEffect);
    }

    [Fact]
    public void TransferIntentEngine_ProtectedCacheOrVirtualDrive_DefaultsToCopyButHonorsExplicitShiftMove()
    {
        var wechatOrVirtualDriveOrigin = new SourceOriginSnapshot(
            InboundSourceProtectionKind.ExternalRemovableOrNetwork,
            HasMoveProtectedRoot: false,
            HasUnhydratedCloudPlaceholder: false,
            HasApplicationCacheSource: true,
            HasTransientTempOrMsixCacheSource: false);

        // Unmodified same-volume FollowWindows -> automatically protected as Copy!
        TransferIntentDecision unmodified = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.ExternalInbound,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: false,
            ShiftDown: false,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move,
            NoOperationMetadata: false,
            DefaultMove: false,
            FollowWindows: true,
            SameVolume: true,
            SourceOrigin: wechatOrVirtualDriveOrigin));
        Assert.Equal(FileDropIntent.Copy, unmodified.Intent);
        Assert.Equal(NativeDropEffectPolicy.Copy, unmodified.FeedbackEffect);

        // Explicit Shift -> honors user's explicit Move request!
        TransferIntentDecision shiftMove = TransferIntentEngine.Evaluate(
            unmodified is var _ ? new TransferIntentRequest(
                Domain: TransferDomain.ExternalInbound,
                TargetKind: TransferTargetKind.DirectorySurface,
                HasFileData: true,
                HasVirtualFileData: false,
                HasShellApplicationOrNamespaceData: false,
                ForceCopy: false,
                HasMappedFolder: true,
                ControlDown: false,
                ShiftDown: true,
                AltDown: false,
                AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move,
                NoOperationMetadata: false,
                DefaultMove: false,
                FollowWindows: true,
                SameVolume: true,
                SourceOrigin: wechatOrVirtualDriveOrigin) : default);
        Assert.Equal(FileDropIntent.Move, shiftMove.Intent);
        Assert.Equal(NativeDropEffectPolicy.Move, shiftMove.FeedbackEffect);
    }

    [Fact]
    public void TransferIntentEngine_InternalDomain_DecouplesCrossBoxMoveFromOutboundCopyWireBit()
    {
        // Simulates Win10 StartDragAsync / ManagedDragOutAction=Copy where wire AllowedEffects only has Copy (1),
        // while Domain B (InternalDeskBox) moves between same-volume boxes.
        TransferIntentDecision decision = TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.InternalDeskBox,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: false,
            ShiftDown: false,
            AltDown: false,
            AllowedEffects: NativeDropEffectPolicy.Copy,
            NoOperationMetadata: false,
            DefaultMove: false,
            FollowWindows: true,
            SameVolume: true,
            SourceOrigin: SourceOriginSnapshot.Empty));

        Assert.Equal(FileDropIntent.Move, decision.Intent);
        // Wire effect falls back to Copy so OLE DoDragDrop accepts the drop instead of returning None!
        Assert.Equal(NativeDropEffectPolicy.Copy, decision.FeedbackEffect);
    }

    [Fact]
    public void TransferIntentEngine_ResolveProspectiveManagedDestination_MatchesAutoProvisionedStorageRoot()
    {
        string managedRoot = Path.Combine(_tempRoot, "ManagedBoxes");
        string prospective = TransferIntentEngine.ResolveProspectiveManagedDestination(
            currentFolderPath: null,
            mappedFolderPath: null,
            defaultManagedStorageRoot: managedRoot);
        Assert.Equal(Path.GetFullPath(managedRoot), prospective);

        string fallbackDefault = TransferIntentEngine.ResolveProspectiveManagedDestination(
            currentFolderPath: null,
            mappedFolderPath: null,
            defaultManagedStorageRoot: string.Empty);
        Assert.Equal(
            SettingsService.NormalizeManagedStorageRootPath(string.Empty),
            fallbackDefault);
    }

    [Fact]
    public void MixedDropOrigins_PreservesPerItemOriginAcrossStagingAndShellIdListMaterialization()
    {
        string realDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "RealFolder")).FullName;
        string realFile = Path.Combine(realDir, "keep-real.txt");
        File.WriteAllText(realFile, "real-data");

        string fake7zDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"7zO{Guid.NewGuid():N}"[..11])).FullName;
        string archiveTempFile = Path.Combine(fake7zDir, "from-archive.txt");
        File.WriteAllText(archiveTempFile, "archive-data");

        try
        {
            var staged = DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                [realFile, archiveTempFile],
                hasShellIdList: true,
                out var origins,
                out bool stagedAny);

            Assert.True(stagedAny);
            Assert.Equal(2, staged.Count);
            Assert.Equal(2, origins.Count);
            Assert.Equal(realFile, staged[0]);
            Assert.Equal(DroppedItemOriginKind.RealFileSystemPath, origins[0]);
            Assert.NotEqual(archiveTempFile, staged[1]);
            Assert.Equal(DroppedItemOriginKind.TransientArchiveOrCache, origins[1]);

            NativeDropMaterializationWorker.CleanupVirtualDropDirectories(staged);
            Assert.True(File.Exists(realFile));
        }
        finally
        {
            try { Directory.Delete(fake7zDir, recursive: true); } catch { }
        }

        // Mixed ShellIdList items: real file + virtual namespace item (.lnk in VirtualDrops).
        var shellItems = new[]
        {
            new NativeDropTarget.ShellIdListDropItem(
                NativeDropTarget.ShellIdListItemKind.FileSystemPath,
                AppUserModelId: string.Empty,
                ParsingName: realFile,
                DisplayName: "keep-real.txt",
                FileSystemPath: realFile),
            new NativeDropTarget.ShellIdListDropItem(
                NativeDropTarget.ShellIdListItemKind.VirtualNamespace,
                AppUserModelId: string.Empty,
                ParsingName: "shell:RecycleBinFolder",
                DisplayName: "Recycle Bin",
                FileSystemPath: string.Empty),
        };

        var materialized = NativeDropTarget.MaterializeShellIdListItemsWithOrigins(
            shellItems,
            out var shellOrigins);
        try
        {
            Assert.Equal(2, materialized.Count);
            Assert.Equal(2, shellOrigins.Count);
            Assert.Equal(realFile, materialized[0]);
            Assert.Equal(DroppedItemOriginKind.RealFileSystemPath, shellOrigins[0]);
            Assert.EndsWith(".lnk", materialized[1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DroppedItemOriginKind.ShellNamespaceLinkOnly, shellOrigins[1]);

            var classified = NativeDropTarget.ClassifyShellIdListExtractedOrigins(materialized);
            Assert.Equal(DroppedItemOriginKind.RealFileSystemPath, classified[0]);
            Assert.Equal(DroppedItemOriginKind.ShellNamespaceLinkOnly, classified[1]);
        }
        finally
        {
            NativeDropMaterializationWorker.CleanupVirtualDropDirectories(materialized);
            Assert.True(File.Exists(realFile));
        }
    }

    [Fact]
    public void WritePerformedAndLogicalPerformedDropEffect_RecordIndependentDwordsOnDataObject()
    {
        var dataObject = new RecordingComDataObject();
        IntPtr pDataObj = Marshal.GetComInterfaceForObject<RecordingComDataObject, IDataObject>(dataObject);
        try
        {
            NativeDropTarget.WritePerformedDropEffect(pDataObj, NativeDropEffectPolicy.None);
            NativeDropTarget.WriteLogicalPerformedDropEffect(pDataObj, NativeDropEffectPolicy.Move);

            Assert.Equal(NativeDropEffectPolicy.None, dataObject.GetRecordedDword("Performed DropEffect"));
            Assert.Equal(NativeDropEffectPolicy.Move, dataObject.GetRecordedDword("Logical Performed DropEffect"));
        }
        finally
        {
            Marshal.Release(pDataObj);
        }
    }

    private sealed class RecordingComDataObject : IDataObject
    {
        private readonly Dictionary<ushort, uint> _recordedEffects = new();

        public uint? GetRecordedDword(string formatName)
        {
            ushort cf = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW(formatName);
            return _recordedEffects.TryGetValue(cf, out uint value) ? value : null;
        }

        public void SetData(ref FORMATETC pFormatetcIn, ref STGMEDIUM pmedium, bool fRelease)
        {
            try
            {
                if (pmedium.tymed == TYMED.TYMED_HGLOBAL && pmedium.unionmember != IntPtr.Zero)
                {
                    IntPtr ptr = OleDropTargetNativeMethods.GlobalLock(pmedium.unionmember);
                    if (ptr != IntPtr.Zero)
                    {
                        try
                        {
                            byte[] managed = new byte[4];
                            Marshal.Copy(ptr, managed, 0, 4);
                            _recordedEffects[(ushort)pFormatetcIn.cfFormat] =
                                BinaryPrimitives.ReadUInt32LittleEndian(managed);
                        }
                        finally
                        {
                            OleDropTargetNativeMethods.GlobalUnlock(pmedium.unionmember);
                        }
                    }
                }
            }
            finally
            {
                if (fRelease && pmedium.tymed == TYMED.TYMED_HGLOBAL && pmedium.unionmember != IntPtr.Zero)
                {
                    Win32Helper.FreeGlobalMemory(pmedium.unionmember);
                    pmedium.unionmember = IntPtr.Zero;
                }
            }
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => throw new NotSupportedException();
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int QueryGetData(ref FORMATETC format) => 1;
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            return 1;
        }
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return unchecked((int)0x80004001);
        }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
        {
            enumAdvise = null;
            return unchecked((int)0x80004001);
        }
    }
}
