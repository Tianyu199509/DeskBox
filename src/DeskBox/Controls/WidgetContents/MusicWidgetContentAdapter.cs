using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

public sealed class MusicWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetResponsiveLayoutContent,
    IWidgetPerformanceAwareContent
{
    public MusicWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        MusicSessionService? musicSessionService = null,
        Func<MusicWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new MusicWidgetViewModel(
                config,
                musicSessionService ?? new MusicSessionService(),
                localizationService,
                settingsService),
            viewFactory)
    {
    }

    private MusicWidgetContentAdapter(
        WidgetConfig config,
        MusicWidgetViewModel viewModel,
        Func<MusicWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new MusicWidgetContent(vm)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Music)
        {
            throw new ArgumentException("Music content requires a Music widget config.", nameof(config));
        }

        ViewModel = viewModel;
    }

    public MusicWidgetViewModel ViewModel { get; }

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
        if (MaterializedView is MusicWidgetContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public override void OnWindowRevealCompleted()
    {
        ViewModel.OnWindowRevealCompleted();
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        ViewModel.OnCompactStateChanged(collapsed);
        if (MaterializedView is MusicWidgetContent content)
        {
            content.OnCompactStateChanged(collapsed);
        }
    }

    public void ApplyPerformanceSettings()
    {
        if (MaterializedView is MusicWidgetContent content)
        {
            content.ApplyPerformanceSettings();
        }
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (MaterializedView is MusicWidgetContent content)
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
        if (MaterializedView is MusicWidgetContent content)
        {
            content.CompleteResponsiveLayoutTransition(finalContentWidth, finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (MaterializedView is MusicWidgetContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    protected override void Dispose(bool disposing)
    {
        // Detach the View's PropertyChanged subscription by clearing the
        // ViewModel reference. The setter removes the event handler.
        if (MaterializedView is MusicWidgetContent musicContent)
        {
            musicContent.Dispose();
        }

        // Dispose the ViewModel first (stops timers, detaches service events).
        ViewModel.Dispose();

        ReleaseView();
    }
}
