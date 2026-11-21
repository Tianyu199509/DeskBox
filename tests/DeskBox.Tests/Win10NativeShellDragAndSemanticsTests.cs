using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Platform.DragDrop;
using DeskBox.Services;
using DeskBox.Services.DragDrop;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class Win10NativeShellDragAndSemanticsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxTests",
        nameof(Win10NativeShellDragAndSemanticsTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void TargetAwareDropEffectPolicy_AllowsMoveOnlyForVerifiedExplorerOrDeskBoxWindows()
    {
        const uint shellPid = 1000;
        const uint separateExplorerPid = 2000;
        const uint weChatPid = 3000;
        const uint spoofedPid = 4000;

        bool IsExplorerPid(uint pid) => pid is shellPid or separateExplorerPid;

        // Desktop (Progman / WorkerW) owned by shell PID — including wallpaper engine child HWND
        Assert.True(TargetAwareDropEffectPolicy.IsTrustedMoveTargetWindow(
            hitHwnd: new IntPtr(0x9999),
            rootHwnd: new IntPtr(0x1000),
            rootPid: shellPid,
            shellWindowPid: shellPid,
            isCurrentProcess: false,
            isSettingsHostProcess: false,
            rootClass: "WorkerW",
            hitClass: "WallpaperChildRenderClass",
            isExplorerProcessProbe: IsExplorerPid));

        // Separate-process Explorer folder window ("Launch folder windows in a separate process"):
        // classified as ExplorerFolderWindow so Preferred DropEffect stays unset (0) and
        // Windows applies native same-volume Move / cross-volume & USB Copy.
        Assert.Equal(
            OutboundMoveTargetTrust.ExplorerFolderWindow,
            TargetAwareDropEffectPolicy.ClassifyMoveTargetWindow(
                hitHwnd: new IntPtr(0x2001),
                rootHwnd: new IntPtr(0x2000),
                rootPid: separateExplorerPid,
                shellWindowPid: shellPid,
                isCurrentProcess: false,
                isSettingsHostProcess: false,
                rootClass: "CabinetWClass",
                hitClass: "DirectUIHWND",
                isExplorerProcessProbe: IsExplorerPid));
        Assert.Equal(
            TargetAwareDropEffectPolicy.DropEffectNone,
            TargetAwareDropEffectPolicy.ResolvePreferredDropEffect(
                TargetAwareDropEffectPolicy.DropEffectMove,
                () => OutboundMoveTargetTrust.ExplorerFolderWindow));
        Assert.Equal(
            TargetAwareDropEffectPolicy.DropEffectMove,
            TargetAwareDropEffectPolicy.ResolvePreferredDropEffect(
                TargetAwareDropEffectPolicy.DropEffectMove,
                () => OutboundMoveTargetTrust.DesktopOrDeskBox));
        Assert.Equal(
            TargetAwareDropEffectPolicy.DropEffectCopy,
            TargetAwareDropEffectPolicy.ResolvePreferredDropEffect(
                TargetAwareDropEffectPolicy.DropEffectMove,
                () => OutboundMoveTargetTrust.Untrusted));

        // DeskBox own process & settings host
        Assert.True(TargetAwareDropEffectPolicy.IsTrustedMoveTargetWindow(
            hitHwnd: new IntPtr(0x3001),
            rootHwnd: new IntPtr(0x3000),
            rootPid: 5000,
            shellWindowPid: shellPid,
            isCurrentProcess: true,
            isSettingsHostProcess: false,
            rootClass: "WinUIDesktopWin32WindowClass",
            hitClass: "Microsoft.UI.Content.DesktopChildSiteBridge",
            isExplorerProcessProbe: IsExplorerPid));

        // Third-party apps (WeChat / Chrome / VS Code) -> Fail-Closed Copy
        Assert.False(TargetAwareDropEffectPolicy.IsTrustedMoveTargetWindow(
            hitHwnd: new IntPtr(0x4001),
            rootHwnd: new IntPtr(0x4000),
            rootPid: weChatPid,
            shellWindowPid: shellPid,
            isCurrentProcess: false,
            isSettingsHostProcess: false,
            rootClass: "WeChatMainWndForPC",
            hitClass: "WeChatMainWndForPC",
            isExplorerProcessProbe: IsExplorerPid));

        // Spoofed CabinetWClass from non-explorer process -> Fail-Closed Copy
        Assert.False(TargetAwareDropEffectPolicy.IsTrustedMoveTargetWindow(
            hitHwnd: new IntPtr(0x5001),
            rootHwnd: new IntPtr(0x5000),
            rootPid: spoofedPid,
            shellWindowPid: shellPid,
            isCurrentProcess: false,
            isSettingsHostProcess: false,
            rootClass: "CabinetWClass",
            hitClass: "DirectUIHWND",
            isExplorerProcessProbe: IsExplorerPid));

        // Explorer taskbar / tray -> Fail-Closed Copy
        Assert.False(TargetAwareDropEffectPolicy.IsTrustedMoveTargetWindow(
            hitHwnd: new IntPtr(0x6001),
            rootHwnd: new IntPtr(0x6000),
            rootPid: shellPid,
            shellWindowPid: shellPid,
            isCurrentProcess: false,
            isSettingsHostProcess: false,
            rootClass: "Shell_TrayWnd",
            hitClass: "MSTaskListWClass",
            isExplorerProcessProbe: IsExplorerPid));

        // Null HWNDs -> Fail-Closed Copy
        Assert.False(TargetAwareDropEffectPolicy.IsTrustedMoveTargetWindow(
            hitHwnd: IntPtr.Zero,
            rootHwnd: IntPtr.Zero,
            rootPid: shellPid,
            shellWindowPid: shellPid,
            isCurrentProcess: false,
            isSettingsHostProcess: false,
            rootClass: string.Empty,
            hitClass: string.Empty,
            isExplorerProcessProbe: IsExplorerPid));
    }

    [Fact]
    public void FileDragSourceGuardDataObject_TargetAwareResolver_DowngradesMoveToCopyForThirdPartyTargets()
    {
        Directory.CreateDirectory(_tempRoot);
        string filePath = Path.Combine(_tempRoot, "payload.txt");
        File.WriteAllText(filePath, "target-aware-guard");

        OutboundMoveTargetTrust targetTrust = OutboundMoveTargetTrust.DesktopOrDeskBox;
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([filePath]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(
            inner,
            configured => TargetAwareDropEffectPolicy.ResolvePreferredDropEffect(
                configured,
                () => targetTrust));
        try
        {
            var receiver = new NativeOleDataObject(guard);
            ushort preferredFormatId = Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect");
            var format = new NativeFormatEtc
            {
                ClipboardFormat = preferredFormatId,
                Aspect = 1,
                Index = -1,
                MediumType = 1,
            };

            Assert.True(Win32Helper.TryCreateGlobalMemory(BitConverter.GetBytes(2u), out nint mem));
            var medium = new NativeStorageMedium { MediumType = 1, Content = mem };
            Assert.Equal(0, receiver.SetData(ref format, ref medium, release: true));

            // Over Desktop / DeskBox -> Move (2)
            targetTrust = OutboundMoveTargetTrust.DesktopOrDeskBox;
            Assert.Equal(0, receiver.QueryGetData(ref format));
            Assert.Equal(0, receiver.GetData(ref format, out NativeStorageMedium desktopMedium));
            try
            {
                Assert.Equal(2u, ReadDword(desktopMedium.Content));
            }
            finally
            {
                Win32Helper.ReleaseStorageMedium(ref desktopMedium);
            }

            // Over Explorer folder window (e.g., USB drive or local folder) -> DV_E_FORMATETC (unset)
            // so Windows Explorer applies native same-volume Move / cross-volume & USB Copy!
            targetTrust = OutboundMoveTargetTrust.ExplorerFolderWindow;
            Assert.Equal(unchecked((int)0x80040064), receiver.QueryGetData(ref format));
            Assert.Equal(unchecked((int)0x80040064), receiver.GetData(ref format, out _));

            // Over WeChat / third-party -> Fail-Closed Copy (1)
            targetTrust = OutboundMoveTargetTrust.Untrusted;
            Assert.Equal(0, receiver.QueryGetData(ref format));
            Assert.Equal(0, receiver.GetData(ref format, out NativeStorageMedium thirdPartyMedium));
            try
            {
                Assert.Equal(1u, ReadDword(thirdPartyMedium.Content));
            }
            finally
            {
                Win32Helper.ReleaseStorageMedium(ref thirdPartyMedium);
            }
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
        }
    }

    [Fact]
    public void Win10ShellDropSource_HonorsEscapeDropAnd300msInputStarvationWatchdog()
    {
        long nowTick = 10_000;
        bool physicalButtonDown = true;
        var source = new Win10ShellDropSource(
            isSecondaryButtonDrag: false,
            watchdogMs: 300,
            tickProvider: () => nowTick,
            isPhysicalButtonDownProvider: () => physicalButtonDown);

        Assert.Equal(
            Win10ShellDropSource.DRAGDROP_S_USEDEFAULTCURSORS,
            source.GiveFeedback(1));

        Assert.Equal(
            Win10ShellDropSource.S_OK,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: Win10ShellDropSource.MkLButton));

        // Escape cancels immediately
        Assert.Equal(
            Win10ShellDropSource.DRAGDROP_S_CANCEL,
            source.QueryContinueDrag(fEscapePressed: 1, grfKeyState: Win10ShellDropSource.MkLButton));

        // Normal mouse release drops immediately
        Assert.Equal(
            Win10ShellDropSource.DRAGDROP_S_DROP,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: 0));

        // Capture starvation: OLE still reports MK_LBUTTON held while hardware button is UP
        physicalButtonDown = false;
        Assert.Equal(
            Win10ShellDropSource.S_OK,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: Win10ShellDropSource.MkLButton));

        // Brief glitch (< 300ms) followed by button down resets watchdog
        nowTick += 150;
        Assert.Equal(
            Win10ShellDropSource.S_OK,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: Win10ShellDropSource.MkLButton));
        physicalButtonDown = true;
        Assert.Equal(
            Win10ShellDropSource.S_OK,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: Win10ShellDropSource.MkLButton));

        // Sustained button-up for >= 300ms triggers clean cancel and sets WatchdogAborted
        physicalButtonDown = false;
        nowTick += 100;
        Assert.Equal(
            Win10ShellDropSource.S_OK,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: Win10ShellDropSource.MkLButton));
        nowTick += 300;
        Assert.Equal(
            Win10ShellDropSource.DRAGDROP_S_CANCEL,
            source.QueryContinueDrag(fEscapePressed: 0, grfKeyState: Win10ShellDropSource.MkLButton));
        Assert.True(source.WatchdogAborted);
        Assert.Equal(8, source.QueryContinueCalls);
        Assert.Equal(1, source.GiveFeedbackCalls);
        Assert.Equal(Win10ShellDropSource.MkLButton, source.LastKeyState);
    }

    [Fact]
    public void Win10DragImageHelper_StructLayoutMatchesWin64Sdk_AndAttachesToGuardedDataObject()
    {
        if (IntPtr.Size == 8)
        {
            Assert.Equal(32, Marshal.SizeOf<Win10DragImageHelper.NativeShDragImage>());
        }

        Directory.CreateDirectory(_tempRoot);
        string filePath = Path.Combine(_tempRoot, "preview.txt");
        File.WriteAllText(filePath, "drag-image");

        bool attached = false;
        Exception? threadError = null;
        var staThread = new Thread(() =>
        {
            try
            {
                nint inner = NativeShellFileDragProvider.CreateShellDataObject([filePath]);
                nint guard = FileDragSourceGuardDataObject.CreateTargetAwareInterfacePointer(inner);
                try
                {
                    attached = Win10DragImageHelper.TryAttachDragImage(
                        guard,
                        [filePath],
                        IntPtr.Zero);
                }
                finally
                {
                    FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
                    NativeShellFileDragProvider.ReleaseInterface(inner);
                }
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(threadError);
        Assert.True(attached);
    }

    [Fact]
    public void OutboundDragRoutePolicy_DefaultsWin10ToWinRtXaml_OptInDiagnosticShell_AndProtectsSpecialMedia()
    {
        // WinUI 3 Mouse-in-Pointer starves in-process SHDoDragDrop of WM_MOUSEMOVE,
        // so Win10 non-elevated drags default to WinRtXaml (StartDragAsync) and only
        // route to Win10NativeShell when explicitly forced for diagnostics.
        Assert.False(OutboundDragRoutePolicy.ShouldUseWin10NativeShellForProcess(
            isElevatedOverride: false,
            isWindows11OrLaterOverride: false,
            forceOverride: false,
            disableOverride: false));
        Assert.True(OutboundDragRoutePolicy.ShouldUseWin10NativeShellForProcess(
            isElevatedOverride: false,
            isWindows11OrLaterOverride: false,
            forceOverride: true,
            disableOverride: false));
        Assert.False(OutboundDragRoutePolicy.ShouldUseWin10NativeShellForProcess(
            isElevatedOverride: true,
            isWindows11OrLaterOverride: false,
            forceOverride: true,
            disableOverride: false));

        Assert.Equal(
            OutboundDragDriverKind.WinRtXaml,
            OutboundDragRoutePolicy.ResolveDriverKind(
                isElevated: false,
                isWindows11OrLater: false,
                PointerDeviceType.Mouse));
        Assert.Equal(
            OutboundDragDriverKind.WinRtXaml,
            OutboundDragRoutePolicy.ResolveDriverKind(
                isElevated: false,
                isWindows11OrLater: false,
                PointerDeviceType.Touchpad));
        Assert.Equal(
            OutboundDragDriverKind.Win10NativeShell,
            OutboundDragRoutePolicy.ResolveDriverKind(
                isElevated: false,
                isWindows11OrLater: false,
                PointerDeviceType.Mouse,
                forceWin10ShellDrag: true));
        Assert.Equal(
            OutboundDragDriverKind.WinRtXaml,
            OutboundDragRoutePolicy.ResolveDriverKind(
                isElevated: false,
                isWindows11OrLater: false,
                PointerDeviceType.Touch,
                forceWin10ShellDrag: true));
        Assert.Equal(
            OutboundDragDriverKind.WinRtXaml,
            OutboundDragRoutePolicy.ResolveDriverKind(
                isElevated: false,
                isWindows11OrLater: true,
                PointerDeviceType.Mouse));
        Assert.Equal(
            OutboundDragDriverKind.ElevatedPointer,
            OutboundDragRoutePolicy.ResolveDriverKind(
                isElevated: true,
                isWindows11OrLater: false,
                PointerDeviceType.Mouse,
                forceWin10ShellDrag: true));

        Assert.Equal(
            new NativeOleDragEffects(OutboundDragRoutePolicy.DropEffectAll, 0),
            OutboundDragRoutePolicy.ResolveNativeOleDragEffects(
                SettingsService.ManagedDragOutActionFollowWindows,
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: false,
                hasAppExecutionAlias: false));
        Assert.Equal(
            new NativeOleDragEffects(OutboundDragRoutePolicy.DropEffectAll, OutboundDragRoutePolicy.DropEffectCopy),
            OutboundDragRoutePolicy.ResolveNativeOleDragEffects(
                SettingsService.ManagedDragOutActionMove,
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: true,
                hasAppExecutionAlias: false));
        Assert.Equal(
            new NativeOleDragEffects(OutboundDragRoutePolicy.DropEffectCopyLink, OutboundDragRoutePolicy.DropEffectCopy),
            OutboundDragRoutePolicy.ResolveNativeOleDragEffects(
                SettingsService.ManagedDragOutActionMove,
                isProtectedCopyOnly: true,
                isDefaultCopySpecialMedia: false,
                hasAppExecutionAlias: false));
        Assert.Equal(
            new NativeOleDragEffects(OutboundDragRoutePolicy.DropEffectLink, OutboundDragRoutePolicy.DropEffectLink),
            OutboundDragRoutePolicy.ResolveNativeOleDragEffects(
                SettingsService.ManagedDragOutActionMove,
                isProtectedCopyOnly: false,
                isDefaultCopySpecialMedia: false,
                hasAppExecutionAlias: true));

        // D3: Special media (UNC / WeChat cache) defaults to Copy on drag-out even under Move/FollowWindows
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionMove,
                sourcePaths: [@"\\nas\share\photo.jpg"],
                isProtectedCopyOnly: false));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionFollowWindows,
                sourcePaths: [@"D:\Users\Demo\Documents\WeChat Files\wxid_1\FileStorage\File\2026-11\doc.pdf"],
                isProtectedCopyOnly: false));
    }

    [Fact]
    public void Win10MoveCopyFallbackPolicy_AndCtrlStartOverride_PromoteSingleDragToCopyWithoutMutatingGlobalMove()
    {
        // Plain Win10 Move stays single-bit Move
        Assert.Equal(
            DataPackageOperation.Move,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                isProtectedCopyOnly: false));
        Assert.Equal(
            DataPackageOperation.Move,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionMove,
                isProtectedCopyOnly: false));

        // Holding Ctrl at drag start promotes only this Win10 drag to single-bit Copy
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                isProtectedCopyOnly: false,
                controlPressed: true));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionMove,
                isProtectedCopyOnly: false,
                controlPressed: true));

        // One-shot fallback after non-Explorer rejection promotes only this Win10 drag to Copy
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: false,
                isProtectedCopyOnly: false,
                oneShotCopyFallback: true));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionMove,
                isProtectedCopyOnly: false,
                oneShotCopyFallback: true));

        // Win11 Move continues to advertise Copy | Move regardless of fallback flags
        Assert.Equal(
            FileItemDragPackage.SupportedOperations,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: true,
                isProtectedCopyOnly: false,
                oneShotCopyFallback: true));

        long tick = 50_000;
        var policy = new Win10MoveCopyFallbackPolicy(
            ttlMs: Win10MoveCopyFallbackPolicy.DefaultTtlMs,
            tickProvider: () => tick);

        string fileA = Path.Combine(_tempRoot, "Report.pdf");
        string fileB = Path.Combine(_tempRoot, "Slide.png");
        string otherFile = Path.Combine(_tempRoot, "Other.txt");

        policy.ArmForRejectedPaths([fileA, fileB]);

        // Different file batch does not match or consume the armed token
        Assert.False(policy.TryConsumeForPaths([otherFile]));
        Assert.False(policy.TryConsumeForPaths([fileA]));

        // Exact same batch (different order & casing) matches and consumes strictly once
        tick += 3_000;
        Assert.True(policy.TryConsumeForPaths([fileB.ToUpperInvariant(), fileA.ToLowerInvariant()]));
        Assert.False(policy.TryConsumeForPaths([fileA, fileB]));

        // Expired token (> 15s) does not match
        policy.ArmForRejectedPaths([fileA]);
        tick += Win10MoveCopyFallbackPolicy.DefaultTtlMs + 1;
        Assert.False(policy.TryConsumeForPaths([fileA]));

        // Clear immediately disarms an active token
        policy.ArmForRejectedPaths([fileA]);
        policy.Clear();
        Assert.False(policy.TryConsumeForPaths([fileA]));
    }

    [Fact]
    public void DesktopSuppression_HandlesCopyReplaceNtfsTunnelingBulkDragsAndShortcutCandidates()
    {
        Directory.CreateDirectory(_tempRoot);
        string srcDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "src")).FullName;
        string dstDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "desktop")).FullName;

        // 1. Copy + Replace existing desktop file: destination matches source fingerprint after overwrite
        string srcCopyReplace = Path.Combine(srcDir, "report.txt");
        string dstCopyReplace = Path.Combine(dstDir, "report.txt");
        File.WriteAllText(srcCopyReplace, "new-version-payload-12345");
        DateTime srcWriteUtc = DateTime.UtcNow.AddDays(-5);
        File.SetLastWriteTimeUtc(srcCopyReplace, srcWriteUtc);
        File.WriteAllText(dstCopyReplace, "old-version");
        File.SetCreationTimeUtc(dstCopyReplace, DateTime.UtcNow.AddDays(-20));

        var registry = new DesktopAutoOrganizationSuppressionRegistry();
        registry.SuppressDraggedArrivals([(srcCopyReplace, dstCopyReplace)]);

        // Simulate Explorer Copy+Replace preserving source Length and LastWriteTimeUtc
        File.Copy(srcCopyReplace, dstCopyReplace, overwrite: true);
        File.SetLastWriteTimeUtc(dstCopyReplace, srcWriteUtc);
        Assert.True(registry.TryConsume(dstCopyReplace));

        // 2. NTFS Tunneling (<15s): newly copied file inherits old CreationTimeUtc but matches source fingerprint
        string srcTunneled = Path.Combine(srcDir, "tunneled.txt");
        string dstTunneled = Path.Combine(dstDir, "tunneled.txt");
        File.WriteAllText(srcTunneled, "tunneled-copy-content");
        DateTime tunneledWriteUtc = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(srcTunneled, tunneledWriteUtc);

        registry.SuppressDraggedArrivals([(srcTunneled, dstTunneled)]);
        File.Copy(srcTunneled, dstTunneled, overwrite: true);
        File.SetLastWriteTimeUtc(dstTunneled, tunneledWriteUtc);
        File.SetCreationTimeUtc(dstTunneled, DateTime.UtcNow.AddDays(-30)); // NTFS tunneled creation time
        Assert.True(registry.TryConsume(dstTunneled));

        // 3. Compact candidate list supports 50+ files without overflowing MaxEntries and includes .lnk
        string[] candidates = DesktopOutboundDropCoordinator
            .GetDesktopArrivalNameCandidates("notes.pdf", isDirectory: false, uiLanguageName: "zh-CN")
            .ToArray();
        Assert.True(candidates.Length <= 10, $"Expected <=10 candidates per file, got {candidates.Length}");
        Assert.Contains("notes.pdf", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notes - 副本.pdf", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notes.pdf.lnk", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notes.lnk", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.True(50 * candidates.Length < DesktopAutoOrganizationSuppressionRegistry.MaxEntries);

        // 4. User desktop path detection (for Explorer folder windows opened to Desktop)
        string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(userDesktop))
        {
            Assert.True(DesktopOutboundDropCoordinator.IsUserDesktopPath(userDesktop));
            Assert.True(DesktopOutboundDropCoordinator.IsUserDesktopPath(userDesktop + Path.DirectorySeparatorChar));
            Assert.False(DesktopOutboundDropCoordinator.IsUserDesktopPath(_tempRoot));
        }
    }

    [Fact]
    public void ActiveDeskBoxDragRegistry_TracksCollapsedStackAndNativeShellMetadata()
    {
        Directory.CreateDirectory(_tempRoot);
        string a = Path.Combine(_tempRoot, "a.txt");
        string b = Path.Combine(_tempRoot, "b.txt");
        string sessionId = Guid.NewGuid().ToString("N");

        try
        {
            ActiveDeskBoxDragRegistry.Begin(
                sessionId,
                "widget-1",
                [a, b],
                fromStackPopover: false,
                sourceStackKey: null,
                stackReorderKey: "stack-docs",
                isNativeShellDrag: true);

            Assert.True(ActiveDeskBoxDragRegistry.IsActiveCollapsedStackDrag([a, b]));
            Assert.True(ActiveDeskBoxDragRegistry.TryMatchDetailed([a, b], out var matched));
            Assert.Equal("widget-1", matched.WidgetId);
            Assert.Equal("stack-docs", matched.StackReorderKey);
            Assert.True(matched.IsNativeShellDrag);
        }
        finally
        {
            ActiveDeskBoxDragRegistry.End(sessionId);
        }

        Assert.False(ActiveDeskBoxDragRegistry.IsActiveCollapsedStackDrag([a, b]));
    }

    [Fact]
    public void ElevatedExplorerCacheReuse_RequiresSameTabAndFreshTimestamp()
    {
        IntPtr root = new(0x100);
        IntPtr tab1 = new(0x201);
        IntPtr tab2 = new(0x202);

        Assert.True(ElevatedShellOutboundDropInterop.ShouldReuseCachedExplorerFolderTarget(
            cachedRootHwnd: root,
            cachedTabHwnd: tab1,
            cachedResolvedTickMs: 10_000,
            cachedKind: ElevatedOutboundTargetKind.ExplorerFolder,
            currentRootHwnd: root,
            currentTabHwnd: tab1,
            nowTickMs: 11_500));

        // Switched tab -> must NOT reuse Tab 1 cache
        Assert.False(ElevatedShellOutboundDropInterop.ShouldReuseCachedExplorerFolderTarget(
            cachedRootHwnd: root,
            cachedTabHwnd: tab1,
            cachedResolvedTickMs: 10_000,
            cachedKind: ElevatedOutboundTargetKind.ExplorerFolder,
            currentRootHwnd: root,
            currentTabHwnd: tab2,
            nowTickMs: 10_500));

        // Non-tab chrome hit (currentTab == 0) -> must NOT reuse Tab 1 cache
        Assert.False(ElevatedShellOutboundDropInterop.ShouldReuseCachedExplorerFolderTarget(
            cachedRootHwnd: root,
            cachedTabHwnd: tab1,
            cachedResolvedTickMs: 10_000,
            cachedKind: ElevatedOutboundTargetKind.ExplorerFolder,
            currentRootHwnd: root,
            currentTabHwnd: IntPtr.Zero,
            nowTickMs: 10_500));

        // Stale cache (> 2000ms) -> must NOT reuse
        Assert.False(ElevatedShellOutboundDropInterop.ShouldReuseCachedExplorerFolderTarget(
            cachedRootHwnd: root,
            cachedTabHwnd: tab1,
            cachedResolvedTickMs: 10_000,
            cachedKind: ElevatedOutboundTargetKind.ExplorerFolder,
            currentRootHwnd: root,
            currentTabHwnd: tab1,
            nowTickMs: 12_500));
    }

    [Fact]
    public void ShortcutLaunchPolicy_MatchesExplorerParity_AndLaunchedVirtualRetentionLasts24HoursUntilStartup()
    {
        // D7: Direct executable tiles launch
        foreach (string exePath in new[]
                 {
                     @"C:\Tools\Notepad.exe",
                     @"C:\Scripts\convert.bat",
                     @"C:\Scripts\deploy.cmd",
                     @"C:\Legacy\tool.com",
                     @"C:\Windows\System32\services.msc",
                 })
        {
            Assert.Equal(
                ShortcutLaunchDecision.Launch,
                ShortcutLaunchPolicy.Evaluate(
                    isShortcut: false,
                    path: exePath,
                    targetPath: null,
                    targetIsDirectory: false));
        }

        // D7: Document shortcuts (.lnk -> .pdf / .docx / .txt) do NOT swallow drops
        foreach (string docTarget in new[]
                 {
                     @"C:\Docs\report.pdf",
                     @"C:\Docs\spec.docx",
                     @"C:\Docs\notes.txt",
                     @"C:\Images\photo.png",
                 })
        {
            Assert.Equal(
                ShortcutLaunchDecision.None,
                ShortcutLaunchPolicy.Evaluate(
                    isShortcut: true,
                    path: @"C:\Grid\Doc.lnk",
                    targetPath: docTarget,
                    targetIsDirectory: false));
        }

        // D7: Launched virtual staging batches stay alive for 24h during runtime and sweep on startup
        string fakeStagingRoot = Path.Combine(_tempRoot, "staging");
        string launchedBatch = Directory.CreateDirectory(Path.Combine(fakeStagingRoot, "VirtualDrops", "batch-launched")).FullName;
        string unlaunchedBatch = Directory.CreateDirectory(Path.Combine(fakeStagingRoot, "VirtualDrops", "batch-plain")).FullName;
        string launchedFile = Path.Combine(launchedBatch, "note.txt");
        string plainFile = Path.Combine(unlaunchedBatch, "temp.txt");
        File.WriteAllText(launchedFile, "editing in notepad");
        File.WriteAllText(plainFile, "abandoned temp");

        DropStagingFileOps.MarkStagingBatchLaunchedRetention([launchedFile], tempRootOverride: fakeStagingRoot);

        DateTimeOffset baseTime = DateTimeOffset.UtcNow;
        // 1 hour later (runtime sweep): unlaunched batch removed, launched batch retained!
        int removedAt1h = DropStagingFileOps.CleanupStaleDropStagingDirectories(
            tempRootOverride: fakeStagingRoot,
            maxAge: TimeSpan.FromMinutes(30),
            nowUtc: baseTime + TimeSpan.FromHours(1),
            isStartupSweep: false);
        Assert.Equal(1, removedAt1h);
        Assert.False(Directory.Exists(unlaunchedBatch));
        Assert.True(File.Exists(launchedFile));

        // 12 hours later (runtime sweep): launched batch STILL retained!
        int removedAt12h = DropStagingFileOps.CleanupStaleDropStagingDirectories(
            tempRootOverride: fakeStagingRoot,
            maxAge: TimeSpan.FromMinutes(30),
            nowUtc: baseTime + TimeSpan.FromHours(12),
            isStartupSweep: false);
        Assert.Equal(0, removedAt12h);
        Assert.True(File.Exists(launchedFile));

        // Next startup sweep cleans launched batch cleanly
        int removedOnStartup = DropStagingFileOps.CleanupStaleDropStagingDirectories(
            tempRootOverride: fakeStagingRoot,
            maxAge: TimeSpan.FromMinutes(30),
            nowUtc: baseTime + TimeSpan.FromHours(12),
            isStartupSweep: true);
        Assert.Equal(1, removedOnStartup);
        Assert.False(Directory.Exists(launchedBatch));
    }

    [Fact]
    public void ActiveDeskBoxDragRegistry_SyncDropTargetClaimSurvivesEnd_AndPopoverTrackerCapturesSnapshot()
    {
        Directory.CreateDirectory(_tempRoot);
        string draggedFile = Path.Combine(_tempRoot, "stack-member.txt");
        File.WriteAllText(draggedFile, "win10-sync-claim");
        string sessionId = Guid.NewGuid().ToString("N");

        ActiveDeskBoxDragRegistry.Begin(
            sessionId,
            widgetId: "widget-a",
            paths: [draggedFile],
            fromStackPopover: true,
            sourceStackKey: "stack-alpha",
            stackReorderKey: null,
            isNativeShellDrag: true);

        try
        {
            var tracker = new StackPopoverDropClaimTracker();
            tracker.OnNativeDragEnter();

            // Simulate synchronous NativeDropTarget::Drop inside SHDoDragDrop modal loop:
            // capture snapshot and mark claimed BEFORE SHDoDragDrop exits and End() runs.
            Assert.True(tracker.TryBeginNativeDropIntent(
                [draggedFile],
                gestureId: 42,
                nowTick64: 1000,
                out PendingStackPopoverNativeDrop pendingDrop));
            Assert.True(pendingDrop.IsDeskBoxOwnedSource);
            Assert.True(pendingDrop.FromStackPopover);
            Assert.NotNull(pendingDrop.CapturedSelfDrag);
            Assert.True(pendingDrop.CapturedSelfDrag!.Value.IsNativeShellDrag);
            Assert.Equal("stack-alpha", pendingDrop.CapturedSelfDrag.Value.SourceStackKey);

            ActiveDeskBoxDragRegistry.MarkClaimedByDeskBoxDropTarget(sessionId, "widget-a");

            // Now SHDoDragDrop returns and CompleteDragItemsSession calls End():
            ActiveDeskBoxDragRegistry.End(sessionId, out string? claimedByWidgetId);
            Assert.Equal("widget-a", claimedByWidgetId);
            Assert.False(ActiveDeskBoxDragRegistry.TryMatchDetailed([draggedFile], out _));

            // Deferred UI turn still holds the captured self-drag metadata cleanly.
            Assert.True(tracker.TryClaimDeferredNativeDrop(pendingDrop, [draggedFile]));
            Assert.True(pendingDrop.CapturedSelfDrag.Value.IsNativeShellDrag);
            Assert.Equal("widget-a", pendingDrop.CapturedSelfDrag.Value.WidgetId);
            tracker.CompletePendingNativeDrop();
        }
        finally
        {
            ActiveDeskBoxDragRegistry.End(sessionId);
        }
    }

    [Fact]
    public void Win10DragImageHelper_FinalizeDragBitmapColorKey_AvoidsMagentaFringeFor32BitAlphaIcons()
    {
        // Modern 32-bit premultiplied alpha icon on transparent black background:
        // must keep 0x00000000 pixels intact and select CLR_NONE (0xFFFFFFFF).
        uint[] alphaIconPixels =
        [
            0x00000000u, 0x00000000u,
            0x80402010u, 0xFF112233u,
        ];
        uint alphaColorKey = Win10DragImageHelper.FinalizeDragBitmapColorKey(alphaIconPixels);
        Assert.Equal(Win10DragImageHelper.ColorKeyNone, alphaColorKey);
        Assert.Equal(0x00000000u, alphaIconPixels[0]);
        Assert.Equal(0x80402010u, alphaIconPixels[2]);

        // Legacy 0-alpha icon: untouched zero pixels are backfilled with ColorKeyMagenta.
        uint[] legacyIconPixels =
        [
            0x00000000u, 0x00112233u,
            0x00000000u, 0x00AABBCCu,
        ];
        uint legacyColorKey = Win10DragImageHelper.FinalizeDragBitmapColorKey(legacyIconPixels);
        Assert.Equal(0x00FF00FFu, legacyColorKey);
        Assert.Equal(0x00FF00FFu, legacyIconPixels[0]);
        Assert.Equal(0x00112233u, legacyIconPixels[1]);
        Assert.Equal(0x00FF00FFu, legacyIconPixels[2]);
    }

    [Fact]
    public void LegacyDropFilesDedup_MatchesStagedTempPathAgainstOriginalTempPathWithinWindow()
    {
        string stagedPath = Path.Combine(
            DropStagingFileOps.GetVirtualDropsRootDirectory(),
            "batch-1",
            "archive-doc.pdf");
        string originalTempPath = @"C:\Users\Demo\AppData\Local\Temp\7zO99\archive-doc.pdf";
        string differentNameTempPath = @"C:\Users\Demo\AppData\Local\Temp\7zO99\other.pdf";

        Assert.True(NativeDropHostBridge.IsDuplicateOfRecentNativeDrop(
            [originalTempPath],
            stagedPath,
            lastNativeDropTick64: 1000,
            nowTick64: 1120));
        Assert.False(NativeDropHostBridge.IsDuplicateOfRecentNativeDrop(
            [differentNameTempPath],
            stagedPath,
            lastNativeDropTick64: 1000,
            nowTick64: 1120));
        Assert.False(NativeDropHostBridge.IsDuplicateOfRecentNativeDrop(
            [originalTempPath],
            stagedPath,
            lastNativeDropTick64: 1000,
            nowTick64: 1300));

        var tracker = new StackPopoverDropClaimTracker();
        Assert.True(tracker.TryBeginNativeDropIntent([stagedPath], gestureId: 7, nowTick64: 2000, out _));
        Assert.True(tracker.ShouldSkipLegacyDropFiles([originalTempPath], nowTick64: 2100));
        Assert.False(tracker.ShouldSkipLegacyDropFiles([differentNameTempPath], nowTick64: 2100));
    }

    [Fact]
    public void VolumeGuidLongPathAndAppAliasPolicies_HandleExtendedPathsAndLinkOnly()
    {
        Directory.CreateDirectory(_tempRoot);
        string fileA = Path.Combine(_tempRoot, "a.txt");
        string fileB = Path.Combine(_tempRoot, "b.txt");
        File.WriteAllText(fileA, "a");

        // Regular and \\?\ extended local paths resolve to the exact same mount root (without \\?\)
        // and canonical \\?\Volume{GUID}\ identity.
        string? rootNormal = FileService.GetComparableVolumeRoot(fileA, useParentPath: false);
        string? rootExtended = FileService.GetComparableVolumeRoot(@"\\?\" + fileA, useParentPath: false);
        Assert.False(string.IsNullOrWhiteSpace(rootNormal));
        Assert.False(rootNormal!.StartsWith(@"\\", StringComparison.Ordinal));
        Assert.Equal(rootNormal, rootExtended, StringComparer.OrdinalIgnoreCase);

        string? identityNormal = FileService.GetComparableVolumeIdentity(fileA, useParentPath: false);
        string? identityExtended = FileService.GetComparableVolumeIdentity(@"\\?\" + fileA, useParentPath: false);
        Assert.False(string.IsNullOrWhiteSpace(identityNormal));
        Assert.StartsWith(@"\\?\Volume{", identityNormal!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(identityNormal, identityExtended, StringComparer.OrdinalIgnoreCase);
        Assert.True(FileService.CanUseAtomicMove(fileA, fileB));

        // Staging root safety allows normal directories and rejects directory junctions.
        string normalStagingBucket = Path.Combine(_tempRoot, "SafeRoot", "VirtualDrops");
        Directory.CreateDirectory(normalStagingBucket);
        DropStagingFileOps.EnsureSafeStagingRootDirectory(normalStagingBucket);

        // Win11 AppExecutionAlias advertises single-bit Link (never Copy | Link).
        Assert.Equal(
            DataPackageOperation.Link,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionMove,
                isWindows11OrLater: true,
                sourcePaths: [@"C:\Users\Demo\AppData\Local\Microsoft\WindowsApps\wt.exe"],
                appExecLinkProbe: static _ => true));
    }

    [Fact]
    public void ConcurrentAttributeStripAndActiveStagingLease_PreserveAttributesAndActiveImports()
    {
        Directory.CreateDirectory(_tempRoot);
        string hiddenFile = Path.Combine(_tempRoot, "secret.txt");
        File.WriteAllText(hiddenFile, "hidden-data");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        // Overlapping StripBlockingAttributes / RestoreAttributes calls must ref-count
        // so the first caller returning does not prematurely restore or drop Hidden.
        FileAttributes? first = FileService.StripBlockingAttributes(hiddenFile);
        Assert.NotNull(first);
        Assert.Equal(0, (int)(File.GetAttributes(hiddenFile) & FileAttributes.Hidden));

        FileAttributes? second = FileService.StripBlockingAttributes(hiddenFile);
        Assert.NotNull(second);
        Assert.True((second!.Value & FileAttributes.Hidden) != 0);

        FileService.RestoreAttributes(hiddenFile, first);
        // Still stripped while second holder is active!
        Assert.Equal(0, (int)(File.GetAttributes(hiddenFile) & FileAttributes.Hidden));

        FileService.RestoreAttributes(hiddenFile, second);
        // Original Hidden attribute restored cleanly once last holder finishes!
        Assert.True((File.GetAttributes(hiddenFile) & FileAttributes.Hidden) != 0);

        // Active staging batch lease protects a >30-min in-progress import from stale reaper.
        string fakeTempRoot = Path.Combine(_tempRoot, "lease-staging");
        string activeBatch = Directory.CreateDirectory(Path.Combine(fakeTempRoot, "VirtualDrops", "active-copy")).FullName;
        string stagedFile = Path.Combine(activeBatch, "large.bin");
        File.WriteAllText(stagedFile, "copying");
        DateTime expiredUtc = DateTime.UtcNow.AddHours(-2);
        Directory.SetCreationTimeUtc(activeBatch, expiredUtc);
        Directory.SetLastWriteTimeUtc(activeBatch, expiredUtc);

        using (DropStagingFileOps.AcquireActiveStagingBatchLease([stagedFile], tempRootOverride: fakeTempRoot))
        {
            int removedWhileLeased = DropStagingFileOps.CleanupStaleDropStagingDirectories(
                tempRootOverride: fakeTempRoot,
                maxAge: TimeSpan.FromMinutes(30),
                nowUtc: DateTimeOffset.UtcNow);
            Assert.Equal(0, removedWhileLeased);
            Assert.True(Directory.Exists(activeBatch));
        }

        int removedAfterRelease = DropStagingFileOps.CleanupStaleDropStagingDirectories(
            tempRootOverride: fakeTempRoot,
            maxAge: TimeSpan.FromMinutes(30),
            nowUtc: DateTimeOffset.UtcNow);
        Assert.Equal(1, removedAfterRelease);
        Assert.False(Directory.Exists(activeBatch));
    }

    [Fact]
    public void DragDropInterop_UsesBlittableNativeStructsAndZeroManualDllImports()
    {
        // Verify exact Windows SDK ABI layout for blittable structs with zero managed references.
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NativeDropTarget.FILEDESCRIPTORW>());
        Assert.Equal(592, Unsafe.SizeOf<NativeDropTarget.FILEDESCRIPTORW>());
        Assert.Equal(592, Marshal.SizeOf<NativeDropTarget.FILEDESCRIPTORW>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<FileTransferNativeMethods.FileDispositionInfo>());
        Assert.Equal(1, Unsafe.SizeOf<FileTransferNativeMethods.FileDispositionInfo>());
        Assert.Equal(1, Marshal.SizeOf<FileTransferNativeMethods.FileDispositionInfo>());

        var disposition = new FileTransferNativeMethods.FileDispositionInfo { Delete = true };
        Assert.True(disposition.Delete);
        disposition.Delete = false;
        Assert.False(disposition.Delete);

        // Verify FILEDESCRIPTORW fixed-buffer Unicode round-trip and safe 259-char truncation.
        string longName = new string('文', 300);
        NativeDropTarget.FILEDESCRIPTORW unicodeDescriptor =
            NativeDropTarget.FILEDESCRIPTORW.Create("目录\\报告-测试.pdf", dwFlags: 0x40, nFileSizeLow: 42);
        Assert.Equal("目录\\报告-测试.pdf", unicodeDescriptor.GetFileName());
        NativeDropTarget.FILEDESCRIPTORW truncatedDescriptor =
            NativeDropTarget.FILEDESCRIPTORW.Create(longName);
        Assert.Equal(259, truncatedDescriptor.GetFileName().Length);

        // Verify all migrated drag/drop platform interop classes have zero classic [DllImport]
        // methods (only [LibraryImport] declarations and generated __PInvoke shims are permitted).
        Type[] migratedTypes =
        [
            typeof(FileTransferNativeMethods),
            typeof(DragDropPermissionNativeMethods),
            typeof(StorageMigrationNativeMethods),
            typeof(DriveMediaInterop),
            typeof(ProcessImageNameResolver),
            typeof(DesktopToastHudInterop),
            typeof(PathOriginClassifier),
        ];

        foreach (Type type in migratedTypes)
        {
            MethodInfo[] classicDllImports = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m =>
                    (m.Attributes & MethodAttributes.PinvokeImpl) != 0 &&
                    !m.Name.Contains("__PInvoke", StringComparison.Ordinal) &&
                    m.GetCustomAttribute<LibraryImportAttribute>() is null)
                .ToArray();
            Assert.True(
                classicDllImports.Length == 0,
                $"{type.Name} still declares classic [DllImport]: {string.Join(", ", classicDllImports.Select(m => m.Name))}");
        }
    }

    [Fact]
    public void StackDropCommitHelper_IncludesOverwrittenExistingFilesAndResolvesPromotedKeys()
    {
        Directory.CreateDirectory(_tempRoot);
        string untouched = Path.Combine(_tempRoot, "untouched.txt");
        string overwritten = Path.Combine(_tempRoot, "report.pdf");
        string newlyCreated = Path.Combine(_tempRoot, "notes.md");

        var currentItems = new[]
        {
            new WidgetItem { Name = "untouched.txt", Path = untouched },
            new WidgetItem { Name = "report.pdf", Path = overwritten },
            new WidgetItem { Name = "notes.md", Path = newlyCreated },
        };
        var preExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(untouched),
            Path.GetFullPath(overwritten),
        };

        // Dropping a replacement report.pdf + new notes.md onto a stack must commit BOTH
        // items into the stack without pulling in the untouched pre-existing file.
        WidgetItem[] selected = StackDropCommitHelper.SelectImportedItemsForStack(
            currentItems,
            preExisting,
            importedDestinationPaths: [overwritten, newlyCreated]);

        Assert.Equal(2, selected.Length);
        Assert.Contains(selected, item => string.Equals(item.Path, overwritten, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selected, item => string.Equals(item.Path, newlyCreated, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selected, item => string.Equals(item.Path, untouched, StringComparison.OrdinalIgnoreCase));

        // Promoted auto-stack -> manual-stack key resolution.
        Assert.Equal("manual:abc", StackDropCommitHelper.ResolveEffectiveStackKey("type:images", "manual:abc"));
        Assert.Equal("type:images", StackDropCommitHelper.ResolveEffectiveStackKey("type:images", null));
    }

    [Fact]
    public async Task LegacyUpgrade_PreservesIndependentManagedDragOutAction()
    {
        string profileDir = Path.Combine(_tempRoot, "legacy-profile");
        Directory.CreateDirectory(profileDir);
        await File.WriteAllTextAsync(
            Path.Combine(profileDir, "settings.json"),
            """
            {
              "managedDropAction": "Copy",
              "managedDragOutAction": "FollowWindows"
            }
            """);

        var settingsService = new SettingsService(profileDir);
        await settingsService.LoadAsync();

        Assert.Equal(SettingsService.ManagedDropActionCopy, settingsService.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionFollowWindows, settingsService.Settings.FileWidget.ManagedDragOutAction);
    }

    [Fact]
    public void Win10NativeShellDragVisualPolicy_KeepsShellVisualEnabledOnWin10WhileSuppressingWin11XamlDuplicates()
    {
        // Win11 WinRT XAML drag draws its own XAML preview -> suppress IDropTargetHelper.
        Assert.True(DeskBox.Controls.WidgetContents.FileSurfaceContent.ShouldSuppressNativeShellDragVisual(
            isWin10NativeShellDragActive: false,
            hasActiveDragSession: true,
            isSurfaceReorderDragActive: false,
            isStackPopoverDragActive: false,
            isDeskBoxFileDragSnapshot: false));
        Assert.True(DeskBox.Controls.WidgetContents.FileSurfaceContent.ShouldSuppressNativeShellDragVisual(
            isWin10NativeShellDragActive: false,
            hasActiveDragSession: false,
            isSurfaceReorderDragActive: false,
            isStackPopoverDragActive: true,
            isDeskBoxFileDragSnapshot: true));

        // Win10 native SHDoDragDrop disables WinUI CanDrag -> IDropTargetHelper MUST stay active.
        Assert.False(DeskBox.Controls.WidgetContents.FileSurfaceContent.ShouldSuppressNativeShellDragVisual(
            isWin10NativeShellDragActive: true,
            hasActiveDragSession: true,
            isSurfaceReorderDragActive: false,
            isStackPopoverDragActive: true,
            isDeskBoxFileDragSnapshot: true));

        // Process-wide registry flags accurately distinguish Win10 native Shell drags per widget.
        Directory.CreateDirectory(_tempRoot);
        string dragged = Path.Combine(_tempRoot, "win10-visual.txt");
        File.WriteAllText(dragged, "visual");
        string sessionId = Guid.NewGuid().ToString("N");
        try
        {
            ActiveDeskBoxDragRegistry.Begin(
                sessionId,
                widgetId: "widget-win10",
                paths: [dragged],
                fromStackPopover: false,
                isNativeShellDrag: true);
            Assert.True(ActiveDeskBoxDragRegistry.HasActiveNativeShellDrag);
            Assert.True(ActiveDeskBoxDragRegistry.IsActiveNativeShellDragFromWidget("widget-win10"));
            Assert.False(ActiveDeskBoxDragRegistry.IsActiveNativeShellDragFromWidget("widget-other"));
        }
        finally
        {
            ActiveDeskBoxDragRegistry.End(sessionId);
        }

        Assert.False(ActiveDeskBoxDragRegistry.HasActiveNativeShellDrag);
        Assert.False(ActiveDeskBoxDragRegistry.IsActiveNativeShellDragFromWidget("widget-win10"));
    }

    [Fact]
    public void Win10DragImageHelper_MultiFileBadgeAndEndToEndDropTargetHelper_ProducesVisibleDragWindow()
    {
        // 1. Multi-file count badge renders opaque accent blue + white glyph pixels on 32-bit DIBs.
        uint[] canvas = new uint[76 * 76];
        Win10DragImageHelper.DrawMultiFileCountBadge(canvas, 76, 76, itemCount: 5, useAlphaChannel: true);
        int accentPixels = canvas.Count(static p => p == 0xFF0078D4u);
        int whitePixels = canvas.Count(static p => p == 0xFFFFFFFFu);
        Assert.True(accentPixels > 50, $"Expected accent pill pixels, got {accentPixels}");
        Assert.True(whitePixels > 20, $"Expected white border/glyph pixels, got {whitePixels}");

        // 2. End-to-end Shell attachment + IDropTargetHelper::DragEnter on guarded data object.
        Directory.CreateDirectory(_tempRoot);
        string fileA = Path.Combine(_tempRoot, "a.txt");
        string fileB = Path.Combine(_tempRoot, "b.txt");
        File.WriteAllText(fileA, "first");
        File.WriteAllText(fileB, "second");

        Win10DragImageAttachResult attachResult = default;
        bool hasDragWindow = false;
        bool dragEntered = false;
        nint dragWindowHandle = 0;
        bool dragFullWindowsEnabled = false;
        Exception? threadError = null;
        var staThread = new Thread(() =>
        {
            try
            {
                nint inner = NativeShellFileDragProvider.CreateShellDataObject([fileA, fileB]);
                nint guard = FileDragSourceGuardDataObject.CreateTargetAwareInterfacePointer(inner);
                try
                {
                    attachResult = Win10DragImageHelper.TryAttachDragImageWithDiagnostics(
                        guard,
                        [fileA, fileB],
                        IntPtr.Zero);

                    var dummyTarget = new NativeDropTarget(Win32Helper.GetDesktopWindow());
                    try
                    {
                        bool entered = NativeDropImageManager.TryDragEnter(
                            dummyTarget,
                            Win32Helper.GetDesktopWindow(),
                            guard,
                            new NativeDropTarget.POINT { X = 240, Y = 240 },
                            NativeDropEffectPolicy.Move);
                        dragEntered = entered;
                        if (entered)
                        {
                            NativeDropImageManager.Show(dummyTarget, visible: true);
                            hasDragWindow = NativeDropDescriptionWriter.HasDragWindow(guard);
                            dragWindowHandle = NativeDropDescriptionWriter.TryGetDragWindowHandleForDiagnostics(guard);
                            dragFullWindowsEnabled = NativeDropDescriptionWriter.IsDragFullWindowsEnabled();
                            NativeDropImageManager.DragLeave(dummyTarget);
                        }
                    }
                    finally
                    {
                        dummyTarget.Dispose();
                    }
                }
                finally
                {
                    FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
                    NativeShellFileDragProvider.ReleaseInterface(inner);
                }
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(threadError);
        Assert.True(attachResult.Attached);
        Assert.True(attachResult.HResult >= 0, $"Unexpected attach HRESULT: 0x{attachResult.HResult:X8}");
        Assert.True(attachResult.SizePx >= 48);
        Assert.Equal(2, attachResult.ItemCount);
        Assert.True(dragEntered, "IDropTargetHelper::DragEnter must accept the guarded data object");
        // The shell drag window exists regardless of the machine's "show window contents
        // while dragging" policy (off by default on CI server images), so probe the handle
        // directly instead of the policy-gated HasDragWindow.
        Assert.NotEqual(0, dragWindowHandle);
        Assert.Equal(dragFullWindowsEnabled, hasDragWindow);
    }

    private static uint ReadDword(nint hGlobal)
    {
        nint ptr = OleDropTargetNativeMethods.GlobalLock(hGlobal);
        Assert.NotEqual(0, ptr);
        try
        {
            return (uint)Marshal.ReadInt32(ptr);
        }
        finally
        {
            OleDropTargetNativeMethods.GlobalUnlock(hGlobal);
        }
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
}
