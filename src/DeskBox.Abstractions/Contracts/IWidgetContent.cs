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

    /// <summary>
    /// Called once the host's reveal animation has completed. Expensive refresh,
    /// watcher, and device work should resume here so it cannot contend with the
    /// first visible frame.
    /// </summary>
    void OnWindowRevealCompleted() { }

    /// <summary>
    /// Called only after the host has remained hidden for the configured long-idle
    /// interval. Implementations may cancel transient rendering work, but must not
    /// discard the view, its data projection, warm media, or background resources
    /// whose recreation would add work to the next reveal or first interaction.
    /// </summary>
    void OnWindowLongHidden() { }

    /// <summary>
    /// Called when the host switches between its expanded content and capsule
    /// presentation. Content that owns purely visual animations can suspend them
    /// while the expanded surface is covered without suspending its live data.
    /// </summary>
    void OnCompactStateChanged(bool collapsed) { }
}

/// <summary>
/// Optional lifecycle contract for content whose initialization can be stopped
/// when a newer group-member switch supersedes it.
/// </summary>
public interface ICancellableWidgetContent
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Opts a content surface into the small, per-group inactive-member cache.
/// Cacheable content must tolerate OnDeactivated/OnActivated cycles and must
/// release all resources when disposed by the owning content window.
/// </summary>
public interface IWidgetGroupContentCacheable : IDisposable
{
    bool IsReadyForReuse { get; }

    void PrepareForReuse() { }
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

/// <summary>
/// Optional contract for content that must lay itself out from the host's
/// viewport instead of its own desired size. This avoids a child retaining an
/// obsolete width when its internal minimums are wider than the resized host.
/// </summary>
public interface IWidgetHostViewportContent
{
    void OnHostViewportSizeChanged(double width, double height);
}

/// <summary>
/// Optional contract for content that owns continuous decorative effects and
/// can apply performance-setting changes without rebuilding the content view.
/// </summary>
public interface IWidgetPerformanceAwareContent
{
    void ApplyPerformanceSettings();
}

/// <summary>
/// Optional lifecycle for a live user resize. Expensive readers and adaptive
/// measurements can stay frozen while the HWND follows the pointer, then run
/// once against the committed size.
/// </summary>
public interface IWidgetInteractiveResizeContent
{
    void BeginInteractiveResize(double contentWidth, double contentHeight);
    void CompleteInteractiveResize(double contentWidth, double contentHeight);
}
