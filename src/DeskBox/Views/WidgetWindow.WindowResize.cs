using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow
{
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

        string direction = element.Tag as string ?? string.Empty;
        if (!CanResizeCurrentWidgetState(direction))
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginWidgetBoundsInteraction();
        _isResizing = true;
        _resizeDirection = direction;
        _displayChangeWatcher?.SuppressRestore();
        BeginInteractionLayer("file-resize-started");
        Win32Helper.GetCursorPos(out InitialCursorPt);
        var initialBounds = GetActualWindowBounds();
        _initialWindowPos = new Windows.Graphics.PointInt32(initialBounds.X, initialBounds.Y);
        _initialWindowSize = new Windows.Graphics.SizeInt32(initialBounds.Width, initialBounds.Height);
        element.CapturePointer(e.Pointer);
        App.Current.ResizeGuideOverlay.BeginResize(_hWnd, RootGrid);
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

        if (IsCompactBoundsStateActive)
        {
            var limits = GetCompactPhysicalWidthLimits();
            if (_resizeDirection == "Right")
            {
                newWidth = Math.Clamp(_initialWindowSize.Width + deltaX, limits.MinWidth, limits.MaxWidth);
            }
            else if (_resizeDirection == "Left")
            {
                int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
                newWidth = Math.Clamp(_initialWindowSize.Width - deltaX, limits.MinWidth, limits.MaxWidth);
                newX = rightEdge - newWidth;
            }

            var compactProposed = new Windows.Graphics.RectInt32(
                newX,
                newY,
                newWidth,
                newHeight);
            var compactSnapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(
                compactProposed,
                _resizeDirection,
                limits.MinWidth,
                limits.MaxWidth);
            ApplyWindowBounds(
                compactSnapped.X,
                compactSnapped.Y,
                compactSnapped.Width,
                compactSnapped.Height,
                persist: false);
            e.Handled = true;
            return;
        }

        var minSize = GetPhysicalMinimumWindowSize(
            _initialWindowPos.X,
            _initialWindowPos.Y,
            _initialWindowSize.Width,
            _initialWindowSize.Height);

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        var proposed = new Windows.Graphics.RectInt32(newX, newY, newWidth, newHeight);
        var snapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(proposed, _resizeDirection);
        snapped = AnchorExpandedResizeBounds(snapped);
        ApplyWindowBounds(snapped.X, snapped.Y, snapped.Width, snapped.Height, persist: false);
        e.Handled = true;
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || sender is not FrameworkElement element)
        {
            return;
        }

        _isResizing = false;
        element.ReleasePointerCapture(e.Pointer);
        App.Current.ResizeGuideOverlay.EndResize();
        PersistCompletedWidgetResize(GetActualWindowBounds());
        _resizeDirection = string.Empty;
        EndWidgetBoundsInteraction();
        ReleaseInteractionLayer("file-resize-ended");
        _displayChangeWatcher?.ResumeRestore();
        e.Handled = true;
    }

    private void ResizeBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        _dragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndResize();
        PersistCompletedWidgetResize(GetActualWindowBounds());
        _resizeDirection = string.Empty;
        EndWidgetBoundsInteraction();
        ReleaseInteractionLayer("file-resize-capture-lost");
        _displayChangeWatcher?.ResumeRestore();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = element is FrameworkElement frameworkElement
            ? GetResizeCursorShapeForCurrentState(frameworkElement.Tag as string)
            : InputSystemCursorShape.Arrow;

        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }
}
