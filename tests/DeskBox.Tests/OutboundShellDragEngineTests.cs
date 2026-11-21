using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Tests;

public sealed class OutboundShellDragEngineTests
{
    [Theory]
    [InlineData(1u)] // DROPEFFECT_COPY
    [InlineData(2u)] // DROPEFFECT_MOVE
    [InlineData(4u)] // DROPEFFECT_LINK
    public void GuardDataObject_EnumFormatEtc_ExposesServiceablePreferredDropEffect_AndBlocksCompletionFormats(
        uint preferredEffect)
    {
        string root = CreateTempTestDir();
        string filePath = Path.Combine(root, "outbound.txt");
        File.WriteAllText(filePath, "phase3-guard");
        nint inner = NativeShellFileDragProvider.CreateShellDataObject([filePath]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);

        try
        {
            var receiver = new NativeOleDataObject(guard);
            ushort preferredId = Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect");
            ushort performedId = Win32Helper.GetRegisteredClipboardFormat("Performed DropEffect");
            ushort logicalPerformedId = Win32Helper.GetRegisteredClipboardFormat("Logical Performed DropEffect");
            ushort pasteSucceededId = Win32Helper.GetRegisteredClipboardFormat("Paste Succeeded");
            ushort targetClsidId = Win32Helper.GetRegisteredClipboardFormat("TargetCLSID");
            ushort shellIdListId = Win32Helper.GetRegisteredClipboardFormat("Shell IDList Array");

            // 1. Before setting any preference, Preferred DropEffect must NOT be enumerated.
            HashSet<ushort> initialFormats = receiver.EnumerateGetFormatIds();
            Assert.Contains((ushort)15, initialFormats); // CF_HDROP
            Assert.Contains(shellIdListId, initialFormats);
            Assert.DoesNotContain(preferredId, initialFormats);

            // 2. Write a serviceable single-bit Preferred DropEffect and forged completion receipts.
            WriteDwordFormat(guard, "Preferred DropEffect", preferredEffect);
            WriteDwordFormat(guard, "Performed DropEffect", 2u);
            WriteDwordFormat(guard, "Paste Succeeded", 2u);

            HashSet<ushort> activeFormats = receiver.EnumerateGetFormatIds();
            Assert.Contains((ushort)15, activeFormats);
            Assert.Contains(shellIdListId, activeFormats);
            Assert.Contains(preferredId, activeFormats);
            Assert.DoesNotContain(performedId, activeFormats);
            Assert.DoesNotContain(logicalPerformedId, activeFormats);
            Assert.DoesNotContain(pasteSucceededId, activeFormats);
            Assert.DoesNotContain(targetClsidId, activeFormats);

            // 3. Clearing with DROPEFFECT_NONE (0) or multi-bit mask (7) removes Preferred DropEffect from enumeration.
            WriteDwordFormat(guard, "Preferred DropEffect", 0u);
            HashSet<ushort> clearedFormats = receiver.EnumerateGetFormatIds();
            Assert.DoesNotContain(preferredId, clearedFormats);

            WriteDwordFormat(guard, "Preferred DropEffect", 7u);
            HashSet<ushort> maskFormats = receiver.EnumerateGetFormatIds();
            Assert.DoesNotContain(preferredId, maskFormats);

            // 4. Null enumerator out-pointer returns E_POINTER cleanly.
            Assert.Equal(unchecked((int)0x80004003), receiver.EnumFormatEtcRaw(1u, 0));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MultiParentShellDataObject_RoundTripsNativeFormats_AndPreservesAllSourcesUnderMoveReceipt()
    {
        string root = CreateTempTestDir();
        string dirA = Directory.CreateDirectory(Path.Combine(root, "DirA")).FullName;
        string dirB = Directory.CreateDirectory(Path.Combine(root, "DirB")).FullName;
        string fileA = Path.Combine(dirA, "alpha.txt");
        string fileB = Path.Combine(dirB, "beta.lnk");
        File.WriteAllText(fileA, "alpha-content");
        File.WriteAllBytes(fileB, [0x4C, 0x00, 0x00, 0x00, 1, 2, 3, 4]);

        nint inner = NativeShellFileDragProvider.CreateShellDataObject([fileA, fileB]);
        Assert.NotEqual(0, inner);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);

        try
        {
            var receiver = new NativeOleDataObject(guard);
            WriteDwordFormat(guard, "Preferred DropEffect", 2u);
            WriteDwordFormat(guard, "Performed DropEffect", 2u);
            WriteDwordFormat(guard, "Logical Performed DropEffect", 2u);

            HashSet<ushort> enumerated = receiver.EnumerateGetFormatIds();
            Assert.Contains((ushort)15, enumerated); // CF_HDROP
            Assert.Contains(Win32Helper.GetRegisteredClipboardFormat("Shell IDList Array"), enumerated);
            Assert.Contains(Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect"), enumerated);

            string[] droppedPaths = ReadHDropPaths(receiver);
            Assert.Equal(2, droppedPaths.Length);
            Assert.Contains(fileA, droppedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(fileB, droppedPaths, StringComparer.OrdinalIgnoreCase);

            Assert.True(File.Exists(fileA));
            Assert.True(File.Exists(fileB));
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            NativeShellFileDragProvider.ReleaseInterface(inner);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DecisionAAndA1_Win11RestoresLinkCapability_AndWin10FollowWindowsDefaultsToSilentCopyWithGuide()
    {
        // Win11 FollowWindows: Copy | Move (0x3 != 0x7) with None preference
        // so WinUI 3 C++ writes AllowedOperations without collapsing to 0x0.
        Assert.Equal(
            DataPackageOperation.Copy | DataPackageOperation.Move,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true));
        Assert.Equal(
            DataPackageOperation.None,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionFollowWindows));

        // Win10 FollowWindows (Decision A1): single-bit silent Copy with None preference
        DataPackageOperation win10FollowAllowed =
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false);
        Assert.Equal(DataPackageOperation.Copy, win10FollowAllowed);
        Assert.Equal(
            "Widget.DragOutTip.AfterCopy.NoModifiers",
            FileSurfaceContent.GetDragOutResultHintKey(DataPackageOperation.Copy, win10FollowAllowed));

        // D3: Win10 mouse drag-out now uses native SHDoDragDrop (supporting all three
        // modes silently without #32768) and never prompts a one-click switch to Move.
        var desktopSnapshot = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: false,
            default,
            IsDesktopShellView: true);
        var folderSnapshot = new DropReleaseSnapshot(
            DropTargetWindowKind.ExplorerFileView,
            EscapePressed: false,
            default,
            IsDesktopShellView: false);

        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            desktopSnapshot,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false,
            protectedCopyOnlySource: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            folderSnapshot,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false,
            protectedCopyOnlySource: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            desktopSnapshot,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: true,
            crossVolumeFromDesktop: false,
            protectedCopyOnlySource: false));
        Assert.Null(FileSurfaceContent.EvaluateContextualDragOutGuideId(
            DataPackageOperation.Copy,
            SettingsService.ManagedDragOutActionFollowWindows,
            isWindows11OrLater: false,
            desktopSnapshot,
            handledAsStackMembership: false,
            hasStorageItems: true,
            controlHeld: false,
            crossVolumeFromDesktop: false,
            protectedCopyOnlySource: true));
    }

    [Fact]
    public void AllThirteenLocales_IncludeWin10DefaultCopyHudAndUpdatedWin10Note()
    {
        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(stringsDir, "*.json");
        Assert.Equal(13, localeFiles.Length);

        foreach (string file in localeFiles)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(
                doc.RootElement.TryGetProperty("Widget.DragHud.Win10DefaultCopy", out JsonElement hudEl) &&
                !string.IsNullOrWhiteSpace(hudEl.GetString()),
                $"Missing Widget.DragHud.Win10DefaultCopy in {Path.GetFileName(file)}");
            Assert.True(
                doc.RootElement.TryGetProperty("Settings.DragOutAction.Win10Note", out JsonElement noteEl) &&
                !string.IsNullOrWhiteSpace(noteEl.GetString()),
                $"Missing Settings.DragOutAction.Win10Note in {Path.GetFileName(file)}");
        }
    }

    [Fact]
    public void ColdStartVisualRefresh_FirstExternalDrag_ExecutesZeroNetDisplacementPulsesAndLatchesWarmup()
    {
        var coordinator = new OutboundDragVisualRefreshCoordinator();
        var origin = new Win32Helper.POINT { X = 500, Y = 500 };
        var nudged = new Win32Helper.POINT { X = 501, Y = 500 };

        Assert.True(coordinator.TryBeginDragSession(1_000, out long serial));
        Assert.False(coordinator.IsWarmupCompleted);

        var insideWidget = new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: false,
            CursorPoint: new Win32Helper.POINT { X = 220, Y = 220 },
            SafeNudgeTarget: null,
            TargetKind: DropTargetWindowKind.DeskBoxOwnWindow);
        Assert.Equal(
            OutboundDragVisualRefreshAction.ContinueWatching,
            coordinator.EvaluateTick(serial, 1_040, in insideWidget).Action);

        var desktopOrigin = new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: true,
            CursorPoint: origin,
            SafeNudgeTarget: nudged,
            TargetKind: DropTargetWindowKind.ExplorerFileView);

        // First outside tick at +80ms: not yet settled for two consecutive ticks or >=100ms.
        Assert.Equal(
            OutboundDragVisualRefreshAction.ContinueWatching,
            coordinator.EvaluateTick(serial, 1_080, in desktopOrigin).Action);

        // Second consecutive settled outside tick at +120ms: Step 1 (+1px) of Pulse 1.
        OutboundDragVisualRefreshDecision pulse1Step1 =
            coordinator.EvaluateTick(serial, 1_120, in desktopOrigin);
        Assert.Equal(OutboundDragVisualRefreshAction.MoveCursor, pulse1Step1.Action);
        Assert.Equal(nudged, pulse1Step1.TargetPoint);
        Assert.Equal(1, pulse1Step1.PulseNumber);
        Assert.False(pulse1Step1.IsReturnStep);
        Assert.False(pulse1Step1.CompletesWarmup);

        // Next tick at +160ms while stationary at nudged pixel: Step 2 (-1px return to exact origin).
        var desktopNudged = desktopOrigin with { CursorPoint = nudged };
        OutboundDragVisualRefreshDecision pulse1Step2 =
            coordinator.EvaluateTick(serial, 1_160, in desktopNudged);
        Assert.Equal(OutboundDragVisualRefreshAction.MoveCursor, pulse1Step2.Action);
        Assert.Equal(origin, pulse1Step2.TargetPoint);
        Assert.Equal(1, pulse1Step2.PulseNumber);
        Assert.True(pulse1Step2.IsReturnStep);
        Assert.False(pulse1Step2.CompletesWarmup);

        // Still stationary before SecondaryFollowUpDelayMs (160ms): keep watching quietly.
        Assert.Equal(
            OutboundDragVisualRefreshAction.ContinueWatching,
            coordinator.EvaluateTick(serial, 1_280, in desktopOrigin).Action);

        // Still stationary 160ms after Pulse 1 completed: Step 1 (+1px) of follow-up Pulse 2.
        OutboundDragVisualRefreshDecision pulse2Step1 =
            coordinator.EvaluateTick(serial, 1_320, in desktopOrigin);
        Assert.Equal(OutboundDragVisualRefreshAction.MoveCursor, pulse2Step1.Action);
        Assert.Equal(nudged, pulse2Step1.TargetPoint);
        Assert.Equal(2, pulse2Step1.PulseNumber);
        Assert.False(pulse2Step1.IsReturnStep);
        Assert.False(pulse2Step1.CompletesWarmup);

        // Step 2 (-1px return) of Pulse 2 completes warmup permanently for this process.
        OutboundDragVisualRefreshDecision pulse2Step2 =
            coordinator.EvaluateTick(serial, 1_360, in desktopNudged);
        Assert.Equal(OutboundDragVisualRefreshAction.MoveCursor, pulse2Step2.Action);
        Assert.Equal(origin, pulse2Step2.TargetPoint);
        Assert.Equal(2, pulse2Step2.PulseNumber);
        Assert.True(pulse2Step2.IsReturnStep);
        Assert.True(pulse2Step2.CompletesWarmup);
        Assert.True(coordinator.IsWarmupCompleted);

        // Subsequent drag sessions incur zero watch overhead.
        Assert.False(coordinator.TryBeginDragSession(2_000, out long secondSerial));
        Assert.Equal(0, secondSerial);
    }

    [Fact]
    public void ColdStartVisualRefresh_InternalOnlyFirstDrag_PreservesWarmupForFirstDesktopDrag()
    {
        var coordinator = new OutboundDragVisualRefreshCoordinator();
        Assert.True(coordinator.TryBeginDragSession(1_000, out long internalSerial));

        var insideWidget = new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: false,
            CursorPoint: new Win32Helper.POINT { X = 180, Y = 180 },
            SafeNudgeTarget: null,
            TargetKind: DropTargetWindowKind.DeskBoxOwnWindow);
        Assert.Equal(
            OutboundDragVisualRefreshAction.ContinueWatching,
            coordinator.EvaluateTick(internalSerial, 1_040, in insideWidget).Action);
        Assert.Equal(
            OutboundDragVisualRefreshAction.ContinueWatching,
            coordinator.EvaluateTick(internalSerial, 1_120, in insideWidget).Action);
        coordinator.EndDragSession(internalSerial);

        // Because Drag #1 never pulsed an external target, Drag #2 to Desktop still arms.
        Assert.False(coordinator.IsWarmupCompleted);
        Assert.True(coordinator.TryBeginDragSession(2_000, out long externalSerial));

        var origin = new Win32Helper.POINT { X = 640, Y = 420 };
        var nudged = new Win32Helper.POINT { X = 641, Y = 420 };
        var desktopOrigin = new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: true,
            CursorPoint: origin,
            SafeNudgeTarget: nudged,
            TargetKind: DropTargetWindowKind.ExplorerFileView);
        var desktopNudged = desktopOrigin with { CursorPoint = nudged };

        _ = coordinator.EvaluateTick(externalSerial, 2_060, in desktopOrigin);
        Assert.Equal(
            OutboundDragVisualRefreshAction.MoveCursor,
            coordinator.EvaluateTick(externalSerial, 2_120, in desktopOrigin).Action);
        Assert.Equal(
            OutboundDragVisualRefreshAction.MoveCursor,
            coordinator.EvaluateTick(externalSerial, 2_160, in desktopNudged).Action);

        // Ending the session after Pulse 1 latches warmup so Drag #3 will not arm.
        coordinator.EndDragSession(externalSerial);
        Assert.True(coordinator.IsWarmupCompleted);
        Assert.False(coordinator.TryBeginDragSession(3_000, out _));
    }

    [Fact]
    public void ColdStartVisualRefresh_UserMovementDuringOrAfterPulse_YieldsWithoutSnappingCursorBack()
    {
        var coordinator = new OutboundDragVisualRefreshCoordinator();
        Assert.True(coordinator.TryBeginDragSession(1_000, out long serial));

        var origin = new Win32Helper.POINT { X = 500, Y = 500 };
        var nudged = new Win32Helper.POINT { X = 501, Y = 500 };
        var desktopOrigin = new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: true,
            CursorPoint: origin,
            SafeNudgeTarget: nudged,
            TargetKind: DropTargetWindowKind.ExplorerFileView);

        _ = coordinator.EvaluateTick(serial, 1_060, in desktopOrigin);
        OutboundDragVisualRefreshDecision step1 =
            coordinator.EvaluateTick(serial, 1_120, in desktopOrigin);
        Assert.Equal(OutboundDragVisualRefreshAction.MoveCursor, step1.Action);

        // User physically moves cursor to (540, 520) before the return tick: must NOT snap back to (500, 500).
        var userMoved = desktopOrigin with
        {
            CursorPoint = new Win32Helper.POINT { X = 540, Y = 520 },
            SafeNudgeTarget = new Win32Helper.POINT { X = 541, Y = 520 },
        };
        OutboundDragVisualRefreshDecision yieldedStep2 =
            coordinator.EvaluateTick(serial, 1_160, in userMoved);
        Assert.Equal(OutboundDragVisualRefreshAction.ContinueWatching, yieldedStep2.Action);

        // Continued live movement after cold-start threshold (>=220ms) completes warmup without Pulse 2.
        var userMovedWarm = desktopOrigin with
        {
            CursorPoint = new Win32Helper.POINT { X = 565, Y = 535 },
            SafeNudgeTarget = new Win32Helper.POINT { X = 566, Y = 535 },
        };
        OutboundDragVisualRefreshDecision warmDone =
            coordinator.EvaluateTick(serial, 1_240, in userMovedWarm);
        Assert.Equal(OutboundDragVisualRefreshAction.StopWatching, warmDone.Action);
        Assert.True(warmDone.CompletesWarmup);
        Assert.True(coordinator.IsWarmupCompleted);
    }

    [Fact]
    public void ColdStartVisualRefresh_BlockedMovesAndMouseRelease_CancelCleanlyWithoutLatchingPrematurely()
    {
        var coordinator = new OutboundDragVisualRefreshCoordinator();
        Assert.True(coordinator.TryBeginDragSession(1_000, out long serial));

        var origin = new Win32Helper.POINT { X = 700, Y = 500 };
        var nudged = new Win32Helper.POINT { X = 701, Y = 500 };
        var desktopOrigin = new OutboundCursorEnvironmentSnapshot(
            MouseButtonDown: true,
            IsOutsideEligibleTarget: true,
            CursorPoint: origin,
            SafeNudgeTarget: nudged,
            TargetKind: DropTargetWindowKind.NonExplorerWindow);

        _ = coordinator.EvaluateTick(serial, 1_060, in desktopOrigin);
        Assert.Equal(
            OutboundDragVisualRefreshAction.MoveCursor,
            coordinator.EvaluateTick(serial, 1_120, in desktopOrigin).Action);

        // Simulate UIPI blocking SetCursorPos: rolls back pending return step and retries once.
        Assert.Equal(
            OutboundDragVisualRefreshAction.ContinueWatching,
            coordinator.NotifyMoveFailed(serial).Action);

        // Next tick tries Step 1 again (IsReturnStep == false, NOT snapping back to origin).
        OutboundDragVisualRefreshDecision retry =
            coordinator.EvaluateTick(serial, 1_160, in desktopOrigin);
        Assert.Equal(OutboundDragVisualRefreshAction.MoveCursor, retry.Action);
        Assert.False(retry.IsReturnStep);

        // Second blocked attempt stops watching for this session without falsely latching warmup.
        Assert.Equal(
            OutboundDragVisualRefreshAction.StopWatching,
            coordinator.NotifyMoveFailed(serial).Action);
        Assert.False(coordinator.IsWarmupCompleted);

        // New session where mouse button is released mid-pulse cancels immediately without moving cursor.
        Assert.True(coordinator.TryBeginDragSession(2_000, out long releaseSerial));
        _ = coordinator.EvaluateTick(releaseSerial, 2_060, in desktopOrigin);
        Assert.Equal(
            OutboundDragVisualRefreshAction.MoveCursor,
            coordinator.EvaluateTick(releaseSerial, 2_120, in desktopOrigin).Action);

        var buttonReleased = desktopOrigin with
        {
            MouseButtonDown = false,
            CursorPoint = nudged,
        };
        Assert.Equal(
            OutboundDragVisualRefreshAction.StopWatching,
            coordinator.EvaluateTick(releaseSerial, 2_160, in buttonReleased).Action);
    }

    [Fact]
    public void ChooseSafeOnePixelNudgeTarget_RespectsMonitorEdgesNegativeCoordinatesAndWidgetExclusions()
    {
        var primaryMonitor = new Win32Helper.RECT
        {
            Left = 0,
            Top = 0,
            Right = 1920,
            Bottom = 1080,
        };

        // Bottom-right corner of primary monitor: (+1, 0) is out of bounds, (-1, 0) stays inside.
        Win32Helper.POINT? cornerTarget = DesktopToastHudInterop.ChooseSafeOnePixelNudgeTarget(
            new Win32Helper.POINT { X = 1919, Y = 1079 },
            in primaryMonitor);
        Assert.Equal(new Win32Helper.POINT { X = 1918, Y = 1079 }, cornerTarget);

        // Negative-coordinate secondary monitor to the left of primary.
        var leftSecondaryMonitor = new Win32Helper.RECT
        {
            Left = -1920,
            Top = -240,
            Right = 0,
            Bottom = 840,
        };
        Win32Helper.POINT? leftEdgeTarget = DesktopToastHudInterop.ChooseSafeOnePixelNudgeTarget(
            new Win32Helper.POINT { X = -1920, Y = -240 },
            in leftSecondaryMonitor);
        Assert.Equal(new Win32Helper.POINT { X = -1919, Y = -240 }, leftEdgeTarget);

        // 1px to the left of a DeskBox widget window: (+1, 0) would step back into DeskBox, so (-1, 0) is chosen.
        var widgetRect = new Win32Helper.RECT
        {
            Left = 400,
            Top = 200,
            Right = 760,
            Bottom = 560,
        };
        Win32Helper.POINT? outsideWidgetTarget = DesktopToastHudInterop.ChooseSafeOnePixelNudgeTarget(
            new Win32Helper.POINT { X = 399, Y = 320 },
            in primaryMonitor,
            primaryExclusionRect: widgetRect);
        Assert.Equal(new Win32Helper.POINT { X = 398, Y = 320 }, outsideWidgetTarget);

        // Degenerate 1x1 bounds cannot safely nudge in any direction.
        var tinyBounds = new Win32Helper.RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
        Assert.Null(DesktopToastHudInterop.ChooseSafeOnePixelNudgeTarget(
            new Win32Helper.POINT { X = 0, Y = 0 },
            in tinyBounds));
    }

    [Fact]
    public void DesktopOutboundDropCoordinator_GetArrivalNameCandidates_CoversConflictAndCopySiblings()
    {
        string[] candidates = DesktopOutboundDropCoordinator.GetArrivalNameCandidates("report.pdf").ToArray();
        Assert.Contains("report.pdf", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("report (2).pdf", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("report (3).pdf", candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("report - Copy.pdf", candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileItemDragPackage_AppExecutionAlias_PrefersLinkWhileRegularExecutableKeepsCopy()
    {
        const string aliasPath = @"C:\Users\Demo\AppData\Local\Microsoft\WindowsApps\wt.exe";
        const string regularExePath = @"C:\Program Files\DemoApp\tool.exe";

        Assert.Equal(
            DataPackageOperation.Link,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true,
                sourcePaths: [aliasPath],
                appExecLinkProbe: static path => path.EndsWith("wt.exe", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            DataPackageOperation.Link,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false,
                sourcePaths: [aliasPath],
                appExecLinkProbe: static path => path.EndsWith("wt.exe", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            DataPackageOperation.Link,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionFollowWindows,
                sourcePaths: [aliasPath],
                appExecLinkProbe: static path => path.EndsWith("wt.exe", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(
            FileItemDragPackage.ProtectedWin11Operations,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: true,
                sourcePaths: [regularExePath],
                isProtectedCopyOnly: true,
                appExecLinkProbe: static _ => false));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutAllowedOperations(
                SettingsService.ManagedDragOutActionFollowWindows,
                isWindows11OrLater: false,
                sourcePaths: [regularExePath],
                isProtectedCopyOnly: true,
                appExecLinkProbe: static _ => false));
        Assert.Equal(
            DataPackageOperation.Copy,
            FileItemDragPackage.ResolveDragOutPreferredOperation(
                SettingsService.ManagedDragOutActionFollowWindows,
                sourcePaths: [regularExePath],
                isProtectedCopyOnly: true,
                appExecLinkProbe: static _ => false));

        var internalPkg = new DataPackage();
        internalPkg.Properties[DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        Assert.Equal(
            DataPackageOperation.Link,
            DeskBoxDragData.GetFileAssociationOperation(
                internalPkg.GetView(),
                DataPackageOperation.Link));

        var externalPkg = new DataPackage();
        Assert.Equal(
            DataPackageOperation.Link,
            DeskBoxDragData.GetFileAssociationOperation(
                externalPkg.GetView(),
                DataPackageOperation.Link));
    }

    [Fact]
    public void ActiveDeskBoxDragRegistry_BeginWithPhysicalPaths_MatchesBothLogicalAndUnvirtualizedDropPaths()
    {
        string sessionId = Guid.NewGuid().ToString("N");
        string logicalVfsPath = @"C:\Users\Demo\AppData\Local\DeskBox\Managed\report.pdf";
        string physicalPackagePath = @"C:\Users\Demo\AppData\Local\Packages\DeskBox_8wekyb3d8bbwe\LocalCache\Local\DeskBox\Managed\report.pdf";

        try
        {
            ActiveDeskBoxDragRegistry.Begin(
                sessionId,
                widgetId: "widget-store-vfs",
                paths: [logicalVfsPath],
                fromStackPopover: true,
                physicalPaths: [physicalPackagePath]);

            Assert.True(ActiveDeskBoxDragRegistry.TryMatch(
                [logicalVfsPath],
                out string matchedLogicalWidget,
                out bool matchedLogicalPopover));
            Assert.Equal("widget-store-vfs", matchedLogicalWidget);
            Assert.True(matchedLogicalPopover);

            Assert.True(ActiveDeskBoxDragRegistry.TryMatch(
                [physicalPackagePath],
                out string matchedPhysicalWidget,
                out bool matchedPhysicalPopover));
            Assert.Equal("widget-store-vfs", matchedPhysicalWidget);
            Assert.True(matchedPhysicalPopover);
        }
        finally
        {
            ActiveDeskBoxDragRegistry.End(sessionId);
        }

        Assert.False(ActiveDeskBoxDragRegistry.TryMatch(
            [physicalPackagePath],
            out _,
            out _));
    }

    [Fact]
    public void FileDragSourceGuardDataObject_KeepManagedRootForActiveDrag_PreservesRootUntilExplicitClear()
    {
        string root = CreateTempTestDir();
        string filePath = Path.Combine(root, "root-keepalive.txt");
        File.WriteAllText(filePath, "keepalive");

        nint inner = NativeShellFileDragProvider.CreateShellDataObject([filePath]);
        nint guard = FileDragSourceGuardDataObject.CreateInterfacePointer(inner);
        try
        {
            Assert.True(FileDragSourceGuardDataObject.HasActiveManagedRoot);
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(
                guard,
                keepManagedRootForActiveDrag: true);
            guard = 0;

            Assert.True(FileDragSourceGuardDataObject.HasActiveManagedRoot);
            FileDragSourceGuardDataObject.ClearActiveManagedRoot();
            Assert.False(FileDragSourceGuardDataObject.HasActiveManagedRoot);
        }
        finally
        {
            FileDragSourceGuardDataObject.ReleaseInterfacePointer(guard);
            FileDragSourceGuardDataObject.ClearActiveManagedRoot();
            NativeShellFileDragProvider.ReleaseInterface(inner);
        }
    }

    [Fact]
    public void FileItemDragPackage_TryPreparePaths_CancelsWithoutCallingStorageBrokerWhenUncShellAttachFails()
    {
        var package = new DataPackage();
        int brokerCalls = 0;
        const string unreachableUncPath = @"\\127.0.0.1\DeskBoxNonExistentShare_9f81\missing.txt";

        bool prepared = FileItemDragPackage.TryPreparePaths(
            package,
            [unreachableUncPath],
            _ =>
            {
                Interlocked.Increment(ref brokerCalls);
                return [];
            },
            static paths => paths[0],
            "unit-test-unc",
            out _,
            allowNativeShellDataObject: true);

        Assert.False(prepared);
        Assert.Equal(0, brokerCalls);
    }

    [Fact]
    public void FileItemDragPackage_TryPreparePaths_DropsMissingOnlineNetworkMemberAndKeepsExistingLocalFile()
    {
        string tempDir = CreateTempTestDir();
        string localFile = Path.Combine(tempDir, "keep-me.txt");
        File.WriteAllText(localFile, "payload");
        const string staleOnlineUncPath = @"\\127.0.0.1\DeskBoxNonExistentShare_9f81\deleted.pdf";

        try
        {
            var package = new DataPackage();
            int brokerCalls = 0;

            bool prepared = FileItemDragPackage.TryPreparePaths(
                package,
                [localFile, staleOnlineUncPath],
                _ =>
                {
                    Interlocked.Increment(ref brokerCalls);
                    return [];
                },
                static paths => paths[0],
                "unit-test-mixed-unc",
                out FileItemDragPackageResult result,
                allowNativeShellDataObject: true);

            // Only assert survivor filtering when localhost SMB rejected within the
            // 650ms window (as standard Windows SMB loopback does in ~2ms); if a
            // firewall silently blackholes TCP 445 past 650ms, TimedOut cancels cleanly.
            if (prepared)
            {
                Assert.Equal([localFile], result.SourcePaths);
                Assert.True(result.UsesNativeShellDataObject);
            }

            Assert.Equal(0, brokerCalls);
        }
        finally
        {
            FileDragSourceGuardDataObject.ClearActiveManagedRoot();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteDwordFormat(nint dataObject, string formatName, uint value)
    {
        var format = new NativeFormatEtc
        {
            ClipboardFormat = Win32Helper.GetRegisteredClipboardFormat(formatName),
            Aspect = 1,
            Index = -1,
            MediumType = 1,
        };
        Assert.True(Win32Helper.TryCreateGlobalMemory(BitConverter.GetBytes(value), out nint memory));
        var medium = new NativeStorageMedium { MediumType = 1, Content = memory };
        int hr = new NativeOleDataObject(dataObject).SetData(ref format, ref medium, release: true);
        if (hr < 0)
        {
            Win32Helper.ReleaseStorageMedium(ref medium);
        }

        Assert.Equal(0, hr);
    }

    private static string[] ReadHDropPaths(NativeOleDataObject receiver)
    {
        var hdropFormat = new NativeFormatEtc
        {
            ClipboardFormat = 15, // CF_HDROP
            Aspect = 1,
            Index = -1,
            MediumType = 1,
        };
        Assert.Equal(0, receiver.GetData(ref hdropFormat, out NativeStorageMedium medium));
        try
        {
            uint count = Win32Helper.DragQueryFile(medium.Content, 0xFFFFFFFF, null, 0);
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint length = Win32Helper.DragQueryFile(medium.Content, i, null, 0);
                var sb = new System.Text.StringBuilder((int)length + 1);
                Win32Helper.DragQueryFile(medium.Content, i, sb, (uint)sb.Capacity);
                paths[i] = sb.ToString();
            }

            return paths;
        }
        finally
        {
            Win32Helper.ReleaseStorageMedium(ref medium);
        }
    }

    private static string CreateTempTestDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"))).FullName;
}
