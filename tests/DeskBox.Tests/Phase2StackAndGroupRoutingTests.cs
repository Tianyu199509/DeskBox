using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class Phase2StackAndGroupRoutingTests
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
          "schemaVersion": "1.0",
          "id": "custom.display.only",
          "name": "Clock Display",
          "view": {
            "type": "Text",
            "text": "Hello"
          }
        }
        """;
        Assert.False(CustomWidgetViewModel.DoesManifestJsonAcceptFileDrops(displayOnlyManifest));

        string rootOnDropManifest = """
        {
          "schemaVersion": "1.0",
          "id": "custom.root.drop",
          "name": "Inbox",
          "onDrop": {
            "accept": "files",
            "mode": "copy",
            "actions": [
              { "type": "set", "key": "lastFile", "value": "{{drop.path}}" }
            ]
          },
          "view": { "type": "Text", "text": "{{state.lastFile}}" }
        }
        """;
        Assert.True(CustomWidgetViewModel.DoesManifestJsonAcceptFileDrops(rootOnDropManifest));

        string dropZoneCopyManifest = """
        {
          "schemaVersion": "1.0",
          "id": "custom.dropzone.copy",
          "name": "DropZone Copy",
          "state": {
            "savedPath": "",
            "savedMode": ""
          },
          "view": {
            "type": "Column",
            "children": [
              {
                "type": "DropZone",
                "title": "Drop files here",
                "dropMode": "copy",
                "onDrop": [
                  { "type": "set", "key": "savedPath", "value": "{{drop.path}}" },
                  { "type": "set", "key": "savedMode", "value": "{{drop.mode}}" }
                ]
              }
            ]
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

            var settingsService = new SettingsService();
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
}
