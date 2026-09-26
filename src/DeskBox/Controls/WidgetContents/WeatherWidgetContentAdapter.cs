using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class WeatherWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetResponsiveLayoutContent,
    IWidgetFeedbackSource
{
    private readonly LocalizationService _localizationService;

    public WeatherWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        WeatherService? weatherService = null,
        Func<WeatherWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new WeatherWidgetViewModel(
                config,
                weatherService ?? new WeatherService(),
                localizationService,
                settingsService),
            localizationService,
            viewFactory)
    {
    }

    private WeatherWidgetContentAdapter(
        WidgetConfig config,
        WeatherWidgetViewModel viewModel,
        LocalizationService localizationService,
        Func<WeatherWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new WeatherWidgetContent(vm)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Weather)
        {
            throw new ArgumentException("Weather content requires a Weather widget config.", nameof(config));
        }

        _localizationService = localizationService;
        ViewModel = viewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public WeatherWidgetViewModel ViewModel { get; }

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    private void ViewModel_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WeatherWidgetViewModel.ShowRefreshStatus) ||
            !ViewModel.ShowRefreshStatus)
        {
            return;
        }

        bool success = string.Equals(
            ViewModel.RefreshStatusText,
            _localizationService.T("Weather.RefreshSuccess"),
            StringComparison.Ordinal);
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    ViewModel.RefreshStatusText,
                    success
                        ? WidgetFeedbackSeverity.Success
                        : WidgetFeedbackSeverity.Error,
                    "weather-refresh")));
    }

    public override Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public override Task RefreshAsync()
    {
        return ViewModel.RefreshAsync();
    }

    public override void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public override void OnActivated()
    {
        ViewModel.OnActivated();
    }

    public override void OnDeactivated()
    {
        ViewModel.OnDeactivated();
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        ViewModel.OnWindowVisibilityChanged(visible);
    }

    public override void OnWindowRevealCompleted()
    {
        ViewModel.OnWindowRevealCompleted();
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        ViewModel.BeginResponsiveLayoutTransition(
            targetContentWidth,
            targetContentHeight,
            isCollapsing);
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        ViewModel.CompleteResponsiveLayoutTransition(finalContentWidth, finalContentHeight);
    }

    public void CancelResponsiveLayoutTransition()
    {
        ViewModel.CancelResponsiveLayoutTransition();
    }

    protected override void Dispose(bool disposing)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Dispose();
    }
}
