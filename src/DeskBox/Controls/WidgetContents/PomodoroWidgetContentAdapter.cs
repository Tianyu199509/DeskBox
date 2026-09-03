using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>将番茄钟界面接入统一格子内容生命周期。</summary>
public sealed class PomodoroWidgetContentAdapter :
    IWidgetContent,
    IWidgetResponsiveLayoutContent,
    IDisposable
{
    private readonly Func<PomodoroWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;
    private bool _isDisposed;

    public PomodoroWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Func<PomodoroWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Pomodoro)
        {
            throw new ArgumentException(
                "Pomodoro content requires a Pomodoro widget config.",
                nameof(config));
        }

        Config = config;
        ViewModel = new PomodoroWidgetViewModel(
            config,
            localizationService,
            settingsService);
        _viewFactory = viewFactory ?? (vm => new PomodoroWidgetContent(vm));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _view ??= _viewFactory(ViewModel);
        }
    }

    public PomodoroWidgetViewModel ViewModel { get; }

    public Task InitializeAsync() => ViewModel.InitializeAsync();

    public Task RefreshAsync() => ViewModel.RefreshAsync();

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
        if (_view is PomodoroWidgetContent content)
        {
            content.ApplyAppearance();
        }
    }

    public void OnActivated() => ViewModel.OnActivated();

    public void OnDeactivated() => ViewModel.OnDeactivated();

    public void OnWindowVisibilityChanged(bool visible) =>
        ViewModel.OnWindowVisibilityChanged(visible);

    public void OnWindowRevealCompleted() =>
        ViewModel.OnWindowRevealCompleted();

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (_view is PomodoroWidgetContent content)
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
        if (_view is PomodoroWidgetContent content)
        {
            content.CompleteResponsiveLayoutTransition(
                finalContentWidth,
                finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (_view is PomodoroWidgetContent content)
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
        (_view as IDisposable)?.Dispose();
        ViewModel.Dispose();
        _view = null;
    }
}
