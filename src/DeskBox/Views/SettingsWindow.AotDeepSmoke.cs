#if DESKBOX_NATIVE_AOT
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    internal async Task<AotDeepSettingsSnapshot> ExerciseAotDeepReadOnlySettingsAsync()
    {
        string[] deepSettingsRoutes =
        [
            "AppearanceDetail",
            "CapsuleMode",
            "WidgetGroups",
            "FileDisplaySettings",
            "ManagedStorage",
            "FileStackSettings",
            "DesktopOrganizationSettings",
            "QuickCaptureSettings",
            "TodoSettings",
            "MusicSettings",
            "WeatherSettings",
            "GlanceSettings",
            "SearchSettings",
            "AppearanceMaterialSettings",
            "AppearanceDensitySettings",
            "AppearanceWindowSettings",
            "AppearanceAnimationSettings",
            "CapsuleBehaviorSettings",
            "CapsuleArrangementSettings",
            "CapsuleAnimationSettings",
            "CapsuleOverridesSettings",
            "BackupRestoreSettings",
            "DataHealthSettings",
            "CompatibilityDiagnosticsSettings",
            "PerformanceSettings"
        ];

        string searchQuery = _localizationService.T("Settings.DataBackup.Title");
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            throw new InvalidOperationException("The deep-settings search query is empty.");
        }

        SettingsSearchBox.Focus(FocusState.Programmatic);
        SettingsSearchBox.Text = searchQuery;
        UpdateSettingsSearchSuggestions(searchQuery);
        await Task.Delay(150);

        List<SettingsSearchResult> matches = GetAotSettingsSearchSuggestions();
        List<AotDeepSettingsSearchSuggestionSnapshot> searchSuggestions = matches
            .Select(match => new AotDeepSettingsSearchSuggestionSnapshot(
                match.SectionTag,
                match.Title,
                match.Breadcrumb,
                match.Description,
                match.IsPage))
            .ToList();
        SettingsSearchResult? exactNestedPage = matches.FirstOrDefault(match =>
            match.IsPage &&
            string.Equals(
                match.SectionTag,
                deepSettingsRoutes[21],
                StringComparison.Ordinal));
        if (exactNestedPage is null ||
            !SettingsSearchBox.IsSuggestionListOpen ||
            searchSuggestions.Count == 0)
        {
            throw new InvalidOperationException(
                "The non-empty settings search did not project the exact nested page suggestion.");
        }

        ActivateSettingsSearchResult(exactNestedPage, SettingsSearchBox);
        AotDeepSettingsPageSnapshot activatedPage =
            await WaitForAotDeepSettingsPageAsync(deepSettingsRoutes[21]);

        var pageTransitions = new List<AotDeepSettingsPageSnapshot>(deepSettingsRoutes.Length);
        bool breadcrumbParentReturned = false;
        int fileStackRuleCount = 0;
        int backupSnapshotCount = 0;
        foreach (string sectionTag in deepSettingsRoutes)
        {
            App.Log($"[AotManagedUiSmoke] DeepSettings route begin: {sectionTag}");
            NavigateToSettingsSection(sectionTag);
            AotDeepSettingsPageSnapshot page =
                await WaitForAotDeepSettingsPageAsync(sectionTag);
            pageTransitions.Add(page);

            if (string.Equals(sectionTag, deepSettingsRoutes[5], StringComparison.Ordinal))
            {
                fileStackRuleCount = await WaitForAotFileStackRuleProjectionAsync();
            }

            if (string.Equals(sectionTag, deepSettingsRoutes[21], StringComparison.Ordinal))
            {
                backupSnapshotCount = await WaitForAotBackupSnapshotProjectionAsync();
            }

            if (string.Equals(sectionTag, deepSettingsRoutes[17], StringComparison.Ordinal))
            {
                List<SettingsBreadcrumbItem> breadcrumbItems =
                    GetAotSettingsBreadcrumbItems();
                if (breadcrumbItems.Count != 2)
                {
                    throw new InvalidOperationException(
                        "The nested capsule page did not expose its two-item breadcrumb.");
                }

                NavigateFromSettingsBreadcrumbItem(breadcrumbItems[0]);
                AotDeepSettingsPageSnapshot parentPage =
                    await WaitForAotDeepSettingsPageAsync(deepSettingsRoutes[1]);
                breadcrumbParentReturned = string.Equals(
                        parentPage.CurrentSection,
                        deepSettingsRoutes[1],
                        StringComparison.Ordinal) &&
                    parentPage.BreadcrumbItems.Count == 0;
            }

            await Task.Delay(100);
            SettingsRoot.UpdateLayout();
            App.Log($"[AotManagedUiSmoke] DeepSettings route completed: {sectionTag}");
        }

        return new AotDeepSettingsSnapshot(
            searchQuery,
            searchSuggestions,
            activatedPage.CurrentSection,
            pageTransitions,
            breadcrumbParentReturned,
            fileStackRuleCount,
            backupSnapshotCount);
    }

    private async Task<int> WaitForAotFileStackRuleProjectionAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            FileStackRulesListView.UpdateLayout();
            List<FileStackCustomRuleEditor> projectedRules =
                FileStackRulesListView.ItemsSource is System.Collections.IEnumerable items
                    ? items.Cast<object>().OfType<FileStackCustomRuleEditor>().ToList()
                    : [];
            if (projectedRules.Count == ViewModel.FileStackCustomRules.Count &&
                projectedRules.Count > 0 &&
                FileStackRulesListView.ContainerFromIndex(0) is FrameworkElement
                {
                    XamlRoot: not null,
                    ActualHeight: > 0
                })
            {
                return projectedRules.Count;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            "The seeded file-stack rule did not reach its NativeAOT list projection.");
    }

    private async Task<int> WaitForAotBackupSnapshotProjectionAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (_isRefreshingBackupSnapshots && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (_isRefreshingBackupSnapshots)
        {
            throw new InvalidOperationException("The backup snapshot inventory did not become idle.");
        }

        await RefreshBackupSnapshotInventoryAsync();
        BackupSnapshotsList.UpdateLayout();
        List<BackupSnapshotListItem> projectedSnapshots =
            BackupSnapshotsList.ItemsSource is System.Collections.IEnumerable items
                ? items.Cast<object>().OfType<BackupSnapshotListItem>().ToList()
                : [];
        if (projectedSnapshots.Count == 0 ||
            BackupSnapshotsList.ContainerFromIndex(0) is not FrameworkElement
            {
                XamlRoot: not null,
                ActualHeight: > 0
            })
        {
            throw new InvalidOperationException(
                "The non-empty backup inventory did not reach its NativeAOT list projection.");
        }

        return projectedSnapshots.Count;
    }

    private async Task<AotDeepSettingsPageSnapshot> WaitForAotDeepSettingsPageAsync(
        string sectionTag)
    {
        if (!TryGetSectionRoute(sectionTag, out SettingsSectionRoute route) ||
            !_settingsSectionElements.TryGetValue(sectionTag, out FrameworkElement? sectionElement))
        {
            throw new InvalidOperationException(
                $"Deep settings route '{sectionTag}' is not registered.");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        AotDeepSettingsPageSnapshot? lastSnapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            SettingsRoot.UpdateLayout();
            List<SettingsBreadcrumbItem> breadcrumbItems =
                GetAotSettingsBreadcrumbItems();
            string? selectedNavTag =
                (SettingsNavigationView.SelectedItem as NavigationViewItem)?.Tag as string;
            string[] visibleSections = _settingsSectionElements
                .Where(entry => entry.Value.Visibility == Visibility.Visible)
                .Select(entry => entry.Key)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToArray();
            var breadcrumbSnapshots = breadcrumbItems
                .Select(item => new AotDeepSettingsBreadcrumbSnapshot(
                    item.SectionTag,
                    item.Title,
                    item.Opacity))
                .ToList();
            bool isNested = !string.IsNullOrWhiteSpace(route.ParentTag);
            lastSnapshot = new AotDeepSettingsPageSnapshot(
                sectionTag,
                route.ParentTag,
                route.NavTag,
                _currentSettingsSection,
                selectedNavTag,
                sectionElement.XamlRoot is not null,
                sectionElement.ActualWidth,
                sectionElement.ActualHeight,
                visibleSections,
                SettingsBreadcrumbHost.Visibility == Visibility.Visible,
                SettingsBreadcrumbBar.Visibility == Visibility.Visible,
                SettingsNavigationView.IsBackButtonVisible ==
                    NavigationViewBackButtonVisible.Visible,
                breadcrumbSnapshots);

            bool breadcrumbValid = isNested
                ? breadcrumbItems.Count == 2 &&
                    string.Equals(
                        breadcrumbItems[0].SectionTag,
                        route.ParentTag,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        breadcrumbItems[1].SectionTag,
                        sectionTag,
                        StringComparison.Ordinal) &&
                    lastSnapshot.BreadcrumbHostVisible &&
                    lastSnapshot.BreadcrumbBarVisible &&
                    lastSnapshot.BackButtonVisible
                : breadcrumbItems.Count == 0 &&
                    !lastSnapshot.BreadcrumbHostVisible &&
                    !lastSnapshot.BreadcrumbBarVisible &&
                    !lastSnapshot.BackButtonVisible;
            if (string.Equals(
                    lastSnapshot.CurrentSection,
                    sectionTag,
                    StringComparison.Ordinal) &&
                string.Equals(
                    lastSnapshot.SelectedNavTag,
                    route.NavTag,
                    StringComparison.Ordinal) &&
                lastSnapshot.HasXamlRoot &&
                lastSnapshot.ActualWidth > 0 &&
                lastSnapshot.ActualHeight > 0 &&
                lastSnapshot.VisibleSections.Contains(sectionTag, StringComparer.Ordinal) &&
                breadcrumbValid)
            {
                return lastSnapshot;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"Deep settings route '{sectionTag}' did not reach its loaded breadcrumb state. " +
            $"Last snapshot: {lastSnapshot}.");
    }

    private List<SettingsSearchResult> GetAotSettingsSearchSuggestions()
    {
        return SettingsSearchBox.ItemsSource is System.Collections.IEnumerable items
            ? items.Cast<object>().OfType<SettingsSearchResult>().ToList()
            : [];
    }

    private List<SettingsBreadcrumbItem> GetAotSettingsBreadcrumbItems()
    {
        return SettingsBreadcrumbBar.ItemsSource is System.Collections.IEnumerable items
            ? items.Cast<object>().OfType<SettingsBreadcrumbItem>().ToList()
            : [];
    }
}

internal sealed record AotDeepSettingsSnapshot(
    string SearchQuery,
    IReadOnlyList<AotDeepSettingsSearchSuggestionSnapshot> SearchSuggestions,
    string SearchActivatedSection,
    IReadOnlyList<AotDeepSettingsPageSnapshot> PageTransitions,
    bool BreadcrumbParentReturned,
    int FileStackRuleCount,
    int BackupSnapshotCount);

internal sealed record AotDeepSettingsSearchSuggestionSnapshot(
    string SectionTag,
    string Title,
    string Breadcrumb,
    string Description,
    bool IsPage);

internal sealed record AotDeepSettingsPageSnapshot(
    string Section,
    string? ExpectedParentTag,
    string ExpectedNavTag,
    string CurrentSection,
    string? SelectedNavTag,
    bool HasXamlRoot,
    double ActualWidth,
    double ActualHeight,
    IReadOnlyList<string> VisibleSections,
    bool BreadcrumbHostVisible,
    bool BreadcrumbBarVisible,
    bool BackButtonVisible,
    IReadOnlyList<AotDeepSettingsBreadcrumbSnapshot> BreadcrumbItems);

internal sealed record AotDeepSettingsBreadcrumbSnapshot(
    string SectionTag,
    string Title,
    double Opacity);
#endif
