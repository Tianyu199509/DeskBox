using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class MusicWidgetContentAdapter : IWidgetContent, IWidgetResponsiveLayoutContent, IDisposable
{
    private readonly Func<MusicWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;
    private bool _isDisposed;

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

    public FrameworkElement View
    {
        get
        {
            if (_view is null && !_isDisposed)
            {
                _view = _viewFactory(ViewModel);
            }
            return _view!;
        }
    }

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

    public void OnWindowVisibilityChanged(bool visible)
    {
        ViewModel.OnWindowVisibilityChanged(visible);
        if (_view is MusicWidgetContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (_view is MusicWidgetContent content)
        {
            content.BeginResponsiveLayoutTransition(
                targetContentWidth,
                targetContentHeight,
                isCollapsing);
        }
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        if (_view is MusicWidgetContent content)
        {
            content.CompleteResponsiveLayoutTransition(finalContentWidth, finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (_view is MusicWidgetContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Detach the View's PropertyChanged subscription by clearing the
        // ViewModel reference.  The setter removes the event handler.
        if (_view is MusicWidgetContent musicContent)
        {
            musicContent.Dispose();
        }

        // Dispose the ViewModel first (stops timers, detaches service events).
        ViewModel.Dispose();

        _view = null;
    }
}
