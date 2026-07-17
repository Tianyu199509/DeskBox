using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class WeatherWidgetContentAdapter : IWidgetContent, IWidgetResponsiveLayoutContent, IDisposable
{
    private readonly Func<WeatherWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;

    public WeatherWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        WeatherService? weatherService = null,
        Func<WeatherWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Weather)
        {
            throw new ArgumentException("Weather content requires a Weather widget config.", nameof(config));
        }

        Config = config;
        ViewModel = new WeatherWidgetViewModel(
            config,
            weatherService ?? new WeatherService(),
            localizationService,
            settingsService);
        _viewFactory = viewFactory ?? (vm => new WeatherWidgetContent(vm));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View => _view ??= _viewFactory(ViewModel);

    public WeatherWidgetViewModel ViewModel { get; }

    public Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public Task RefreshAsync()
    {
        return ViewModel.RefreshAsync();
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public void OnActivated()
    {
        ViewModel.OnActivated();
    }

    public void OnDeactivated()
    {
        ViewModel.OnDeactivated();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        ViewModel.OnWindowVisibilityChanged(visible);
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

    public void Dispose()
    {
        ViewModel.Dispose();
    }
}
