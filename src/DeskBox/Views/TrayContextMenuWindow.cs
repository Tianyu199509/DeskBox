// Copyright (c) DeskBox. All rights reserved.

using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using DrawingPoint = System.Drawing.Point;

namespace DeskBox.Views;

/// <summary>
/// Ephemeral borderless WinUI 3 Fluent tray context menu surface.
/// Created on-demand only when the user right-clicks the notification-area icon
/// and explicitly torn down (<c>SystemBackdrop = null</c> + <see cref="Window.Close"/>)
/// as soon as an item is invoked or focus leaves the menu. This preserves a
/// zero-XAML-window idle resident footprint while delivering genuine Windows 11
/// DesktopAcrylic, DWM rounded corners, crisp vector glyphs, and Fluent hover pills.
/// </summary>
internal sealed class TrayContextMenuWindow : Window
{
    private const double MinMenuWidthDip = 196;
    private const double MaxMenuWidthDip = 320;
    private const double PresenterVerticalPaddingDip = 4;
    private const double ItemRowHeightDip = 32;
    private const double ItemVerticalMarginDip = 2;
    private const double ItemHorizontalMarginDip = 5;
    private const double TaskbarGapDip = 6;
    private const double ScreenEdgeMarginDip = 6;

    private static TrayContextMenuWindow? s_activeWindow;

    private readonly AppWindow _appWindow;
    private readonly Grid _rootGrid;
    private readonly Border _presenterBorder;
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _itemsPanel;
    private readonly List<MenuRowVisual> _itemRows = new(18);
    private readonly Action _ensureLifelineBeforeClose;
    private readonly Action<NativeTrayMenuEntry> _onEntryInvoked;

    private readonly Brush _rowNormalBackground = new SolidColorBrush(Colors.Transparent);
    private readonly Brush _rowHoverBackground;
    private readonly Brush _rowPressedBackground;
    private readonly Brush _primaryForeground;
    private readonly Brush _pressedForeground;
    private readonly Brush _disabledForeground;

    private int _highlightedRowIndex = -1;
    private int _pressedRowIndex = -1;
    private bool _hasBeenActivated;
    private bool _closed;

    private sealed record MenuRowVisual(
        NativeTrayMenuEntry Entry,
        Border PillBorder,
        FontIcon? IconElement,
        TextBlock LabelElement);

    private TrayContextMenuWindow(
        IReadOnlyList<NativeTrayMenuEntry> entries,
        bool isDarkTheme,
        bool isRightToLeft,
        Action ensureLifelineBeforeClose,
        Action<NativeTrayMenuEntry> onEntryInvoked)
    {
        Title = "DeskBox Tray Menu";
        _ensureLifelineBeforeClose = ensureLifelineBeforeClose;
        _onEntryInvoked = onEntryInvoked;

        _rowHoverBackground = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x09, 0x00, 0x00, 0x00));
        _rowPressedBackground = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x06, 0x00, 0x00, 0x00));
        _primaryForeground = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE4, 0x00, 0x00, 0x00));
        _pressedForeground = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
        _disabledForeground = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x5C, 0x00, 0x00, 0x00));

        WindowHandle = WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        ConfigureNativeWindowStyles(isDarkTheme);

        string backdropKind = WindowsCompatibilityService.ApplySafeBackdrop(this, preferMica: false);
        bool hasAcrylicBackdrop = string.Equals(backdropKind, "Acrylic", StringComparison.Ordinal);
        bool roundedCorners = WindowsCompatibilityService.SupportsNativeWindowCorners;

        Brush presenterBackground = new SolidColorBrush(hasAcrylicBackdrop
            ? (isDarkTheme
                ? Color.FromArgb(0x22, 0x2C, 0x2C, 0x2C)
                : Color.FromArgb(0x28, 0xFC, 0xFC, 0xFC))
            : (isDarkTheme
                ? Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C)
                : Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9)));
        Brush presenterStroke = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Brush separatorBrush = new SolidColorBrush(isDarkTheme
            ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x10, 0x00, 0x00, 0x00));

        FontFamily menuFontFamily = ResolveMenuFontFamily();
        double menuFontSize = ResolveResourceDouble("DeskBoxMenuItemFontSize", 13.0);

        _itemsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        BuildMenuVisualRows(entries, menuFontFamily, menuFontSize, separatorBrush);

        _scrollViewer = new ScrollViewer
        {
            Content = _itemsPanel,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, PresenterVerticalPaddingDip, 0, PresenterVerticalPaddingDip),
        };
        ElementCompositionPreview.SetIsTranslationEnabled(_scrollViewer, true);

        _presenterBorder = new Border
        {
            MinWidth = MinMenuWidthDip,
            MaxWidth = MaxMenuWidthDip,
            Background = presenterBackground,
            BorderBrush = presenterStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(roundedCorners ? 8 : 0),
            Child = _scrollViewer,
        };

        _rootGrid = new Grid
        {
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            FlowDirection = isRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _rootGrid.Children.Add(_presenterBorder);
        _rootGrid.KeyDown += OnRootGridKeyDown;

        Content = _rootGrid;

        Activated += OnWindowActivated;
        Closed += (_, _) => _closed = true;
    }

    internal IntPtr WindowHandle { get; }

    internal static bool HasActiveMenu => s_activeWindow is { _closed: false };

    internal static void DismissActiveIfOpen()
    {
        if (s_activeWindow is { _closed: false } active)
        {
            active.CloseMenu(invokedEntry: null);
        }
    }

    internal static bool TryShow(
        IReadOnlyList<NativeTrayMenuEntry> entries,
        DrawingPoint anchor,
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment,
        bool isDarkTheme,
        bool isRightToLeft,
        Action ensureLifelineBeforeClose,
        Action<NativeTrayMenuEntry> onEntryInvoked)
    {
        if (entries.Count == 0)
        {
            return false;
        }

        DismissActiveIfOpen();
        ensureLifelineBeforeClose();

        TrayContextMenuWindow? window = null;
        try
        {
            window = new TrayContextMenuWindow(
                entries,
                isDarkTheme,
                isRightToLeft,
                ensureLifelineBeforeClose,
                onEntryInvoked);
            s_activeWindow = window;
            window.PositionAndReveal(anchor, horizontalAlignment, verticalAlignment);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Tray] Transient Fluent tray menu failed; falling back to native menu: {ex.Message}");
            if (window is not null)
            {
                try
                {
                    window.CloseMenu(invokedEntry: null);
                }
                catch
                {
                    // Best-effort cleanup before native HMENU fallback.
                }
            }

            return false;
        }
    }

    internal static RectInt32 ComputeWindowBounds(
        DrawingPoint anchor,
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment,
        int desiredWidthPx,
        int desiredHeightPx,
        double dpiScale,
        Win32Helper.RECT workArea)
    {
        double safeScale = dpiScale > 0 ? dpiScale : 1.0;
        int gapPx = Math.Max(2, (int)Math.Round(TaskbarGapDip * safeScale));
        int edgeMarginPx = Math.Max(2, (int)Math.Round(ScreenEdgeMarginDip * safeScale));

        int availableWidth = Math.Max(64, (workArea.Right - workArea.Left) - (edgeMarginPx * 2));
        int availableHeight = Math.Max(64, (workArea.Bottom - workArea.Top) - (edgeMarginPx * 2));

        int width = Math.Clamp(desiredWidthPx, 1, availableWidth);
        int height = Math.Clamp(desiredHeightPx, 1, availableHeight);

        int horizontalGapPx = verticalAlignment == TrayMenuVerticalAlignment.Center ? gapPx : 0;
        int x = horizontalAlignment switch
        {
            TrayMenuHorizontalAlignment.Center => anchor.X - (width / 2),
            TrayMenuHorizontalAlignment.Right => anchor.X - width - horizontalGapPx,
            _ => anchor.X + horizontalGapPx,
        };

        int y = verticalAlignment switch
        {
            TrayMenuVerticalAlignment.Top => anchor.Y + gapPx,
            TrayMenuVerticalAlignment.Center => anchor.Y - (height / 2),
            _ => anchor.Y - height - gapPx,
        };
        int minX = workArea.Left + edgeMarginPx;
        int maxX = Math.Max(minX, workArea.Right - edgeMarginPx - width);
        int minY = workArea.Top + edgeMarginPx;
        int maxY = Math.Max(minY, workArea.Bottom - edgeMarginPx - height);

        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        return new RectInt32(x, y, width, height);
    }

    private void ConfigureNativeWindowStyles(bool isDarkTheme)
    {
        int extendedStyle = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TOPMOST;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE, extendedStyle);

        int style = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
                   Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME |
                   Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_STYLE, style);

        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
            Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOACTIVATE |
            Win32Helper.SWP_FRAMECHANGED);

        Win32Helper.SetWindowBorderColor(WindowHandle, unchecked((int)0xFFFFFFFE));
        Win32Helper.ApplyFullWindowFrame(WindowHandle);
        Win32Helper.SetWindowTheme(WindowHandle, isDarkTheme);

        if (WindowsCompatibilityService.SupportsNativeWindowCorners)
        {
            int cornerPreference = Win32Helper.DWMWCP_ROUND;
            _ = Win32Helper.TrySetDwmWindowAttribute(
                WindowHandle,
                Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference);
        }
    }

    private void PositionAndReveal(
        DrawingPoint anchor,
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment)
    {
        if (!Win32Helper.TryGetMonitorWorkArea(anchor.X, anchor.Y, out _, out Win32Helper.RECT workArea))
        {
            workArea = new Win32Helper.RECT
            {
                Left = anchor.X - 640,
                Top = anchor.Y - 640,
                Right = anchor.X + 640,
                Bottom = anchor.Y + 640,
            };
        }

        // Park the still-hidden HWND inside the target monitor first so
        // Per-Monitor-V2 DPI scaling reflects the monitor where the menu opens.
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            workArea.Left,
            workArea.Top,
            1,
            1,
            Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);

        double dpiScale = Win32Helper.GetDpiScaleForWindow(WindowHandle, _rootGrid.XamlRoot);

        _itemsPanel.Measure(new Size(MaxMenuWidthDip, double.PositiveInfinity));
        double naturalWidthDip = Math.Clamp(
            Math.Ceiling(_itemsPanel.DesiredSize.Width) + 2,
            MinMenuWidthDip,
            MaxMenuWidthDip);
        double naturalHeightDip =
            Math.Ceiling(_itemsPanel.DesiredSize.Height) +
            (PresenterVerticalPaddingDip * 2) +
            2;

        int desiredWidthPx = Math.Max(1, (int)Math.Ceiling(naturalWidthDip * dpiScale));
        int desiredHeightPx = Math.Max(1, (int)Math.Ceiling(naturalHeightDip * dpiScale));

        RectInt32 bounds = ComputeWindowBounds(
            anchor,
            horizontalAlignment,
            verticalAlignment,
            desiredWidthPx,
            desiredHeightPx,
            dpiScale,
            workArea);

        _appWindow.MoveAndResize(bounds);
        _appWindow.Show();
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            Win32Helper.SWP_SHOWWINDOW);

        Activate();
        _ = Win32Helper.SetForegroundWindow(WindowHandle);
        _rootGrid.Focus(FocusState.Programmatic);

        PlayEntranceTransition(horizontalAlignment, verticalAlignment);

        // Ensure activation settles even if Explorer's taskbar releases
        // foreground focus one tick after WM_RBUTTONUP.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_closed)
            {
                return;
            }

            if (!_hasBeenActivated)
            {
                Activate();
                _ = Win32Helper.SetForegroundWindow(WindowHandle);
                _hasBeenActivated = true;
            }

            _rootGrid.Focus(FocusState.Programmatic);
        });
    }

    private void PlayEntranceTransition(
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment)
    {
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(_scrollViewer);
            Compositor compositor = visual.Compositor;

            Vector3 startOffset = verticalAlignment switch
            {
                TrayMenuVerticalAlignment.Bottom => new Vector3(0f, 6f, 0f),
                TrayMenuVerticalAlignment.Top => new Vector3(0f, -6f, 0f),
                _ => horizontalAlignment switch
                {
                    TrayMenuHorizontalAlignment.Right => new Vector3(6f, 0f, 0f),
                    TrayMenuHorizontalAlignment.Left => new Vector3(-6f, 0f, 0f),
                    _ => new Vector3(0f, 6f, 0f),
                },
            };

            using CubicBezierEasingFunction fluentOut = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            using Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, startOffset);
            slide.InsertKeyFrame(1f, Vector3.Zero, fluentOut);
            slide.Duration = TimeSpan.FromMilliseconds(140);
            visual.StartAnimation("Translation", slide);
        }
        catch
        {
            // Animation is purely cosmetic; menu remains usable immediately.
        }
    }

    private void BuildMenuVisualRows(
        IReadOnlyList<NativeTrayMenuEntry> entries,
        FontFamily menuFontFamily,
        double menuFontSize,
        Brush separatorBrush)
    {
        foreach (NativeTrayMenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                _itemsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = separatorBrush,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
                continue;
            }

            int rowIndex = _itemRows.Count;
            Brush initialForeground = entry.IsEnabled ? _primaryForeground : _disabledForeground;

            var rowGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(16) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
                ColumnSpacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };

            FontIcon? iconElement = null;
            if (!string.IsNullOrEmpty(entry.Glyph))
            {
                iconElement = new FontIcon
                {
                    Glyph = entry.Glyph,
                    FontSize = 15,
                    Foreground = initialForeground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(iconElement, 0);
                rowGrid.Children.Add(iconElement);
            }

            var labelElement = new TextBlock
            {
                Text = entry.Text ?? string.Empty,
                FontFamily = menuFontFamily,
                FontSize = menuFontSize,
                Foreground = initialForeground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            WidgetTextShadow.SetCast(labelElement, false);
            Grid.SetColumn(labelElement, 1);
            rowGrid.Children.Add(labelElement);

            var pillBorder = new Border
            {
                MinHeight = ItemRowHeightDip,
                Margin = new Thickness(
                    ItemHorizontalMarginDip,
                    ItemVerticalMarginDip,
                    ItemHorizontalMarginDip,
                    ItemVerticalMarginDip),
                Padding = new Thickness(11, 0, 14, 0),
                CornerRadius = new CornerRadius(4),
                Background = _rowNormalBackground,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = rowGrid,
            };

            if (entry.IsEnabled)
            {
                AttachRowPointerHandlers(pillBorder, rowIndex);
            }

            _itemRows.Add(new MenuRowVisual(entry, pillBorder, iconElement, labelElement));
            _itemsPanel.Children.Add(pillBorder);
        }
    }

    private void AttachRowPointerHandlers(Border pillBorder, int rowIndex)
    {
        pillBorder.PointerEntered += (_, _) =>
        {
            if (!_closed)
            {
                SetHighlightedRow(rowIndex);
            }
        };

        pillBorder.PointerMoved += (_, _) =>
        {
            if (!_closed && _highlightedRowIndex != rowIndex)
            {
                SetHighlightedRow(rowIndex);
            }
        };

        pillBorder.PointerExited += (_, _) =>
        {
            if (!_closed)
            {
                if (_pressedRowIndex == rowIndex)
                {
                    _pressedRowIndex = -1;
                }

                if (_highlightedRowIndex == rowIndex)
                {
                    SetHighlightedRow(-1);
                }
                else
                {
                    ApplyRowVisualState(rowIndex);
                }
            }
        };

        pillBorder.PointerPressed += (_, args) =>
        {
            if (_closed)
            {
                return;
            }

            PointerPointProperties props = args.GetCurrentPoint(pillBorder).Properties;
            if (props.IsLeftButtonPressed || props.IsRightButtonPressed)
            {
                _pressedRowIndex = rowIndex;
                SetHighlightedRow(rowIndex);
                ApplyRowVisualState(rowIndex);
                args.Handled = true;
            }
        };

        pillBorder.PointerReleased += (_, args) =>
        {
            if (_closed || _pressedRowIndex != rowIndex)
            {
                return;
            }

            _pressedRowIndex = -1;
            Point pos = args.GetCurrentPoint(pillBorder).Position;
            bool inside = pos.X >= 0 &&
                          pos.Y >= 0 &&
                          pos.X <= pillBorder.ActualWidth &&
                          pos.Y <= pillBorder.ActualHeight;
            args.Handled = true;
            if (inside)
            {
                InvokeRowAndClose(rowIndex);
            }
            else
            {
                ApplyRowVisualState(rowIndex);
            }
        };
    }

    private void SetHighlightedRow(int nextIndex)
    {
        int previous = _highlightedRowIndex;
        if (previous == nextIndex)
        {
            if (nextIndex >= 0 && nextIndex < _itemRows.Count)
            {
                ApplyRowVisualState(nextIndex);
            }

            return;
        }

        _highlightedRowIndex = nextIndex;
        if (previous >= 0 && previous < _itemRows.Count)
        {
            ApplyRowVisualState(previous);
        }

        if (nextIndex >= 0 && nextIndex < _itemRows.Count)
        {
            ApplyRowVisualState(nextIndex);
            _itemRows[nextIndex].PillBorder.StartBringIntoView();
        }
    }

    private void ApplyRowVisualState(int rowIndex)
    {
        MenuRowVisual row = _itemRows[rowIndex];
        if (!row.Entry.IsEnabled)
        {
            row.PillBorder.Background = _rowNormalBackground;
            row.LabelElement.Foreground = _disabledForeground;
            if (row.IconElement is not null)
            {
                row.IconElement.Foreground = _disabledForeground;
            }

            return;
        }

        bool isPressed = _pressedRowIndex == rowIndex;
        bool isHighlighted = _highlightedRowIndex == rowIndex;

        row.PillBorder.Background = isPressed
            ? _rowPressedBackground
            : (isHighlighted ? _rowHoverBackground : _rowNormalBackground);

        Brush foreground = isPressed ? _pressedForeground : _primaryForeground;
        row.LabelElement.Foreground = foreground;
        if (row.IconElement is not null)
        {
            row.IconElement.Foreground = foreground;
        }
    }

    private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        switch (args.Key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Menu:
            case VirtualKey.F10:
                args.Handled = true;
                CloseMenu(invokedEntry: null);
                break;

            case VirtualKey.Down:
                args.Handled = true;
                MoveKeyboardHighlight(step: 1);
                break;

            case VirtualKey.Up:
                args.Handled = true;
                MoveKeyboardHighlight(step: -1);
                break;

            case VirtualKey.Home:
                args.Handled = true;
                JumpKeyboardHighlight(first: true);
                break;

            case VirtualKey.End:
                args.Handled = true;
                JumpKeyboardHighlight(first: false);
                break;

            case VirtualKey.Enter:
            case VirtualKey.Space:
                if (_highlightedRowIndex >= 0 &&
                    _highlightedRowIndex < _itemRows.Count &&
                    _itemRows[_highlightedRowIndex].Entry.IsEnabled)
                {
                    args.Handled = true;
                    InvokeRowAndClose(_highlightedRowIndex);
                }

                break;
        }
    }

    private void MoveKeyboardHighlight(int step)
    {
        int count = _itemRows.Count;
        if (count == 0)
        {
            return;
        }

        int cursor = _highlightedRowIndex;
        for (int visited = 0; visited < count; visited++)
        {
            cursor = cursor < 0
                ? (step > 0 ? 0 : count - 1)
                : ((cursor + step + count) % count);

            if (_itemRows[cursor].Entry.IsEnabled)
            {
                _pressedRowIndex = -1;
                SetHighlightedRow(cursor);
                return;
            }
        }
    }

    private void JumpKeyboardHighlight(bool first)
    {
        if (first)
        {
            for (int i = 0; i < _itemRows.Count; i++)
            {
                if (_itemRows[i].Entry.IsEnabled)
                {
                    _pressedRowIndex = -1;
                    SetHighlightedRow(i);
                    return;
                }
            }
        }
        else
        {
            for (int i = _itemRows.Count - 1; i >= 0; i--)
            {
                if (_itemRows[i].Entry.IsEnabled)
                {
                    _pressedRowIndex = -1;
                    SetHighlightedRow(i);
                    return;
                }
            }
        }
    }

    private void InvokeRowAndClose(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _itemRows.Count)
        {
            return;
        }

        NativeTrayMenuEntry entry = _itemRows[rowIndex].Entry;
        if (!entry.IsEnabled)
        {
            return;
        }

        CloseMenu(invokedEntry: entry);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _hasBeenActivated = true;
            return;
        }

        if (_hasBeenActivated)
        {
            CloseMenu(invokedEntry: null);
        }
    }

    private void CloseMenu(NativeTrayMenuEntry? invokedEntry)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (ReferenceEquals(s_activeWindow, this))
        {
            s_activeWindow = null;
        }

        _ensureLifelineBeforeClose();

        _rootGrid.KeyDown -= OnRootGridKeyDown;
        Activated -= OnWindowActivated;

        _itemRows.Clear();
        _itemsPanel.Children.Clear();
        _scrollViewer.Content = null;
        _presenterBorder.Child = null;
        _rootGrid.Children.Clear();
        Content = null;
        SystemBackdrop = null;

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            App.Log($"[Tray] Transient tray menu window close failed: {ex.Message}");
        }

        if (invokedEntry is { IsEnabled: true, Action: not null })
        {
            UiDispatch.PostAfterCallback(() => _onEntryInvoked(invokedEntry));
        }
    }

    private static FontFamily ResolveMenuFontFamily()
    {
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue("DeskBoxMenuFontFamily", out object? value) &&
            value is FontFamily fontFamily)
        {
            return fontFamily;
        }

        return new FontFamily("Microsoft YaHei UI, Segoe UI Variable, Segoe UI");
    }

    private static double ResolveResourceDouble(string key, double fallback)
    {
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(key, out object? value) &&
            value is double number &&
            number > 0)
        {
            return number;
        }

        return fallback;
    }
}
