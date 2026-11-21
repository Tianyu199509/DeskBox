using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class FileStackPopoverAndGroupDropRoutingTests
{
    [Fact]
    public void ShouldDeferStackPopoverLightDismiss_DefersWhileDragActiveOrMouseInitiallyDown()
    {
        // Initial mouse-down outside popover defers light-dismiss inside the 450ms window.
        Assert.True(FileSurfaceContent.ShouldDeferStackPopoverLightDismiss(
            mouseButtonDown: true,
            dragActive: false,
            nativeDragOver: false,
            elapsedSinceDeactivateMs: 0));
        Assert.True(FileSurfaceContent.ShouldDeferStackPopoverLightDismiss(
            mouseButtonDown: true,
            dragActive: false,
            nativeDragOver: false,
            elapsedSinceDeactivateMs: 250));

        // Stationary mouse hold without starting a drag expires at 450ms.
        Assert.False(FileSurfaceContent.ShouldDeferStackPopoverLightDismiss(
            mouseButtonDown: true,
            dragActive: false,
            nativeDragOver: false,
            elapsedSinceDeactivateMs: FileSurfaceContent.StackPopoverLightDismissMaxDeferralMilliseconds));

        // Plain click release outside without drag dismisses immediately.
        Assert.False(FileSurfaceContent.ShouldDeferStackPopoverLightDismiss(
            mouseButtonDown: false,
            dragActive: false,
            nativeDragOver: false,
            elapsedSinceDeactivateMs: 60));

        // Active DeskBox / external drag or native OLE hover keeps popover alive past 450ms.
        Assert.True(FileSurfaceContent.ShouldDeferStackPopoverLightDismiss(
            mouseButtonDown: true,
            dragActive: true,
            nativeDragOver: false,
            elapsedSinceDeactivateMs: 2_500));
        Assert.True(FileSurfaceContent.ShouldDeferStackPopoverLightDismiss(
            mouseButtonDown: true,
            dragActive: false,
            nativeDragOver: true,
            elapsedSinceDeactivateMs: 2_500));
    }

    [Fact]
    public void ShouldArmStackSpringLoad_EnforcesPopoverModeAndExcludesHeadersOpenAndSourceStacks()
    {
        Assert.Equal(500, FileSurfaceContent.StackSpringLoadDelayMilliseconds);

        // Eligible collapsed stack tile in popover mode.
        Assert.True(FileSurfaceContent.ShouldArmStackSpringLoad(
            usesStackPopover: true,
            isStackExpanded: false,
            isDraggingStackHeader: false,
            hoveredStackKey: "stack-docs",
            openPopoverStackKey: null,
            sourceStackKey: null));

        // Blocked when inline stack mode is configured instead of popover mode.
        Assert.False(FileSurfaceContent.ShouldArmStackSpringLoad(
            usesStackPopover: false,
            isStackExpanded: false,
            isDraggingStackHeader: false,
            hoveredStackKey: "stack-docs",
            openPopoverStackKey: null,
            sourceStackKey: null));

        // Blocked when dragging a collapsed stack header for reorder.
        Assert.False(FileSurfaceContent.ShouldArmStackSpringLoad(
            usesStackPopover: true,
            isStackExpanded: false,
            isDraggingStackHeader: true,
            hoveredStackKey: "stack-docs",
            openPopoverStackKey: null,
            sourceStackKey: null));

        // Blocked when this stack's popover is already open or is the drag's own source stack.
        Assert.False(FileSurfaceContent.ShouldArmStackSpringLoad(
            usesStackPopover: true,
            isStackExpanded: false,
            isDraggingStackHeader: false,
            hoveredStackKey: "stack-docs",
            openPopoverStackKey: "stack-docs",
            sourceStackKey: null));
        Assert.False(FileSurfaceContent.ShouldArmStackSpringLoad(
            usesStackPopover: true,
            isStackExpanded: false,
            isDraggingStackHeader: false,
            hoveredStackKey: "stack-docs",
            openPopoverStackKey: null,
            sourceStackKey: "stack-docs"));
    }

    [Fact]
    public void HasDragWindow_ZeroDataObjectReturnsFalse_EnablingXamlCaptionFallback()
    {
        Assert.False(NativeDropDescriptionWriter.HasDragWindow(0));
    }

    [Fact]
    public void ResolveNextGroupMemberId_CyclesStackModeGroupMembersDeterministically()
    {
        Assert.Null(WidgetGroupTitleSwitcher.ResolveNextGroupMemberId([], "a"));
        Assert.Null(WidgetGroupTitleSwitcher.ResolveNextGroupMemberId(["a"], "a"));

        Assert.Equal("b", WidgetGroupTitleSwitcher.ResolveNextGroupMemberId(["a", "b"], "a"));
        Assert.Equal("a", WidgetGroupTitleSwitcher.ResolveNextGroupMemberId(["a", "b"], "b"));

        Assert.Equal("m2", WidgetGroupTitleSwitcher.ResolveNextGroupMemberId(["m1", "m2", "m3"], "m1"));
        Assert.Equal("m3", WidgetGroupTitleSwitcher.ResolveNextGroupMemberId(["m1", "m2", "m3"], "m2"));
        Assert.Equal("m1", WidgetGroupTitleSwitcher.ResolveNextGroupMemberId(["m1", "m2", "m3"], "m3"));
    }

    [Fact]
    public async Task CustomWidgetManifestFileDrop_DistinguishesDisplayOnlyFromDropZoneAndHonorsDropModeCopy()
    {
        string displayOnlyManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.display.only",
          "name": "Clock Display",
          "root": "root",
          "elements": {
            "root": {
              "type": "Text",
              "text": "Hello"
            }
          }
        }
        """;
        Assert.False(CustomWidgetViewModel.DoesManifestJsonAcceptFileDrops(displayOnlyManifest));

        string rootOnDropManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.root.drop",
          "name": "Inbox",
          "onDrop": {
            "accept": "files",
            "mode": "copy",
            "actions": [
              { "type": "set", "key": "lastFile", "value": "{{drop.path}}" }
            ]
          },
          "root": "root",
          "elements": {
            "root": { "type": "Text", "text": "{{state.lastFile}}" }
          }
        }
        """;
        Assert.True(CustomWidgetViewModel.DoesManifestJsonAcceptFileDrops(rootOnDropManifest));

        string dropZoneCopyManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.dropzone.copy",
          "name": "DropZone Copy",
          "state": {
            "savedPath": "",
            "savedMode": ""
          },
          "root": "root",
          "elements": {
            "root": {
              "type": "Column",
              "children": ["drop_zone"]
            },
            "drop_zone": {
              "type": "DropZone",
              "title": "Drop files here",
              "dropMode": "copy",
              "onDrop": [
                { "type": "set", "key": "savedPath", "value": "{{drop.path}}" },
                { "type": "set", "key": "savedMode", "value": "{{drop.mode}}" }
              ]
            }
          }
        }
        """;
        Assert.True(CustomWidgetViewModel.DoesManifestJsonAcceptFileDrops(dropZoneCopyManifest));

        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Phase2CustomDrop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourceFile = Path.Combine(tempRoot, "sample.txt");
            await File.WriteAllTextAsync(sourceFile, "portable copy payload");

            var config = new WidgetConfig
            {
                Id = "custom-dropzone-copy-test",
                WidgetKind = WidgetKind.Custom,
                Name = "DropZone Copy"
            };
            var store = new CustomWidgetDataStore(config.Id, tempRoot);
            await File.WriteAllTextAsync(store.ManifestPath, dropZoneCopyManifest);

            var localization = TestServices.CreateLocalizationService();
            using var vm = new CustomWidgetViewModel(
                config,
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false);
            await vm.InitializeAsync();

            Assert.True(vm.AcceptsFileDrops);
            Assert.Equal("copy", vm.EffectiveDropMode);

            CustomWidgetNodeSpec dropZoneNode = vm.Manifest.View.Children.Single();
            bool handled = await vm.HandleDropZoneFilesAsync(
                [sourceFile],
                dropZoneNode.OnDropActions,
                forceCopyToPortableStore: false,
                nodeDropMode: dropZoneNode.StorageMode);

            Assert.True(handled);
            string[] attachments = Directory.Exists(store.AttachmentsDirectory)
                ? Directory.GetFiles(store.AttachmentsDirectory)
                : [];
            Assert.Single(attachments);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task HibernatedFileWidgetContentAdapter_ImportsFilesWithoutMaterializingViewAndTracksLaunchLatch()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Phase2HibernatedFile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string mappedDir = Directory.CreateDirectory(Path.Combine(tempRoot, "BoxFolder")).FullName;
            string externalDir = Directory.CreateDirectory(Path.Combine(tempRoot, "External")).FullName;
            string sourceFile = Path.Combine(externalDir, "report.txt");
            await File.WriteAllTextAsync(sourceFile, "hibernated capsule drop");

            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings"));
            settingsService.Settings.FileWidget.ManagedDropAction = SettingsService.ManagedDropActionCopy;
            var localization = new LocalizationService(settingsService);
            var fileService = new FileService();
            var organizer = TestOrganizerServices.Create(settingsService, fileService);
            var config = new WidgetConfig
            {
                Id = "file-hibernated-capsule",
                WidgetKind = WidgetKind.File,
                Name = "Work",
                MappedFolderPath = mappedDir
            };

            int viewFactoryCalls = 0;
            using var adapter = new FileWidgetContentAdapter(
                config,
                fileService,
                organizer,
                settingsService,
                localization,
                dispatcherQueue: null,
                viewFactory: _ =>
                {
                    viewFactoryCalls++;
                    throw new InvalidOperationException("Hibernated File adapter must not materialize FileSurfaceContent.");
                });

            await adapter.InitializeAsync();
            Assert.False(adapter.HasMaterializedView);
            Assert.Equal(0, viewFactoryCalls);

            // Without a prior shortcut launch, WasLaunchConsumedRecently MUST be false even while Surface is null.
            Assert.False(adapter.WasLaunchConsumedRecently());
            adapter.MarkNativeLaunchConsumed();
            Assert.True(adapter.WasLaunchConsumedRecently());

            int feedbackCount = 0;
            adapter.FeedbackRequested += (_, _) => feedbackCount++;

            bool imported = await adapter.ImportNativeDroppedFilesAsync(
                [sourceFile],
                containsTemporaryFiles: false,
                copyWhenMapped: true);

            Assert.True(imported);
            Assert.False(adapter.HasMaterializedView);
            Assert.Equal(0, viewFactoryCalls);
            Assert.Equal(1, feedbackCount);
            Assert.True(File.Exists(Path.Combine(mappedDir, "report.txt")));
            Assert.True(File.Exists(sourceFile));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void StackPopoverDropClaimTracker_ExternalDesktopDropDoesNotSelfCancelDeferredImport_AndBlocksDuplicateDelivery()
    {
        var tracker = new StackPopoverDropClaimTracker();
        string[] desktopPaths = [@"C:\Users\Public\Desktop\notes.xlsx"];

        tracker.OnNativeDragEnter();
        Assert.True(tracker.TryBeginNativeDropIntent(
            desktopPaths,
            gestureId: 24,
            nowTick64: 10_000,
            out PendingStackPopoverNativeDrop pendingDrop));
        Assert.False(pendingDrop.IsDeskBoxOwnedSource);
        Assert.Equal(1, tracker.PendingDropCount);

        // Legacy WM_DROPFILES arriving within 250ms of the same OLE drop is suppressed.
        Assert.True(tracker.ShouldSkipLegacyDropFiles(desktopPaths, nowTick64: 10_120));
        Assert.False(tracker.ShouldSkipLegacyDropFiles(desktopPaths, nowTick64: 10_350));

        // Deferred UI-thread import (left-click or right-click menu) MUST succeed and claim gesture 24.
        Assert.True(tracker.TryClaimDeferredNativeDrop(pendingDrop, desktopPaths));
        tracker.CompletePendingNativeDrop();
        Assert.Equal(0, tracker.PendingDropCount);

        // Duplicate delivery of the same gesture is rejected at both phases.
        Assert.False(tracker.TryClaimDeferredNativeDrop(pendingDrop, desktopPaths));
        Assert.False(tracker.TryBeginNativeDropIntent(
            desktopPaths,
            gestureId: 24,
            nowTick64: 10_050,
            out _));

        // Pre-resolved follow-up import from right-click menu (Unclaimed sentinel) is allowed through.
        Assert.True(tracker.TryClaimDeferredNativeDrop(
            PendingStackPopoverNativeDrop.Unclaimed,
            desktopPaths));

        // Subsequent external drag with higher gesture ID succeeds cleanly.
        tracker.OnNativeDragEnter();
        Assert.True(tracker.TryBeginNativeDropIntent(
            desktopPaths,
            gestureId: 25,
            nowTick64: 12_000,
            out PendingStackPopoverNativeDrop nextDrop));
        Assert.True(tracker.TryClaimDeferredNativeDrop(nextDrop, desktopPaths));
        tracker.CompletePendingNativeDrop();
    }

    [Fact]
    public void StackPopoverDropClaimTracker_XamlDropWinsWhetherClaimedBeforeOrAfterNativeDropIntent_EvenAfterDragRegistryEnds()
    {
        string[] draggedPaths = [Path.GetTempFileName()];
        const string sessionA = "session-xaml-first";
        const string sessionB = "session-native-intent-first";
        try
        {
            var tracker = new StackPopoverDropClaimTracker();

            // Case 1: XAML Drop runs before NativeDropTarget DropIntent.
            ActiveDeskBoxDragRegistry.Begin(sessionA, "widget-1", draggedPaths, fromStackPopover: false);
            tracker.OnNativeDragEnter();
            tracker.ObserveXamlDragSession(sessionA);
            tracker.MarkXamlDropClaimed(sessionA, maxActiveNativeGestureId: 17);
            Assert.False(tracker.TryBeginNativeDropIntent(
                draggedPaths,
                gestureId: 17,
                nowTick64: 20_000,
                out _));
            ActiveDeskBoxDragRegistry.End(sessionA);

            // Case 2: NativeDropTarget DropIntent runs FIRST, then XAML Drop + Items_DropCompleted
            // clear ActiveDeskBoxDragRegistry BEFORE Low-priority TryClaimDeferredNativeDrop runs.
            ActiveDeskBoxDragRegistry.Begin(sessionB, "widget-1", draggedPaths, fromStackPopover: false);
            tracker.OnNativeDragEnter();
            tracker.ObserveXamlDragSession(sessionB);
            Assert.True(tracker.TryBeginNativeDropIntent(
                draggedPaths,
                gestureId: 18,
                nowTick64: 21_000,
                out PendingStackPopoverNativeDrop deferredInternalDrop));
            Assert.True(deferredInternalDrop.IsDeskBoxOwnedSource);
            Assert.Equal(sessionB, deferredInternalDrop.MatchedSessionId);

            // XAML Drop lands while native target already reset ActiveGestureId to 0, then source completes.
            tracker.MarkXamlDropClaimed(sessionB, maxActiveNativeGestureId: 0);
            ActiveDeskBoxDragRegistry.End(sessionB);

            Assert.False(tracker.TryClaimDeferredNativeDrop(deferredInternalDrop, draggedPaths));
            tracker.CompletePendingNativeDrop();
            Assert.Equal(0, tracker.PendingDropCount);
        }
        finally
        {
            ActiveDeskBoxDragRegistry.End(sessionA);
            ActiveDeskBoxDragRegistry.End(sessionB);
            try
            {
                File.Delete(draggedPaths[0]);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ShouldAutoCloseSpringLoadedPopover_HoldsOpenAcrossPostReleaseDropWindowAndPendingImports_ThenClosesWhenCancelledOrDraggedAway()
    {
        long? outsideSince = null;
        long? releasedSince = null;

        // Hovering inside popover with mouse button held stays open.
        Assert.False(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: true,
            mouseButtonDown: true,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 1_000,
            ref outsideSince,
            ref releasedSince));
        Assert.Null(outsideSince);
        Assert.Null(releasedSince);

        // Hardware mouse-up over popover starts release grace instead of wiping DataContext immediately.
        Assert.False(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: true,
            mouseButtonDown: false,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 1_090,
            ref outsideSince,
            ref releasedSince));
        Assert.Equal(1_090, releasedSince);

        // If a native drop or right-click menu becomes pending during the grace window, timer stays open and resets.
        Assert.False(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: true,
            mouseButtonDown: false,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: true,
            nowTick: 1_500,
            ref outsideSince,
            ref releasedSince));
        Assert.Null(releasedSince);

        // If drag was Esc-cancelled and no drop arrived, popover closes after release grace expires.
        Assert.False(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: true,
            mouseButtonDown: false,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 2_000,
            ref outsideSince,
            ref releasedSince));
        Assert.True(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: true,
            mouseButtonDown: false,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 2_000 + FileSurfaceContent.StackSpringLoadedReleaseGraceMilliseconds,
            ref outsideSince,
            ref releasedSince));

        // Dragging away from popover & anchor while holding mouse button closes after outside grace (270ms).
        outsideSince = null;
        releasedSince = null;
        Assert.False(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: false,
            mouseButtonDown: true,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 3_000,
            ref outsideSince,
            ref releasedSince));
        Assert.False(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: false,
            mouseButtonDown: true,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 3_180,
            ref outsideSince,
            ref releasedSince));
        Assert.True(FileSurfaceContent.ShouldAutoCloseSpringLoadedPopover(
            insidePopoverOrAnchor: false,
            mouseButtonDown: true,
            nativeDragOver: false,
            hasPendingOrBlockingActivity: false,
            nowTick: 3_000 + FileSurfaceContent.StackSpringLoadedAutoCloseGraceMilliseconds,
            ref outsideSince,
            ref releasedSince));
    }
}
