using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;
using System.Collections.Concurrent;
using WinRT;

namespace DeskBox.Views;

/// <summary>
/// Spotlight-style search popup window.
/// Appears centered horizontally, 1/3 from the top of the primary display.
/// Layout: search box, dynamic tab bar, result list, and a persistent footer.
/// The surface material and border follow the widget appearance settings.
/// </summary>
public sealed partial class SearchPopupWindow : Window
{
    private readonly SearchPopupViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly QuickLookPreviewService _quickLookService = new();
    private DispatcherTimer? _statusHideTimer;
    private AppWindow? _appWindow;
    private IntPtr _hwnd;

    // Native backdrop controllers (same approach as desktop widgets).
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _micaControllerAttached;
    private bool _acrylicControllerAttached;

    private const int MinPopupWidth = 400;
    private const int MinPopupHeight = 300;

    // The popup uses the same pointer-capture interaction model as widget windows,
    // avoiding the visible native WS_THICKFRAME border.
    private FrameworkElement? _windowInteractionElement;
    private bool _isWindowDragging;
    private bool _isWindowResizing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _interactionStartCursor;
    private RectInt32 _interactionStartBounds;

    // Keyboard-selected result row. Hover feedback is owned by the row control itself.
    private SearchResultRowControl? _selectedRow;

    // Drag state for result rows: distinguishes a click (execute) from a drag
    // (export the file/folder path to another app or widget).
    private SearchResultItem? _dragCandidate;
    private SearchResultRowControl? _dragSourceRow;
    private Windows.Foundation.Point _dragStartPoint;
    private bool _dragOccurred;
    private bool _restoreResultFocusAfterFlyout;

    // One stable search surface. Layout differences should follow available width,
    // not a user-facing mode that only changes the window dimensions.
    private const int PopupWidth = 680;
    private const int PopupHeight = 500;

    public SearchPopupWindow(
        SearchPopupViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        _localizationService = localizationService;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _viewModel.OwnerWindowHandle = _hwnd;
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindow();
        SetupBindings();

        _viewModel.ActionRequested += OnViewModelActionRequested;
        _viewModel.ContentRequested += OnViewModelContentRequested;
        _viewModel.QueryApplied += OnViewModelQueryApplied;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ResultsRepeater.ElementPrepared += OnResultsElementPrepared;
        _settingsService.SettingsChanged += OnAppearanceSettingsChanged;
        _settingsService.AppearancePreviewChanged += OnAppearanceSettingsChanged;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    public IntPtr WindowHandle => _hwnd;
    public bool IsPopupVisible { get; private set; }

    /// <summary>
    /// Raised when an action needs to be handled by the app (e.g., open settings).
    /// </summary>
    public event EventHandler<string>? ActionRequested;

    public event EventHandler<SearchResultItem>? ContentRequested;

    /// <summary>
    /// Shows the popup at the correct position and focuses the search box.
    /// </summary>
    public async void ShowPopup()
    {
        // Cancel any in-flight exit animation (and its pending window hide) so a fast
        // Alt+D re-toggle interrupts the dismissal instead of racing with it.
        PopupHideStoryboard.Stop();
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;

        // Appearance settings may have changed while the popup was hidden.
        ApplyMaterialFromSettings();

        // Restore the user's custom position/size if one was persisted (drag/resize
        // is remembered across shows); otherwise fall back to the mode-based default
        // dimensions and centered placement.
        if (!TryApplyCustomBounds())
        {
            _appWindow?.Resize(new SizeInt32(PopupWidth, PopupHeight));
            PositionOnScreen();
        }
        RootGrid.Opacity = 0.92;
        PopupTranslateTransform.Y = 6;
        _appWindow?.Show();
        IsPopupVisible = true;

        // This is an interactive search window, so it must be activatable again after
        // the user works in another app.
        Activate();
        Win32Helper.SetForegroundWindow(_hwnd);

        // Fluent entrance: quick scale + fade + rise, matching the Windows 11
        // menu/popup transition language.
        PopupShowStoryboard.Begin();

        // Focus immediately; recommendations can continue loading without making the
        // freshly shown window feel inert.
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus(FocusState.Programmatic);
        UpdatePanelVisibility();

        await _viewModel.OnPopupOpenedAsync();
        UpdatePanelVisibility();
    }

    /// <summary>
    /// Hides the popup without destroying it.
    /// </summary>
    public void HidePopup()
    {
        if (!IsPopupVisible)
        {
            return;
        }

        IsPopupVisible = false;
        _viewModel.ClearSearch();

        // Fluent exit: fast shrink + fade, then remove the window from view once the
        // animation completes. The window stays interactive-looking for ~150ms, which
        // is imperceptible but gives the dismissal a physical feel.
        PopupShowStoryboard.Stop();
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;
        PopupHideStoryboard.Completed += OnPopupHideCompleted;
        PopupHideStoryboard.Begin();
    }

    private void OnPopupHideCompleted(object? sender, object e)
    {
        PopupHideStoryboard.Completed -= OnPopupHideCompleted;

        // If the popup was re-shown while this callback was queued, the storyboard was
        // already stopped and this completion is stale; never hide a visible popup.
        if (!IsPopupVisible)
        {
            _appWindow?.Hide();
        }
    }

    /// <summary>
    /// Toggles the popup visibility.
    /// </summary>
    public void TogglePopup()
    {
        if (IsPopupVisible)
        {
            HidePopup();
        }
        else
        {
            ShowPopup();
        }
    }

    private void ConfigureWindow()
    {
        if (_appWindow is null)
        {
            return;
        }

        // Remove title bar
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        _appWindow.Resize(new SizeInt32(PopupWidth, PopupHeight));

        // Keep the popup off the taskbar, but leave it activatable so text input and
        // pointer interaction recover normally after another app receives focus.
        int extendedStyle = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        Win32Helper.SetWindowLongPtr(_hwnd, Win32Helper.GWL_EXSTYLE, new IntPtr(extendedStyle));

        // Strip all classic window chrome, including the thick resize frame. Pointer
        // hit targets in XAML provide resize behavior without the visible black ring.
        int style = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(_hwnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        // Kill the DWM-drawn border (the thick light edge that otherwise rings the
        // backdrop) and extend the frame edge-to-edge so the material reaches the corners.
        Win32Helper.SetWindowBorderColor(_hwnd, unchecked((int)0xFFFFFFFE));
        Win32Helper.ApplyFullWindowFrame(_hwnd);

        // Apply corner preference from settings (Default/Square/Small/Round).
        ApplyWindowCornerPreference();

        // Native material per the widget appearance settings (Mica/Acrylic/Solid).
        ApplyMaterialFromSettings();

    }

    private void OnAppearanceSettingsChanged()
    {
        void ApplyAppearance()
        {
            ApplyWindowCornerPreference();
            ApplyMaterialFromSettings();
            UpdateHotkeyHint();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyAppearance();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyAppearance);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive =
                args.WindowActivationState != WindowActivationState.Deactivated;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            UpdateSelectionActions();
        }
    }

    // Borderless drag and resize gestures.

    private void PersistCustomBounds()
    {
        if (!Win32Helper.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        _settingsService.Settings.SearchPopupCustomX = rect.Left;
        _settingsService.Settings.SearchPopupCustomY = rect.Top;
        _settingsService.Settings.SearchPopupCustomWidth = rect.Right - rect.Left;
        _settingsService.Settings.SearchPopupCustomHeight = rect.Bottom - rect.Top;
        _settingsService.SaveDebounced();
    }

    private void ResetToDefaultBounds()
    {
        _settingsService.Settings.SearchPopupCustomX = null;
        _settingsService.Settings.SearchPopupCustomY = null;
        _settingsService.Settings.SearchPopupCustomWidth = null;
        _settingsService.Settings.SearchPopupCustomHeight = null;
        _settingsService.SaveDebounced();

        _appWindow?.Resize(new SizeInt32(PopupWidth, PopupHeight));
        PositionOnScreen();
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalized = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(logicalPixels * normalized, MidpointRounding.AwayFromZero));
    }

    private void TopDragHotZone_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TopDragHandle.Opacity = 0.72;
        SetPointerCursor(TopDragHotZone, InputSystemCursorShape.SizeAll);
    }

    private void TopDragHotZone_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isWindowDragging)
        {
            TopDragHandle.Opacity = 0;
        }
    }

    private void TopDragHotZone_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            !e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!TryBeginWindowInteraction(element, e.Pointer))
        {
            return;
        }

        _isWindowDragging = true;
        TopDragHandle.Opacity = 1;
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
        if (string.IsNullOrEmpty(direction) || !TryBeginWindowInteraction(element, e.Pointer))
        {
            return;
        }

        _resizeDirection = direction;
        _isWindowResizing = true;
        e.Handled = true;
    }

    private bool TryBeginWindowInteraction(FrameworkElement element, Pointer pointer)
    {
        if (!Win32Helper.GetCursorPos(out _interactionStartCursor) ||
            !Win32Helper.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        _interactionStartBounds = new RectInt32(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
        _windowInteractionElement = element;
        if (!element.CapturePointer(pointer))
        {
            _windowInteractionElement = null;
            return false;
        }

        return true;
    }

    private void WindowInteraction_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if ((!_isWindowDragging && !_isWindowResizing) || _appWindow is null ||
            !Win32Helper.GetCursorPos(out var cursor))
        {
            return;
        }

        int deltaX = cursor.X - _interactionStartCursor.X;
        int deltaY = cursor.Y - _interactionStartCursor.Y;
        if (_isWindowDragging)
        {
            _appWindow.Move(new PointInt32(
                _interactionStartBounds.X + deltaX,
                _interactionStartBounds.Y + deltaY));
        }
        else
        {
            ApplyResizeDelta(deltaX, deltaY);
        }

        e.Handled = true;
    }

    private void ApplyResizeDelta(int deltaX, int deltaY)
    {
        if (_appWindow is null)
        {
            return;
        }

        int x = _interactionStartBounds.X;
        int y = _interactionStartBounds.Y;
        int width = _interactionStartBounds.Width;
        int height = _interactionStartBounds.Height;
        double scale = Win32Helper.GetDpiScaleForWindow(_hwnd, Content?.XamlRoot);
        int minWidth = ToPhysicalPixels(MinPopupWidth, scale);
        int minHeight = ToPhysicalPixels(MinPopupHeight, scale);

        if (_resizeDirection.Contains("Right", StringComparison.Ordinal))
        {
            width = Math.Max(minWidth, width + deltaX);
        }
        else if (_resizeDirection.Contains("Left", StringComparison.Ordinal))
        {
            int right = x + width;
            width = Math.Max(minWidth, width - deltaX);
            x = right - width;
        }

        if (_resizeDirection.Contains("Bottom", StringComparison.Ordinal))
        {
            height = Math.Max(minHeight, height + deltaY);
        }
        else if (_resizeDirection.Contains("Top", StringComparison.Ordinal))
        {
            int bottom = y + height;
            height = Math.Max(minHeight, height - deltaY);
            y = bottom - height;
        }

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
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

        var captureElement = _windowInteractionElement;
        _windowInteractionElement = null;
        _isWindowDragging = false;
        _isWindowResizing = false;
        _resizeDirection = string.Empty;
        captureElement?.ReleasePointerCapture(pointer);
        TopDragHandle.Opacity = 0;
        if (persist)
        {
            PersistCustomBounds();
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
            "Top" or "Bottom" => InputSystemCursorShape.SizeNorthSouth,
            "TopLeft" or "BottomRight" => InputSystemCursorShape.SizeNorthwestSoutheast,
            "TopRight" or "BottomLeft" => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => InputSystemCursorShape.Arrow
        };
        SetPointerCursor(element, shape);
    }

    private static void SetPointerCursor(UIElement element, InputSystemCursorShape shape)
    {
        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    /// <summary>
    /// Applies the native DWM corner style from the widget appearance settings.
    /// </summary>
    private void ApplyWindowCornerPreference()
    {
        int cornerPreference = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.DwmSetWindowAttribute(
            _hwnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference, sizeof(int));

        // Keep the XAML border overlay corner radius in sync with the native corner.
        PopupBorderOverlay.CornerRadius = cornerPreference switch
        {
            Win32Helper.DWMWCP_DONOTROUND => new CornerRadius(0),
            Win32Helper.DWMWCP_ROUNDSMALL => new CornerRadius(4),
            Win32Helper.DWMWCP_DEFAULT => new CornerRadius(4),
            _ => new CornerRadius(8)
        };
    }

    private void ApplyMaterialFromSettings()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = (App.Current as App)?.ThemeService?.GetEffectiveAccentColor()
                          ?? AccentColorHelper.DefaultAccentColor;

        string materialType = _settingsService.Settings.WidgetMaterialType;
        double surfaceOpacity = Math.Clamp(_settingsService.Settings.WidgetOpacity, 0.0, 1.0);
        double materialIntensity = Math.Clamp(_settingsService.Settings.WidgetMaterialIntensity, 0.0, 1.0);

        try
        {
            Win32Helper.SetWindowTheme(_hwnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hwnd);

            int backdropType;
            bool controllerApplied = false;

            if (SettingsService.IsMicaMaterial(materialType))
            {
                controllerApplied = ApplyMicaController(
                    isDark,
                    BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity),
                    materialType == SettingsService.WidgetMaterialTypeMicaAlt);
            }

            if (!controllerApplied && SettingsService.IsAcrylicMaterial(materialType))
            {
                controllerApplied = ApplyAcrylicController(
                    isDark,
                    BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity),
                    surfaceOpacity,
                    materialType == SettingsService.WidgetMaterialTypeAcrylicBase);
            }

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hwnd);
                // Keep the XAML surface transparent so the native backdrop shows through.
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00));
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hwnd);
                RootGrid.Background = new SolidColorBrush(
                    BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity, materialIntensity, materialType));
            }
            else
            {
                // Fallback: use legacy accent blur.
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hwnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                Win32Helper.ApplyAccentBlur(_hwnd, BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity), Math.Min(surfaceOpacity, 0.52), true);
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x01, 0x00, 0x00, 0x00));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] ApplyMaterialFromSettings fallback: {ex.Message}");
            DisposeAcrylicController();
            DisposeMicaController();
            Win32Helper.ApplyAccentBlur(_hwnd, BuildNativeBackdropTintColor(isDark, accentColor, materialIntensity), Math.Min(surfaceOpacity, 0.52), true);
        }

        var (thickness, borderColor) = GetPopupBorderVisuals(isDark, accentColor);
        PopupBorderOverlay.BorderThickness = new Thickness(thickness);
        PopupBorderOverlay.BorderBrush = borderColor.A == 0
            ? null
            : new SolidColorBrush(borderColor);

    }

    /// <summary>
    /// Builds the tint color for native backdrop materials by blending the base
    /// surface color with the accent color according to material intensity.
    /// </summary>
    private static Windows.UI.Color BuildNativeBackdropTintColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double materialIntensity)
    {
        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double accentMix = 0.07 * materialIntensity;
        return BlendColors(baseColor, accentColor, accentMix);
    }

    private bool ApplyMicaController(bool isDark, Windows.UI.Color tintColor, bool useAlt)
    {
        if (!MicaController.IsSupported())
        {
            DisposeMicaController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (_micaController is not null)
        {
            DisposeMicaController();
        }

        _micaController = new MicaController
        {
            Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base
        };

        DetachAcrylicControllerTarget();
        if (!_micaControllerAttached)
        {
            if (!_micaController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeMicaController();
                return false;
            }

            _micaControllerAttached = true;
            _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
        }

        _micaController.TintColor = tintColor;
        _micaController.FallbackColor = useAlt
            ? isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x16, 0x18, 0x1D)
                : Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xEA, 0xEF)
            : isDark
                ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x22, 0x26)
                : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double intensity = Math.Clamp(_settingsService.Settings.WidgetMaterialIntensity, 0.0, 1.0);
        double tintOpacity = useAlt
            ? Lerp(0.28, 0.82, intensity)
            : Lerp(0.04, 0.46, intensity);
        double luminosityOpacity = useAlt
            ? Lerp(isDark ? 0.34 : 0.42, isDark ? 0.72 : 0.76, intensity)
            : Lerp(isDark ? 0.78 : 0.82, isDark ? 0.94 : 0.96, intensity);

        _micaController.TintOpacity = (float)tintOpacity;
        _micaController.LuminosityOpacity = (float)luminosityOpacity;
        return true;
    }

    private bool ApplyAcrylicController(bool isDark, Windows.UI.Color tintColor, double surfaceOpacity, bool useBase)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _backdropConfiguration.HighContrastBackgroundColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        if (_acrylicController is not null && !_acrylicController.IsClosed)
        {
            DisposeAcrylicController();
        }

        _acrylicController = new DesktopAcrylicController
        {
            Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin
        };

        DetachMicaControllerTarget();
        if (!_acrylicControllerAttached)
        {
            if (!_acrylicController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }

            _acrylicControllerAttached = true;
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        }

        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;

        double intensity = Math.Clamp(_settingsService.Settings.WidgetMaterialIntensity, 0.0, 1.0);
        double surfaceStrength = Lerp(0.08, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = useBase
            ? Lerp(isDark ? 0.18 : 0.12, isDark ? 0.72 : 0.62, intensity)
            : Lerp(isDark ? 0.04 : 0.02, isDark ? 0.42 : 0.34, intensity);
        double luminosityOpacity = useBase
            ? Lerp(isDark ? 0.38 : 0.46, isDark ? 0.82 : 0.90, intensity)
            : Lerp(isDark ? 0.16 : 0.22, isDark ? 0.56 : 0.64, intensity);

        _acrylicController.TintOpacity = (float)Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0);
        _acrylicController.LuminosityOpacity = (float)Math.Clamp(luminosityOpacity * surfaceStrength, 0.0, 1.0);
        return true;
    }

    private void DisposeMicaController()
    {
        if (_micaController is null)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
            _micaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _micaController = null;
            _micaControllerAttached = false;
        }
    }

    private void DetachMicaControllerTarget()
    {
        if (_micaController is null || !_micaControllerAttached)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            _micaControllerAttached = false;
        }
    }

    private void DisposeAcrylicController()
    {
        if (_acrylicController is null)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
            _acrylicController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _acrylicController = null;
            _acrylicControllerAttached = false;
        }
    }

    private void DetachAcrylicControllerTarget()
    {
        if (_acrylicController is null || !_acrylicControllerAttached)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            _acrylicControllerAttached = false;
        }
    }

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0.0, 1.0));

    /// <summary>
    /// Mirrors the widget border visuals (style thickness + neutral/accent color mode)
    /// so the popup edge matches the desktop widgets.
    /// </summary>
    private (double Thickness, Windows.UI.Color BorderColor) GetPopupBorderVisuals(
        bool isDark, Windows.UI.Color accentColor)
    {
        string borderStyle = _settingsService.Settings.WidgetBorderStyle;
        string colorMode = _settingsService.Settings.WidgetBorderColorMode;
        var (thickness, alpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick => (1.6d, (byte)0x48),
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            _ => (0.8d, (byte)0x18)
        };

        if (colorMode == SettingsService.WidgetBorderColorModeNone)
        {
            return (0d, Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        bool useAccent = colorMode == SettingsService.WidgetBorderColorModeAccent;
        byte borderAlpha = useAccent
            ? (byte)Math.Clamp(Math.Round(alpha * 1.35), 0, 255)
            : alpha;
        byte red = useAccent ? accentColor.R : isDark ? (byte)0xFF : (byte)0x00;
        byte green = useAccent ? accentColor.G : isDark ? (byte)0xFF : (byte)0x00;
        byte blue = useAccent ? accentColor.B : isDark ? (byte)0xFF : (byte)0x00;
        return (thickness, Windows.UI.Color.FromArgb(borderAlpha, red, green, blue));
    }

    // Solid-mode surface color (mirrors the widget frosted surface).

    private static Windows.UI.Color BuildFrostedSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double surfaceOpacity,
        double materialIntensity,
        string materialType)
    {
        // Mica uses a slightly different base blend than Solid.
        bool isMica = SettingsService.IsMicaMaterial(materialType);

        var baseColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x21, 0x24, 0x2A)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        // Blend accent color into the base according to intensity.
        double accentMix = (isMica ? 0.07 : 0.05) * materialIntensity;
        var blended = BlendColors(baseColor, accentColor, accentMix);

        // Apply surface opacity (alpha channel).
        double materialOpacity = isDark
            ? Math.Clamp(surfaceOpacity * 0.78, 0.10, 0.82)
            : Math.Clamp(surfaceOpacity * 0.78, 0.0, 0.78);

        return ApplySurfaceOpacity(blended, materialOpacity);
    }

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        return Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            color.R,
            color.G,
            color.B);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    /// <summary>
    /// Applies the persisted custom bounds (drag/resize memory) if they are complete and
    /// still valid on the current display configuration. Returns false when the default
    /// placement should be used instead (no custom bounds saved, bounds too small, or
    /// the saved rectangle no longer intersects any visible work area — e.g. after a
    /// monitor was disconnected).
    /// </summary>
    private bool TryApplyCustomBounds()
    {
        if (_appWindow is null)
        {
            return false;
        }

        var settings = _settingsService.Settings;
        if (settings.SearchPopupCustomX is not int x ||
            settings.SearchPopupCustomY is not int y ||
            settings.SearchPopupCustomWidth is not int width ||
            settings.SearchPopupCustomHeight is not int height)
        {
            return false;
        }

        if (width < MinPopupWidth || height < MinPopupHeight)
        {
            return false;
        }

        // Validate against the work area of the display the saved position belongs to.
        var displayArea = DisplayArea.GetFromPoint(
            new PointInt32(x, y), DisplayAreaFallback.Nearest);
        var work = displayArea.WorkArea;
        bool intersects = x < work.X + work.Width && x + width > work.X &&
                          y < work.Y + work.Height && y + height > work.Y;
        if (!intersects)
        {
            return false;
        }

        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        return true;
    }

    private void PositionOnScreen()
    {
        if (_appWindow is null)
        {
            return;
        }

        var displayArea = DisplayArea.GetFromWindowId(
            _appWindow.Id, DisplayAreaFallback.Primary);

        int workWidth = displayArea.WorkArea.Width;
        int workHeight = displayArea.WorkArea.Height;
        int workLeft = displayArea.WorkArea.X;
        int workTop = displayArea.WorkArea.Y;

        int x = workLeft + (workWidth - PopupWidth) / 2;
        int y = workTop + (int)(workHeight * 0.25);

        _appWindow.Move(new PointInt32(x, y));
    }

    private void SetupBindings()
    {
        UpdateHotkeyHint();
        SearchTextBox.PlaceholderText = _localizationService.T("Search.Placeholder");
        ToolTipService.SetToolTip(ClosePopupButton, _localizationService.T("Search.Close"));
        NoResultsTitle.Text = _localizationService.T("Search.NoResults.Title");
        NoResultsSubtitle.Text = _localizationService.T("Search.NoResults.Subtitle");
        EmptyTabHintText.Text = _localizationService.T("Search.Tab.Empty");

        OpenSettingsLabel.Text = _localizationService.T("Search.Action.OpenSettings");

        SortNameLabel.Text = _localizationService.T("Search.Sort.Name");
        SortSizeLabel.Text = _localizationService.T("Search.Sort.Size");
        SortDateLabel.Text = _localizationService.T("Search.Sort.Date");
        ResultFilterLabel.Text = _localizationService.T("Search.Filter.Label");
        FilterAllItem.Content = _localizationService.T("Search.Filter.All");
        FilterFilesItem.Content = _localizationService.T("Search.Filter.Files");
        FilterAppsItem.Content = _localizationService.T("Search.Filter.Apps");
        FilterImagesItem.Content = _localizationService.T("Search.Filter.Images");
        FilterDocumentsItem.Content = _localizationService.T("Search.Filter.Documents");
        FilterDeskBoxItem.Content = _localizationService.T("Search.Filter.DeskBox");
        HomeSectionHeader.Text = _localizationService.T("Search.Section.RecommendedApps");
        OpenSelectedLabel.Text = _localizationService.T("Search.Menu.Open");
        OpenLocationLabel.Text = _localizationService.T("Search.Menu.OpenLocation");
        PreviewSelectedLabel.Text = _localizationService.T("Search.Menu.Preview");
        AttachSelectedLabel.Text = _localizationService.T("Search.Menu.AttachToTodo");
        SaveSelectedLabel.Text = _localizationService.T("Search.Menu.SaveToNote");

        // Recommendation panel localization
        FavoritesHeaderText.Text = _localizationService.T("Search.Recommend.Favorite");
        RecentSearchesHeaderText.Text = _localizationService.T("Search.Recommend.History");
        ClearAllButton.Content = _localizationService.T("Search.Section.ClearHistory");
        ClearRecentButton.Content = _localizationService.T("Search.Section.ClearHistory");
        ConfirmClearAllItem.Text = _localizationService.T("Search.Section.ClearHistory");
        ConfirmClearRecentItem.Text = _localizationService.T("Search.Section.ClearHistory");

        TabsList.ItemsSource = _viewModel.Tabs;
        ResultsRepeater.ItemsSource = _viewModel.CurrentResults;
        RecommendedAppsRepeater.ItemsSource = _viewModel.CurrentResults;
        
        // Bind recommendation panels (favorites and recent searches)
        var favorites = _viewModel.FavoriteQueries.Select(q => new { Title = q }).ToList();
        var recent = _viewModel.RecentQueries.Take(8).Select(q => new { Title = q }).ToList();
        FavoritesRepeater.ItemsSource = favorites;
        RecentSearchesRepeater.ItemsSource = recent;
        
        // Hook up item tap events for recommendations
        FavoritesRepeater.ElementPrepared += (s, e) => UpdateRecItemClickEvent(e.Element);
        RecentSearchesRepeater.ElementPrepared += (s, e) => UpdateRecItemClickEvent(e.Element);

        UpdatePanelVisibility();
        UpdateSortHeaders();
    }

    private void UpdateHotkeyHint()
    {
        string hint = _viewModel.HotkeyHint;
        HotkeyHintText.Text = hint;
        HotkeyHintBadge.Visibility = string.IsNullOrWhiteSpace(hint)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HidePopup();
            e.Handled = true;
        }
    }

    private void ClosePopupButton_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
    }

    private void UpdatePanelVisibility()
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchTextBox.Text);
        bool searching = _viewModel.IsSearching;
        bool hasResults = _viewModel.HasResults;
        bool tabHasItems = _viewModel.HasCurrentResults;
        bool tabSelected = _viewModel.SelectedTab is not null;

        SearchProgressBar.Visibility = searching && hasQuery
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoadingStatusText.Text = _localizationService.T("Search.Status.Searching");
        LoadingPanel.Visibility = searching && hasQuery && !hasResults
            ? Visibility.Visible
            : Visibility.Collapsed;
        TabsList.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;

        NoResultsPanel.Visibility = hasQuery && !searching && !hasResults
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool showResults = hasQuery && hasResults && tabHasItems;
        ResultsPanel.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        bool showResultChrome = hasQuery && hasResults && tabSelected;

        bool showRecommendedApps = !hasQuery && tabHasItems;
        RecommendedAppsPanel.Visibility = showRecommendedApps
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Sortable header only for file-style tabs (All / extension tabs / File / Folder).
        bool fileSortTab = _viewModel.SelectedTab?.SupportsFileSort == true;
        SortHeaderRow.Visibility = showResultChrome && fileSortTab
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResultFilterBar.Visibility = showResultChrome && _viewModel.SelectedTab?.Id == "all"
            ? Visibility.Visible
            : Visibility.Collapsed;

        EmptyTabHintPanel.Visibility = !searching && !showResults && !showRecommendedApps && tabSelected
                                       && !(hasQuery && !hasResults)
            ? Visibility.Visible
            : Visibility.Collapsed;

        HomeSectionHeader.Visibility = showRecommendedApps
            ? Visibility.Visible
            : Visibility.Collapsed;

        RecommendationPanel.Visibility = Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.Query = SearchTextBox.Text;
        UpdatePanelVisibility();
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                if (!string.IsNullOrEmpty(SearchTextBox.Text))
                {
                    SearchTextBox.Text = string.Empty;
                    _viewModel.ClearSearch();
                }
                else
                {
                    HidePopup();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                _viewModel.MoveSelectionUp();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                _viewModel.MoveSelectionDown();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                bool controlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
                bool executed = controlPressed
                    ? _viewModel.OpenSelectedLocation()
                    : _viewModel.ExecuteSelectedItem();
                if (executed)
                {
                    HidePopup();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Space:
                if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
                {
                    TryPreviewSelectedItem();
                    e.Handled = true;
                }
                break;

            case Windows.System.VirtualKey.Tab:
                if (FocusSelectedResult())
                {
                    e.Handled = true;
                }
                break;
        }
    }

    private bool FocusSelectedResult()
    {
        if (_viewModel.SelectedItem is not { } selected ||
            FindRowByDataContext(ResultsRepeater, selected) is not { } row)
        {
            return false;
        }

        row.IsTabStop = true;
        return row.Focus(FocusState.Programmatic);
    }

    private void ResultsPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                SearchTextBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                _viewModel.MoveSelectionUp();
                FocusSelectedResult();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                _viewModel.MoveSelectionDown();
                FocusSelectedResult();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                bool opened = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
                    ? _viewModel.OpenSelectedLocation()
                    : _viewModel.ExecuteSelectedItem();
                if (opened)
                {
                    HidePopup();
                }
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Space:
                TryPreviewSelectedItem();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Updates click event handlers on recommendation list items so tapping applies the query.
    /// </summary>
    private void UpdateRecItemClickEvent(DependencyObject element)
    {
        if (element is FrameworkElement fe && fe.DataContext is { } dc)
        {
            if (dc.GetType().GetProperty("Title")?.GetValue(dc) is string queryText)
            {
                fe.PointerPressed -= OnRecommendationItem_PointerPressed;
                fe.PointerPressed += OnRecommendationItem_PointerPressed;
            }
        }
    }

    private void OnRecommendationItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is { } dc)
        {
            if (dc.GetType().GetProperty("Title")?.GetValue(dc) is string queryText)
            {
                _viewModel.ApplyQuery(queryText);
                e.Handled = true;
            }
        }
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.InvokeAction("open-settings");
    }

    private void RecommendedAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchResultItem item })
        {
            _viewModel.ExecuteItem(item);
        }
    }

    // 鈹€鈹€ Tab bar 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabsList.SelectedItem is SearchTabItem tab &&
            !ReferenceEquals(tab, _viewModel.SelectedTab))
        {
            _viewModel.SelectedTab = tab;
        }
    }

    private void SyncTabSelection()
    {
        if (!ReferenceEquals(TabsList.SelectedItem, _viewModel.SelectedTab))
        {
            TabsList.SelectedItem = _viewModel.SelectedTab;
        }
    }

    /// <summary>
    /// Clear history button clicked - confirms via flyout and clears appropriate data.
    /// </summary>
    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        // This method is a placeholder; actual clearing happens in ConfirmClearHistory_Click
    }

    /// <summary>
    /// Confirms clear action from the confirmation menu item.
    /// Uses the button's Tag property to identify type of clear.
    /// </summary>
    private void ConfirmClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuFlyoutItem;
        if (menuItem?.Parent is MenuFlyout parentFlyout)
        {
            // Get tag from the button to determine type
            if (parentFlyout.Target is Button buttonElement && buttonElement.Tag is string clearType)
            {
                if (clearType == "all")
                {
                    _viewModel.ClearAllHistory();
                }
                else
                {
                    _viewModel.ClearRecentSearches();
                }
            }
        }
    }

    // 鈹€鈹€ Sort headers 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void SortNameHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Name);
    }

    private void SortSizeHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Size);
    }

    private void SortDateHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Date);
    }

    private void ResultFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultFilterComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: true, out SearchResultFilter filter))
        {
            _viewModel.ResultFilter = filter;
        }
    }

    private void UpdateSortHeaders()
    {
        var column = _viewModel.SortColumn;
        bool ascending = _viewModel.SortAscending;
        SetSortIndicator(SortNameDirection, column == ResultSortColumn.Name, ascending);
        SetSortIndicator(SortSizeDirection, column == ResultSortColumn.Size, ascending);
        SetSortIndicator(SortDateDirection, column == ResultSortColumn.Date, ascending);
    }

    private static void SetSortIndicator(FontIcon icon, bool active, bool ascending)
    {
        icon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        icon.Glyph = ascending ? "\uE74A" : "\uE74B";
    }

    // Result row interaction (hover, click, drag, and context menu).

    private void ResultsPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ResultsPanel);
        var item = FindDataContext<SearchResultItem>(e.OriginalSource as DependencyObject);
        var row = FindItemRow(e.OriginalSource as DependencyObject);

        if (item is not null &&
            (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed))
        {
            SelectResultItem(item, row);
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            _dragSourceRow = null;
            _dragOccurred = false;
            return;
        }

        _dragCandidate = item;
        _dragSourceRow = row;
        _dragStartPoint = e.GetCurrentPoint(null).Position;
        _dragOccurred = false;
    }

    private async void ResultsPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCandidate is null || _dragSourceRow is null || _dragOccurred ||
            string.IsNullOrWhiteSpace(_dragCandidate.DetailPath))
        {
            return;
        }

        var current = e.GetCurrentPoint(null).Position;
        double dx = current.X - _dragStartPoint.X;
        double dy = current.Y - _dragStartPoint.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < 10)
        {
            return;
        }

        // Begin a drag operation carrying the file/folder payload.
        _dragOccurred = true;
        var item = _dragCandidate;
        var row = _dragSourceRow;

        Windows.Foundation.TypedEventHandler<UIElement, DragStartingEventArgs> handler = null!;
        handler = async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Data.Properties.Title = item.Title;
                args.Data.RequestedOperation = DataPackageOperation.Copy;
                await SetDragPayloadAsync(args.Data, item.DetailPath!);
            }
            finally
            {
                deferral.Complete();
                row.DragStarting -= handler;
            }
        };
        row.DragStarting += handler;

        try
        {
            await row.StartDragAsync(e.GetCurrentPoint(row));
        }
        finally
        {
            row.DragStarting -= handler;
            _dragCandidate = null;
            _dragSourceRow = null;
        }
    }

    private void ResultsPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var releasedItem = FindDataContext<SearchResultItem>(e.OriginalSource as DependencyObject);
        bool isLeftRelease = e.GetCurrentPoint(ResultsPanel).Properties.PointerUpdateKind ==
                             PointerUpdateKind.LeftButtonReleased;
        if (isLeftRelease && _dragCandidate is not null && !_dragOccurred &&
            ReferenceEquals(_dragCandidate, releasedItem))
        {
            _viewModel.ExecuteItem(_dragCandidate);
            e.Handled = true;
        }

        _dragCandidate = null;
        _dragSourceRow = null;
        _dragOccurred = false;
    }

    private void ResultsPanel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = FindDataContext<SearchResultItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        SelectResultItem(item, FindItemRow(e.OriginalSource as DependencyObject));
        if (_viewModel.ExecuteItem(item))
        {
            e.Handled = true;
        }
    }

    private void SelectResultItem(SearchResultItem item, SearchResultRowControl? row = null)
    {
        int index = _viewModel.CurrentResults.IndexOf(item);
        if (index >= 0)
        {
            _viewModel.SelectedIndex = index;
        }

        _viewModel.SelectedItem = item;
        row ??= FindRowByDataContext(ResultsRepeater, item);
        if (row is not null)
        {
            row.IsTabStop = true;
            row.Focus(FocusState.Pointer);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchPopupViewModel.SelectedItem):
                UpdateSelectionHighlight();
                UpdateSelectionActions();
                break;

            case nameof(SearchPopupViewModel.IsSearching):
            case nameof(SearchPopupViewModel.HasResults):
            case nameof(SearchPopupViewModel.HasCurrentResults):
                UpdatePanelVisibility();
                break;

            case nameof(SearchPopupViewModel.SelectedTab):
                SyncTabSelection();
                RefreshPreparedResultRows();
                UpdatePanelVisibility();
                break;

            case nameof(SearchPopupViewModel.SortColumn):
            case nameof(SearchPopupViewModel.SortAscending):
                UpdateSortHeaders();
                break;

            case nameof(SearchPopupViewModel.StatusText):
                // Result counts and timing remain diagnostic data, not persistent UI.
                break;
        }
    }

    /// <summary>
    /// Highlights the row for the keyboard-selected result and brings it into view.
    /// </summary>
    private void UpdateSelectionHighlight()
    {
        if (_selectedRow is not null)
        {
            _selectedRow.IsSelected = false;
            _selectedRow.IsTabStop = false;
            _selectedRow = null;
        }

        if (_viewModel.SelectedItem is not { } selected)
        {
            return;
        }

        if (FindRowByDataContext(ResultsRepeater, selected) is { } row)
        {
            _selectedRow = row;
            row.IsSelected = true;
            row.IsTabStop = true;
            row.StartBringIntoView();
        }
    }

    /// <summary>
    /// Freshly populated results are realized one layout pass after the collection
    /// changes, so a selection made before that finds no row. Re-apply the highlight
    /// and the lazy icon visuals as each row element is prepared.
    /// </summary>
    private void OnResultsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not SearchResultRowControl row)
        {
            return;
        }

        // Lazy shell icon: show the real icon once resolved, otherwise the glyph block.
        // Recycled rows can be re-bound to the same item instance (no DataContextChanged),
        // so this must run on every prepare.
        row.RefreshIconVisuals();
        row.SetFileColumnsVisible(_viewModel.SelectedTab?.SupportsFileSort == true);

        bool isSelectedRow = _viewModel.SelectedItem is { } selected &&
                             ReferenceEquals(row.DataContext, selected);
        if (isSelectedRow && !ReferenceEquals(row, _selectedRow))
        {
            if (_selectedRow is not null)
            {
                _selectedRow.IsSelected = false;
            }

            _selectedRow = row;
            row.IsSelected = true;
            row.IsTabStop = true;
        }
        else if (!isSelectedRow && row.IsSelected)
        {
            // A recycled element may still carry stale selection visuals.
            row.IsSelected = false;
            row.IsTabStop = false;
        }
    }

    private void ResultsPanel_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _dragCandidate = null;
        _dragSourceRow = null;
        _dragOccurred = false;

        var item = FindDataContext<SearchResultItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        var row = FindItemRow(e.OriginalSource as DependencyObject);
        SelectResultItem(item, row);
        var anchor = (UIElement?)row ?? ResultsPanel;
        ShowResultFlyout(item, anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private void ShowResultFlyout(SearchResultItem item, UIElement anchor, Windows.Foundation.Point point)
    {
        var flyout = BuildResultContextMenu(item);
        if (flyout.Items.Count == 0)
        {
            return;
        }

        _restoreResultFocusAfterFlyout = true;
        flyout.Closed += (_, _) =>
        {
            if (_restoreResultFocusAfterFlyout && IsPopupVisible && _viewModel.SelectedItem is not null)
            {
                DispatcherQueue.TryEnqueue(() => FocusSelectedResult());
            }

            _restoreResultFocusAfterFlyout = false;
        };
        flyout.ShowAt(anchor, point);
    }

    /// <summary>
    /// Builds a context menu of secondary actions for a search result row.
    /// </summary>
    private MenuFlyout BuildResultContextMenu(SearchResultItem item)
    {
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.Open"),
            Icon = new FontIcon { Glyph = "\uE8E5" }
        };
        openItem.Click += (_, _) =>
        {
            _viewModel.ExecuteItem(item);
        };
        flyout.Items.Add(openItem);

        bool isFileSystemItem = item.Kind is SearchResultKind.File or SearchResultKind.Folder &&
                                !string.IsNullOrWhiteSpace(item.DetailPath) &&
                                (File.Exists(item.DetailPath) || Directory.Exists(item.DetailPath));
        if (!isFileSystemItem)
        {
            return flyout;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var cutItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cut"),
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopyFileSystemItemAsync(item, DataPackageOperation.Move);
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopyFileSystemItemAsync(item, DataPackageOperation.Copy);
        flyout.Items.Add(copyItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += async (_, _) =>
        {
            _restoreResultFocusAfterFlyout = false;
            await RenameFileSystemItemAsync(item);
        };
        flyout.Items.Add(renameItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyPathItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.CopyPath"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathItem.Click += (_, _) => CopyPathToClipboard(item);
        flyout.Items.Add(copyPathItem);

        var openLocationItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.OpenLocation"),
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        openLocationItem.Click += (_, _) => Win32Helper.ShowInExplorer(item.DetailPath!);
        flyout.Items.Add(openLocationItem);

        var propertiesItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Properties"),
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        propertiesItem.Click += (_, _) =>
        {
            _restoreResultFocusAfterFlyout = false;
            ShowFileSystemItemProperties(item);
        };
        flyout.Items.Add(propertiesItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) =>
        {
            _restoreResultFocusAfterFlyout = false;
            await DeleteFileSystemItemAsync(item);
        };
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private async Task CopyFileSystemItemAsync(SearchResultItem item, DataPackageOperation operation)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        try
        {
            var data = new DataPackage { RequestedOperation = operation };
            await SetDragPayloadAsync(data, item.DetailPath);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            ShowTransientStatus(_localizationService.T(
                operation == DataPackageOperation.Move
                    ? "Search.Action.CutReady"
                    : "Search.Action.CopyReady"));
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Clipboard operation failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private async Task RenameFileSystemItemAsync(SearchResultItem item)
    {
        string path = item.DetailPath ?? string.Empty;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var nameBox = new TextBox
        {
            Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            MinWidth = 320,
            SelectionStart = 0
        };
        nameBox.SelectionLength = nameBox.Text.Length;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("Search.Rename.Title"),
            Content = nameBox,
            PrimaryButtonText = _localizationService.T("Common.Rename"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string newName = nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowTransientStatus(_localizationService.T("Search.Action.InvalidName"));
            return;
        }

        string? parent = Path.GetDirectoryName(path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        string targetPath = Path.Combine(parent, newName);
        if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(path))
                {
                    File.Move(path, targetPath);
                }
                else
                {
                    Directory.Move(path, targetPath);
                }
            });
            ShowTransientStatus(_localizationService.T("Search.Action.Renamed"));
            await RefreshResultsAfterFileOperationAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Rename failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private async Task DeleteFileSystemItemAsync(SearchResultItem item)
    {
        string path = item.DetailPath ?? string.Empty;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = string.Format(_localizationService.T("Search.Delete.Title"), item.Title),
            Content = _localizationService.T("Search.Delete.Message"),
            PrimaryButtonText = _localizationService.T("Common.Delete"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            });
            ShowTransientStatus(_localizationService.T("Search.Action.Deleted"));
            await RefreshResultsAfterFileOperationAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Delete failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private void ShowFileSystemItemProperties(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        try
        {
            if (!ShellContextMenuHelper.ShowProperties(_hwnd, item.DetailPath))
            {
                App.Log($"[SearchPopup] Properties failed for '{item.DetailPath}'.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Properties failed: {ex.Message}");
        }
    }

    private async Task RefreshResultsAfterFileOperationAsync()
    {
        await Task.Delay(120);
        await _viewModel.RefreshSearchAsync();
    }

    private static bool CanAttachItem(SearchResultItem item) =>
        item.Kind == SearchResultKind.File &&
        !string.IsNullOrWhiteSpace(item.DetailPath) &&
        File.Exists(item.DetailPath);

    private static bool CanSaveItem(SearchResultItem item) => CanAttachItem(item);

    private void TryPreviewSelectedItem()
    {
        var item = _viewModel.SelectedItem;
        if (item is not null)
        {
            _ = PreviewItemAsync(item);
        }
    }

    private async Task PreviewItemAsync(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        bool shown = await _quickLookService.TryToggleAsync(item.DetailPath);
        if (!shown)
        {
            App.Log($"[SearchPopup] QuickLook preview unavailable for '{item.DetailPath}'.");
        }
    }

    private async Task AttachItemToTodoAsync(SearchResultItem item)
    {
        var actionService = (App.Current as App)?.SearchActionService;
        if (actionService is null)
        {
            return;
        }

        bool ok = await actionService.AttachFileToTodoAsync(item.DetailPath);
        ShowTransientStatus(_localizationService.T(
            ok ? "Search.Action.AttachedToTodo" : "Search.Action.AttachFailed"));
    }

    private async Task SaveItemToNoteAsync(SearchResultItem item)
    {
        var actionService = (App.Current as App)?.SearchActionService;
        if (actionService is null)
        {
            return;
        }

        bool ok = await actionService.SaveFileToNoteAsync(item.DetailPath);
        ShowTransientStatus(_localizationService.T(
            ok ? "Search.Action.SavedToNote" : "Search.Action.SaveFailed"));
    }

    private void CopyPathToClipboard(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(item.DetailPath);
            Clipboard.SetContent(dataPackage);
            ShowTransientStatus(_localizationService.T("Search.Action.PathCopied"));
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Failed to copy path: {ex.Message}");
        }
    }

    // File-system clipboard and status helpers.

    /// <summary>
    /// Shows a transient status message in the footer, auto-hiding after a delay.
    /// </summary>
    private void ShowTransientStatus(string message)
    {
        StatusTextBlock.Text = message;
        SelectionActionBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Visible;

        _statusHideTimer?.Stop();
        _statusHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _statusHideTimer.Tick += (_, _) =>
        {
            StatusBar.Visibility = Visibility.Collapsed;
            _statusHideTimer?.Stop();
            UpdateSelectionActions();
        };
        _statusHideTimer.Start();
    }

    /// <summary>
    /// Populates the drag payload with the result's file or folder, falling back to
    /// the raw path as text when the item cannot be resolved.
    /// </summary>
    private static async Task SetDragPayloadAsync(DataPackage data, string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                data.SetStorageItems(new IStorageItem[] { folder });
                return;
            }

            if (File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                data.SetStorageItems(new IStorageItem[] { file });
                return;
            }
        }
        catch
        {
            // Fall through to a plain-text path payload.
        }

        data.SetText(path);
    }

    private static SearchResultRowControl? FindItemRow(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is SearchResultRowControl row)
            {
                return row;
            }

            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static SearchResultRowControl? FindRowByDataContext(DependencyObject root, object data)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is SearchResultRowControl { DataContext: var dc } row && ReferenceEquals(dc, data))
            {
                return row;
            }

            if (FindRowByDataContext(child, data) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? element) where T : class
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: T data })
            {
                return data;
            }

            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnViewModelActionRequested(object? sender, string actionId)
    {
        HidePopup();
        ActionRequested?.Invoke(this, actionId);
    }

    private void RefreshPreparedResultRows()
    {
        RefreshPreparedResultRows(ResultsRepeater);
    }

    private void RefreshPreparedResultRows(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is SearchResultRowControl row)
            {
                row.SetFileColumnsVisible(_viewModel.SelectedTab?.SupportsFileSort == true);
            }

            RefreshPreparedResultRows(child);
        }
    }

    private void UpdateSelectionActions()
    {
        var item = _viewModel.SelectedItem;
        bool show = item is not null &&
                    ResultsPanel.Visibility == Visibility.Visible &&
                    StatusBar.Visibility != Visibility.Visible;
        SelectionActionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || item is null)
        {
            return;
        }

        bool isFileSystemItem = item.Kind is SearchResultKind.File or SearchResultKind.Folder &&
                                !string.IsNullOrWhiteSpace(item.DetailPath);
        OpenLocationButton.Visibility = isFileSystemItem ? Visibility.Visible : Visibility.Collapsed;
        PreviewSelectedButton.Visibility = isFileSystemItem && _quickLookService.CanPreview(item.DetailPath)
            ? Visibility.Visible
            : Visibility.Collapsed;
        AttachSelectedButton.Visibility = CanAttachItem(item) ? Visibility.Visible : Visibility.Collapsed;
        SaveSelectedButton.Visibility = CanSaveItem(item) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ExecuteSelectedItem())
        {
            HidePopup();
        }
    }

    private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.OpenSelectedLocation())
        {
            HidePopup();
        }
    }

    private void PreviewSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        TryPreviewSelectedItem();
    }

    private async void AttachSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is { } item)
        {
            await AttachItemToTodoAsync(item);
        }
    }

    private async void SaveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedItem is { } item)
        {
            await SaveItemToNoteAsync(item);
        }
    }

    private void OnViewModelContentRequested(object? sender, SearchResultItem item)
    {
        HidePopup();
        ContentRequested?.Invoke(this, item);
    }

    private void OnViewModelQueryApplied(object? sender, string query)
    {
        // Reflect the applied history/favorite query into the search box and re-focus.
        SearchTextBox.Text = query;
        SearchTextBox.Focus(FocusState.Programmatic);
        UpdatePanelVisibility();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _viewModel.ActionRequested -= OnViewModelActionRequested;
        _viewModel.ContentRequested -= OnViewModelContentRequested;
        _viewModel.QueryApplied -= OnViewModelQueryApplied;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ResultsRepeater.ElementPrepared -= OnResultsElementPrepared;
        _settingsService.SettingsChanged -= OnAppearanceSettingsChanged;
        _settingsService.AppearancePreviewChanged -= OnAppearanceSettingsChanged;
        Activated -= OnWindowActivated;
        DisposeAcrylicController();
        DisposeMicaController();
        _viewModel.Dispose();
    }
}

// ────────────────────────────────────────────────────────────────────────────

