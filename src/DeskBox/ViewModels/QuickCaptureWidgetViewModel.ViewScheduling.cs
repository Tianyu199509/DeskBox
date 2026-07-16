using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.ViewModels;

public sealed partial class QuickCaptureWidgetViewModel
{
    private void SetViewSwitchLoading(bool isLoading)
    {
        if (_isSwitchingView == isLoading)
        {
            return;
        }

        _isSwitchingView = isLoading;
        OnPropertyChanged(nameof(IsSwitchingView));
        OnPropertyChanged(nameof(ViewSwitchLoadingVisibility));
    }

    private void ScheduleViewSwitchRefresh()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ScheduleViewSwitchRefresh);
            return;
        }

        SetViewSwitchLoading(true);
        _searchRefreshTimer.Stop();
        _viewSwitchRefreshTimer.Stop();
        _viewSwitchRefreshTimer.Start();
    }

    private void ViewSwitchRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isDisposed)
        {
            return;
        }

        RefreshVisibleItemsForViewSwitchAsync().LogQuickCaptureFailure();
    }

    private async Task RefreshVisibleItemsForViewSwitchAsync()
    {
        try
        {
            if (_cachedData is { } data)
            {
                await RefreshFromDataAsync(data);
                return;
            }

            await RefreshVisibleItemsAsync();
        }
        finally
        {
            if (!_isDisposed)
            {
                SetViewSwitchLoading(false);
            }
        }
    }

    private void ScheduleCurrentViewSave()
    {
        if (_isDisposed)
        {
            return;
        }

        _currentViewSaveTimer.Stop();
        _currentViewSaveTimer.Start();
    }

    private void CurrentViewSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isDisposed)
        {
            return;
        }

        var view = SelectedView;
        _ = Task.Run(async () =>
        {
            try
            {
                await _quickCaptureService.SetCurrentViewAsync(view);
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCapture] Failed to save current view: {ex}");
            }
        });
    }
}
