using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Adapts SearchWidgetContent to the IWidgetContent contract.
/// </summary>
public sealed class SearchWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetResponsiveLayoutContent
{
    private EventHandler? _searchRequested;

    public SearchWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Func<FrameworkElement>? viewFactory = null)
        : base(
            config,
            viewFactory ?? (() => new SearchWidgetContent(localizationService, settingsService)))
    {
        if (config.WidgetKind != WidgetKind.Search)
        {
            throw new ArgumentException("Search content requires a Search widget config.", nameof(config));
        }
    }

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is SearchWidgetContent content)
        {
            content.SearchRequested += Content_SearchRequested;
        }
    }

    /// <summary>
    /// Raised when the user clicks the widget to open the search popup.
    /// The adapter holds the handler list itself: subscribing a cold
    /// adapter must not materialize the view tree (roadmap section 4.5,
    /// Search row — the accessors used to read View).
    /// </summary>
    public event EventHandler? SearchRequested
    {
        add => _searchRequested += value;
        remove => _searchRequested -= value;
    }

    private void Content_SearchRequested(object? sender, EventArgs e)
    {
        _searchRequested?.Invoke(this, e);
    }

    public override Task RefreshAsync()
    {
        if (MaterializedView is SearchWidgetContent content)
        {
            content.UpdateContent();
        }

        return Task.CompletedTask;
    }

    public override void ApplyAppearance()
    {
        if (MaterializedView is SearchWidgetContent content)
        {
            content.ApplyAppearance();
        }
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (MaterializedView is SearchWidgetContent content)
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
        if (MaterializedView is SearchWidgetContent content)
        {
            content.CompleteResponsiveLayoutTransition(
                finalContentWidth,
                finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (MaterializedView is SearchWidgetContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (MaterializedView is SearchWidgetContent content)
        {
            content.SearchRequested -= Content_SearchRequested;
        }

        (MaterializedView as IDisposable)?.Dispose();
        _searchRequested = null;
    }
}
