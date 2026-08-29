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
    private FileTransferPathState _transferState = FileTransferPathState.None;
    private string _transferStatusText = string.Empty;
    private WidgetViewModel? _subscribedLayoutContext;
    private bool _isSurfaceLoaded;

    public FileItemSurface()
    {
        InitializeComponent();
        DataContextChanged += FileItemSurface_DataContextChanged;
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
        Mode == FileItemSurfaceMode.List
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;

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

    public Visibility PathTooltipVisibility =>
        LayoutContext?.ShowFileItemPathTooltips == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    public bool ToolTipEnabled =>
        TransferStatusVisibility == Visibility.Visible ||
        PathTooltipVisibility == Visibility.Visible;

    public Border InteractiveBorder => SurfaceBorder;

    public TextBlock ItemNameText =>
        Mode == FileItemSurfaceMode.List
            ? ListItemNameText
            : IconItemNameText;

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
        AutomationProperties.SetItemStatus(
            SurfaceBorder,
            normalizedStatus);
        OnPropertyChanged(nameof(TransferState));
        OnPropertyChanged(nameof(IsTransferActive));
        OnPropertyChanged(nameof(TransferBadgeVisibility));
        OnPropertyChanged(nameof(TransferStatusVisibility));
        OnPropertyChanged(nameof(TransferStatusText));
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
        _visualState = FileItemSurfaceVisualState.Normal;
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
        _isSurfaceLoaded = true;
        RefreshLayoutContextSubscription();
        SetVisualState(FileItemSurfaceVisualState.Normal);
        NotifyPresentationChanged();
    }

    private void SurfaceBorder_Unloaded(object sender, RoutedEventArgs e)
    {
        _isSurfaceLoaded = false;
        DetachLayoutContextSubscription();
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Hover);
    }

    private void SurfaceBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Pressed);
    }

    private void SurfaceBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Windows.Foundation.Point point = e.GetCurrentPoint(SurfaceBorder).Position;
        bool inside =
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X <= SurfaceBorder.ActualWidth &&
            point.Y <= SurfaceBorder.ActualHeight;
        SetVisualState(
            inside
                ? FileItemSurfaceVisualState.Hover
                : FileItemSurfaceVisualState.Normal);
    }

    private void SurfaceBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        SetVisualState(FileItemSurfaceVisualState.Normal);
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
