using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Adapter for existing widget views while content is migrated incrementally.
/// </summary>
public sealed class ExistingWidgetContent : IWidgetContent
{
    public ExistingWidgetContent(WidgetConfig config, FrameworkElement view)
    {
        Config = config;
        View = view;
    }

    public WidgetConfig Config { get; }
    public string WidgetId => Config.Id;
    public WidgetKind WidgetKind => Config.WidgetKind;
    public FrameworkElement View { get; }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        return Task.CompletedTask;
    }

    public void ApplyAppearance()
    {
    }

    public void OnActivated()
    {
    }

    public void OnDeactivated()
    {
    }
}
