using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Adapts SearchWidgetContent to the IWidgetContent contract.
/// </summary>
public sealed class SearchWidgetContentAdapter : IWidgetContent
{
    private readonly Func<FrameworkElement> _viewFactory;
    private FrameworkElement? _view;

    public SearchWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        Func<FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Search)
        {
            throw new ArgumentException("Search content requires a Search widget config.", nameof(config));
        }

        Config = config;
        _viewFactory = viewFactory ?? (() => new SearchWidgetContent(localizationService, settingsService));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View
    {
        get
        {
            _view ??= _viewFactory();
            return _view;
        }
    }

    /// <summary>
    /// Raised when the user clicks the widget to open the search popup.
    /// </summary>
    public event EventHandler? SearchRequested
    {
        add
        {
            if (View is SearchWidgetContent content)
            {
                content.SearchRequested += value;
            }
        }
        remove
        {
            if (View is SearchWidgetContent content)
            {
                content.SearchRequested -= value;
            }
        }
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        if (_view is SearchWidgetContent content)
        {
            content.UpdateContent();
        }

        return Task.CompletedTask;
    }

    public void ApplyAppearance()
    {
        if (_view is SearchWidgetContent content)
        {
            content.ApplyAppearance();
        }
    }

    public void OnActivated()
    {
    }

    public void OnDeactivated()
    {
    }
}
