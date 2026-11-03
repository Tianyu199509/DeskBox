using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

/// <summary>
/// Appends only high-frequency visual module toggles (<c>moduleList</c>) to the desktop
/// widget context menu, matching native <c>Glance</c> minimalism, plus a single
/// "Modify with AI" entry that opens the standalone Widget Workshop window.
/// Low-frequency parameters stay inside the Settings page.
/// </summary>
public static class CustomWidgetContextMenuBuilder
{
    public static void PopulateMenuItems(
        MenuFlyout flyout,
        CustomWidgetContentAdapter adapter,
        LocalizationService localization,
        Action<string>? openSettingsSection = null,
        Action<string>? openWidgetStudio = null)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(localization);
        _ = openSettingsSection;

        CustomWidgetViewModel vm = adapter.ViewModel;
        bool addedHeaderItems = false;

        if (openWidgetStudio is not null)
        {
            string widgetId = adapter.Config.Id;
            var modifyItem = new MenuFlyoutItem
            {
                Text = localization.T("CustomWidget.Menu.ModifyWithAi"),
                Icon = new FontIcon { Glyph = "\uE945" }
            };
            modifyItem.Click += (_, _) => openWidgetStudio(widgetId);
            flyout.Items.Add(modifyItem);
            addedHeaderItems = true;
        }

        if (vm.SecretSettingDefinitions.Count > 0)
        {
            var secretItem = new MenuFlyoutItem
            {
                Text = localization.T("CustomWidget.Menu.ConfigureSecrets"),
                Icon = new FontIcon { Glyph = "\uE8D7" }
            };
            secretItem.Click += async (_, _) =>
            {
                FrameworkElement? hostFe = (adapter.HasMaterializedView ? adapter.View : null) ?? (flyout.Target as FrameworkElement);
                if (hostFe?.XamlRoot is { } root)
                {
                    await CustomWidgetNativeRenderer.ShowSecretSettingsDialogAsync(root, hostFe.ActualTheme, vm);
                }
            };
            flyout.Items.Add(secretItem);
            addedHeaderItems = true;
        }

        CustomWidgetSettingDefinition? moduleSetting = vm.Manifest
            .ResolveLocalizedSettings(localization.CurrentCultureName)
            .FirstOrDefault(
                static s => string.Equals(s.Type, "moduleList", StringComparison.OrdinalIgnoreCase) &&
                            s.Options.Count > 1);

        if (moduleSetting is null)
        {
            if (addedHeaderItems)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            return;
        }

        string currentVal = vm.Settings.TryGetValue(moduleSetting.Key, out string? v)
            ? v
            : moduleSetting.DefaultValue;

        var subMenu = new MenuFlyoutSubItem
        {
            Text = localization.T("Glance.Display.Title"),
            Icon = new FontIcon { Glyph = "\uE7B3" }
        };

        IReadOnlyList<CustomWidgetModuleEntry> entries =
            CustomWidgetModuleListHelper.ParseEntries(currentVal, moduleSetting.Options);
        int enabledCount = entries.Count(static e => e.Enabled);
        string key = moduleSetting.Key;

        foreach (CustomWidgetModuleEntry entry in entries)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = entry.Label,
                IsChecked = entry.Enabled,
                IsEnabled = !entry.Enabled || enabledCount > 1
            };
            string moduleId = entry.Id;
            item.Click += async (_, _) =>
            {
                string latestRaw = vm.Settings.TryGetValue(key, out string? cur)
                    ? cur
                    : currentVal;
                string updated = CustomWidgetModuleListHelper.SetModuleEnabled(
                    latestRaw,
                    moduleSetting.Options,
                    moduleId,
                    item.IsChecked);
                await vm.SetSettingValueAsync(key, updated);
            };
            subMenu.Items.Add(item);
        }

        flyout.Items.Add(subMenu);
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    public static IReadOnlyList<CustomWidgetOptionSpec> ResolveEffectiveSettingOptions(
        CustomWidgetSettingDefinition setting,
        string? cultureName = null)
    {
        if (!string.Equals(setting.Key, "targetDrive", StringComparison.OrdinalIgnoreCase))
        {
            return setting.Options;
        }

        var combined = new List<CustomWidgetOptionSpec>(setting.Options);
        var seen = new HashSet<string>(
            setting.Options.Select(static o => o.Value),
            StringComparer.OrdinalIgnoreCase);

        foreach ((string driveKey, string label) in CustomWidgetSystemMetricsSource.GetAvailableFixedDrives(cultureName))
        {
            if (seen.Add(driveKey))
            {
                combined.Add(new CustomWidgetOptionSpec(driveKey, label));
            }
        }

        return combined;
    }
}
