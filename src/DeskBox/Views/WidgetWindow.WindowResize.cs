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

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isResizing = true;
        _resizeDirection = element.Tag as string ?? string.Empty;
        _displayChangeWatcher?.SuppressRestore();
        BeginInteractionLayer("file-resize-started");
        Win32Helper.GetCursorPos(out InitialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
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
        _resizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        App.Current.ResizeGuideOverlay.EndResize();
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
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
        _resizeDirection = string.Empty;
        _dragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndResize();
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
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
}
