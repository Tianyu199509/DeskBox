using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Shared residency plumbing for widget content adapters. The adapter owns
/// the view model and the lazy view; the leaf control stays a disposable
/// view (widget-group-residency-roadmap-20260918 sections 1.5 and 4.6).
/// Per revision R8 the base class only forwards lifecycle and identity —
/// never business behavior — so every kind-specific contract keeps living
/// on its adapter subclass.
/// </summary>
public abstract class WidgetContentAdapterBase : IWidgetContent, IDisposable
{
    private readonly Func<FrameworkElement> _viewFactory;
    private FrameworkElement? _view;
    private bool _isDisposed;

    protected WidgetContentAdapterBase(
        WidgetConfig config,
        Func<FrameworkElement> viewFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(viewFactory);

        Config = config;
        _viewFactory = viewFactory;
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    /// <summary>
    /// The lazy leaf view. Materializing happens on the first access inside
    /// the owning switch transaction; accessing after disposal without a
    /// materialized view is a programming error and throws.
    ///
    /// Disposal policy (uniform across adapters): Dispose tears down the leaf
    /// and the view model but deliberately keeps the materialized view
    /// reference returnable, matching the File/QuickCapture semantics the
    /// residency roadmap froze. Callers must treat a view handed back after
    /// Dispose as a detached, already-disposed element and never re-attach
    /// it; only ReleaseView (the cold-eviction primitive) clears the
    /// reference so a later access rematerializes.
    /// </summary>
    public FrameworkElement View
    {
        get
        {
            if (_view is null)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                _view = _viewFactory();
                OnViewMaterialized(_view);
            }

            return _view;
        }
    }

    /// <summary>
    /// The materialized leaf, or null when it was never attached. Reading
    /// this never constructs the view; cache probes rely on that.
    /// </summary>
    protected FrameworkElement? MaterializedView => _view;

    protected bool IsDisposed => _isDisposed;

    /// <summary>
    /// Drops the materialized view without disposing the adapter or the
    /// view model (the residency roadmap's release-view primitive). The
    /// next View access rematerializes through the factory.
    ///
    /// Precondition before enabling this for cached-member eviction: a cold
    /// adapter transient capture strategy must be defined first. Today the
    /// group transient-capture path treats a null CaptureTransientState
    /// result as "nothing to preserve" and silently discards it, so evicting
    /// an adapter whose leaf held un-captured state would lose that state on
    /// rematerialization.
    /// </summary>
    protected void ReleaseView()
    {
        _view = null;
    }

    /// <summary>
    /// Raised once when the leaf is first materialized; adapters subscribe
    /// leaf events and replay host callbacks (like the window handle) here.
    /// </summary>
    protected virtual void OnViewMaterialized(FrameworkElement view)
    {
    }

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task RefreshAsync() => Task.CompletedTask;

    public virtual void ApplyAppearance()
    {
    }

    public virtual void OnActivated()
    {
    }

    public virtual void OnDeactivated()
    {
    }

    public virtual void OnWindowVisibilityChanged(bool visible)
    {
    }

    public virtual void OnWindowRevealCompleted()
    {
    }

    public virtual void OnWindowLongHidden()
    {
    }

    public virtual void OnCompactStateChanged(bool collapsed)
    {
    }

    public virtual void OnCompactBoundsTransitionActiveChanged(bool isActive)
    {
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Dispose(disposing: true);
    }

    /// <summary>
    /// Subclasses release their leaf subscriptions and dispose the view
    /// model here. The leaf dispose chain is expected to dispose the view
    /// model, matching the pre-adapter ownership.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}
