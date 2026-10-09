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
            : ViewModel;
        XamlBindingHelper.GetDataTemplateComponent(section)?.ProcessBindings(compiledBindingsRoot, 0, 0, out _);

        switch (section)
        {
            case SearchSettingsSection searchSettings:
                searchSettings.Configure(_searchSettingsViewModel, _localizationService, _hWnd);
                break;
            case FileWidgetSettingsSection fileSettings:
                fileSettings.FileStack = _fileStackSettingsViewModel;
                fileSettings.FeatureWidgets = _featureWidgetsSettingsViewModel;
                fileSettings.Interaction = _interactionSettingsViewModel;
                break;
            case CapsuleModeSettingsSection capsuleSettings:
                capsuleSettings.ViewModel = ViewModel;
                break;
            case GlanceWidgetSettingsSection glanceSettings:
                glanceSettings.SetOwnerWindow(_hWnd);
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

        // The appearance family (main section plus the material, density,
        // window and animation subsections) binds through the appearance
        // editor; {Binding} markup resolves through its generated custom
        // property provider under Native AOT.
        if (sectionTag is "Appearance" or
            "AppearanceMaterialSettings" or
            "AppearanceDensitySettings" or
            "AppearanceWindowSettings" or
            "AppearanceAnimationSettings")
        {
            section.DataContext = _appearanceSettingsViewModel;
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
}
