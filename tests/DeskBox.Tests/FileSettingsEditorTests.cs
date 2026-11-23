using DeskBox.Contracts;
using DeskBox.Features.FileDisplay;
using DeskBox.Features.ManagedStorage;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Second copy batch of the settings-shell binding-facade retirement: the
/// file-display section and the managed-storage section are re-bound to their
/// editors through section-level DataContext switches, exactly like the
/// pilot's music section and the interaction copy batch. These tests pin the
/// editors' behavior (read snapshot projection, write-through, no write-back
/// on external sync, pushed quick-access presentation, root-path commit,
/// localization refresh) and the migration pattern itself (XAML paths, AOT
/// bridges, window wiring, facade removal) so later batches can keep copying
/// the shape.
/// </summary>
public sealed class FileSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, FileDisplaySettingsViewModel Display, ManagedStorageSettingsViewModel Storage)
        CreateEditors(string root, Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        return (
            settings,
            new FileDisplaySettingsViewModel(new FileDisplaySettingsCoordinator(settings)),
            new ManagedStorageSettingsViewModel(
                new ManagedStorageSettingsCoordinator(settings),
                PassthroughLocalize));
    }

    [Fact]
    public void Constructor_ProjectsPersistedPresentationFromTheReadSnapshot()
    {
        (_, FileDisplaySettingsViewModel display, ManagedStorageSettingsViewModel storage) =
            CreateEditors(
                _root,
                settings =>
                {
                    FileWidgetSettingsSlice fileWidget = settings.Settings.FileWidget;
                    fileWidget.ShowFileExtensions = true;
                    fileWidget.HideShortcutExtensionWhenShowingFileExtensions = false;
                    fileWidget.HideShortcutArrowOverlay = true;
                    fileWidget.ShowImageFilesAsIcons = true;
                    fileWidget.ShowListItemDetails = true;
                    fileWidget.ShowFileItemPathTooltips = false;
                    fileWidget.ManagedDropAction = SettingsService.ManagedDropActionFollowWindows;
                });

        Assert.True(display.ShowFileExtensions);
        Assert.False(display.HideShortcutExtensionWhenShowingFileExtensions);
        Assert.True(display.HideShortcutArrowOverlay);
        Assert.True(display.ShowImageFilesAsIcons);
        Assert.True(display.ShowListItemDetails);
        Assert.False(display.ShowFileItemPathTooltips);
        Assert.Equal(ManagedDropActions.FollowWindows, storage.DropAction);
        Assert.Equal(
            SettingsService.NormalizeManagedStorageRootPath(
                SettingsService.GetDefaultManagedStorageRootPath()),
            storage.RootPath,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadSnapshot_NormalizesDropActionAndRootPath()
    {
        (_, FileDisplaySettingsViewModel _, ManagedStorageSettingsViewModel storage) =
            CreateEditors(
                _root,
                settings =>
                {
                    settings.Settings.FileWidget.ManagedDropAction = "Nonsense";
                    settings.Settings.FileWidget.DefaultManagedStorageRootPath = " ";
                });

        Assert.Equal(ManagedDropActions.Move, storage.DropAction);
        Assert.Equal(
            SettingsService.GetDefaultManagedStorageRootPath(),
            storage.RootPath,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileDisplay_UserEdits_WriteThroughTheCoordinatorAndPersist()
    {
        (SettingsService settings, FileDisplaySettingsViewModel display, _) = CreateEditors(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        display.ShowFileExtensions = true;
        display.HideShortcutExtensionWhenShowingFileExtensions = false;
        display.HideShortcutArrowOverlay = false;
        display.ShowImageFilesAsIcons = true;
        display.ShowListItemDetails = true;
        display.ShowFileItemPathTooltips = false;

        FileWidgetSettingsSlice fileWidget = settings.Settings.FileWidget;
        Assert.True(fileWidget.ShowFileExtensions);
        Assert.False(fileWidget.HideShortcutExtensionWhenShowingFileExtensions);
        Assert.False(fileWidget.HideShortcutArrowOverlay);
        Assert.True(fileWidget.ShowImageFilesAsIcons);
        Assert.True(fileWidget.ShowListItemDetails);
        Assert.False(fileWidget.ShowFileItemPathTooltips);
        Assert.Equal(6, notified);
    }

    [Fact]
    public void ManagedStorage_UserEdits_WriteThroughTheCoordinatorAndPersist()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        storage.DropAction = ManagedDropActions.Copy;
        Assert.Equal(ManagedDropActions.Copy, settings.Settings.FileWidget.ManagedDropAction);
        int notifiedAfterWrite = notified;

        // Unchanged writes skip the redundant debounced save.
        storage.DropAction = ManagedDropActions.Copy;
        Assert.Equal(notifiedAfterWrite, notified);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void ManagedStorage_DragOutEdits_PersistIndependentlyWithoutClobberingDropAction()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // Editing DragOutAction independently leaves ManagedDropAction intact.
        storage.DragOutAction = ManagedDropActions.Copy;
        Assert.Equal(ManagedDropActions.Move, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(ManagedDropActions.Copy, settings.Settings.FileWidget.ManagedDragOutAction);
        Assert.True(storage.IsDragOutCustomized);
        Assert.Equal(1, notified);

        // Editing the unified DropAction selector always re-synchronizes BOTH
        // ManagedDropAction and ManagedDragOutAction so no hidden split state remains
        // now that the redundant inner DragOut dropdown card has been removed.
        storage.DropAction = ManagedDropActions.FollowWindows;
        Assert.Equal(ManagedDropActions.FollowWindows, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(ManagedDropActions.FollowWindows, settings.Settings.FileWidget.ManagedDragOutAction);
        Assert.Equal(ManagedDropActions.FollowWindows, storage.DragOutAction);
        Assert.False(storage.IsDragOutCustomized);
        Assert.Equal(2, notified);

        // Unknown DragOutAction values normalize to canonical Move without clobbering DropAction.
        storage.DragOutAction = "Nonsense";
        Assert.Equal(ManagedDropActions.FollowWindows, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(ManagedDropActions.Move, settings.Settings.FileWidget.ManagedDragOutAction);

        // Unchanged writes skip the redundant debounced save.
        int notifiedAfterWrite = notified;
        storage.DragOutAction = ManagedDropActions.Move;
        Assert.Equal(notifiedAfterWrite, notified);
    }

    [Fact]
    public void ManagedStorage_DropActionEdit_KeepsDragOutLinkedWhenPreviouslyInSync()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        Assert.False(storage.IsDragOutCustomized);

        storage.DropAction = ManagedDropActions.Copy;
        Assert.Equal(ManagedDropActions.Copy, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(ManagedDropActions.Copy, settings.Settings.FileWidget.ManagedDragOutAction);
        Assert.Equal(ManagedDropActions.Copy, storage.DragOutAction);
        Assert.False(storage.IsDragOutCustomized);
    }

    [Fact]
    public void ManagedStorage_DragOutOptions_MatchUnifiedDropActionOptionsWithLocalizedNames()
    {
        (_, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);

        SettingsOption[] options = [.. storage.AvailableDragOutActionOptions];
        Assert.Equal(3, options.Length);
        Assert.Equal(ManagedDropActions.Move, options[0].Value);
        Assert.Equal(ManagedDropActions.Copy, options[1].Value);
        Assert.Equal(ManagedDropActions.FollowWindows, options[2].Value);
        Assert.Equal("Settings.DropAction.Move", options[0].DisplayName);
        Assert.Equal("Settings.DropAction.Copy", options[1].DisplayName);
        Assert.Equal("Settings.DropAction.System", options[2].DisplayName);
    }

    [Fact]
    public void ManagedStorage_DragOutSync_RefreshesIndependentProjectionWithoutWritingBack()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) =
            CreateEditors(
                _root,
                static settings =>
                {
                    settings.Settings.FileWidget.ManagedDropAction =
                        SettingsService.ManagedDropActionMove;
                    settings.Settings.FileWidget.ManagedDragOutAction =
                        SettingsService.ManagedDragOutActionCopy;
                });
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        Assert.Equal(ManagedDropActions.Move, storage.DropAction);
        Assert.Equal(ManagedDropActions.Copy, storage.DragOutAction);
        Assert.True(storage.IsDragOutCustomized);

        settings.Settings.FileWidget.ManagedDragOutAction =
            SettingsService.ManagedDragOutActionFollowWindows;
        storage.SyncPresentation();

        Assert.Equal(ManagedDropActions.Move, storage.DropAction);
        Assert.Equal(ManagedDropActions.FollowWindows, storage.DragOutAction);
        Assert.True(storage.IsDragOutCustomized);
        Assert.Equal(0, notified);
    }

    [Fact]
    public void ExternalSync_RefreshesTheProjectionWithoutWritingBack()
    {
        (SettingsService settings, FileDisplaySettingsViewModel display, ManagedStorageSettingsViewModel storage) =
            CreateEditors(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        FileWidgetSettingsSlice fileWidget = settings.Settings.FileWidget;
        fileWidget.ShowFileExtensions = true;
        fileWidget.ShowImageFilesAsIcons = true;
        fileWidget.ManagedDropAction = SettingsService.ManagedDropActionCopy;

        int notifiedBeforeSync = notified;
        display.SyncPresentation();
        storage.SyncPresentation();

        Assert.True(display.ShowFileExtensions);
        Assert.True(display.ShowImageFilesAsIcons);
        Assert.Equal(ManagedDropActions.Copy, storage.DropAction);
        Assert.Equal(notifiedBeforeSync, notified);
    }

    [Fact]
    public void CommitRootPath_NormalizesPersistsAndReProjects()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        string newPath = Path.Combine(_root, "committed-storage");

        string normalizedPath = storage.CommitRootPath(newPath);

        Assert.Equal(
            SettingsService.NormalizeManagedStorageRootPath(newPath),
            normalizedPath,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(storage.RootPath, normalizedPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            normalizedPath,
            settings.Settings.FileWidget.DefaultManagedStorageRootPath,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuickAccessPush_UpdatesThePresentationWithoutTouchingPersistence()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        storage.UpdateQuickAccessPresentation(new QuickAccessPresentationSettings(
            CanInvoke: false,
            ShouldUnpin: true,
            StatusText: "pinned",
            ButtonText: "unpin",
            ToolTipText: "unpin tooltip"));

        Assert.False(storage.CanInvokeQuickAccessAction);
        Assert.True(storage.ShouldUnpinQuickAccessAction);
        Assert.Equal("pinned", storage.QuickAccessStatusText);
        Assert.Equal("unpin", storage.PinQuickAccessButtonText);
        Assert.Equal("unpin tooltip", storage.PinQuickAccessToolTipText);
        Assert.Equal(0, notified);

        storage.UpdateQuickAccessPresentation(new QuickAccessPresentationSettings(
            CanInvoke: true,
            ShouldUnpin: false,
            StatusText: "not pinned",
            ButtonText: "pin",
            ToolTipText: "pin tooltip"));
        Assert.True(storage.CanInvokeQuickAccessAction);
        Assert.False(storage.ShouldUnpinQuickAccessAction);
        Assert.Equal(0, notified);
    }

    [Fact]
    public void Options_ListCanonicalValuesWithLocalizedNames()
    {
        (_, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);

        SettingsOption[] options = [.. storage.AvailableDropActionOptions];
        Assert.Equal(3, options.Length);
        Assert.Equal(ManagedDropActions.Move, options[0].Value);
        Assert.Equal(ManagedDropActions.Copy, options[1].Value);
        Assert.Equal(ManagedDropActions.FollowWindows, options[2].Value);
        Assert.Equal("Settings.DropAction.Move", options[0].DisplayName);
        Assert.Equal("Settings.DropAction.Copy", options[1].DisplayName);
        Assert.Equal("Settings.DropAction.System", options[2].DisplayName);
    }

    [Fact]
    public void RefreshLocalization_RebuildsTheOptionsAndNotifies()
    {
        (_, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        int notified = 0;
        storage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ManagedStorageSettingsViewModel.AvailableDropActionOptions))
            {
                notified++;
            }
        };

        storage.RefreshLocalization();
        Assert.Equal(1, notified);
        Assert.Equal(ManagedDropActions.FollowWindows, storage.AvailableDropActionOptions[2].Value);

        var relabeled = new ManagedStorageSettingsViewModel(
            new ManagedStorageSettingsCoordinator(
                new SettingsService(Path.Combine(_root, "second"))),
            static key => key == "Settings.DropAction.Move" ? "zh:移动" : key);
        Assert.Equal("zh:移动", relabeled.AvailableDropActionOptions[0].DisplayName);
    }

    [Fact]
    public void SettingsService_DropActionConstants_AliasTheContractCanonicalValues()
    {
        Assert.Equal(ManagedDropActions.Copy, SettingsService.ManagedDropActionCopy);
        Assert.Equal(ManagedDropActions.Move, SettingsService.ManagedDropActionMove);
        Assert.Equal(ManagedDropActions.FollowWindows, SettingsService.ManagedDropActionFollowWindows);
    }

    [Fact]
    public void SettingsShell_NoLongerExposesTheFileDomainCompatFacade()
    {
        System.Reflection.PropertyInfo[] properties = typeof(ViewModels.SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        string[] removed =
        [
            "ShowFileExtensions",
            "HideShortcutExtensionWhenShowingFileExtensions",
            "HideShortcutArrowOverlay",
            "ShowImageFilesAsIcons",
            "ShowListItemDetails",
            "ShowFileItemPathTooltips",
            "SelectedManagedDropAction",
            "AvailableManagedDropActionOptions",
            "AvailableManagedDropActionDisplayNames",
            "ManagedStorageRootPath",
            "ManagedStorageQuickAccessPinState",
            "IsQuickAccessBusy",
            "CanInvokeQuickAccessAction",
            "QuickAccessStatusText",
            "PinQuickAccessButtonText",
            "PinQuickAccessToolTipText",
            "ShouldUnpinManagedStorageFromQuickAccess"
        ];
        foreach (string name in removed)
        {
            Assert.DoesNotContain(properties, property => property.Name == name);
        }
    }

}
