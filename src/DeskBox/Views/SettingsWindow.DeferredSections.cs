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
        if (_currentSettingsSection is "AppearanceDetail" or "FileStorageSettings")
        {
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
            _ = ViewModel.RefreshQuickAccessStateAsync();
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
        XamlBindingHelper.GetDataTemplateComponent(section)?.ProcessBindings(ViewModel, 0, 0, out _);

        switch (section)
        {
            case FileWidgetSettingsSection fileSettings:
                fileSettings.ViewModel = ViewModel;
                break;
            case CapsuleModeSettingsSection capsuleSettings:
                capsuleSettings.ViewModel = ViewModel;
                break;
            case GlanceWidgetSettingsSection glanceSettings:
                glanceSettings.SetOwnerWindow(_hWnd);
                break;
        }

        if (sectionTag == "FileStorageSettings")
        {
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
        }
        if (sectionTag == "InteractionWindowSettings")
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }

        section.Loaded += DeferredSettingsSection_Loaded;
        ContentHost.Children.Add(section);
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
