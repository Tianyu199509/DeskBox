using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views.SettingsSections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void RefreshVisibleSettingsPageData()
    {
        UpdateSearchSettingsActivity();
        UpdateBackupSettingsActivity();
        if (_currentSettingsSection == "General")
        {
            RefreshAiSettingsEntrySummary();
        }
        if (_currentSettingsSection == "AppearanceDetail")
        {
            AppearanceDetailSection.OrganizationSection.Refresh();
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
            SyncManagedStorageQuickAccessToggleState();
            _ = ViewModel.RefreshQuickAccessStateAsync();
            _ = RefreshOrphanFoldersSummaryAsync();
        }
        if (_currentSettingsSection == "Interaction")
        {
            SyncCapsuleModeEntryCard();
            RefreshDisplaysEntrySummary();
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }
        if (_currentSettingsSection == "QuickCaptureSettings")
        {
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        }
        if (_currentSettingsSection == "SearchSettings" &&
            _settingsSectionElements.TryGetValue("SearchSettings", out FrameworkElement? search) &&
            search is SearchSettingsSection section)
        {
            section.RefreshFromSettings();
        }
        if (_currentSettingsSection == "CustomWidgetSettings" &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
            customEl is CustomWidgetSettingsSection customSection)
        {
            customSection.RefreshInstances();
        }
        if (_currentSettingsSection == "AiSettings" &&
            _settingsSectionElements.TryGetValue("AiSettings", out FrameworkElement? aiEl) &&
            aiEl is AiSettingsSection aiSection)
        {
            aiSection.RefreshState();
        }
        if (_currentSettingsSection == "ClipboardSettings" &&
            _settingsSectionElements.TryGetValue("ClipboardSettings", out FrameworkElement? clipboardEl) &&
            clipboardEl is ClipboardSettingsSection clipboardSection)
        {
            clipboardSection.RefreshFromSettings();
        }
        if (_currentSettingsSection is "MusicSettings" or "WeatherSettings")
        {
            SyncMusicWeatherFeatureEnableState(_currentSettingsSection);
        }
        if (_currentSettingsSection == "Maintenance")
        {
            _backupSettingsViewModel.RefreshLocalStatus();
            SyncMaintenanceEntryCards();
            _ = RefreshBackupSnapshotInventoryAsync();
        }
        if (_currentSettingsSection == "About")
        {
            ViewModel.RefreshDragDropPermissionDiagnostic();
            ViewModel.RefreshRuntimeDiagnostics();
        }
    }

    private void UpdateSearchSettingsActivity()
    {
        if (_settingsSectionElements.TryGetValue("SearchSettings", out FrameworkElement? element) &&
            element is SearchSettingsSection section)
        {
            section.SetActive(!_isClosed && IsVisibleToUser && _currentSettingsSection == "SearchSettings");
        }
    }

    private void UpdateBackupSettingsActivity()
    {
        if (!_isClosed && IsVisibleToUser && _currentSettingsSection == "Maintenance")
            _backupSettingsViewModel.Activate();
        else
            _backupSettingsViewModel.Deactivate();
    }

    private void RefreshLiveClipboardSettingsSection()
    {
        if (_isClosed)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshLiveClipboardSettingsSection);
            return;
        }

        if (_settingsSectionElements.TryGetValue("ClipboardSettings", out FrameworkElement? clipboardEl) &&
            clipboardEl is ClipboardSettingsSection { IsLoaded: true } clipboardSection)
        {
            clipboardSection.RefreshFromSettings();
        }
    }

    private FrameworkElement EnsureSettingsSectionCreated(string sectionTag)
    {
        if (_settingsSectionElements.TryGetValue(sectionTag, out FrameworkElement? existing))
        {
            return existing;
        }
        if (!ContentHost.Resources.TryGetValue(sectionTag + "SectionTemplate", out object resource) ||
            resource is not DataTemplate template)
        {
            throw new InvalidOperationException($"Settings section '{sectionTag}' is not registered.");
        }

        var stopwatch = Stopwatch.StartNew();
        FrameworkElement section = (FrameworkElement)template.LoadContent();
        _settingsSectionElements.Add(sectionTag, section);
        section.DataContext = ViewModel;
        // LoadContent is used outside an ItemsControl, so initialize the
        // template's generated compiled bindings with the actual view model.
        object compiledBindingsRoot = sectionTag == "FileStackSettings"
            ? _fileStackSettingsViewModel
            : sectionTag == "MusicSettings"
                ? _musicSettingsViewModel
                : sectionTag == "QuickCaptureSettings"
                    ? _quickCaptureSettingsViewModel
                    : sectionTag == "TodoSettings"
                        ? _todoSettingsViewModel
                        : ViewModel;

        XamlBindingHelper.GetDataTemplateComponent(section)?.ProcessBindings(compiledBindingsRoot, 0, 0, out _);

        switch (section)
        {
            case SearchSettingsSection searchSettings:
                searchSettings.Configure(_searchSettingsViewModel, _localizationService, _hotkeyActions, _hWnd);
                break;
            case FileWidgetSettingsSection fileSettings:
                fileSettings.FileStack = _fileStackSettingsViewModel;
                fileSettings.FeatureWidgets = _featureWidgetsSettingsViewModel;
                fileSettings.Interaction = _interactionSettingsViewModel;
                fileSettings.ManagedStorage = _managedStorageSettingsViewModel;
                fileSettings.FileDisplay = _fileDisplaySettingsViewModel;
                fileSettings.Appearance = _appearanceSettingsViewModel;
                fileSettings.AppActions = _appActions;
                fileSettings.OrganizationSection.Configure(_widgetActions, _appActions, _hWnd);
                break;
            case AppearanceSettingsSection appearanceSettings:
                appearanceSettings.ViewModel = ViewModel;
                RefreshSystemAppearanceStates();
                break;
            case GlanceWidgetSettingsSection glanceSettings:
                glanceSettings.Configure(_widgetActions, _hWnd);
                break;
            case CustomWidgetSettingsSection customWidgetSettings:
                customWidgetSettings.Configure(_widgetActions, _hWnd, _localizationService);
                customWidgetSettings.SubPageChanged -= OnCustomWidgetSubPageChanged;
                customWidgetSettings.SubPageChanged += OnCustomWidgetSubPageChanged;
                customWidgetSettings.NavigateToAiSettingsRequested -= OnNavigateToAiSettingsRequested;
                customWidgetSettings.NavigateToAiSettingsRequested += OnNavigateToAiSettingsRequested;
                break;
            case AiSettingsSection aiSettings:
                aiSettings.Configure(_widgetActions, _hWnd, _localizationService);
                aiSettings.AiConfigurationChanged -= AiSettingsSection_AiConfigurationChanged;
                aiSettings.AiConfigurationChanged += AiSettingsSection_AiConfigurationChanged;
                break;
            case DisplaySettingsSection displaySettings:
                displaySettings.Configure(_widgetActions, _appActions);
                break;
            case ClipboardSettingsSection clipboardSettings:
                clipboardSettings.Configure(_settingsService, _localizationService, _widgetActions, _dataActions, _hWnd, _hotkeyActions);
                clipboardSettings.ResetClipboardRequested -= OnFeatureSubPageWidgetReset;
                clipboardSettings.ResetClipboardRequested += OnFeatureSubPageWidgetReset;
                ViewModel.ClipboardSettingsRefreshRequested -= RefreshLiveClipboardSettingsSection;
                ViewModel.ClipboardSettingsRefreshRequested += RefreshLiveClipboardSettingsSection;
                break;
        }

        if (sectionTag == "MusicSettings")
        {
            section.DataContext = _musicSettingsViewModel;
            SyncMusicWeatherFeatureEnableState(sectionTag);
        }

        if (sectionTag == "Interaction")
        {
            section.DataContext = _interactionSettingsViewModel;
            if (CapsuleModeSection is { } capsuleSettings)
            {
                capsuleSettings.ViewModel = ViewModel;
                capsuleSettings.Performance = _performanceSettingsViewModel;
                capsuleSettings.DataContext = _capsuleSettingsViewModel;
            }
            if (WidgetGroupsEntryComboBox is { } widgetGroupsCombo)
            {
                widgetGroupsCombo.DataContext = _groupNavigationSettingsViewModel;
            }
            SyncCapsuleModeEntryCard(reloadOptions: true);
            RefreshDisplaysEntrySummary();
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }

        if (sectionTag == "QuickCaptureSettings")
        {
            section.DataContext = _quickCaptureSettingsViewModel;
            QuickCaptureAttachmentStorageModeComboBox.DataContext = _featureWidgetsSettingsViewModel;
        }

        if (sectionTag == "TodoSettings")
        {
            section.DataContext = _todoSettingsViewModel;
            TodoAttachmentStorageModeComboBox.DataContext = _featureWidgetsSettingsViewModel;
        }

        if (sectionTag == "WeatherSettings")
        {
            section.DataContext = _weatherSettingsViewModel;
            SyncMusicWeatherFeatureEnableState(sectionTag);
            _ = ViewModel.EnsureWeatherNearbyCitiesInitializedAsync();
        }

        if (sectionTag == "Maintenance")
        {
            section.DataContext = _backupSettingsViewModel;
            SyncMaintenanceEntryCards();
        }

        if (sectionTag == "About" && AboutDiagnosticsSubPagePanel is { } aboutDiagSubPage)
        {
            aboutDiagSubPage.DataContext = _backupSettingsViewModel;
        }

        if (sectionTag == "FileStackSettings")
        {
            section.DataContext = _fileStackSettingsViewModel;
        }

        if (sectionTag == "Appearance")
        {
            section.DataContext = _appearanceSettingsViewModel;
        }

        if (sectionTag == "AppearanceDetail")
        {
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
            SyncManagedStorageQuickAccessToggleState();
        }

        if (sectionTag == "WidgetGroups")
        {
            section.DataContext = _groupNavigationSettingsViewModel;
        }

        section.Loaded += DeferredSettingsSection_Loaded;
        if (sectionTag == "CustomWidgetSettings")
        {
            EnsureSettingsSectionCreated("FeatureWidgets");
            CustomWidgetHostPanel.Children.Add(section);
        }
        else
        {
            ContentHost.Children.Add(section);
        }
        App.Log(
            $"[SettingsPerf] Section created tag={sectionTag} " +
            $"createdSections={_settingsSectionElements.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return section;
    }

    private void DeferredSettingsSection_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isClosed || sender is not FrameworkElement section)
        {
            return;
        }
        section.Loaded -= DeferredSettingsSection_Loaded;
        ApplyToggleSwitchContentVisibility();
        CollectResponsiveRows(SettingsRoot);
        UpdateResponsiveLayout(GetWindowWidth());
    }

    private static FrameworkElement? FindSettingsSearchTargetWithLocation(
        DependencyObject root,
        string headerKey,
        HashSet<DependencyObject> visited,
        out SettingsExpander? owningExpander,
        out int expanderItemIndex)
    {
        owningExpander = null;
        expanderItemIndex = -1;
        if (!visited.Add(root))
        {
            return null;
        }
        if (root is FrameworkElement element &&
            string.Equals(Localized.GetHeaderKey(element), headerKey, StringComparison.Ordinal))
        {
            return element;
        }

        // Collapsed expander items already exist in the realized section but
        // are not necessarily visual children yet. Expand only the matching
        // branch so search can reveal a setting without opening every group.
        if (root is SettingsExpander expander)
        {
            for (int i = 0; i < expander.Items.Count; i++)
            {
                if (expander.Items[i] is DependencyObject item &&
                    FindSettingsSearchTargetWithLocation(
                        item,
                        headerKey,
                        visited,
                        out SettingsExpander? nestedExpander,
                        out int nestedIndex) is { } target)
                {
                    expander.IsExpanded = true;
                    owningExpander = nestedExpander ?? expander;
                    expanderItemIndex = nestedExpander is not null ? nestedIndex : i;
                    return target;
                }
            }
        }
        if (root is Expander { Content: DependencyObject expanderContent } nativeExpander &&
            FindSettingsSearchTargetWithLocation(
                expanderContent,
                headerKey,
                visited,
                out owningExpander,
                out expanderItemIndex) is { } expanderTarget)
        {
            nativeExpander.IsExpanded = true;
            return expanderTarget;
        }
        if (root is UserControl { Content: DependencyObject userControlContent } &&
            FindSettingsSearchTargetWithLocation(
                userControlContent,
                headerKey,
                visited,
                out owningExpander,
                out expanderItemIndex) is { } userControlTarget)
        {
            return userControlTarget;
        }
        if (root is ContentControl { Content: DependencyObject content } &&
            FindSettingsSearchTargetWithLocation(
                content,
                headerKey,
                visited,
                out owningExpander,
                out expanderItemIndex) is { } contentTarget)
        {
            return contentTarget;
        }
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (FindSettingsSearchTargetWithLocation(
                        child,
                        headerKey,
                        visited,
                        out owningExpander,
                        out expanderItemIndex) is { } target)
                {
                    return target;
                }
            }
        }
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            if (FindSettingsSearchTargetWithLocation(
                    VisualTreeHelper.GetChild(root, index),
                    headerKey,
                    visited,
                    out owningExpander,
                    out expanderItemIndex) is { } target)
            {
                return target;
            }
        }
        return null;
    }

    private async void MusicLocalLyricsBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                _musicSettingsViewModel.LocalLyricsFolder = folderPath;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSettings] Failed to pick local lyrics folder: {ex}");
        }
    }

    private void MusicLocalLyricsOpenButton_Click(object sender, RoutedEventArgs e)
    {
        string? folderPath = _musicSettingsViewModel.LocalLyricsFolder;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            Services.ShellLaunchService.FireAndForgetRevealInExplorer(folderPath);
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSettings] Failed to open local lyrics folder: {ex}");
        }
    }

    private void MusicLocalLyricsClearButton_Click(object sender, RoutedEventArgs e)
    {
        _musicSettingsViewModel.LocalLyricsFolder = string.Empty;
    }

    private void AiSettingsSection_AiConfigurationChanged(object? sender, EventArgs e)
    {
        if (!_isClosed)
        {
            RefreshAiSettingsEntrySummary();
        }
    }

    private void OnNavigateToAiSettingsRequested(object? sender, bool focusAgentHub)
    {
        if (_isClosed)
        {
            return;
        }

        if (focusAgentHub)
        {
            NavigateToSettingsSection("AiSettings:agent-hub");
        }
        else
        {
            NavigateToSettingsSection("AiSettings");
        }
    }


    private void OnCustomWidgetSubPageChanged(object? sender, EventArgs e)
    {
        if (_isClosed || !string.Equals(_currentSettingsSection, "CustomWidgetSettings", StringComparison.Ordinal))
        {
            return;
        }

        SyncFeatureWidgetsHubState("CustomWidgetSettings");
        PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
        if (TryGetSectionRoute("CustomWidgetSettings", out SettingsSectionRoute route))
        {
            SettingsNavigationView.IsBackButtonVisible =
                _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
                customEl is CustomWidgetSettingsSection { IsDetailViewOpen: true }
                    ? NavigationViewBackButtonVisible.Visible
                    : NavigationViewBackButtonVisible.Collapsed;
            UpdateBreadcrumb(route);
        }
    }

    private void AppearanceDetailSection_SubPageChanged(object? sender, EventArgs e)
    {
        if (_isClosed || !string.Equals(_currentSettingsSection, "AppearanceDetail", StringComparison.Ordinal))
        {
            return;
        }

        PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
        if (TryGetSectionRoute("AppearanceDetail", out SettingsSectionRoute route))
        {
            bool showBack = _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detailEl) &&
                detailEl is FileWidgetSettingsSection { IsDetailSubPageOpen: true };
            SettingsNavigationView.IsBackButtonVisible = showBack
                ? NavigationViewBackButtonVisible.Visible
                : NavigationViewBackButtonVisible.Collapsed;
            UpdateBreadcrumb(route);
        }
    }
}
