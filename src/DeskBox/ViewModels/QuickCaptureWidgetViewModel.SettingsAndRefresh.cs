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
    private void OnQuickCaptureChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshVisibleItemsImmediately);
            return;
        }

        RefreshVisibleItemsImmediately();
    }

    private void OnLanguageChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateEmptyStateText();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RecordsTabText));
        OnPropertyChanged(nameof(PinnedTabText));
        OnPropertyChanged(nameof(RecentTabText));
        OnPropertyChanged(nameof(EnableRecentCaptureText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(InputPlaceholderText));
        OnPropertyChanged(nameof(AddNoteText));
        OnPropertyChanged(nameof(ExpandInputTooltipText));
        OnPropertyChanged(nameof(DetailBackText));
        OnPropertyChanged(nameof(AddFileText));
        OnPropertyChanged(nameof(DetailRemoveAttachmentText));
        OnPropertyChanged(nameof(DetailTitlePlaceholderText));
        OnPropertyChanged(nameof(DetailBodyPlaceholderText));
        OnPropertyChanged(nameof(DetailAppearanceText));
        OnPropertyChanged(nameof(DetailCopyText));
        OnPropertyChanged(nameof(DetailCopyImageText));
        OnPropertyChanged(nameof(DetailReplaceImageText));
        OnPropertyChanged(nameof(DetailDeleteText));
        OnPropertyChanged(nameof(MaterialDefaultText));
        OnPropertyChanged(nameof(MaterialPaperText));
        OnPropertyChanged(nameof(MaterialYellowText));
        OnPropertyChanged(nameof(MaterialRoseText));
        OnPropertyChanged(nameof(MaterialMintText));
        OnPropertyChanged(nameof(MaterialBlueText));
        OnPropertyChanged(nameof(SearchScopeText));
        OnPropertyChanged(nameof(SearchScopeVisibility));
        OnPropertyChanged(nameof(RecentCaptureStatusText));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
        foreach (var item in Items)
        {
            item.Update(item.ToModel());
            item.UpdateSearchText(SearchText);
        }
    }

    private void OnSettingsChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        UpdateEmptyStateText();
        RefreshAppearanceFromSettings();
        OnPropertyChanged(nameof(CreatedTimeVisibility));
        OnPropertyChanged(nameof(RecentCaptureStatusText));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
    }

    private void RefreshAppearanceFromSettings()
    {
        var settings = _settingsService.Settings;
        WidgetOpacity = settings.WidgetOpacity;
        TabStyle = settings.QuickCaptureTabStyle;
        TextSize = SettingsService.NormalizeTextSize(settings.TextSize);
        IconSize = SettingsService.NormalizeIconSize(settings.IconSize);
        LayoutDensityScale = NormalizeDensity(settings.LayoutDensityScale);
        OnPropertyChanged(nameof(TitleIconSize));
        OnPropertyChanged(nameof(ActionIconSize));
        OnPropertyChanged(nameof(EmptyIconSize));

        foreach (var item in Items)
        {
            item.UpdateAppearance(TextSize, IconSize);
            item.UpdateSearchText(SearchText);
        }
    }

    private static double NormalizeDensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, SettingsService.MinLayoutDensityScale, SettingsService.MaxLayoutDensityScale)
            : SettingsService.DefaultLayoutDensityScale;

    private double DensityMetric(double compact, double standard, double relaxed)
    {
        const double standardPoint = SettingsService.DefaultLayoutDensityScale;
        double value = LayoutDensityScale <= standardPoint
            ? Lerp(compact, standard, LayoutDensityScale / standardPoint)
            : Lerp(standard, relaxed, (LayoutDensityScale - standardPoint) / (1 - standardPoint));
        return Math.Round(value, 2);
    }

    private static double Lerp(double min, double max, double t) =>
        min + ((max - min) * Math.Clamp(t, 0, 1));

    private async Task RefreshVisibleItemsAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        int generation = System.Threading.Interlocked.Increment(ref _visibleItemsRefreshGeneration);
        var data = await _quickCaptureService.GetDataAsync();
        if (_isDisposed || generation != System.Threading.Volatile.Read(ref _visibleItemsRefreshGeneration))
        {
            return;
        }

        _cachedData = data;
        await RefreshFromDataAsync(data);
    }

    private void RefreshVisibleItemsFromCacheOrService()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshVisibleItemsFromCacheOrService);
            return;
        }

        _searchRefreshTimer.Stop();
        if (_cachedData is { } data)
        {
            _ = RefreshFromDataAsync(data);
            return;
        }

        RefreshVisibleItemsAsync().LogQuickCaptureFailure();
    }

    private void ScheduleVisibleItemsRefresh()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ScheduleVisibleItemsRefresh);
            return;
        }

        _searchRefreshTimer.Stop();
        _searchRefreshTimer.Start();
    }

    private void RefreshVisibleItemsImmediately()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(RefreshVisibleItemsImmediately);
            return;
        }

        _searchRefreshTimer.Stop();
        RefreshVisibleItemsAsync().LogQuickCaptureFailure();
    }

    private void SearchRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed)
        {
            return;
        }

        sender.Stop();
        RefreshVisibleItemsAsync().LogQuickCaptureFailure();
    }
}
