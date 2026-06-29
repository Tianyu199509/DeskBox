using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Contracts;

/// <summary>
/// Common contract for the content area of every widget kind.
/// Window, z-order, animation, and DWM behavior remain owned by the host window.
/// </summary>
public interface IWidgetContent
{
    WidgetConfig Config { get; }
    string WidgetId { get; }
    WidgetKind WidgetKind { get; }
    FrameworkElement View { get; }

    Task InitializeAsync();
    Task RefreshAsync();
    void ApplyAppearance();
    void OnActivated();
    void OnDeactivated();
}
