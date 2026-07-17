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

    /// <summary>
    /// Called when the host window becomes visible or hidden.
    /// Use this to start/stop animations and timers based on actual visibility,
    /// independent of activation state.
    /// </summary>
    void OnWindowVisibilityChanged(bool visible) { }
}

/// <summary>
/// Optional contract for content whose layout changes at size breakpoints.
/// Capsule transitions can lock that content to its start or target layout
/// instead of letting intermediate animated window sizes trigger every layout.
/// </summary>
public interface IWidgetResponsiveLayoutContent
{
    void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing);

    void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight);

    void CancelResponsiveLayoutTransition();
}
