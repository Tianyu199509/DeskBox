using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
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
        if (_currentSettingsSection is "AppearanceDetail" or "FileStorageSettings")
        {
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
        }
        if (_currentSettingsSection is "Interaction" or "Advanced")
        {
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
        if (!_isClosed && IsVisibleToUser && _currentSettingsSection == "CloudBackupSettings")
            _backupSettingsViewModel.Activate();
        else
            _backupSettingsViewModel.Deactivate();
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
        // The file-stack template keeps its compiled x:Bind surface typed to
        // the section editor (batch 45), so its bindings initialize with the
        // editor instead of the shell view model.
        object compiledBindingsRoot = sectionTag == "FileStackSettings"
            ? _fileStackSettingsViewModel
            : sectionTag == "MusicSettings"
                ? _musicSettingsViewModel
                : sectionTag == "InteractionWindowSettings"
                    ? _interactionSettingsViewModel
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
                fileSettings.AppActions = _appActions;
                break;
            case AppearanceSettingsSection appearanceSettings:
                appearanceSettings.ViewModel = ViewModel;
                break;
            case CapsuleModeSettingsSection capsuleSettings:
                capsuleSettings.ViewModel = ViewModel;
                break;
            case GlanceWidgetSettingsSection glanceSettings:
                glanceSettings.Configure(_widgetActions, _hWnd);
                break;
            case CustomWidgetSettingsSection customWidgetSettings:
                customWidgetSettings.Configure(_widgetActions, _hWnd);
                customWidgetSettings.SubPageChanged -= OnCustomWidgetSubPageChanged;
                customWidgetSettings.SubPageChanged += OnCustomWidgetSubPageChanged;
                customWidgetSettings.NavigateToAiSettingsRequested -= OnNavigateToAiSettingsRequested;
                customWidgetSettings.NavigateToAiSettingsRequested += OnNavigateToAiSettingsRequested;
                break;
            case AiSettingsSection aiSettings:
                aiSettings.Configure(_widgetActions, _hWnd);
                aiSettings.NavigateToCustomWidgetRequested -= OnAiNavigateToCustomWidgetRequested;
                aiSettings.NavigateToCustomWidgetRequested += OnAiNavigateToCustomWidgetRequested;
                break;
            case DisplaySettingsSection displaySettings:
                displaySettings.Configure(_widgetActions, _appActions);
                break;
            case DesktopOrganizationSettingsSection organizationSettings:
                organizationSettings.Configure(_widgetActions, _appActions, _hWnd);
                organizationSettings.SubPageChanged -= OnDesktopOrganizationSubPageChanged;
                organizationSettings.SubPageChanged += OnDesktopOrganizationSubPageChanged;
                break;
            case ClipboardSettingsSection clipboardSettings:
                clipboardSettings.Configure(_settingsService, _localizationService, _widgetActions, _dataActions, _hWnd);
                break;
        }

        // Pilot pattern for retiring the shell binding facade: sections whose
        // editor owns the binding surface get the editor as their DataContext,
        // overriding the shell view model set above. {Binding} markup resolves
        // through the editor's generated custom-property provider under
        // Native AOT, so no per-property shell bridge is needed anymore.
        if (sectionTag == "MusicSettings")
        {
            section.DataContext = _musicSettingsViewModel;
        }

        if (sectionTag is "Interaction" or "InteractionWindowSettings")
        {
            section.DataContext = _interactionSettingsViewModel;
        }

        // The Quick Capture section binds through the Quick Capture editor
        // (batch 46): {Binding} markup resolves through its generated custom
        // property provider under Native AOT. The clipboard-diagnostics and
        // image-cache lines are pushed in by the shell (host services).
        if (sectionTag == "QuickCaptureSettings")
        {
            section.DataContext = _quickCaptureSettingsViewModel;
        }

        // The Todo section binds through the Todo editor (batch 47):
        // {Binding} markup resolves through its generated custom property
        // provider under Native AOT; the batch-14 default-filter/tab
        // linkage renders through the coordinator's snapshot
        // re-projection.
        if (sectionTag == "TodoSettings")
        {
            section.DataContext = _todoSettingsViewModel;
        }

        // The Weather section binds through the Weather editor (batch 48):
        // {Binding} markup resolves through its generated custom property
        // provider under Native AOT. The city-search state machine (the
        // search service, the Windows location lookup, the debounced
        // cancellation) stays on the shell and pushes the suggestion list
        // and the location status into the editor; the editor answers
        // user location-mode edits with the AutoLocationUserChanged event.
        if (sectionTag == "WeatherSettings")
        {
            section.DataContext = _weatherSettingsViewModel;
        }

        // The backup family (local backups, cloud backups and the
        // compatibility-diagnostics section, batch 49) binds through the
        // backup editor: {Binding} markup resolves through its generated
        // custom property provider under Native AOT. The batch-4 visit
        // state machine (endpoint-scoped reads, generations, cancellation)
        // stays inside the editor; the manual backup/restore/delete flows
        // and the drag-drop/runtime diagnostics computation stay on the
        // shell (host services) and push their results into the editor.
        if (sectionTag is "BackupRestoreSettings" or
            "CloudBackupSettings" or
            "CompatibilityDiagnosticsSettings")
        {
            section.DataContext = _backupSettingsViewModel;
        }

        // The performance section binds through the performance editor
        // (batch 50): {Binding} markup resolves through its generated custom
        // property provider under Native AOT. The section's writes (the
        // legacy eleven custom-mode lambda facade writes, the preset write
        // and the quiescence trim switch) go through the performance
        // coordinator; the idle and immediate-hidden trim switches persist
        // through the interaction coordinator from the editor's handlers.
        if (sectionTag == "PerformanceSettings")
        {
            section.DataContext = _performanceSettingsViewModel;
        }

        // The file-stack section binds through the file-stack editor
        // (batch 45): {Binding} markup resolves through its generated custom
        // property provider under Native AOT, and the template's compiled
        // x:Bind paths (open-mode combo and the rule list) are typed to the
        // editor as well.
        if (sectionTag == "FileStackSettings")
        {
            section.DataContext = _fileStackSettingsViewModel;
        }

        if (sectionTag == "FileDisplaySettings")
        {
            section.DataContext = _fileDisplaySettingsViewModel;
        }

        if (sectionTag == "FileStorageSettings")
        {
            section.DataContext = _managedStorageSettingsViewModel;
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
            RefreshDragOutWin10State();
        }

        // The appearance family (main section plus the advanced material
        // subsection) binds through the appearance editor; {Binding} markup
        // resolves through its generated custom property provider under
        // Native AOT.
        if (sectionTag is "Appearance" or
            "AppearanceMaterialSettings")
        {
            section.DataContext = _appearanceSettingsViewModel;
            if (sectionTag == "AppearanceMaterialSettings")
            {
                // The section hosts the system visual-effect status cards;
                // creation-time refresh covers state changed while the
                // window sat closed (activation only fires on focus).
                RefreshSystemAppearanceStates();
            }
        }

        // The WidgetGroups section binds through the group-navigation
        // editor (batch 44); the four defaults are the editor's own persisted
        // surface and the existing-groups projection is pushed in by the
        // shell's group-editing state machine.
        if (sectionTag == "WidgetGroups")
        {
            section.DataContext = _groupNavigationSettingsViewModel;
        }

        // The capsule family (main capsule section plus the overrides
        // subsection) binds through the capsule editor (batch 44); the
        // override-list projection is pushed in by the shell's override state
        // machine.
        if (sectionTag is "CapsuleMode" or
            "CapsuleOverridesSettings")
        {
            section.DataContext = _capsuleSettingsViewModel;
        }

        if (sectionTag == "FeatureWidgets")
        {
            Segmented hubSegmented = FeatureWidgetsHubSegmented;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(hubSegmented);
            hubSegmented.Loaded += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(hubSegmented);
            hubSegmented.ActualThemeChanged += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(hubSegmented);
        }

        if (sectionTag == "InteractionWindowSettings")
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }

        section.Loaded += DeferredSettingsSection_Loaded;
        if (sectionTag == "FileStorageSettings" &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? parent) &&
            parent is FileWidgetSettingsSection fileWidgetSection)
        {
            fileWidgetSection.AttachManagedStorageSection(section);
        }
        else if (sectionTag == "CustomWidgetSettings")
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

    private static FrameworkElement? FindSettingsSearchTarget(
        DependencyObject root,
        string headerKey,
        HashSet<DependencyObject> visited)
    {
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
            foreach (DependencyObject item in expander.Items.OfType<DependencyObject>())
            {
                if (FindSettingsSearchTarget(item, headerKey, visited) is { } target)
                {
                    expander.IsExpanded = true;
                    return target;
                }
            }
        }
        if (root is Expander { Content: DependencyObject expanderContent } nativeExpander &&
            FindSettingsSearchTarget(expanderContent, headerKey, visited) is { } expanderTarget)
        {
            nativeExpander.IsExpanded = true;
            return expanderTarget;
        }
        if (root is ContentControl { Content: DependencyObject content } &&
            FindSettingsSearchTarget(content, headerKey, visited) is { } contentTarget)
        {
            return contentTarget;
        }
        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (FindSettingsSearchTarget(child, headerKey, visited) is { } target)
                {
                    return target;
                }
            }
        }
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            if (FindSettingsSearchTarget(VisualTreeHelper.GetChild(root, index), headerKey, visited) is { } target)
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

    private void MusicLocalLyricsClearButton_Click(object sender, RoutedEventArgs e)
    {
        _musicSettingsViewModel.LocalLyricsFolder = string.Empty;
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

    private void OnAiNavigateToCustomWidgetRequested(object? sender, string widgetId)
    {
        if (_isClosed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            EnsureSettingsSectionCreated("CustomWidgetSettings");
            CustomWidgetSettingsSection.SelectWidget(widgetId);
        }

        NavigateToSettingsSection("CustomWidgetSettings");
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

    private void OnDesktopOrganizationSubPageChanged(object? sender, EventArgs e)
    {
        if (_isClosed || !string.Equals(_currentSettingsSection, "DesktopOrganizationSettings", StringComparison.Ordinal))
        {
            return;
        }

        PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
        if (TryGetSectionRoute("DesktopOrganizationSettings", out SettingsSectionRoute route))
        {
            UpdateBreadcrumb(route);
        }
    }
}
