using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow : Window
{
    private enum ItemSurfaceState
    {
        Normal,
        Hover,
        Pressed
    }

    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;

    private readonly Microsoft.UI.WindowId _windowId;
    private readonly SettingsService _settingsService;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private bool _isDragging;
    private bool _isResizing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private Windows.Graphics.PointInt32 _initialWindowPos;
    private Windows.Graphics.SizeInt32 _initialWindowSize;
    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private bool _emptyStateUpdateQueued;
    private bool _deletePending;
    private string[] _cutClipboardPaths = [];
    private int _lastSelectionAnchorIndex = -1;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<WidgetItem> _selectionSnapshot = [];
    private Flyout? _itemRenameFlyout;
    private TextBox? _itemRenameTextBox;
    private WidgetItem? _itemRenameTarget;
    private bool _isCommittingItemRename;
    private DateTime _lastTitleBarClickTimeUtc;
    private Win32Helper.POINT _lastTitleBarClickPoint;
    private bool _hasPendingTitleBarClick;

    public WidgetViewModel ViewModel { get; }

    private bool _isVisibleOnDesktop;
    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    public new void Activate()
    {
        base.Activate();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
    }

    public WidgetWindow(WidgetViewModel viewModel, SettingsService settingsService)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        TitleEditBox.PlaceholderText = "\u8f93\u5165\u7ec4\u4ef6\u540d\u79f0";
        ToolTipService.SetToolTip(AddButton, "\u6dfb\u52a0\u9879\u76ee");
        ToolTipService.SetToolTip(MoreButton, "\u66f4\u591a\u9009\u9879");
        ToolTipService.SetToolTip(CloseButton, "\u5220\u9664\u7ec4\u4ef6");

        _hWnd = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);

        ConfigureWindow();
        SetupEventHandlers();

        ViewModel.Items.CollectionChanged += (_, _) => QueueEmptyStateUpdate();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEmptyState();
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            _hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        _appWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;

        var config = ViewModel.Config;
        ApplyWindowBounds(
            (int)config.X,
            (int)config.Y,
            (int)config.Width,
            (int)config.Height,
            persist: false);

        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));

        int cornerPreference = Win32Helper.DWMWCP_ROUNDSMALL;
        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(_hWnd);
        ApplyBackdropPreference();

        RootGrid.Loaded += (_, _) =>
        {
            var parent = VisualTreeHelper.GetParent(RootGrid) as FrameworkElement;
            while (parent is not null)
            {
                if (parent is Control control)
                {
                    control.Background = new SolidColorBrush(Colors.Transparent);
                }
                else if (parent is Panel panel)
                {
                    panel.Background = new SolidColorBrush(Colors.Transparent);
                }
                else if (parent is Border border)
                {
                    border.Background = new SolidColorBrush(Colors.Transparent);
                }
                else if (parent is ContentPresenter presenter)
                {
                    presenter.Background = new SolidColorBrush(Colors.Transparent);
                }

                parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
            }

            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
        };

        RootGrid.ActualThemeChanged += (_, _) => ApplyBackdropPreference();
        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;

        Activated += (_, _) => DispatcherQueue.TryEnqueue(ApplyBackdropPreference);

        _appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                var pos = _appWindow.Position;
                var size = _appWindow.Size;
                ViewModel.UpdateBounds(pos.X, pos.Y, size.Width, size.Height, persist: false);
            }
        };

        foreach (var child in ResizeGrid.Children)
        {
            if (child is FrameworkElement element && element.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                element.PointerMoved += ResizeBorder_PointerMoved;
                element.PointerReleased += ResizeBorder_PointerReleased;
            }
        }

        Closed += (_, _) =>
        {
            Visible = false;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        };
    }

    public void PushToBottom()
    {
        Win32Helper.SetWindowToBottom(_hWnd);
    }

    public void RevealFromTray()
    {
        ElevateForInteraction();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        base.Activate();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();

        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(1200);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_isDragging && !_isResizing)
            {
                RestoreDesktopLayer();
            }
        };
        timer.Start();
    }

    public void HideWindow()
    {
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        Visible = false;
        ViewModel.Config.IsVisible = false;
        _settingsService.SaveDebounced();
    }

    private void ElevateForInteraction()
    {
        Win32Helper.BringWindowToFront(_hWnd);
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void RestoreDesktopLayer()
    {
        if (_isDragging || _isResizing || TitleEditBox.Visibility == Visibility.Visible || _deletePending)
        {
            return;
        }

        if (DeleteConfirmBar.Visibility == Visibility.Visible)
        {
            return;
        }

        PushToBottom();
        ApplyBackdropPreference();
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist)
    {
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        ViewModel.UpdateBounds(x, y, width, height, persist: false);

        if (persist)
        {
            ViewModel.UpdateBounds(x, y, width, height, persist: true);
        }
    }

    private void OnSettingsChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyBackdropPreference();
            UpdateInteractiveSurfaces();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyBackdropPreference();
            UpdateInteractiveSurfaces();
        });
    }

    private void ApplyBackdropPreference()
    {
        bool useNativeBlur = _settingsService.Settings.UseNativeBackdropBlur;
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xF7, 0xF7, 0xF8);

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

            int backdropType;
            if (useNativeBlur)
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                SystemBackdrop ??= new DesktopAcrylicBackdrop();
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                SystemBackdrop = null;
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{_hWnd.ToInt64():X} useNativeBlur={useNativeBlur} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} systemBackdrop={(SystemBackdrop?.GetType().Name ?? "null")}");

            if (useNativeBlur)
            {
                // Keep the window composition layer neutral and let the widget surface provide the tint.
                Win32Helper.ApplyAccentBlur(_hWnd, tintColor, 0.0, enabled: false);
            }
            else
            {
                Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.56), enabled: false);
            }
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            SystemBackdrop = null;
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    private void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        double materialOpacity = Math.Clamp(surfaceOpacity * 0.78, 0.10, 0.82);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;

        var backgroundColor = ApplySurfaceOpacity(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x21, 0x24, 0x2A)
                    : ColorHelper.FromArgb(0xFF, 0xFB, 0xFB, 0xFC),
                accentMix: isDark ? 0.18 : 0.11,
                overlayMix: isDark ? 0.15 : 0.10),
            materialOpacity);

        var borderColor = isDark
            ? ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x12, 0x00, 0x00, 0x00);

        var dividerColor = isDark
            ? ColorHelper.FromArgb(0x10, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x0A, 0x00, 0x00, 0x00);

        var iconForeground = Windows.UI.Color.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        var editorBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x1A, 0x1C, 0x21)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: isDark ? 0.14 : 0.08,
            overlayMix: isDark ? 0.12 : 0.08);

        var editorBorder = isDark
            ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);

        var confirmBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x22, 0x24, 0x29)
                : ColorHelper.FromArgb(0xFF, 0xFA, 0xFA, 0xFB),
            accentMix: isDark ? 0.06 : 0.03,
            overlayMix: isDark ? 0.08 : 0.04);

        var confirmBorder = isDark
            ? ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);

        var secondaryText = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.Background = new SolidColorBrush(backgroundColor);
        BackgroundPlate.BorderBrush = new SolidColorBrush(borderColor);
        HeaderDivider.Background = new SolidColorBrush(dividerColor);
        TitleIcon.Foreground = new SolidColorBrush(iconForeground);
        TitleEditBox.Background = new SolidColorBrush(editorBackground);
        TitleEditBox.BorderBrush = new SolidColorBrush(editorBorder);
        TitleEditBox.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
        DeleteConfirmBar.Background = new SolidColorBrush(confirmBackground);
        DeleteConfirmBar.BorderBrush = new SolidColorBrush(confirmBorder);
        DeleteConfirmText.Foreground = new SolidColorBrush(secondaryText);
        EmptyStateTitleText.Foreground = new SolidColorBrush(isDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        EmptyStateDescriptionText.Foreground = new SolidColorBrush(secondaryText);
        EmptyStateIcon.Foreground = new SolidColorBrush(secondaryText);
        SelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));

        UpdateInteractiveSurfaces();
    }

    private void UpdateInteractiveSurfaces()
    {
        foreach (var border in FindInteractiveSurfaceBorders(RootGrid))
        {
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private IEnumerable<Border> FindInteractiveSurfaceBorders(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.Tag as string == "InteractiveSurface")
            {
                yield return border;
            }

            foreach (var nested in FindInteractiveSurfaceBorders(child))
            {
                yield return nested;
            }
        }
    }

    private void ApplyWidgetItemSurfaceState(Border border, ItemSurfaceState state)
    {
        bool useNativeBlur = _settingsService.Settings.UseNativeBackdropBlur;
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var item = border.DataContext as WidgetItem;
        bool isSelected = item?.IsSelected == true;
        bool isCut = item?.IsCut == true;

        var defaultBackground = useNativeBlur
            ? ColorHelper.FromArgb(0x00, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x00, 0x00, 0x00, 0x00);

        var selectedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x31, 0x36, 0x3E)
                    : ColorHelper.FromArgb(0xFF, 0xF1, 0xF6, 0xFC),
                accentMix: isDark ? 0.30 : 0.18,
                overlayMix: isDark ? 0.08 : 0.04),
            useNativeBlur
                ? (isDark ? (byte)0x34 : (byte)0x40)
                : (isDark ? (byte)0x24 : (byte)0x1C));

        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2A, 0x2D, 0x33)
                    : ColorHelper.FromArgb(0xFF, 0xFB, 0xFB, 0xFC),
                accentMix: isDark ? 0.20 : 0.12,
                overlayMix: isDark ? 0.12 : 0.18),
            useNativeBlur
                ? (isDark ? (byte)0x1A : (byte)0x22)
                : (isDark ? (byte)0x12 : (byte)0x10));

        var pressedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2D, 0x30, 0x37)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xFA),
                accentMix: isDark ? 0.24 : 0.15,
                overlayMix: isDark ? 0.10 : 0.16),
            useNativeBlur
                ? (isDark ? (byte)0x26 : (byte)0x30)
                : (isDark ? (byte)0x18 : (byte)0x16));

        var selectedHoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x34, 0x39, 0x42)
                    : ColorHelper.FromArgb(0xFF, 0xEC, 0xF4, 0xFC),
                accentMix: isDark ? 0.34 : 0.21,
                overlayMix: isDark ? 0.08 : 0.05),
            useNativeBlur
                ? (isDark ? (byte)0x3C : (byte)0x48)
                : (isDark ? (byte)0x28 : (byte)0x20));

        var backgroundColor = state switch
        {
            ItemSurfaceState.Hover when isSelected => selectedHoverBackground,
            ItemSurfaceState.Pressed when isSelected => selectedHoverBackground,
            ItemSurfaceState.Hover => hoverBackground,
            ItemSurfaceState.Pressed => pressedBackground,
            _ when isSelected => selectedBackground,
            _ => defaultBackground
        };

        border.Background = new SolidColorBrush(backgroundColor);
        border.BorderBrush = new SolidColorBrush(Colors.Transparent);
        border.BorderThickness = new Thickness(0);
        border.Opacity = isCut ? 0.58 : 1.0;
    }

    private void ApplyWidgetItemLayout(Border border)
    {
        if (IsWithinItemsListView(border))
        {
            border.Width = double.NaN;
            border.Height = double.NaN;
            border.Margin = ViewModel.ListItemMargin;
            border.Padding = ViewModel.ListItemPadding;
            border.CornerRadius = new CornerRadius(8);

            if (border.Child is Grid listGrid)
            {
                listGrid.ColumnSpacing = 10;

                if (TryGetDescendant<Image>(listGrid, out var icon))
                {
                    icon.Width = ViewModel.ListIconSize;
                    icon.Height = ViewModel.ListIconSize;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var label))
                {
                    label.FontSize = ViewModel.ListLabelFontSize;
                }
            }

            return;
        }

        border.Width = ViewModel.IconTileWidth;
        border.Height = ViewModel.IconTileHeight;
        border.Margin = ViewModel.IconTileMargin;
        border.Padding = ViewModel.IconTilePadding;
        border.CornerRadius = new CornerRadius(8);

        if (border.Child is Grid iconGrid)
        {
            iconGrid.RowSpacing = ViewModel.IconContentSpacing;

            if (TryGetDescendant<Image>(iconGrid, out var icon))
            {
                icon.Width = ViewModel.IconImageSize;
                icon.Height = ViewModel.IconImageSize;
            }

            if (TryGetDescendant<TextBlock>(iconGrid, out var label))
            {
                label.FontSize = ViewModel.IconLabelFontSize;
            }
        }
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                result = typedChild;
                return true;
            }

            if (TryGetDescendant(child, out result))
            {
                return true;
            }
        }

        result = null!;
        return false;
    }

    private bool IsWithinItemsListView(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ItemsListView))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ViewModel_PropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName is nameof(WidgetViewModel.IsLoading))
        {
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.WidgetOpacity) or nameof(WidgetViewModel.MappedFolderPath))
        {
            ApplyBackdropPreference();
            UpdateEmptyState();
        }
    }

    private ListViewBase? GetActiveItemsView()
    {
        if (ViewModel.IsIconMode)
        {
            return ItemsGridView;
        }

        if (ViewModel.IsListMode)
        {
            return ItemsListView;
        }

        return null;
    }

    private List<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView()?.SelectedItems.OfType<WidgetItem>().ToList() ?? [];
    }

    private WidgetItem? GetPrimarySelectedItem()
    {
        return GetSelectedItems().FirstOrDefault();
    }

    private void ApplySelectionState(ListViewBase? listView)
    {
        var selectedItems = listView?.SelectedItems.OfType<WidgetItem>().ToHashSet() ?? [];
        foreach (var item in ViewModel.Items)
        {
            item.IsSelected = selectedItems.Contains(item);
            if (!item.IsSelected)
            {
                item.IsCut = false;
            }
        }

        UpdateInteractiveSurfaces();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
        if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath) &&
            string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = "\u5df2\u5728\u5f53\u524d\u7ec4\u4ef6\u4e2d";
            return;
        }

        bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
        bool copyRequested = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        e.AcceptedOperation = movesIntoFolder
            ? (copyRequested ? DataPackageOperation.Copy : DataPackageOperation.Move)
            : DataPackageOperation.Link;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = movesIntoFolder
            ? $"{(e.AcceptedOperation == DataPackageOperation.Copy ? "\u590d\u5236" : "\u79fb\u52a8")}\u5230\u6536\u7eb3\u7ec4\u4ef6"
            : "\u6dfb\u52a0\u5230\u5f15\u7528\u7ec4\u4ef6";
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path));
        bool? moveWhenMapped = !string.IsNullOrEmpty(ViewModel.MappedFolderPath)
            ? e.AcceptedOperation != DataPackageOperation.Copy
            : null;
        await ViewModel.ImportPathsAsync(paths, moveWhenMapped);

        if (moveWhenMapped == true)
        {
            await SyncMoveSourceAsync(
                TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId"),
                TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths"));
        }

        ClearCutState();
    }

    private void ItemsView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WidgetItem item)
        {
            return;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        if (!_settingsService.Settings.DoubleClickToOpen)
        {
            ViewModel.OpenItemCommand.Execute(item);
        }
    }

    private void ItemsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_settingsService.Settings.DoubleClickToOpen &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item)
        {
            ViewModel.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void ItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not WidgetItem item)
        {
            return;
        }

        var listView = GetActiveItemsView();
        if (listView is not null && !item.IsSelected)
        {
            listView.SelectedItems.Clear();
            listView.SelectedItems.Add(item);
            ApplySelectionState(listView);
        }

        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = "\u6253\u5f00",
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        openItem.Click += (_, _) => ViewModel.OpenItemCommand.Execute(item);
        flyout.Items.Add(openItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyItem = new MenuFlyoutItem
        {
            Text = "\u590d\u5236",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: false);
        flyout.Items.Add(copyItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = "\u526a\u5207",
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: true);
        flyout.Items.Add(cutItem);

        var showItem = new MenuFlyoutItem
        {
            Text = "\u5728\u8d44\u6e90\u7ba1\u7406\u5668\u4e2d\u663e\u793a",
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        showItem.Click += (_, _) => ViewModel.ShowInExplorerCommand.Execute(item);
        flyout.Items.Add(showItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = "\u91cd\u547d\u540d",
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += async (_, _) => await StartItemRenameAsync(item);
        flyout.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "\u5220\u9664",
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        var propItem = new MenuFlyoutItem
        {
            Text = "\u5c5e\u6027",
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        propItem.Click += (_, _) => ShellContextMenuHelper.ShowProperties(_hWnd, item.Path);
        flyout.Items.Add(propItem);

        if (string.IsNullOrEmpty(ViewModel.MappedFolderPath))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var removeItem = new MenuFlyoutItem
            {
                Text = "\u4ece\u7ec4\u4ef6\u4e2d\u79fb\u9664",
                Icon = new SymbolIcon(Symbol.Delete)
            };
            removeItem.Click += (_, _) => ViewModel.RemoveItemCommand.Execute(item);
            flyout.Items.Add(removeItem);
        }
        else if (ViewModel.FollowsDefaultStoragePath)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveBackToDesktopItem = new MenuFlyoutItem
            {
                Text = "\u79fb\u56de\u684c\u9762",
                Icon = new FontIcon { Glyph = "\uE74A" }
            };
            moveBackToDesktopItem.Click += async (_, _) =>
            {
                try
                {
                    await ViewModel.MoveItemBackToDesktopAsync(item);
                }
                catch (Exception ex)
                {
                    await ShowErrorDialogAsync("\u79fb\u56de\u684c\u9762\u5931\u8d25", ex.Message);
                }
            };
            flyout.Items.Add(moveBackToDesktopItem);
        }

        ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
        e.Handled = true;
    }

    private void ItemsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            ApplySelectionState(listView);
            _lastSelectionAnchorIndex = GetPrimarySelectedItem() is { } item
                ? ViewModel.Items.IndexOf(item)
                : -1;
        }
    }

    private void ItemsView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartBoxSelection(e.OriginalSource))
        {
            return;
        }

        RootGrid.Focus(FocusState.Programmatic);
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
            ? GetSelectedItems()
            : [];

        if (!Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
        }

        listView.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ItemsView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting && GetSelectionDragDistance(_selectionStartPoint, _selectionCurrentPoint) < 6.0)
        {
            return;
        }

        _isBoxSelecting = true;
        UpdateSelectionRectangleVisual();
        ApplySelectionRectangle(listView);
        e.Handled = true;
    }

    private void ItemsView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            bool handled = _selectionPointerPressed || _isBoxSelecting;
            FinishSelectionRectangle(listView);
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = handled;
        }
    }

    private void ItemsView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishSelectionRectangle(listView);
        }
    }

    private async void ItemsView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var draggedItems = GetSelectedItems();
        if (draggedItems.Count == 0)
        {
            draggedItems = e.Items.OfType<WidgetItem>().ToList();
        }

        if (draggedItems.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = string.IsNullOrEmpty(ViewModel.MappedFolderPath)
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;

        var storageItems = await App.Current.FileService.GetStorageItemsAsync(draggedItems.Select(item => item.Path));
        if (storageItems.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetStorageItems(storageItems);
        e.Data.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
        e.Data.Properties["DeskBoxSourcePaths"] = draggedItems.Select(item => item.Path).ToArray();
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            listView.SelectAll();
            ApplySelectionState(listView);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectionToClipboardAsync(cut: false);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.X)
        {
            await CopySelectionToClipboardAsync(cut: !string.IsNullOrEmpty(ViewModel.MappedFolderPath));
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.V)
        {
            await PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter && GetPrimarySelectedItem() is { } item)
        {
            ViewModel.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private async Task CopySelectionToClipboardAsync(bool cut)
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        var storageItems = await App.Current.FileService.GetStorageItemsAsync(selectedItems.Select(item => item.Path));
        if (storageItems.Count == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy;
        package.SetStorageItems(storageItems);
        package.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
        package.Properties["DeskBoxSourcePaths"] = selectedItems.Select(item => item.Path).ToArray();
        Clipboard.SetContent(package);
        Clipboard.Flush();

        _cutClipboardPaths = cut
            ? selectedItems.Select(item => item.Path).ToArray()
            : [];

        foreach (var item in ViewModel.Items)
        {
            item.IsCut = cut && _cutClipboardPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase);
        }

        UpdateInteractiveSurfaces();
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = Clipboard.GetContent();
        if (!clipboard.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await clipboard.GetStorageItemsAsync();
        if (items.Count == 0)
        {
            return;
        }

        bool? moveWhenMapped = !string.IsNullOrEmpty(ViewModel.MappedFolderPath)
            ? clipboard.RequestedOperation != DataPackageOperation.Copy
            : null;

        await ViewModel.ImportPathsAsync(
            items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)),
            moveWhenMapped);

        if (moveWhenMapped == true)
        {
            await SyncMoveSourceAsync(
                TryGetPackageString(clipboard.Properties, "DeskBoxSourceWidgetId"),
                TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths"));
        }

        ClearCutState();
    }

    private void ClearCutState()
    {
        _cutClipboardPaths = [];
        foreach (var item in ViewModel.Items)
        {
            item.IsCut = false;
        }

        UpdateInteractiveSurfaces();
    }

    private bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return !HasAncestorOfType<SelectorItem>(source) &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, selectionRect.X);
        Canvas.SetTop(SelectionRectangle, selectionRect.Y);
        SelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        SelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        SelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplySelectionRectangle(ListViewBase listView)
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<WidgetItem>(_selectionSnapshot);

        foreach (var item in ViewModel.Items)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = container.TransformToVisual(SelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var itemRect = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                container.ActualWidth,
                container.ActualHeight);

            if (RectsIntersect(selectionRect, itemRect))
            {
                selectedItems.Add(item);
            }
        }

        listView.SelectedItems.Clear();
        foreach (var item in selectedItems)
        {
            listView.SelectedItems.Add(item);
        }

        ApplySelectionState(listView);
    }

    private void FinishSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;
        _selectionSnapshot = [];

        if (_isBoxSelecting)
        {
            ApplySelectionRectangle(listView);
        }

        _isBoxSelecting = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            ApplySelectionState(listView);
        }
    }

    private static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double x = Math.Min(startPoint.X, endPoint.X);
        double y = Math.Min(startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - startPoint.X);
        double height = Math.Abs(endPoint.Y - startPoint.Y);
        return new Windows.Foundation.Rect(x, y, width, height);
    }

    private static double GetSelectionDragDistance(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double deltaX = endPoint.X - startPoint.X;
        double deltaY = endPoint.Y - startPoint.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static bool RectsIntersect(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    private async Task SyncMoveSourceAsync(string? sourceWidgetId, IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId) || sourcePaths.Count == 0)
        {
            return;
        }

        if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            return;
        }

        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.NotifyItemsMovedOutAsync(sourceWidgetId, sourcePaths);
        }
    }

    private static string? TryGetPackageString(DataPackagePropertySetView properties, string key)
    {
        return properties.TryGetValue(key, out object? value) ? value as string : null;
    }

    private static IReadOnlyList<string> TryGetPackageStringArray(DataPackagePropertySetView properties, string key)
    {
        if (!properties.TryGetValue(key, out object? value) || value is null)
        {
            return [];
        }

        return value switch
        {
            string[] array => array,
            IReadOnlyList<string> readOnlyList => readOnlyList,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            _ => []
        };
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsWithin(source, TitleBarGrid))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateContentAreaFlyout(), RootGrid, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, e.GetPosition(TitleBarGrid));
        e.Handled = true;
    }

    private void WidgetItemSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Hover);
        }
    }

    private void WidgetItemSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
        }
    }

    private void WidgetItemSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            bool isInside = e.GetCurrentPoint(border).Position is var point &&
                point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= border.ActualWidth &&
                point.Y <= border.ActualHeight;

            ApplyWidgetItemSurfaceState(border, isInside ? ItemSurfaceState.Hover : ItemSurfaceState.Normal);
        }
    }

    private void TitleText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDragging || _isResizing || TitleEditBox.Visibility == Visibility.Visible)
            {
                return;
            }

            StartRename();
        });
    }

    private void StartRename()
    {
        ElevateForInteraction();
        PrepareRenameEditor();
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        TitleEditBox.SelectAll();
        TitleEditBox.Focus(FocusState.Programmatic);
    }

    private void PrepareRenameEditor()
    {
        double titleWidth = TitleText.ActualWidth > 0
            ? TitleText.ActualWidth + 52
            : (ViewModel.Name.Length * 11) + 52;

        TitleEditBox.Width = Math.Clamp(titleWidth, 168, 280);
        TitleEditBox.Text = ViewModel.Name;
    }

    private void CommitRename()
    {
        if (TitleEditBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = TitleEditBox.Text.Trim();
        if (!string.IsNullOrEmpty(newName))
        {
            ViewModel.Rename(newName);
        }

        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        RestoreDesktopLayer();
    }

    private void CancelRename()
    {
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        RestoreDesktopLayer();
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitRename();
    }

    private void TitleEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private async void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        ElevateForInteraction();

        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files is not null && files.Count > 0)
            {
                var paths = files.Select(file => file.Path);
                await ViewModel.AddItemsCommand.ExecuteAsync(paths);
            }
        }
        finally
        {
            RestoreDesktopLayer();
        }
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        if (!ShouldStartTitleDrag(e.OriginalSource))
        {
            return;
        }

        var properties = e.GetCurrentPoint(TitleBarGrid).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        if (CanStartRenameFromTitleArea(e.OriginalSource) && IsTitleBarDoubleClick(cursorPt))
        {
            _hasPendingTitleBarClick = false;
            StartRename();
            e.Handled = true;
            return;
        }

        TrackTitleBarClick(e.OriginalSource, cursorPt);
        _isDragging = true;
        ElevateForInteraction();
        _initialCursorPt = cursorPt;
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        TitleBarGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;

        if (_hasPendingTitleBarClick && ((deltaX * deltaX) + (deltaY * deltaY) > 25))
        {
            _hasPendingTitleBarClick = false;
        }

        int newX = _initialWindowPos.X + deltaX;
        int newY = _initialWindowPos.Y + deltaY;
        ApplyWindowBounds(newX, newY, _initialWindowSize.Width, _initialWindowSize.Height, persist: false);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        TitleBarGrid.ReleasePointerCapture(e.Pointer);
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        ViewModel.UpdateBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        RestoreDesktopLayer();
        e.Handled = true;
    }

    private async void MapFolderButton_Click(object sender, RoutedEventArgs e)
    {
        ElevateForInteraction();

        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await ViewModel.MapToFolderAsync(folder.Path);
                UpdateEmptyState();
            }
        }
        finally
        {
            RestoreDesktopLayer();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        ShowDeleteConfirmation();
    }

    private void DeleteConfirmCancelButton_Click(object sender, RoutedEventArgs e)
    {
        HideDeleteConfirmation();
    }

    private void DeleteConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        HideDeleteConfirmation();
        QueueDeleteWidget();
    }

    private void ShowDeleteConfirmation()
    {
        ElevateForInteraction();
        DeleteConfirmText.Text = $"确定删除“{ViewModel.Name}”吗？删除后不会恢复。";
        DeleteConfirmBar.Visibility = Visibility.Visible;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFlyoutWithElevation(CreateMoreFlyout(), MoreButton);
    }

    private MenuFlyout CreateContentAreaFlyout()
    {
        var flyout = new MenuFlyout();

        var pasteItem = new MenuFlyoutItem
        {
            Text = "\u7c98\u8d34",
            Icon = new FontIcon { Glyph = "\uE77F" },
            IsEnabled = CanPasteFromClipboard()
        };
        pasteItem.Click += async (_, _) =>
        {
            try
            {
                await PasteFromClipboardAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("\u7c98\u8d34\u5931\u8d25", ex.Message);
            }
        };
        flyout.Items.Add(pasteItem);

        var refreshItem = new MenuFlyoutItem
        {
            Text = "\u5237\u65b0",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        refreshItem.Click += async (_, _) =>
        {
            try
            {
                await ViewModel.RefreshFromConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("\u5237\u65b0\u5931\u8d25", ex.Message);
            }
        };
        flyout.Items.Add(refreshItem);

        if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var newFolderItem = new MenuFlyoutItem
            {
                Text = "\u65b0\u5efa\u6587\u4ef6\u5939",
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            newFolderItem.Click += async (_, _) => await CreateFolderInMappedLocationAsync();
            flyout.Items.Add(newFolderItem);
        }

        return flyout;
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();
        bool isMappedFolderWidget = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);

        var modeInfo = new MenuFlyoutItem
        {
            Text = $"\u5f53\u524d\u6a21\u5f0f\uff1a{ViewModel.ModeLabel}\u7ec4\u4ef6",
            Icon = new FontIcon
            {
                Glyph = isMappedFolderWidget ? "\uE8B7" : "\uE71D"
            },
            IsEnabled = false
        };
        flyout.Items.Add(modeInfo);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var newWidget = new MenuFlyoutItem
        {
            Text = "\u65b0\u5efa\u7ec4\u4ef6",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        newWidget.Click += NewWidget_Click;
        flyout.Items.Add(newWidget);

        var newManagedWidget = new MenuFlyoutItem
        {
            Text = "\u65b0\u5efa\u6536\u7eb3\u7ec4\u4ef6",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        newManagedWidget.Click += NewManagedWidget_Click;
        flyout.Items.Add(newManagedWidget);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var iconView = new ToggleMenuFlyoutItem
        {
            Text = "\u56fe\u6807\u89c6\u56fe",
            IsChecked = ViewModel.IsIconMode
        };
        iconView.Click += SetIconView_Click;
        flyout.Items.Add(iconView);

        var listView = new ToggleMenuFlyoutItem
        {
            Text = "\u5217\u8868\u89c6\u56fe",
            IsChecked = ViewModel.IsListMode
        };
        listView.Click += SetListView_Click;
        flyout.Items.Add(listView);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var positionLock = new ToggleMenuFlyoutItem
        {
            Text = "\u9501\u5b9a\u4f4d\u7f6e",
            IsChecked = ViewModel.IsPositionLocked
        };
        positionLock.Click += TogglePositionLock_Click;
        flyout.Items.Add(positionLock);

        var sizeLock = new ToggleMenuFlyoutItem
        {
            Text = "\u9501\u5b9a\u5c3a\u5bf8",
            IsChecked = ViewModel.IsSizeLocked
        };
        sizeLock.Click += ToggleSizeLock_Click;
        flyout.Items.Add(sizeLock);

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!isMappedFolderWidget)
        {
            var enableManagedStorage = new MenuFlyoutItem
            {
                Text = "\u542f\u7528\u6536\u7eb3\u6a21\u5f0f",
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            enableManagedStorage.Click += EnableManagedStorage_Click;
            flyout.Items.Add(enableManagedStorage);
        }
        else
        {
            var openFolder = new MenuFlyoutItem
            {
                Text = "\u6253\u5f00\u5f53\u524d\u6587\u4ef6\u5939",
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolder.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    Win32Helper.OpenFile(ViewModel.MappedFolderPath);
                }
            };
            flyout.Items.Add(openFolder);
        }

        var mapFolder = new MenuFlyoutItem
        {
            Text = "\u6620\u5c04\u6587\u4ef6\u5939...",
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        mapFolder.Click += MapFolderButton_Click;
        flyout.Items.Add(mapFolder);

        var rename = new MenuFlyoutItem
        {
            Text = "\u91cd\u547d\u540d",
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        rename.Click += Rename_Click;
        flyout.Items.Add(rename);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteWidget = new MenuFlyoutItem
        {
            Text = "\u5220\u9664\u7ec4\u4ef6",
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteWidget.Click += DeleteWidget_Click;
        flyout.Items.Add(deleteWidget);

        return flyout;
    }

    private void NewWidget_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app)
        {
            _ = app.WidgetManager?.CreateNewWidgetAsync();
        }
    }

    private void NewManagedWidget_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app)
        {
            _ = app.WidgetManager?.CreateManagedWidgetAsync();
        }
    }

    private void SetIconView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.Icon)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void SetListView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.List)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        StartRename();
    }

    private async Task CreateFolderInMappedLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return;
        }

        try
        {
            string folderPath = FileService.GetAvailablePath(
                Path.Combine(ViewModel.MappedFolderPath, "\u65b0\u5efa\u6587\u4ef6\u5939"));
            Directory.CreateDirectory(folderPath);
            await ViewModel.RefreshFromConfigAsync();
            if (ViewModel.Items.FirstOrDefault(existingItem =>
                    string.Equals(existingItem.Path, folderPath, StringComparison.OrdinalIgnoreCase)) is { } newFolderItem)
            {
                SelectSingleItem(newFolderItem);
                await StartItemRenameAsync(newFolderItem);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("\u65b0\u5efa\u6587\u4ef6\u5939\u5931\u8d25", ex.Message);
        }
    }

    private async Task DeleteSelectedItemsAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 && GetPrimarySelectedItem() is { } primaryItem)
        {
            selectedItems = [primaryItem];
        }

        if (selectedItems.Count == 0)
        {
            return;
        }

        try
        {
            await ViewModel.DeleteItemsAsync(selectedItems);
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("\u5220\u9664\u5931\u8d25", ex.Message);
        }
    }

    private async Task StartItemRenameAsync(WidgetItem item)
    {
        var target = FindItemSurface(item);
        if (target is null)
        {
            return;
        }

        SelectSingleItem(item);
        EnsureItemRenameFlyout();

        _itemRenameTarget = item;
        _itemRenameTextBox!.Text = item.Name;
        _itemRenameTextBox.SelectAll();
        ElevateForInteraction();
        _itemRenameFlyout!.ShowAt(target);
        _itemRenameTextBox.Focus(FocusState.Programmatic);
        await Task.CompletedTask;
    }

    private void EnsureItemRenameFlyout()
    {
        if (_itemRenameFlyout is not null && _itemRenameTextBox is not null)
        {
            return;
        }

        _itemRenameTextBox = new TextBox
        {
            Width = 220,
            MinWidth = 160,
            MaxWidth = 260,
            MinHeight = 30,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        _itemRenameTextBox.KeyDown += ItemRenameTextBox_KeyDown;
        _itemRenameTextBox.LostFocus += ItemRenameTextBox_LostFocus;

        _itemRenameFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false,
            Content = _itemRenameTextBox
        };
        _itemRenameFlyout.Closed += (_, _) =>
        {
            _itemRenameTarget = null;
            RestoreDesktopLayer();
        };
    }

    private async void ItemRenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitItemRenameAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            _itemRenameFlyout?.Hide();
        }
    }

    private async void ItemRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await CommitItemRenameAsync();
    }

    private async Task CommitItemRenameAsync()
    {
        if (_isCommittingItemRename || _itemRenameTarget is null || _itemRenameTextBox is null)
        {
            return;
        }

        string newName = _itemRenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            _itemRenameFlyout?.Hide();
            return;
        }

        _isCommittingItemRename = true;
        try
        {
            await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            _itemRenameFlyout?.Hide();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("\u91cd\u547d\u540d\u5931\u8d25", ex.Message);
            _itemRenameTextBox.Focus(FocusState.Programmatic);
            _itemRenameTextBox.SelectAll();
        }
        finally
        {
            _isCommittingItemRename = false;
        }
    }

    private FrameworkElement? FindItemSurface(WidgetItem item)
    {
        if (GetActiveItemsView()?.ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        if (TryGetDescendant<Border>(container, out var border) && border.Tag as string == "InteractiveSurface")
        {
            return border;
        }

        return container;
    }

    private void SelectSingleItem(WidgetItem item)
    {
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(item);
        listView.ScrollIntoView(item);
        ApplySelectionState(listView);
    }

    private async void EnableManagedStorage_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        if (ItemsCountForManagedConversion() > 0)
        {
            var dialog = new ContentDialog
            {
                Title = "\u542f\u7528\u6536\u7eb3\u6a21\u5f0f",
                PrimaryButtonText = "\u542f\u7528",
                CloseButtonText = "\u53d6\u6d88",
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = $"\u5f53\u524d\u7ec4\u4ef6\u5df2\u6709 {ItemsCountForManagedConversion()} \u4e2a\u9879\u76ee\u3002\u542f\u7528\u540e\u4f1a\u6309\u5f53\u524d\u8bbe\u7f6e\u5c06\u5b83\u4eec{GetManagedDropActionText()}\u5230\u9ed8\u8ba4\u6536\u7eb3\u8def\u5f84\u3002",
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await app.ShowAppDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }
        }

        try
        {
            if (await app.WidgetManager.EnableManagedStorageAsync(ViewModel.Config.Id))
            {
                UpdateEmptyState();
            }
        }
        catch (Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = "\u542f\u7528\u6536\u7eb3\u5931\u8d25",
                CloseButtonText = "\u786e\u5b9a",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = ex.Message,
                    TextWrapping = TextWrapping.Wrap
                }
            };

            await app.ShowAppDialogAsync(errorDialog);
        }
    }

    private void TogglePositionLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePositionLockCommand.Execute(null);
    }

    private void ToggleSizeLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSizeLockCommand.Execute(null);
    }

    private void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        ShowDeleteConfirmation();
    }

    private async Task ConfirmAndDeleteWidgetAsync()
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            _deletePending = false;
            return;
        }

        try
        {
            App.Log($"[WidgetDelete] Begin delete widget '{ViewModel.Name}' ({ViewModel.Config.Id})");
            await app.WidgetManager.RemoveWidgetAsync(ViewModel.Config.Id);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetDelete] Delete failed for '{ViewModel.Name}' ({ViewModel.Config.Id}): {ex}");
            _deletePending = false;
            RootGrid.IsHitTestVisible = true;
            throw;
        }
    }

    private void HideDeleteConfirmation()
    {
        DeleteConfirmBar.Visibility = Visibility.Collapsed;
    }

    private void QueueDeleteWidget()
    {
        if (_deletePending)
        {
            return;
        }

        _deletePending = true;
        RootGrid.IsHitTestVisible = false;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(16);
            await ConfirmAndDeleteWidgetAsync();
        });
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsSizeLocked)
        {
            return;
        }

        if (sender is not FrameworkElement element)
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isResizing = true;
        _resizeDirection = element.Tag as string ?? string.Empty;
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;

        int newWidth = _initialWindowSize.Width;
        int newHeight = _initialWindowSize.Height;
        int newX = _initialWindowPos.X;
        int newY = _initialWindowPos.Y;

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        ApplyWindowBounds(newX, newY, newWidth, newHeight, persist: false);
        e.Handled = true;
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || sender is not FrameworkElement element)
        {
            return;
        }

        _isResizing = false;
        _resizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        ViewModel.UpdateBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        RestoreDesktopLayer();
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = ViewModel.IsSizeLocked
            ? InputSystemCursorShape.Arrow
            : element is FrameworkElement frameworkElement
                ? frameworkElement.Tag switch
                {
                    "Left" or "Right" => InputSystemCursorShape.SizeWestEast,
                    "Top" or "Bottom" => InputSystemCursorShape.SizeNorthSouth,
                    "TopLeft" or "BottomRight" => InputSystemCursorShape.SizeNorthwestSoutheast,
                    "TopRight" or "BottomLeft" => InputSystemCursorShape.SizeNortheastSouthwest,
                    _ => InputSystemCursorShape.Arrow
                }
                : InputSystemCursorShape.Arrow;

        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.Items.Count == 0 && !ViewModel.IsLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void QueueEmptyStateUpdate()
    {
        if (_emptyStateUpdateQueued)
        {
            return;
        }

        _emptyStateUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _emptyStateUpdateQueued = false;
            UpdateEmptyState();
        });
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        if (App.Current is not App app)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            CloseButtonText = "\u786e\u5b9a",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        await app.ShowAppDialogAsync(dialog);
    }

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        ElevateForInteraction();
        flyout.Closed += (_, _) => RestoreDesktopLayer();

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    private bool ShouldStartTitleDrag(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, AddButton) &&
               !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !IsWithin(source, TitleEditBox);
    }

    private bool CanStartRenameFromTitleArea(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return IsWithin(source, TitleText) || IsWithin(source, TitleIcon);
    }

    private bool IsTitleBarDoubleClick(Win32Helper.POINT currentPoint)
    {
        if (!_hasPendingTitleBarClick)
        {
            return false;
        }

        if ((DateTime.UtcNow - _lastTitleBarClickTimeUtc).TotalMilliseconds > 420)
        {
            return false;
        }

        int deltaX = currentPoint.X - _lastTitleBarClickPoint.X;
        int deltaY = currentPoint.Y - _lastTitleBarClickPoint.Y;
        return ((deltaX * deltaX) + (deltaY * deltaY)) <= 36;
    }

    private void TrackTitleBarClick(object? originalSource, Win32Helper.POINT currentPoint)
    {
        if (!CanStartRenameFromTitleArea(originalSource))
        {
            _hasPendingTitleBarClick = false;
            return;
        }

        _lastTitleBarClickTimeUtc = DateTime.UtcNow;
        _lastTitleBarClickPoint = currentPoint;
        _hasPendingTitleBarClick = true;
    }

    private static bool CanPasteFromClipboard()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, AddButton) &&
               !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !IsWithin(source, TitleEditBox) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void EnsureStoryboards()
    {
        if (_showButtonsStoryboard is not null)
        {
            return;
        }

        _showButtonsStoryboard = new Storyboard();

        var showRightOpacity = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showRightOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(showRightOpacity, "Opacity");
        _showButtonsStoryboard.Children.Add(showRightOpacity);

        var showRightX = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showRightX, RightButtonsTransform);
        Storyboard.SetTargetProperty(showRightX, "X");
        _showButtonsStoryboard.Children.Add(showRightX);

        _hideButtonsStoryboard = new Storyboard();

        var hideRightOpacity = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideRightOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(hideRightOpacity, "Opacity");
        _hideButtonsStoryboard.Children.Add(hideRightOpacity);

        var hideRightX = new DoubleAnimation
        {
            To = 12,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideRightX, RightButtonsTransform);
        Storyboard.SetTargetProperty(hideRightX, "X");
        _hideButtonsStoryboard.Children.Add(hideRightX);
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        EnsureStoryboards();
        _hideButtonsStoryboard?.Stop();
        _showButtonsStoryboard?.Begin();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        EnsureStoryboards();
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Begin();
    }

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var tintedColor = BlendColors(baseColor, accentColor, accentMix);
        var overlayColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BlendColors(tintedColor, overlayColor, overlayMix);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color fromColor, Windows.UI.Color toColor, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        static byte BlendChannel(byte from, byte to, double mix) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * mix)), 0, 255);

        return ColorHelper.FromArgb(
            BlendChannel(fromColor.A, toColor.A, amount),
            BlendChannel(fromColor.R, toColor.R, amount),
            BlendChannel(fromColor.G, toColor.G, amount),
            BlendChannel(fromColor.B, toColor.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private DataPackageOperation GetManagedDropOperation()
    {
        return string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
    }

    private string GetManagedDropActionText()
    {
        return GetManagedDropOperation() == DataPackageOperation.Copy
            ? "\u590d\u5236"
            : "\u79fb\u52a8";
    }

    private int ItemsCountForManagedConversion()
    {
        return ViewModel.Items.Count;
    }
}

