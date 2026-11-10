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
    public void ManagedStorage_DragOutEdits_WriteThroughNormalizeAndPersist()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        storage.DragOutAction = ManagedDropActions.Move;
        Assert.Equal(
            ManagedDropActions.Move,
            settings.Settings.FileWidget.ManagedDragOutAction);
        Assert.Equal(1, notified);

        // The coordinator normalizes unknown values to the safe default:
        // FollowWindows advertises no preferred effect, so a receiver can
        // never gain the Move token that would let it remove the source.
        storage.DragOutAction = "Nonsense";
        Assert.Equal(
            ManagedDropActions.FollowWindows,
            settings.Settings.FileWidget.ManagedDragOutAction);

        // Unchanged writes skip the redundant debounced save.
        int notifiedAfterWrite = notified;
        storage.DragOutAction = ManagedDropActions.FollowWindows;
        Assert.Equal(notifiedAfterWrite, notified);
    }

    [Fact]
    public void ManagedStorage_DragOutOptions_ListFollowWindowsFirstWithLocalizedNames()
    {
        (_, _, ManagedStorageSettingsViewModel storage) = CreateEditors(_root);

        SettingsOption[] options = [.. storage.AvailableDragOutActionOptions];
        Assert.Equal(3, options.Length);
        Assert.Equal(ManagedDropActions.FollowWindows, options[0].Value);
        Assert.Equal(ManagedDropActions.Copy, options[1].Value);
        Assert.Equal(ManagedDropActions.Move, options[2].Value);
        Assert.Equal("Settings.DropAction.System", options[0].DisplayName);
        Assert.Equal("Settings.DropAction.Copy", options[1].DisplayName);
        Assert.Equal("Settings.DropAction.Move", options[2].DisplayName);
    }

    [Fact]
    public void ManagedStorage_DragOutSync_RefreshesProjectionWithoutWritingBack()
    {
        (SettingsService settings, _, ManagedStorageSettingsViewModel storage) =
            CreateEditors(
                _root,
                static settings =>
                    settings.Settings.FileWidget.ManagedDragOutAction =
                        SettingsService.ManagedDragOutActionCopy);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        Assert.Equal(ManagedDropActions.Copy, storage.DragOutAction);

        settings.Settings.FileWidget.ManagedDragOutAction =
            SettingsService.ManagedDragOutActionMove;
        storage.SyncPresentation();

        Assert.Equal(ManagedDropActions.Move, storage.DragOutAction);
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

    [Fact]
    public void FileSections_BindToTheEditorsThroughSectionLevelDataContext()
    {
        string xaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string fileWidgetXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string bindable = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));
        string displayBridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/FileDisplay/FileDisplaySettingsViewModel.AotBindableProperties.cs"));
        string storageBridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/ManagedStorage/ManagedStorageSettingsViewModel.AotBindableProperties.cs"));
        string sync = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.SettingsSync.cs"));
        string smoke = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.AotManagedUiSmoke.cs"));

        // File display and managed storage merged into AppearanceDetail
        // (FileWidgetSettingsSection.xaml) with compiled {x:Bind} editor bridges.
        Assert.Contains("IsOn=\"{x:Bind FileDisplay.ShowImageThumbnails, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind FileDisplay.ShowFileExtensions, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{x:Bind FileDisplay.ShowFileExtensions, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{x:Bind FileDisplay.ShowFileExtensions, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind FileDisplay.HideShortcutExtensionWhenShowingFileExtensions, Mode=TwoWay}\"",
            fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind FileDisplay.HideShortcutArrowOverlay, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind FileDisplay.ShowListItemDetails, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind FileDisplay.ShowFileItemPathTooltips, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("Description=\"{x:Bind ManagedStorage.RootPath, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind ManagedStorage.DragBehaviorSummaryText, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind ManagedStorage.AvailableDragInBehaviorOptionItems, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{x:Bind ManagedStorage.DropAction, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{x:Bind ManagedStorage.AvailableDragOutBehaviorOptionItems, Mode=OneWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{x:Bind ManagedStorage.DragOutAction, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind ManagedStorage.DragOutModifierTipEnabled, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind ManagedStorage.DragOutResultHintEnabled, Mode=TwoWay}\"", fileWidgetXaml, StringComparison.Ordinal);
        // The drag-out card is named so the shell can append the Windows 10
        // note to its description: the content column is an Auto grid column,
        // and a wide note placed there overflows the card on high-scale
        // narrow windows instead of wrapping. The note text itself lives in
        // SettingsWindow.StorageAndUpdates.cs (description concatenation),
        // never as an element inside the content column. The modifier-tip
        // toggle stays a named element: the shell greys it out on Win10,
        // where external drops cannot carry a two-effect advertisement.
        Assert.Contains("x:Name=\"DragOutActionCard\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DragOutWin10Note", fileWidgetXaml + xaml, StringComparison.Ordinal);
        string shellStorage = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.StorageAndUpdates.cs"));
        Assert.Contains(
            "\"Settings.DragOutAction.Win10Note\"",
            shellStorage,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DragOutModifierTipToggle\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        foreach (string legacy in new[]
                 {
                     "{Binding SelectedManagedDropAction",
                     "{Binding AvailableManagedDropActionOptions",
                     "{Binding ManagedStorageRootPath",
                     "{Binding ShouldUnpinManagedStorageFromQuickAccess"
                 })
        {
            Assert.DoesNotContain(legacy, fileWidgetXaml + xaml, StringComparison.Ordinal);
        }

        // The deferred-section host assigns both typed editor bridges on
        // FileWidgetSettingsSection; standalone FileDisplaySettings /
        // FileStorageSettings routes are retired.
        Assert.Contains("fileSettings.FileDisplay = _fileDisplaySettingsViewModel;", deferred, StringComparison.Ordinal);
        Assert.Contains("fileSettings.ManagedStorage = _managedStorageSettingsViewModel;", deferred, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FileDisplaySettings\"", deferred, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FileStorageSettings\"", deferred, StringComparison.Ordinal);

        // The shell bridge drops the thirteen file-domain entries; the
        // editors carry their own NativeAOT custom-property bridges.
        foreach (string name in new[]
                 {
                     "ShowFileExtensions",
                     "HideShortcutExtensionWhenShowingFileExtensions",
                     "HideShortcutArrowOverlay",
                     "ShowImageFilesAsIcons",
                     "ShowListItemDetails",
                     "ShowFileItemPathTooltips",
                     "SelectedManagedDropAction",
                     "AvailableManagedDropActionOptions",
                     "ManagedStorageRootPath",
                     "QuickAccessStatusText",
                     "CanInvokeQuickAccessAction",
                     "PinQuickAccessButtonText",
                     "PinQuickAccessToolTipText"
                 })
        {
            Assert.DoesNotContain($"nameof({name})", bindable, StringComparison.Ordinal);
        }

        Assert.Contains("#if DESKBOX_NATIVE_AOT", displayBridge, StringComparison.Ordinal);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", displayBridge, StringComparison.Ordinal);
        foreach (string name in new[]
                 {
                     "HideShortcutArrowOverlay",
                     "HideShortcutExtensionWhenShowingFileExtensions",
                     "ShowFileExtensions",
                     "ShowFileItemPathTooltips",
                     "ShowImageFilesAsIcons",
                     "ShowImageThumbnails",
                     "ShowListItemDetails"
                 })
        {
            Assert.Contains($"nameof({name})", displayBridge, StringComparison.Ordinal);
        }

        Assert.Contains("#if DESKBOX_NATIVE_AOT", storageBridge, StringComparison.Ordinal);
        foreach (string name in new[]
                 {
                     "AvailableDragInBehaviorOptionItems",
                     "AvailableDragOutActionOptions",
                     "AvailableDragOutBehaviorOptionItems",
                     "AvailableDropActionOptions",
                     "CanInvokeQuickAccessAction",
                     "DragBehaviorSummaryText",
                     "DragOutAction",
                     "DragOutModifierTipEnabled",
                     "DragOutResultHintEnabled",
                     "DropAction",
                     "PinQuickAccessButtonText",
                     "PinQuickAccessToolTipText",
                     "QuickAccessStatusText",
                     "RootPath"
                 })
        {
            Assert.Contains($"nameof({name})", storageBridge, StringComparison.Ordinal);
        }

        // External refresh paths re-sync both editor projections and
        // re-localize the storage option list instead of assigning facade
        // state.
        Assert.Contains("_fileDisplaySettings.SyncPresentation();", sync, StringComparison.Ordinal);
        Assert.Contains("_managedStorageSettings.SyncPresentation();", sync, StringComparison.Ordinal);
        Assert.Contains("_managedStorageSettings.RefreshLocalization();", sync, StringComparison.Ordinal);

        // The AOT persistence smoke toggles the file-display editor's real
        // binding surface instead of the retired shell facade.
        Assert.Contains("settingsWindow.FileDisplaySettings", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsViewModel.ShowFileExtensions", smoke, StringComparison.Ordinal);
    }
}
