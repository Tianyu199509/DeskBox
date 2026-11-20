using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private bool _appearanceSubscribed;
    private bool _appearanceRefreshQueued;
    private bool _isPermanentlyClosed;

    private void TaskView_ActualThemeChanged(FrameworkElement sender, object args) =>
        QueueAppearanceRefresh();

    private void TaskView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isPermanentlyClosed) return;
        _dragController.Attach(WorkspaceRootGrid);
        if (_appearanceSubscribed) return;
        _appearanceSubscribed = true;
        App.Current.SettingsService.SettingsChanged += QueueAppearanceRefresh;
        App.Current.SettingsService.AppearancePreviewChanged += QueueAppearanceRefresh;
        App.Current.ThemeService.AppearanceChanged += QueueAppearanceRefresh;
        WindowsCompatibilityService.TextScaleFactorChanged += QueueAppearanceRefresh;
        if (Session.EffectivePlan is not null && !_hasCompletedExecution && _targetCards.Count == 0)
        {
            RenderSessionPlan();
        }
        QueueAppearanceRefresh();
    }

    private void TaskView_Unloaded(object sender, RoutedEventArgs e) =>
        ReleaseMountedStateCore();

    /// <summary>
    /// Deterministic teardown invoked from <c>DesktopOrganizationWindow.Closed</c>.
    /// WinUI 3 <c>Window.Close()</c> does not reliably fire child <c>UserControl.Unloaded</c>
    /// unless detached, which would otherwise leave singleton/static events and decoded
    /// preview thumbnails pinned in memory.
    /// </summary>
    public void CleanupForWindowClosed()
    {
        if (_isPermanentlyClosed)
        {
            return;
        }

        _isPermanentlyClosed = true;
        Loaded -= TaskView_Loaded;
        Unloaded -= TaskView_Unloaded;
        ActualThemeChanged -= TaskView_ActualThemeChanged;
        ReleaseMountedStateCore();
        UnwirePointerDragController();
        _dragController.Dispose();
        _scanCts?.Dispose();
        _scanCts = null;
        _executionCts?.Dispose();
        _executionCts = null;
        _completedItems.Clear();
        _restoredItems.Clear();
        _runtimeRetainedItems.Clear();
        DestinationPathsPanel.Children.Clear();
        _session = null;
        _lastExecutionPlan = null;
        _coordinator = null;
        Bindings?.StopTracking();
        Content = null;
    }

    private void ReleaseMountedStateCore()
    {
        CancelPendingWork();
        _dragController.Detach();
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
        // The preview cards share one icon cache scope; the tiles are gone
        // now, so their decoded bitmaps and thumbnails can be released
        // instead of occupying the cache until the idle trim.
        IconHelper.ClearCacheScope(DesktopOrganizationPreviewCard.PreviewIconCacheScope);
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
            foreach (var card in _completedCards) card.RefreshAppearance();
            if (!_hasCompletedExecution && !_dragController.IsDragging && Session.EffectivePlan is not null)
            {
                RenderExcludedItems();
            }
            if (!_isScanning && !_isExecuting && !_hasCompletedExecution && Session.BasePlan is not null &&
                !string.Equals(Session.BasePlan.StorageRootPath, SettingsService.NormalizeManagedStorageRootPath(
                    App.Current.SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath), StringComparison.OrdinalIgnoreCase))
            {
                _ = ScanAsync(preserveSelection: true);
            }
            UpdateSectionVisibility();
        });
    }
}
