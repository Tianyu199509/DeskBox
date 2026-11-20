using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace DeskBox.Controls;

internal enum DesktopOrganizationDragOrigin
{
    PreviewCard,
    StayOnDesktopTray
}

internal sealed record DesktopOrganizationDragStartedEventArgs(
    DesktopOrganizationDragOrigin Origin,
    string SourceBucketKey,
    IReadOnlyList<string> SourcePaths,
    Point RootPosition);

internal sealed record DesktopOrganizationDragMovedEventArgs(
    DesktopOrganizationDragOrigin Origin,
    string SourceBucketKey,
    IReadOnlyList<string> SourcePaths,
    Point RootPosition);

internal sealed record DesktopOrganizationDragCompletedEventArgs(
    DesktopOrganizationDragOrigin Origin,
    string SourceBucketKey,
    IReadOnlyList<string> SourcePaths,
    Point RootPosition,
    UIElement? OriginalSource);

/// <summary>
/// Manages root-scoped pointer drag detection and lifecycle for Desktop
/// Organization cards and the Stay-on-Desktop tray. Uses explicit capture on
/// the root element once the drag threshold is exceeded and guards intentional
/// capture release so synchronous PointerCaptureLost notifications never abort
/// valid drops.
/// </summary>
internal sealed class DesktopOrganizationPointerDragController : IDisposable
{
    public const double DragActivationThreshold = 6.0;

    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _releasedHandler;
    private readonly PointerEventHandler _canceledHandler;
    private readonly PointerEventHandler _captureLostHandler;

    private FrameworkElement? _rootElement;
    private bool _isDisposed;

    private uint? _activePointerId;
    private DesktopOrganizationDragOrigin _origin;
    private string _sourceBucketKey = string.Empty;
    private Point _pressRootPosition;
    private Point _lastRootPosition;
    private IReadOnlyList<string> _candidatePaths = [];
    private Func<IReadOnlyList<string>>? _payloadResolver;
    private Action? _clickAction;
    private WeakReference<UIElement>? _pressSourceElement;
    private Pointer? _capturedPointer;
    private bool _hasPointerCapture;
    private bool _isReleasingCaptureIntentionally;

    public DesktopOrganizationPointerDragController()
    {
        _movedHandler = OnRootPointerMoved;
        _releasedHandler = OnRootPointerReleased;
        _canceledHandler = OnRootPointerCanceled;
        _captureLostHandler = OnRootPointerCaptureLost;
    }

    public bool HasPendingCandidate => _activePointerId.HasValue;

    public bool IsDragging { get; private set; }

    public IReadOnlyList<string> ActiveSourcePaths =>
        IsDragging ? _candidatePaths : Array.Empty<string>();

    public DesktopOrganizationDragOrigin? ActiveOrigin =>
        IsDragging ? _origin : null;

    public string ActiveSourceBucketKey =>
        IsDragging ? _sourceBucketKey : string.Empty;

    public event EventHandler<DesktopOrganizationDragStartedEventArgs>? DragStarted;

    public event EventHandler<DesktopOrganizationDragMovedEventArgs>? DragMoved;

    public event EventHandler<DesktopOrganizationDragCompletedEventArgs>? DragCompleted;

    public event EventHandler? DragCanceled;

    public void Attach(FrameworkElement rootElement)
    {
        ArgumentNullException.ThrowIfNull(rootElement);
        Detach();

        _rootElement = rootElement;
        _rootElement.AddHandler(UIElement.PointerMovedEvent, _movedHandler, handledEventsToo: true);
        _rootElement.AddHandler(UIElement.PointerReleasedEvent, _releasedHandler, handledEventsToo: true);
        _rootElement.AddHandler(UIElement.PointerCanceledEvent, _canceledHandler, handledEventsToo: true);
        _rootElement.AddHandler(UIElement.PointerCaptureLostEvent, _captureLostHandler, handledEventsToo: true);
    }

    public void Detach()
    {
        CancelActiveDrag();
        if (_rootElement is null)
        {
            return;
        }

        _rootElement.RemoveHandler(UIElement.PointerMovedEvent, _movedHandler);
        _rootElement.RemoveHandler(UIElement.PointerReleasedEvent, _releasedHandler);
        _rootElement.RemoveHandler(UIElement.PointerCanceledEvent, _canceledHandler);
        _rootElement.RemoveHandler(UIElement.PointerCaptureLostEvent, _captureLostHandler);
        _rootElement = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Detach();
    }

    public bool TryBeginCandidate(
        UIElement sourceElement,
        PointerRoutedEventArgs e,
        DesktopOrganizationDragOrigin origin,
        string sourceBucketKey,
        Func<IReadOnlyList<string>> payloadResolver,
        Action? onClickWithoutDrag = null)
    {
        if (_isDisposed || _rootElement is null || _activePointerId.HasValue)
        {
            return false;
        }

        PointerPoint point = e.GetCurrentPoint(_rootElement);
        if (!IsPrimaryPress(point))
        {
            return false;
        }

        BeginCandidateCore(
            e.Pointer.PointerId,
            origin,
            sourceBucketKey,
            point.Position,
            payloadResolver,
            onClickWithoutDrag,
            sourceElement);
        return true;
    }

    public bool CancelActiveDrag()
    {
        if (!_activePointerId.HasValue)
        {
            return false;
        }

        bool wasDragging = IsDragging;
        _isReleasingCaptureIntentionally = true;
        try
        {
            ReleaseCaptureSafely();
        }
        finally
        {
            _isReleasingCaptureIntentionally = false;
        }

        ResetState();
        if (wasDragging)
        {
            DragCanceled?.Invoke(this, EventArgs.Empty);
        }

        return wasDragging;
    }

    internal void BeginCandidateCore(
        uint pointerId,
        DesktopOrganizationDragOrigin origin,
        string sourceBucketKey,
        Point rootPosition,
        Func<IReadOnlyList<string>> payloadResolver,
        Action? onClickWithoutDrag = null,
        UIElement? sourceElement = null)
    {
        _activePointerId = pointerId;
        _origin = origin;
        _sourceBucketKey = sourceBucketKey;
        _pressRootPosition = rootPosition;
        _lastRootPosition = rootPosition;
        _payloadResolver = payloadResolver;
        _candidatePaths = [];
        _clickAction = onClickWithoutDrag;
        _pressSourceElement = sourceElement is not null ? new WeakReference<UIElement>(sourceElement) : null;
        IsDragging = false;
        _hasPointerCapture = false;
        _capturedPointer = null;
    }

    internal bool UpdatePointerCore(uint pointerId, Point rootPosition, bool primaryButtonStillDown)
    {
        if (_activePointerId != pointerId)
        {
            return false;
        }

        if (!primaryButtonStillDown)
        {
            if (IsDragging)
            {
                CompleteDragSafely(rootPosition);
            }
            else
            {
                Action? click = _clickAction;
                ResetState();
                click?.Invoke();
            }

            return true;
        }

        _lastRootPosition = rootPosition;
        if (!IsDragging)
        {
            double deltaX = rootPosition.X - _pressRootPosition.X;
            double deltaY = rootPosition.Y - _pressRootPosition.Y;
            if ((deltaX * deltaX) + (deltaY * deltaY) < DragActivationThreshold * DragActivationThreshold)
            {
                return false;
            }

            IReadOnlyList<string> resolved = _payloadResolver?.Invoke() ?? Array.Empty<string>();
            if (resolved.Count == 0)
            {
                ResetState();
                return false;
            }

            _candidatePaths = resolved;
            IsDragging = true;
            DragStarted?.Invoke(
                this,
                new DesktopOrganizationDragStartedEventArgs(_origin, _sourceBucketKey, _candidatePaths, rootPosition));
        }

        DragMoved?.Invoke(
            this,
            new DesktopOrganizationDragMovedEventArgs(_origin, _sourceBucketKey, _candidatePaths, rootPosition));
        return true;
    }

    internal void ReleasePointerCore(uint pointerId, Point rootPosition, UIElement? fallbackOriginalSource = null)
    {
        if (_activePointerId != pointerId)
        {
            return;
        }

        if (!IsDragging)
        {
            Action? click = _clickAction;
            ResetState();
            click?.Invoke();
            return;
        }

        CompleteDragSafely(rootPosition, fallbackOriginalSource);
    }

    private void CompleteDragSafely(Point rootPosition, UIElement? fallbackOriginalSource = null)
    {
        _isReleasingCaptureIntentionally = true;
        try
        {
            ReleaseCaptureSafely();
            FinishDropCore(rootPosition, fallbackOriginalSource);
        }
        finally
        {
            _isReleasingCaptureIntentionally = false;
        }
    }

    internal void SetPointerCaptureStateForTest(bool hasCapture) => _hasPointerCapture = hasCapture;

    internal void HandleCaptureLossCore(uint pointerId)
    {
        if (_activePointerId != pointerId || _isReleasingCaptureIntentionally || !IsDragging || !_hasPointerCapture)
        {
            return;
        }

        _hasPointerCapture = false;
        _capturedPointer = null;
        ResetState();
        DragCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void FinishDropCore(Point rootPosition, UIElement? fallbackOriginalSource = null)
    {
        DesktopOrganizationDragOrigin origin = _origin;
        string bucketKey = _sourceBucketKey;
        IReadOnlyList<string> paths = _candidatePaths;
        UIElement? sourceElement = null;
        _pressSourceElement?.TryGetTarget(out sourceElement);
        UIElement? originalSource = sourceElement ?? fallbackOriginalSource ?? _rootElement;

        ResetState();
        DragCompleted?.Invoke(
            this,
            new DesktopOrganizationDragCompletedEventArgs(origin, bucketKey, paths, rootPosition, originalSource));
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rootElement is null || _activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(_rootElement);
        bool primaryDown = IsPrimaryHeld(point);
        bool wasDragging = IsDragging;
        bool handled = UpdatePointerCore(e.Pointer.PointerId, point.Position, primaryDown);
        if (!wasDragging && IsDragging)
        {
            TryAcquireRootCapture(e.Pointer);
        }

        if (handled && IsDragging)
        {
            e.Handled = true;
        }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rootElement is null || _activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        bool wasDragging = IsDragging;
        Point rootPos = e.GetCurrentPoint(_rootElement).Position;
        ReleasePointerCore(e.Pointer.PointerId, rootPos, e.OriginalSource as UIElement);
        if (wasDragging)
        {
            e.Handled = true;
        }
    }

    private void OnRootPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        CancelActiveDrag();
    }

    private void OnRootPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        HandleCaptureLossCore(e.Pointer.PointerId);
    }

    private void TryAcquireRootCapture(Pointer pointer)
    {
        if (_rootElement is null || _hasPointerCapture)
        {
            return;
        }

        try
        {
            if (_pressSourceElement is not null &&
                _pressSourceElement.TryGetTarget(out UIElement? sourceElement))
            {
                sourceElement.CancelDirectManipulations();
            }

            _rootElement.CancelDirectManipulations();
            _hasPointerCapture = _rootElement.CapturePointer(pointer);
            if (_hasPointerCapture)
            {
                _capturedPointer = pointer;
            }
        }
        catch
        {
            _hasPointerCapture = false;
            _capturedPointer = null;
        }
    }

    private void ReleaseCaptureSafely()
    {
        if (_rootElement is null || !_hasPointerCapture || _capturedPointer is null)
        {
            _hasPointerCapture = false;
            _capturedPointer = null;
            return;
        }

        try
        {
            _rootElement.ReleasePointerCapture(_capturedPointer);
        }
        catch
        {
        }
        finally
        {
            _hasPointerCapture = false;
            _capturedPointer = null;
        }
    }

    private void ResetState()
    {
        _activePointerId = null;
        _origin = DesktopOrganizationDragOrigin.PreviewCard;
        _sourceBucketKey = string.Empty;
        _candidatePaths = [];
        _payloadResolver = null;
        _clickAction = null;
        _pressSourceElement = null;
        _capturedPointer = null;
        _hasPointerCapture = false;
        IsDragging = false;
    }

    private static bool IsPrimaryPress(PointerPoint point) =>
        point.PointerDeviceType switch
        {
            PointerDeviceType.Mouse => point.Properties.IsLeftButtonPressed,
            PointerDeviceType.Pen => point.Properties.IsLeftButtonPressed || point.IsInContact,
            _ => point.IsInContact
        };

    private static bool IsPrimaryHeld(PointerPoint point) =>
        point.PointerDeviceType switch
        {
            PointerDeviceType.Mouse => point.Properties.IsLeftButtonPressed,
            PointerDeviceType.Pen => point.Properties.IsLeftButtonPressed || point.IsInContact,
            _ => point.IsInContact
        };
}
