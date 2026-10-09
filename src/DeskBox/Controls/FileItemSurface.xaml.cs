using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public enum FileItemSurfaceMode
{
    Icon,
    List
}

public sealed class FileItemSurfaceVisualStateChangedEventArgs(
    FileItemSurfaceVisualState state) : EventArgs
{
    public FileItemSurfaceVisualState State { get; } = state;
}

public sealed partial class FileItemSurface : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(FileItemSurfaceMode),
            typeof(FileItemSurface),
            new PropertyMetadata(FileItemSurfaceMode.Icon, OnPresentationPropertyChanged));

    public static readonly DependencyProperty LayoutContextProperty =
        DependencyProperty.Register(
            nameof(LayoutContext),
            typeof(WidgetViewModel),
            typeof(FileItemSurface),
            new PropertyMetadata(null, OnPresentationPropertyChanged));

    public static readonly DependencyProperty UseStackChildIndentProperty =
        DependencyProperty.Register(
            nameof(UseStackChildIndent),
            typeof(bool),
            typeof(FileItemSurface),
            new PropertyMetadata(false, OnPresentationPropertyChanged));

    public static readonly DependencyProperty ListItemTextMaxWidthProperty =
        DependencyProperty.Register(
            nameof(ListItemTextMaxWidth),
            typeof(double),
            typeof(FileItemSurface),
            new PropertyMetadata(double.PositiveInfinity));

    private FileItemSurfaceVisualState _visualState = FileItemSurfaceVisualState.Normal;
    private FileItemPointerFeedback _pointerFeedback;
    private FileTransferPathState _transferState = FileTransferPathState.None;
    private string _transferStatusText = string.Empty;
    private bool _isOpening;
    private string _openingStatusText = string.Empty;
    private WidgetViewModel? _subscribedLayoutContext;
    private bool _isSurfaceLoaded;
    private FrameworkElement? _iconLayout;
    private FrameworkElement? _listLayout;
    private TextBlock? _iconItemNameText;
    private TextBlock? _listItemNameText;
    private FrameworkElement? _iconIconHost;
    private FrameworkElement? _listIconHost;

    public FileItemSurface()
    {
        InitializeComponent();
        DataContextChanged += FileItemSurface_DataContextChanged;
        // PointerEntered alone is not a reliable recovery signal during
        // initial layout or container reuse. Observe movement even when a
        // child handles it, without consuming input or taking pointer capture.
        SurfaceBorder.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(SurfaceBorder_PointerMoved),
            handledEventsToo: true);
    }

    public event EventHandler<FileItemSurfaceVisualStateChangedEventArgs>? VisualStateChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileItemSurfaceMode Mode
    {
        get => (FileItemSurfaceMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public WidgetViewModel? LayoutContext
    {
        get => (WidgetViewModel?)GetValue(LayoutContextProperty);
        set => SetValue(LayoutContextProperty, value);
    }

    public bool UseStackChildIndent
    {
        get => (bool)GetValue(UseStackChildIndentProperty);
        set => SetValue(UseStackChildIndentProperty, value);
    }

    public double ListItemTextMaxWidth
    {
        get => (double)GetValue(ListItemTextMaxWidthProperty);
        set => SetValue(ListItemTextMaxWidthProperty, value);
    }

    public Visibility IconLayoutVisibility =>
        Mode == FileItemSurfaceMode.Icon
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ListLayoutVisibility =>
        Mode == FileItemSurfaceMode.List
            ? Visibility.Visible
            : Visibility.Collapsed;

    public HorizontalAlignment SurfaceHorizontalAlignment =>
        // Both layouts stretch so the interactive surface - hover, selection,
        // drop feedback - covers the full row in list mode (Explorer-style
        // full-row select) and the full tile in icon mode. The list content
        // itself stays left-aligned inside the stretched border.
        HorizontalAlignment.Stretch;

    public double SurfaceMaxWidth => double.PositiveInfinity;

    public Thickness SurfaceMargin
    {
        get
        {
            if (Mode == FileItemSurfaceMode.Icon)
            {
                // Match the Windows desktop selection footprint: occupy the
                // full icon column while retaining a narrow visual gutter.
                // Height remains content-driven so hidden, one-line, and
                // two-line labels keep their natural vertical spacing.
                return new Thickness(1, 0, 1, 0);
            }

            if (LayoutContext is null)
            {
                return new Thickness(0);
            }

            Thickness margin = LayoutContext.ListItemMargin;
            return UseStackChildIndent &&
                DataContext is WidgetItem { IsStackChild: true }
                ? new Thickness(
                    margin.Left + 18,
                    margin.Top,
                    margin.Right,
                    margin.Bottom)
                : margin;
        }
    }

    public Thickness SurfacePadding =>
        LayoutContext is null
            ? new Thickness(0)
            : Mode == FileItemSurfaceMode.List
                ? LayoutContext.ListItemPadding
                : LayoutContext.IconTilePadding;

    public FileItemSurfaceVisualState VisualState => _visualState;

    public FileTransferPathState TransferState => _transferState;

    public bool IsTransferActive => _transferState.IsActive;

    public Visibility TransferBadgeVisibility =>
        IsTransferActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TransferStatusVisibility =>
        string.IsNullOrWhiteSpace(_transferStatusText)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string TransferStatusText => _transferStatusText;

    /// <summary>
    /// Whether Windows Shell is currently handling an open request for this
    /// item. It shares the existing compact activity badge with transfers so
    /// opening a file does not add another visual tree per item.
    /// </summary>
    public bool IsOpening => _isOpening;

    public bool IsActivityActive => IsTransferActive || IsOpening;

    public Visibility ActivityBadgeVisibility =>
        IsActivityActive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActivityStatusVisibility =>
        string.IsNullOrWhiteSpace(ActivityStatusText)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string ActivityStatusText =>
        IsTransferActive ? _transferStatusText : _openingStatusText;

    public Visibility PathTooltipVisibility =>
        LayoutContext?.ShowFileItemPathTooltips == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool ToolTipEnabled =>
        ActivityStatusVisibility == Visibility.Visible ||
        PathTooltipVisibility == Visibility.Visible;

    public Border InteractiveBorder => SurfaceBorder;

    /// <summary>
    /// The icon glyph host of the active layout. Application-shortcut launch
    /// hit-testing scopes to this element, so only the icon - not the label
    /// or the tile padding around it - claims a launch drop.
    /// </summary>
    internal FrameworkElement? IconHitTestElement =>
        Mode == FileItemSurfaceMode.List ? _listIconHost : _iconIconHost;

    public TextBlock ItemNameText
    {
        get
        {
            // Rename can ask for the name before the first Loaded event.
            EnsureActiveLayout();
            return Mode == FileItemSurfaceMode.List
                ? _listItemNameText!
                : _iconItemNameText!;
        }
    }

    protected override Windows.Foundation.Size MeasureOverride(
        Windows.Foundation.Size availableSize)
    {
        // XAML assigns Mode after construction. Waiting until measurement
        // avoids creating an unused icon layout for every list-mode item,
        // while still measuring the real content before it can receive input.
        EnsureActiveLayout();
        return base.MeasureOverride(availableSize);
    }

    private void EnsureActiveLayout()
    {
        if (Mode == FileItemSurfaceMode.List)
        {
            if (_listLayout is null)
            {
                (_listLayout, _listItemNameText, _listIconHost) = CreateLayout(
                    "ListItemLayoutTemplate", "ListItemNameText", "ListItemIconHost");
            }
        }
        else if (_iconLayout is null)
        {
            (_iconLayout, _iconItemNameText, _iconIconHost) = CreateLayout(
                "IconItemLayoutTemplate", "IconItemNameText", "IconItemIconHost");
        }
    }

    private (FrameworkElement Layout, TextBlock NameText, FrameworkElement IconHost) CreateLayout(
        string templateKey,
        string nameElement,
        string iconHostElement)
    {
        var template = (DataTemplate)Resources[templateKey];
        var layout = (FrameworkElement)template.LoadContent();
        var nameText = (TextBlock)layout.FindName(nameElement);
        var iconHost = (FrameworkElement)layout.FindName(iconHostElement);
        var bindings = Microsoft.UI.Xaml.Markup.XamlBindingHelper
            .GetDataTemplateComponent(layout) ??
            throw new InvalidOperationException($"Missing file item bindings: {templateKey}");

        // Compiled presentation bindings read this surface. ProcessBindings
        // also detaches the generated DataContextChanged handler before the
        // layout inherits the WidgetItem from its unchanged interaction shell.
        // Ordinary file/thumbnail bindings therefore keep their original source.
        bindings.ProcessBindings(this, 0, 0, out _);
        LayoutHost.Children.Add(layout);
        return (layout, nameText, iconHost);
    }

    internal void SetTransferState(
        FileTransferPathState state,
        string? statusText)
    {
        string normalizedStatus = statusText ?? string.Empty;
        if (_transferState == state &&
            string.Equals(
                _transferStatusText,
                normalizedStatus,
                StringComparison.Ordinal))
        {
            return;
        }

        _transferState = state;
        _transferStatusText = normalizedStatus;
        OnPropertyChanged(nameof(TransferState));
        OnPropertyChanged(nameof(IsTransferActive));
        OnPropertyChanged(nameof(TransferBadgeVisibility));
        OnPropertyChanged(nameof(TransferStatusVisibility));
        OnPropertyChanged(nameof(TransferStatusText));
        UpdateActivityPresentation();
    }

    internal void SetOpeningState(
        bool isOpening,
        string? statusText)
    {
        string normalizedStatus = statusText ?? string.Empty;
        if (_isOpening == isOpening &&
            string.Equals(
                _openingStatusText,
                normalizedStatus,
                StringComparison.Ordinal))
        {
            return;
        }

        _isOpening = isOpening;
        _openingStatusText = normalizedStatus;
        OnPropertyChanged(nameof(IsOpening));
        UpdateActivityPresentation();
    }

    internal void ClearPointerFeedbackAfterOpen() =>
        SetVisualState(_pointerFeedback.OnOpenDispatched());

    private void UpdateActivityPresentation()
    {
        AutomationProperties.SetItemStatus(
            SurfaceBorder,
            ActivityStatusText);
        OnPropertyChanged(nameof(IsActivityActive));
        OnPropertyChanged(nameof(ActivityBadgeVisibility));
        OnPropertyChanged(nameof(ActivityStatusVisibility));
        OnPropertyChanged(nameof(ActivityStatusText));
        OnPropertyChanged(nameof(ToolTipEnabled));
    }

    public static Border? TryGetInteractiveBorder(object? source)
    {
        return source switch
        {
            FileItemSurface surface => surface.InteractiveBorder,
            Border border => border,
            _ => null
        };
    }

    public static FileItemSurface? FindOwner(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FileItemSurface surface)
            {
                return surface;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void OnPresentationPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is FileItemSurface surface)
        {
            if (args.Property == LayoutContextProperty)
            {
                surface.RefreshLayoutContextSubscription();
            }

            if (args.Property == ModeProperty)
            {
                if (surface._iconLayout is not null || surface._listLayout is not null)
                {
                    surface.EnsureActiveLayout();
                }
                surface.InvalidateMeasure();
            }

            surface.NotifyPresentationChanged();
        }
    }

    private void RefreshLayoutContextSubscription()
    {
        if (!_isSurfaceLoaded || ReferenceEquals(_subscribedLayoutContext, LayoutContext))
        {
            return;
        }

        if (_subscribedLayoutContext is not null)
        {
            _subscribedLayoutContext.PropertyChanged -= LayoutContext_PropertyChanged;
        }

        _subscribedLayoutContext = LayoutContext;
        if (_subscribedLayoutContext is not null)
        {
            _subscribedLayoutContext.PropertyChanged += LayoutContext_PropertyChanged;
        }
    }

    private void DetachLayoutContextSubscription()
    {
        if (_subscribedLayoutContext is not null)
        {
            _subscribedLayoutContext.PropertyChanged -= LayoutContext_PropertyChanged;
            _subscribedLayoutContext = null;
        }
    }

    private void LayoutContext_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyPresentationChanged();
    }

    private void FileItemSurface_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        // ListView virtualization can reuse a loaded surface for a different
        // item without raising Loaded again. Reset pointer state and ask the
        // host to reapply all item-dependent styling, especially cut opacity.
        _pointerFeedback.ResetForReuse();
        _visualState = FileItemSurfaceVisualState.Normal;
        SetOpeningState(false, string.Empty);
        SetTransferState(FileTransferPathState.None, string.Empty);
        VisualStateChanged?.Invoke(
            this,
            new FileItemSurfaceVisualStateChangedEventArgs(_visualState));
        OnPropertyChanged(nameof(VisualState));
        NotifyPresentationChanged();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(IconLayoutVisibility));
        OnPropertyChanged(nameof(ListLayoutVisibility));
        OnPropertyChanged(nameof(SurfaceHorizontalAlignment));
        OnPropertyChanged(nameof(SurfaceMaxWidth));
        OnPropertyChanged(nameof(SurfaceMargin));
        OnPropertyChanged(nameof(SurfacePadding));
        OnPropertyChanged(nameof(PathTooltipVisibility));
        OnPropertyChanged(nameof(ToolTipEnabled));
    }

    private void SurfaceBorder_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureActiveLayout();
        _isSurfaceLoaded = true;
        RefreshLayoutContextSubscription();
        _pointerFeedback.ResetForReuse();
        SetVisualState(FileItemSurfaceVisualState.Normal);
        NotifyPresentationChanged();
    }

    private void SurfaceBorder_Unloaded(object sender, RoutedEventArgs e)
    {
        _isSurfaceLoaded = false;
        DetachLayoutContextSubscription();
        SetOpeningState(false, string.Empty);
        _pointerFeedback.ResetForReuse();
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ObservePointerPosition(e);
        SetVisualState(_pointerFeedback.OnPointerEntered());
    }

    private void SurfaceBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SurfaceBorder);
        if (point.Position.X < 0 || point.Position.Y < 0 ||
            point.Position.X > SurfaceBorder.ActualWidth ||
            point.Position.Y > SurfaceBorder.ActualHeight)
        {
            return;
        }

        SetVisualState(_pointerFeedback.OnPointerMoved(
            _visualState,
            point.IsInContact,
            point.Position.X,
            point.Position.Y));
    }

    private void SurfaceBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ObservePointerPosition(e);
        SetVisualState(_pointerFeedback.OnPointerPressed());
    }

    private void SurfaceBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point point = ObservePointerPosition(e);
        bool inside =
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X <= SurfaceBorder.ActualWidth &&
            point.Y <= SurfaceBorder.ActualHeight;
        SetVisualState(_pointerFeedback.OnPointerReleased(inside));
    }

    private void SurfaceBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private Windows.Foundation.Point ObservePointerPosition(PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point point = e.GetCurrentPoint(SurfaceBorder).Position;
        _pointerFeedback.RecordPointerPosition(point.X, point.Y);
        return point;
    }

    private void SetVisualState(FileItemSurfaceVisualState state)
    {
        if (_visualState == state)
        {
            return;
        }

        _visualState = state;
        VisualStateChanged?.Invoke(
            this,
            new FileItemSurfaceVisualStateChangedEventArgs(state));
        OnPropertyChanged(nameof(VisualState));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
