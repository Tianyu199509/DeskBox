using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Small, borderless input host placed directly over a stack popover title.
/// A windowed XAML Popup does not take Win32 focus. The editor therefore uses
/// a real WinUI Window for reliable TSF/IME composition while preserving the
/// inline appearance.
/// </summary>
internal sealed class StackPopoverInlineRenameWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _ownerWindowHandle;
    private readonly Grid _root;
    private WidgetMaterialSystemBackdrop? _materialBackdrop;
    private bool _closed;

    internal StackPopoverInlineRenameWindow(
        string text,
        Style? textBoxStyle,
        Brush background,
        Brush? foreground,
        WidgetMaterialBackdropAppearance materialAppearance,
        IntPtr ownerWindowHandle)
    {
        _ownerWindowHandle = ownerWindowHandle;
        Editor = new TextBox
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            MaxLength = 128,
            Style = textBoxStyle,
            Foreground = foreground
        };
        _root = new Grid
        {
            Background = background,
            RequestedTheme = materialAppearance.IsDark
                ? ElementTheme.Dark
                : ElementTheme.Light
        };
        _root.Children.Add(Editor);
        Content = _root;
        if (WidgetMaterialSystemBackdrop.IsSupported(
                materialAppearance.MaterialType))
        {
            _materialBackdrop =
                new WidgetMaterialSystemBackdrop(materialAppearance);
            SystemBackdrop = _materialBackdrop;
        }
        Closed += (_, _) => _closed = true;

        WindowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.IsShownInSwitchers = false;
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        if (ownerWindowHandle != IntPtr.Zero)
        {
            _ = Win32Helper.SetWindowLongPtr(
                WindowHandle,
                Win32Helper.GWLP_HWNDPARENT,
                ownerWindowHandle);
        }

        int extendedStyle = Win32Helper.GetWindowLong(
            WindowHandle,
            Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(
            WindowHandle,
            Win32Helper.GWL_EXSTYLE,
            extendedStyle);

        int style = Win32Helper.GetWindowLong(
            WindowHandle,
            Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
            Win32Helper.WS_BORDER |
            Win32Helper.WS_DLGFRAME |
            Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLong(
            WindowHandle,
            Win32Helper.GWL_STYLE,
            style);
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);
        Win32Helper.SetWindowBorderColor(
            WindowHandle,
            unchecked((int)0xFFFFFFFE));
        Win32Helper.ApplyFullWindowFrame(WindowHandle);
        Win32Helper.SetWindowTheme(
            WindowHandle,
            materialAppearance.IsDark);
        int cornerPreference = Win32Helper.DWMWCP_DONOTROUND;
        _ = Win32Helper.TrySetDwmWindowAttribute(
            WindowHandle,
            Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference);
    }

    internal TextBox Editor { get; }

    internal IntPtr WindowHandle { get; }

    internal void UpdateAppearance(
        Brush background,
        Brush? foreground,
        WidgetMaterialBackdropAppearance materialAppearance)
    {
        _root.Background = background;
        _root.RequestedTheme = materialAppearance.IsDark
            ? ElementTheme.Dark
            : ElementTheme.Light;
        Editor.Foreground = foreground;

        if (WidgetMaterialSystemBackdrop.IsSupported(
                materialAppearance.MaterialType))
        {
            _materialBackdrop ??=
                new WidgetMaterialSystemBackdrop(materialAppearance);
            _materialBackdrop.UpdateAppearance(materialAppearance);
            if (!ReferenceEquals(SystemBackdrop, _materialBackdrop))
            {
                SystemBackdrop = _materialBackdrop;
            }
        }
        else
        {
            SystemBackdrop = null;
            _materialBackdrop = null;
        }

        Win32Helper.SetWindowTheme(
            WindowHandle,
            materialAppearance.IsDark);
    }

    internal void ShowAndFocus(RectInt32 bounds)
    {
        _appWindow.MoveAndResize(bounds);
        _appWindow.Show();
        // A title editor is a short-lived owned window. Pulse it through the
        // topmost band before activating so a peer widget cannot remain above
        // the editor while the owner is being re-activated.
        Win32Helper.BringWindowTemporarilyToFront(WindowHandle);
        Activate();
        bool foregroundSet = Win32Helper.SetForegroundWindow(WindowHandle);
        FocusEditor();
        App.LogVerbose(
            $"[FileStack] Title editor shown hwnd=0x{WindowHandle.ToInt64():X} " +
            $"owner=0x{_ownerWindowHandle.ToInt64():X} " +
            $"foreground=0x{Win32Helper.GetForegroundWindow().ToInt64():X} " +
            $"foregroundSet={foregroundSet}");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                Win32Helper.BringWindowTemporarilyToFront(WindowHandle);
                Activate();
                bool queuedForegroundSet =
                    Win32Helper.SetForegroundWindow(WindowHandle);
                App.LogVerbose(
                    $"[FileStack] Title editor focus pass hwnd=0x{WindowHandle.ToInt64():X} " +
                    $"foreground=0x{Win32Helper.GetForegroundWindow().ToInt64():X} " +
                    $"foregroundSet={queuedForegroundSet}");
            }
        });
    }

    internal void CloseEditorWindow()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        SystemBackdrop = null;
        _materialBackdrop = null;
        Close();
    }

    private void FocusEditor() =>
        InlineEditorFocus.FocusWhenLoaded(
            Editor,
            static editor => editor.SelectAll(),
            DispatcherQueue,
            "StackPopoverTitleRename");
}
