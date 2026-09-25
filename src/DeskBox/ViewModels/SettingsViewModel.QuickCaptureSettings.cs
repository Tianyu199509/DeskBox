using DeskBox.Contracts;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private readonly IQuickCaptureSettings _quickCaptureSettings;

    private void OnQuickCaptureSettingsChanged()
    {
        if (_isDisposed) return;
        SyncQuickCaptureSettingsFacade();
        SyncQuickCaptureTabsFacade();
        SyncQuickCapturePresentationFacade();
        SyncQuickCaptureRecentLimitFacade();
        SyncQuickCaptureTextSizeFacade();
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void SyncQuickCaptureSettingsFacade()
    {
        QuickCaptureSettingsSnapshot state = _quickCaptureSettings.Read();
        bool wasApplying = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            QuickCaptureEnabled = state.Enabled;
            QuickCaptureClipboardEnabled = state.ClipboardEnabled;
            QuickCaptureImageClipboardEnabled = state.ImageEnabled;
        }
        finally { _isApplyingSettingsSnapshot = wasApplying; }
    }

    private void SyncQuickCaptureTabsFacade()
    {
        QuickCaptureTabSettings tabs = _quickCaptureSettings.ReadTabs();
        bool wasApplying = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            QuickCaptureShowTabBar = tabs.ShowTabBar;
            QuickCaptureShowRecordsTab = tabs.ShowRecordsTab;
            QuickCaptureShowPinnedTab = tabs.ShowPinnedTab;
            QuickCaptureShowRecentTab = tabs.ShowRecentTab;
        }
        finally { _isApplyingSettingsSnapshot = wasApplying; }

        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultView));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        RefreshQuickCaptureTabsPresentation();
    }

    private void SyncQuickCapturePresentationFacade()
    {
        QuickCapturePresentationSettings presentation =
            _quickCaptureSettings.ReadPresentation();
        bool wasApplying = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try { QuickCaptureShowCreatedTime = presentation.ShowCreatedTime; }
        finally { _isApplyingSettingsSnapshot = wasApplying; }

        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyle));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
        OnPropertyChanged(nameof(QuickCaptureTabStyleIndex));
        OnPropertyChanged(nameof(QuickCaptureItemPreviewLineCount));
        RefreshQuickCaptureContentPresentation();
    }

    private void ApplyQuickCaptureTabStyle(string? style)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        try { _quickCaptureSettings.SetTabStyle(style); }
        catch (Exception ex) { App.Log($"[QuickCapture] Tab style update failed: {ex}"); }
        SyncQuickCapturePresentationFacade();
    }

    private void ApplyQuickCaptureShowCreatedTime(bool visible)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        try { _quickCaptureSettings.SetShowCreatedTime(visible); }
        catch (Exception ex) { App.Log($"[QuickCapture] Created time update failed: {ex}"); }
        SyncQuickCapturePresentationFacade();
    }

    private void ApplyQuickCapturePreviewLineCount(int lineCount)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        try { _quickCaptureSettings.SetPreviewLineCount(lineCount); }
        catch (Exception ex) { App.Log($"[QuickCapture] Preview line count update failed: {ex}"); }
        SyncQuickCapturePresentationFacade();
    }

    private void SyncQuickCaptureRecentLimitFacade()
    {
        int limit = _quickCaptureSettings.ReadRecentLimit();
        bool wasApplying = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try { QuickCaptureRecentLimit = limit; }
        finally { _isApplyingSettingsSnapshot = wasApplying; }
        OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
        OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
    }

    private void ApplyQuickCaptureRecentLimit(int limit)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
            OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
            return;
        }

        try { _quickCaptureSettings.SetRecentLimit(limit); }
        catch (Exception ex) { App.Log($"[QuickCapture] Recent limit update failed: {ex}"); }
        SyncQuickCaptureRecentLimitFacade();
    }

    private void SyncQuickCaptureTextSizeFacade()
    {
        QuickCaptureTextSizeSettings sizes = _quickCaptureSettings.ReadTextSizes();
        bool wasApplying = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            QuickCaptureListTextSize = sizes.ListTextSize;
            QuickCaptureContentTextSize = sizes.ContentTextSize;
        }
        finally { _isApplyingSettingsSnapshot = wasApplying; }
        OnPropertyChanged(nameof(QuickCaptureListTextSizeValueText));
        OnPropertyChanged(nameof(QuickCaptureContentTextSizeValueText));
    }

    private bool TrySetQuickCaptureListTextSize(double size)
    {
        try { return _quickCaptureSettings.TrySetListTextSize(size, scheduleSave: false); }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] List text size update failed: {ex}");
            SyncQuickCaptureTextSizeFacade();
            return false;
        }
    }

    private bool TrySetQuickCaptureContentTextSize(double size)
    {
        try { return _quickCaptureSettings.TrySetContentTextSize(size, scheduleSave: false); }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Content text size update failed: {ex}");
            SyncQuickCaptureTextSizeFacade();
            return false;
        }
    }

    private void ApplyQuickCaptureDefaultView(string? view)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        try { _quickCaptureSettings.SetDefaultView(view); }
        catch (Exception ex) { App.Log($"[QuickCapture] Default view update failed: {ex}"); }
        SyncQuickCaptureTabsFacade();
    }

    private void ApplyQuickCaptureTabVisibility(string view, bool visible)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        try { _quickCaptureSettings.SetTabVisible(view, visible); }
        catch (Exception ex) { App.Log($"[QuickCapture] Tab visibility update failed: {ex}"); }
        SyncQuickCaptureTabsFacade();
    }

    private void ApplyQuickCaptureTabBarVisibility(bool visible)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        try { _quickCaptureSettings.SetTabBarVisible(visible); }
        catch (Exception ex) { App.Log($"[QuickCapture] Tab bar update failed: {ex}"); }
        SyncQuickCaptureTabsFacade();
    }

    private void TrackQuickCaptureAction(Task action) => _ = CompleteQuickCaptureActionAsync(action);

    private async Task CompleteQuickCaptureActionAsync(Task action)
    {
        try { await action; }
        catch (OperationCanceledException) when (_isDisposed) { }
        catch (Exception ex) { App.Log($"[QuickCapture] Settings operation failed: {ex}"); }
        finally
        {
            if (!_isDisposed) OnQuickCaptureSettingsChanged();
        }
    }
}
