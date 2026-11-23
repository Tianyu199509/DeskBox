using System;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace DeskBox.Views;

public sealed partial class SearchPopupWindow
{
    // Borderless drag, horizontal-only resize, and custom width/position persistence.

    private void PersistCustomBounds(bool persistWidth)
    {
        if (!Win32Helper.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var searchSlice = _settingsService.Settings.Search;
        searchSlice.SearchPopupCustomX = rect.Left;
        searchSlice.SearchPopupCustomY = rect.Top;
        if (persistWidth)
        {
            double scale = Win32Helper.GetDpiScaleForWindow(_hwnd, Content?.XamlRoot);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            int physicalWidth = rect.Right - rect.Left;
            DisplayArea displayArea = DisplayArea.GetFromPoint(
                new PointInt32(rect.Left, rect.Top),
                DisplayAreaFallback.Nearest);
            int workAreaLogicalWidth = Math.Max(320, (int)Math.Round(displayArea.WorkArea.Width / scale));
            int logicalWidth = (int)Math.Round(physicalWidth / scale, MidpointRounding.AwayFromZero);
            searchSlice.SearchPopupCustomWidth = SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(
                logicalWidth,
                workAreaLogicalWidth);
        }

        searchSlice.SearchPopupCustomHeight = null;
        // Persist without raising SettingsChanged. Raising it here re-triggers
        // OnAppearanceSettingsChanged -> ApplyMaterialFromSettings, which re-applies the
        // DWM backdrop and causes the window to flash on every drag/resize release.
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    private void ResetToDefaultBounds()
    {
        var searchSlice = _settingsService.Settings.Search;
        searchSlice.SearchPopupCustomX = null;
        searchSlice.SearchPopupCustomY = null;
        searchSlice.SearchPopupCustomWidth = null;
        searchSlice.SearchPopupCustomHeight = null;
        _settingsService.SaveDebounced(notifySubscribers: false);

        if (IsPopupVisible && !_isWindowClosed)
        {
            UpdatePanelVisibility(forceBoundsWhenOpening: true);
        }
        else
        {
            UpdatePopupDynamicBounds(forceWhenOpening: true);
        }
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalized = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(logicalPixels * normalized, MidpointRounding.AwayFromZero));
    }

    private void TopDragHotZone_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetPointerCursor(TopDragHotZone, InputSystemCursorShape.SizeAll);
    }

    private void TopDragHotZone_PointerExited(object sender, PointerRoutedEventArgs e)
    {
    }

    private void TopDragHotZone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            !e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            return;
        }

        SnapHeightMorphToTarget();
        if (!TryBeginWindowInteraction(element, e))
        {
            return;
        }

        _isWindowDragging = true;
        e.Handled = true;
    }

    private void TopDragHotZone_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResetToDefaultBounds();
        e.Handled = true;
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            !e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            return;
        }

        string direction = element.Tag as string ?? string.Empty;
        SnapHeightMorphToTarget();
        if (string.IsNullOrEmpty(direction) || !TryBeginWindowInteraction(element, e))
        {
            return;
        }

        _resizeDirection = direction;
        _isWindowResizing = true;
        e.Handled = true;
    }

    private bool TryBeginWindowInteraction(FrameworkElement element, PointerRoutedEventArgs e)
    {
        bool anchored = Win32Helper.TryGetPointerScreenPoint(_hwnd, Content, e, out _interactionStartCursor) ||
            Win32Helper.GetCursorPos(out _interactionStartCursor);
        if (!anchored || !Win32Helper.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        _interactionStartBounds = new RectInt32(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
        _windowInteractionElement = element;
        if (!element.CapturePointer(e.Pointer))
        {
            _windowInteractionElement = null;
            return false;
        }

        return true;
    }

    private void WindowInteraction_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if ((!_isWindowDragging && !_isWindowResizing) || _appWindow is null)
        {
            return;
        }

        if (!Win32Helper.TryGetPointerScreenPoint(_hwnd, Content, e, out var cursor) &&
            !Win32Helper.GetCursorPos(out cursor))
        {
            return;
        }

        int deltaX = cursor.X - _interactionStartCursor.X;
        int deltaY = cursor.Y - _interactionStartCursor.Y;
        if (_isWindowDragging)
        {
            WindowShellState.MoveWindowInteractive(
                _appWindow,
                _hwnd,
                _interactionStartBounds.X + deltaX,
                _interactionStartBounds.Y + deltaY);
        }
        else
        {
            ApplyResizeDelta(deltaX, deltaY);
        }

        e.Handled = true;
    }

    private void ApplyResizeDelta(int deltaX, int deltaY)
    {
        _ = deltaY;
        if (_appWindow is null)
        {
            return;
        }

        int x = _interactionStartBounds.X;
        int y = _interactionStartBounds.Y;
        int width = _interactionStartBounds.Width;
        int height = _interactionStartBounds.Height;
        double scale = Win32Helper.GetDpiScaleForWindow(_hwnd, Content?.XamlRoot);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        DisplayArea displayArea = DisplayArea.GetFromPoint(
            new PointInt32(x + (width / 2), y),
            DisplayAreaFallback.Nearest);
        RectInt32 work = displayArea.WorkArea;
        int workAreaLogicalWidth = Math.Max(320, (int)Math.Round(work.Width / scale));
        int maxLogicalWidth = SearchPopupSurfacePolicy.ComputeMaxLogicalWidth(workAreaLogicalWidth);
        int minWidth = ToPhysicalPixels(Math.Min(MinPopupWidth, maxLogicalWidth), scale);
        int maxWidth = Math.Max(minWidth, ToPhysicalPixels(maxLogicalWidth, scale));

        if (string.Equals(_resizeDirection, "Right", StringComparison.Ordinal))
        {
            int availableRight = Math.Max(minWidth, (work.X + work.Width - 8) - x);
            width = Math.Clamp(width + deltaX, minWidth, Math.Min(maxWidth, availableRight));
        }
        else if (string.Equals(_resizeDirection, "Left", StringComparison.Ordinal))
        {
            int right = x + width;
            int availableLeft = Math.Max(minWidth, right - (work.X + 8));
            width = Math.Clamp(width - deltaX, minWidth, Math.Min(maxWidth, availableLeft));
            x = right - width;
        }
        else
        {
            return;
        }

        double logicalWidth = Math.Max(
            Math.Min(MinPopupWidth, maxLogicalWidth),
            Math.Round(width / scale, 2));
        _widthSpringCurrentLogical = logicalWidth;
        _widthMorphTargetLogical = logicalWidth;
        _lastMorphWindowX = x;
        if (Math.Abs(RootGrid.Width - logicalWidth) > 0.001)
        {
            RootGrid.Width = logicalWidth;
        }

        WindowShellState.ResizeWindowInteractive(_appWindow, _hwnd, x, y, width, height);
        UpdateDetailsSplitLayout(ResultsPanel.Visibility == Visibility.Visible);
    }

    private void WindowInteraction_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteWindowInteraction(e.Pointer, persist: true);
        e.Handled = true;
    }

    private void WindowInteraction_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CompleteWindowInteraction(e.Pointer, persist: true);
    }

    private void CompleteWindowInteraction(Pointer pointer, bool persist)
    {
        if (!_isWindowDragging && !_isWindowResizing)
        {
            return;
        }

        bool wasDragging = _isWindowDragging;
        bool wasResizing = _isWindowResizing;
        var captureElement = _windowInteractionElement;
        _windowInteractionElement = null;
        _isWindowDragging = false;
        _isWindowResizing = false;
        _resizeDirection = string.Empty;
        captureElement?.ReleasePointerCapture(pointer);
        if (persist)
        {
            PersistCustomBounds(persistWidth: wasResizing);
            if (IsPopupVisible && !_isWindowClosed)
            {
                if (wasResizing)
                {
                    UpdatePanelVisibility();
                }
                else if (wasDragging)
                {
                    UpdatePopupDynamicBounds(forceWhenOpening: true);
                }
            }
        }
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var shape = (element.Tag as string) switch
        {
            "Left" or "Right" => InputSystemCursorShape.SizeWestEast,
            _ => InputSystemCursorShape.Arrow
        };
        SetPointerCursor(element, shape);
    }

    private static void SetPointerCursor(UIElement element, InputSystemCursorShape shape)
    {
        UiElementCursorHelper.SetCursor(element, shape);
    }

    private int ResolveConfiguredLogicalWidth()
    {
        var searchSlice = _settingsService.Settings.Search;
        int? customX = searchSlice.SearchPopupCustomX;
        int? customY = searchSlice.SearchPopupCustomY;
        double scale = customX is int targetX && customY is int targetY
            ? Win32Helper.GetDpiScaleForPoint(targetX, targetY)
            : Win32Helper.GetDpiScaleForWindow(_hwnd, Content?.XamlRoot);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        DisplayArea displayArea = _appWindow is not null
            ? (customX is int savedX && customY is int savedY
                ? DisplayArea.GetFromPoint(new PointInt32(savedX, savedY), DisplayAreaFallback.Nearest)
                : DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary))
            : DisplayArea.Primary;
        int workAreaLogicalWidth = Math.Max(320, (int)Math.Round(displayArea.WorkArea.Width / scale));
        return SearchPopupSurfacePolicy.ResolveEffectiveLogicalWidth(
            searchSlice.SearchPopupCustomWidth,
            workAreaLogicalWidth);
    }

    private double GetCurrentPopupLogicalWidth() =>
        _isWindowResizing && double.IsFinite(RootGrid.Width) && RootGrid.Width > 0
            ? RootGrid.Width
            : ResolveConfiguredLogicalWidth();
}
