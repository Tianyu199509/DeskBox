using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class MusicWidgetContentAdapter : IWidgetContent, IDisposable
{
    private readonly Func<MusicWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;

    public MusicWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        MusicSessionService? musicSessionService = null,
        Func<MusicWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Music)
        {
            throw new ArgumentException("Music content requires a Music widget config.", nameof(config));
        }

        Config = config;
        ViewModel = new MusicWidgetViewModel(
            config,
            musicSessionService ?? new MusicSessionService(),
            localizationService,
            settingsService);
        _viewFactory = viewFactory ?? (vm => new MusicWidgetContent(vm));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View => _view ??= _viewFactory(ViewModel);

    public MusicWidgetViewModel ViewModel { get; }

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

    public void Dispose()
    {
        ViewModel.Dispose();
    }
}
