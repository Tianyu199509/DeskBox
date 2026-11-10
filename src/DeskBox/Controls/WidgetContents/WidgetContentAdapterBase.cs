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
    private object? _hibernatedTransientState;
    private bool _hasHibernatedTransientState;
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
                FrameworkElement createdView = _viewFactory();
                _view = createdView;
                bool restoreTransientState = _hasHibernatedTransientState;
                object? savedState = _hibernatedTransientState;
                _hasHibernatedTransientState = false;
                _hibernatedTransientState = null;
                if (restoreTransientState &&
                    this is IWidgetTransientStateContent transientContent)
                {
                    transientContent.RestoreTransientState(savedState);
                }

                OnViewMaterialized(createdView);
            }

            return _view;
        }
    }

    /// <summary>
    /// True when the leaf view is currently materialized in memory. Reading
    /// this never constructs the view.
    /// </summary>
    internal bool HasMaterializedView => _view is not null;

    /// <summary>
    /// The materialized leaf, or null when it was never attached. Reading
    /// this never constructs the view; cache probes rely on that.
    /// </summary>
    protected FrameworkElement? MaterializedView => _view;

    protected internal bool IsDisposed => _isDisposed;

    protected bool HasHibernatedTransientState => _hasHibernatedTransientState;

    protected object? HibernatedTransientState =>
        _hasHibernatedTransientState ? _hibernatedTransientState : null;

    protected void SaveHibernatedTransientState(object? state)
    {
        _hibernatedTransientState = state;
        _hasHibernatedTransientState = true;
    }

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
    /// Captures transient leaf state, detaches leaf event subscriptions,
    /// disposes the leaf view only (keeping the adapter and its view model
    /// alive), and clears the materialized view reference so the next View
    /// access rematerializes and restores transient state seamlessly.
    /// </summary>
    internal bool HibernateCollapsedView()
    {
        if (_isDisposed ||
            _view is null ||
            this is IWidgetGroupContentCacheable { HasActiveOutboundDrag: true })
        {
            return false;
        }

        if (this is IWidgetTransientStateContent transientContent)
        {
            _hibernatedTransientState = transientContent.CaptureTransientState();
            _hasHibernatedTransientState = true;
        }

        FrameworkElement view = _view;
        ReleaseView();
        OnHibernatingView(view);
        return true;
    }

    /// <summary>
    /// Raised once when the leaf is first materialized; adapters subscribe
    /// leaf events and replay host callbacks (like the window handle) here.
    /// </summary>
    protected virtual void OnViewMaterialized(FrameworkElement view)
    {
    }

    /// <summary>
    /// Raised during collapsed hibernation after transient state has been
    /// captured and the materialized view reference has been cleared;
    /// subclasses detach leaf subscriptions and dispose the leaf view without
    /// disposing the resident view model.
    /// </summary>
    protected virtual void OnHibernatingView(FrameworkElement view)
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
        _hibernatedTransientState = null;
        _hasHibernatedTransientState = false;
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
