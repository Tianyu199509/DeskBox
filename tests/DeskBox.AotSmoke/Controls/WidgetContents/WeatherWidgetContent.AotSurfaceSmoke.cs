#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class WeatherWidgetContent
{
    internal async Task<AotWeatherSurfaceSnapshot> WaitForAotWeatherSurfaceAsync(
        bool expectWeekView,
        string expectedTemperatureText,
        string expectedWindValueText,
        bool expectRichSkin)
    {
        AotWeatherSurfaceSnapshot last = CaptureAotWeatherSurface();
        for (int attempt = 0; attempt < 160; attempt++)
        {
            UpdateLayout();
            last = CaptureAotWeatherSurface();
            bool expectedForecastProjected = expectWeekView
                ? last.WeekForecastVisible &&
                    !last.HourlyForecastVisible &&
                    last.DailyItemsCount == 7 &&
                    last.DailyContainerRealized &&
                    last.DailyTemplateTextProjected
                : last.HourlyForecastVisible &&
                    !last.WeekForecastVisible &&
                    last.HourlyItemsCount == 24 &&
                    last.HourlyContainerRealized &&
                    last.HourlyTemplateTextProjected;
            if (last.IsLoaded &&
                last.HasXamlRoot &&
                last.DataContextMatchesViewModel &&
                last.ActualWidth > 0 &&
                last.ActualHeight > 0 &&
                last.HasData &&
                last.ExpandedLayoutVisible &&
                last.IsWeekView == expectWeekView &&
                last.SelectedViewIndex == (expectWeekView ? 1 : 0) &&
                last.RichBackdropVisible == expectRichSkin &&
                string.Equals(
                    last.CurrentTemperatureText,
                    expectedTemperatureText,
                    StringComparison.Ordinal) &&
                string.Equals(
                    last.SurfaceTemperatureText,
                    expectedTemperatureText,
                    StringComparison.Ordinal) &&
                string.Equals(
                    last.WindValueText,
                    expectedWindValueText,
                    StringComparison.Ordinal) &&
                last.SurfaceWindText.StartsWith(
                    expectedWindValueText,
                    StringComparison.Ordinal) &&
                last.HourlyViewModelCount == 24 &&
                last.DailyViewModelCount == 7 &&
                last.LoadingOverlayHidden &&
                expectedForecastProjected)
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"The Weather surface did not stabilize. Snapshot={last}");
    }

    internal async Task SetAotWeatherSurfaceViewModeAsync(bool useWeekView)
    {
        if (!_isViewLoaded || XamlRoot is null)
        {
            throw new InvalidOperationException(
                "The real Weather view switch is unavailable.");
        }

        WeatherViewSegmented.SelectedIndex = useWeekView ? 1 : 0;
        await Task.Yield();
        if (_viewModel.IsWeekView != useWeekView)
        {
            throw new InvalidOperationException(
                "The real Weather segmented control did not update the view model.");
        }
    }

    internal async Task<AotWeatherCompactSurfaceSnapshot>
        WaitForAotWeatherCompactSurfaceAsync(
            string expectedTemperatureText,
            string expectedWindValueText,
            bool expectRichSkin)
    {
        AotWeatherCompactSurfaceSnapshot last = CaptureAotWeatherCompactSurface();
        for (int attempt = 0; attempt < 160; attempt++)
        {
            UpdateLayout();
            last = CaptureAotWeatherCompactSurface();
            if (last.IsLoaded &&
                last.HasXamlRoot &&
                last.DataContextMatchesViewModel &&
                last.ActualWidth > 0 &&
                last.ActualHeight > 0 &&
                last.HasData &&
                string.Equals(last.LayoutMode, "Compact", StringComparison.Ordinal) &&
                !last.MiniLayoutVisible &&
                last.CompactLayoutVisible &&
                !last.ExpandedLayoutVisible &&
                last.RichBackdropVisible == expectRichSkin &&
                last.LoadingOverlayHidden &&
                !string.IsNullOrWhiteSpace(last.LocationDisplay) &&
                string.Equals(
                    last.SurfaceLocationText,
                    last.LocationDisplay,
                    StringComparison.Ordinal) &&
                string.Equals(
                    last.CurrentTemperatureText,
                    expectedTemperatureText,
                    StringComparison.Ordinal) &&
                string.Equals(
                    last.SurfaceTemperatureText,
                    expectedTemperatureText,
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(last.CurrentDescription) &&
                string.Equals(
                    last.SurfaceDescriptionText,
                    last.CurrentDescription,
                    StringComparison.Ordinal) &&
                string.Equals(last.SurfaceHumidityValueText, "64%", StringComparison.Ordinal) &&
                string.Equals(
                    last.SurfaceWindValueText,
                    expectedWindValueText,
                    StringComparison.Ordinal) &&
                string.Equals(
                    last.SurfacePrecipitationValueText,
                    "70%",
                    StringComparison.Ordinal))
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            $"The Weather compact surface did not stabilize. Snapshot={last}");
    }

    private AotWeatherCompactSurfaceSnapshot CaptureAotWeatherCompactSurface()
    {
        return new AotWeatherCompactSurfaceSnapshot(
            _isViewLoaded,
            XamlRoot is not null,
            ReferenceEquals(DataContext, _viewModel),
            ActualWidth,
            ActualHeight,
            _viewModel.HasData,
            _viewModel.LayoutMode,
            MiniLayout.Visibility == Visibility.Visible,
            CompactLayout.Visibility == Visibility.Visible,
            ExpandedLayout.Visibility == Visibility.Visible,
            RichBackdrop.Visibility == Visibility.Visible,
            LoadingOverlay.Visibility != Visibility.Visible,
            _viewModel.LocationDisplay,
            CompactLocationText.Text,
            _viewModel.CurrentTemperatureText,
            CompactTemperatureText.Text,
            _viewModel.CurrentDescription,
            CompactDescriptionText.Text,
            CompactHumidityValueText.Text,
            CompactWindText.Text,
            CompactPrecipitationValueText.Text);
    }

    private AotWeatherSurfaceSnapshot CaptureAotWeatherSurface()
    {
        FrameworkElement? hourlyContainer =
            ExpandedHourlyItems.ContainerFromIndex(0) as FrameworkElement;
        FrameworkElement? dailyContainer =
            ExpandedDailyItems.ContainerFromIndex(0) as FrameworkElement;
        TextBlock? hourlyHourText = hourlyContainer is null
            ? null
            : FindAotWeatherDescendant<TextBlock>(hourlyContainer, "HourlyHourText");
        TextBlock? hourlyTemperatureText = hourlyContainer is null
            ? null
            : FindAotWeatherDescendant<TextBlock>(
                hourlyContainer,
                "HourlyTemperatureText");
        TextBlock? dailyDayText = dailyContainer is null
            ? null
            : FindAotWeatherDescendant<TextBlock>(dailyContainer, "DailyDayText");
        TextBlock? dailyMaxText = dailyContainer is null
            ? null
            : FindAotWeatherDescendant<TextBlock>(dailyContainer, "DailyMaxText");
        TextBlock? dailyMinText = dailyContainer is null
            ? null
            : FindAotWeatherDescendant<TextBlock>(dailyContainer, "DailyMinText");
        WeatherHourViewModel? firstHour = _viewModel.HourlyForecast.FirstOrDefault();
        WeatherDayViewModel? firstDay = _viewModel.DailyForecast.FirstOrDefault();
        WeatherHourViewModel? hourlyDataContext =
            hourlyTemperatureText?.DataContext as WeatherHourViewModel;
        WeatherDayViewModel? dailyDataContext =
            dailyMaxText?.DataContext as WeatherDayViewModel;

        return new AotWeatherSurfaceSnapshot(
            _isViewLoaded,
            XamlRoot is not null,
            ReferenceEquals(DataContext, _viewModel),
            ActualWidth,
            ActualHeight,
            _viewModel.HasData,
            _viewModel.LayoutMode,
            _viewModel.IsWeekView,
            WeatherViewSegmented.SelectedIndex,
            RichBackdrop.Visibility == Visibility.Visible,
            RichBackdropTop.Color.ToString(),
            RichBackdropBottom.Color.ToString(),
            ExpandedLayout.Visibility == Visibility.Visible,
            ExpandedHourlyForecastSection.Visibility == Visibility.Visible,
            ExpandedWeekForecastSection.Visibility == Visibility.Visible,
            LoadingOverlay.Visibility != Visibility.Visible,
            _viewModel.LocationDisplay,
            ExpandedLocationText.Text,
            _viewModel.CurrentTemperatureText,
            ExpandedTemperatureText.Text,
            _viewModel.CurrentDescription,
            ExpandedDescriptionText.Text,
            _viewModel.HumidityValueText,
            ExpandedHumidityValueText.Text,
            _viewModel.WindValueText,
            ExpandedWindText.Text,
            _viewModel.PrecipitationValueText,
            ExpandedPrecipitationValueText.Text,
            _viewModel.UvIndexValueText,
            ExpandedUvValueText.Text,
            _viewModel.PressureValueText,
            ExpandedPressureValueText.Text,
            ExpandedUvMetric.Visibility == Visibility.Visible,
            ExpandedPressureMetric.Visibility == Visibility.Visible,
            _viewModel.HourlyForecast.Count,
            _viewModel.DailyForecast.Count,
            ExpandedHourlyItems.Items.Count,
            ExpandedDailyItems.Items.Count,
            hourlyContainer is not null,
            dailyContainer is not null,
            firstHour?.HourLabel ?? string.Empty,
            firstHour?.TemperatureText ?? string.Empty,
            hourlyHourText?.Text ?? string.Empty,
            hourlyTemperatureText?.Text ?? string.Empty,
            ReferenceEquals(hourlyDataContext, firstHour),
            firstDay?.DayLabel ?? string.Empty,
            firstDay?.TempMaxText ?? string.Empty,
            firstDay?.TempMinText ?? string.Empty,
            dailyDayText?.Text ?? string.Empty,
            dailyMaxText?.Text ?? string.Empty,
            dailyMinText?.Text ?? string.Empty,
            ReferenceEquals(dailyDataContext, firstDay));
    }

    private static T? FindAotWeatherDescendant<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(
                    match.Name,
                    name,
                    StringComparison.Ordinal))
            {
                return match;
            }

            T? nested = FindAotWeatherDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}

internal sealed record AotWeatherSurfaceSnapshot(
    bool IsLoaded,
    bool HasXamlRoot,
    bool DataContextMatchesViewModel,
    double ActualWidth,
    double ActualHeight,
    bool HasData,
    string LayoutMode,
    bool IsWeekView,
    int SelectedViewIndex,
    bool RichBackdropVisible,
    string RichBackdropTopColor,
    string RichBackdropBottomColor,
    bool ExpandedLayoutVisible,
    bool HourlyForecastVisible,
    bool WeekForecastVisible,
    bool LoadingOverlayHidden,
    string LocationDisplay,
    string SurfaceLocationText,
    string CurrentTemperatureText,
    string SurfaceTemperatureText,
    string CurrentDescription,
    string SurfaceDescriptionText,
    string HumidityValueText,
    string SurfaceHumidityValueText,
    string WindValueText,
    string SurfaceWindText,
    string PrecipitationValueText,
    string SurfacePrecipitationValueText,
    string UvIndexValueText,
    string SurfaceUvIndexValueText,
    string PressureValueText,
    string SurfacePressureValueText,
    bool UvMetricVisible,
    bool PressureMetricVisible,
    int HourlyViewModelCount,
    int DailyViewModelCount,
    int HourlyItemsCount,
    int DailyItemsCount,
    bool HourlyContainerRealized,
    bool DailyContainerRealized,
    string FirstHourlyHourLabel,
    string FirstHourlyTemperatureText,
    string SurfaceFirstHourlyHourText,
    string SurfaceFirstHourlyTemperatureText,
    bool HourlyTemplateTextProjected,
    string FirstDailyDayLabel,
    string FirstDailyMaxText,
    string FirstDailyMinText,
    string SurfaceFirstDailyDayText,
    string SurfaceFirstDailyMaxText,
    string SurfaceFirstDailyMinText,
    bool DailyTemplateTextProjected);

internal sealed record AotWeatherCompactSurfaceSnapshot(
    bool IsLoaded,
    bool HasXamlRoot,
    bool DataContextMatchesViewModel,
    double ActualWidth,
    double ActualHeight,
    bool HasData,
    string LayoutMode,
    bool MiniLayoutVisible,
    bool CompactLayoutVisible,
    bool ExpandedLayoutVisible,
    bool RichBackdropVisible,
    bool LoadingOverlayHidden,
    string LocationDisplay,
    string SurfaceLocationText,
    string CurrentTemperatureText,
    string SurfaceTemperatureText,
    string CurrentDescription,
    string SurfaceDescriptionText,
    string SurfaceHumidityValueText,
    string SurfaceWindValueText,
    string SurfacePrecipitationValueText);
#endif
