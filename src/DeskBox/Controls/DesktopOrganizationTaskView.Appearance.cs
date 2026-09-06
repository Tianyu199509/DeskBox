using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private bool _appearanceSubscribed;
    private bool _appearanceRefreshQueued;

    private void TaskView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_appearanceSubscribed) return;
        _appearanceSubscribed = true;
        App.Current.SettingsService.SettingsChanged += QueueAppearanceRefresh;
        App.Current.SettingsService.AppearancePreviewChanged += QueueAppearanceRefresh;
        App.Current.ThemeService.AppearanceChanged += QueueAppearanceRefresh;
        WindowsCompatibilityService.TextScaleFactorChanged += QueueAppearanceRefresh;
        if (_plan is not null && !_hasCompletedExecution && _targetCards.Count == 0) RenderPlan(_plan);
        QueueAppearanceRefresh();
    }

    private void TaskView_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelPendingWork();
        if (_appearanceSubscribed)
        {
            _appearanceSubscribed = false;
            App.Current.SettingsService.SettingsChanged -= QueueAppearanceRefresh;
            App.Current.SettingsService.AppearancePreviewChanged -= QueueAppearanceRefresh;
            App.Current.ThemeService.AppearanceChanged -= QueueAppearanceRefresh;
            WindowsCompatibilityService.TextScaleFactorChanged -= QueueAppearanceRefresh;
        }
        ReleasePreviewCards();
        ReleaseRetainedCards();
        ReleaseCompletedCards();
    }

    private void QueueAppearanceRefresh()
    {
        // Settings preview events can arrive from a worker thread.
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueAppearanceRefresh);
            return;
        }
        if (!_appearanceSubscribed || _appearanceRefreshQueued) return;
        _appearanceRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _appearanceRefreshQueued = false;
            if (!_appearanceSubscribed) return;
            RequestedTheme = App.Current.ThemeService.CurrentTheme;
            foreach (var card in _targetCards) card.RefreshAppearance();
            foreach (var card in _retainedCards) card.RefreshAppearance();
            foreach (var card in _completedCards) card.RefreshAppearance();
            if (!_isScanning && !_isExecuting && !_hasCompletedExecution && _basePlan is not null &&
                !string.Equals(_basePlan.StorageRootPath, SettingsService.NormalizeManagedStorageRootPath(
                    App.Current.SettingsService.Settings.DefaultManagedStorageRootPath), StringComparison.OrdinalIgnoreCase))
                _ = ScanAsync(preserveSelection: true);
            LayoutTargetCards();
            UpdateSectionVisibility();
        });
    }
}
