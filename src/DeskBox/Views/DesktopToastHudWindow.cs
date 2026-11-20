using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Global borderless, non-activating bottom-center HUD flyout window modeled on
/// Windows 11's native Hardware Indicator flyout (Volume / Brightness HUD):
/// a fixed frosted-acrylic pill (<see cref="DesktopAcrylicController"/> with a
/// forced-input-active configuration on a
/// <see cref="SystemBackdropElement"/>, with an opaque solid fallback when
/// system acrylic is unavailable or high contrast is active), 8 DIP rounded
/// corners with no stroke of any kind, 48 DIP minimum single-line height, 14px
/// Segoe UI Variable Text typography, monochrome primary status iconography,
/// and linear slide-up / slide-down show &amp; hide motion.
/// </summary>
internal sealed partial class DesktopToastHudWindow : Window
{
    internal const double CardCornerRadiusDip = 8.0;
    internal const double ControlCornerRadiusDip = 4.0;
    internal const double MinCardHeightDip = 48.0;
    internal const double MinCardWidthDip = 160.0;
    internal const double MaxCardWidthDip = 600.0;
    internal const double BottomMarginDip = 24.0;
    internal const double MessageFontSizeDip = 14.0;
    internal const double MessageLineHeightDip = 20.0;
    /// <summary>
    /// Three lines fit every shipped locale's longest HUD message inside the
    /// 600 DIP pill (see DragHudMessageLengths budget test); 3×20 + 2×10
    /// padding (+2 for the high-contrast stroke) stays under the 96 DIP
    /// minimum card height, so no height change is needed.
    /// </summary>
    internal const int MessageMaxLines = 3;
    internal const double StatusIconFontSizeDip = 16.0;
    internal const double ActionButtonFontSizeDip = 13.0;
    internal const double DismissIconFontSizeDip = 10.0;
    internal const double HorizontalPaddingDip = 16.0;
    internal const double TrailingDismissPaddingDip = 10.0;
    internal const double VerticalPaddingDip = 10.0;
    internal const double IconToMessageSpacingDip = 12.0;
    internal const double MessageToActionSpacingDip = 12.0;
    internal const double ActionToDismissSpacingDip = 6.0;
    internal const double MessageToDismissSpacingDip = 8.0;
    internal const double ShowHideSlideDistanceDip = 16.0;
    internal const int ShowAnimationDurationMs = 180;
    internal const int HideAnimationDurationMs = 150;
    internal const double FixedAcrylicSurfaceOpacity = 0.85;
    internal const double FixedAcrylicMaterialIntensity = 0.5;

    private const int InteractionPollIntervalMs = 40;

    private static readonly FontFamily s_hudFontFamily =
        new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly UIntPtr SubclassId = new(0xDDB9);
    private static readonly Win32Helper.SubclassProc s_subclassProc =
        CallbackDepth.WrapSubclassProc(WindowSubclassProc);

    private readonly AppWindow _appWindow;
    private readonly bool _subclassInstalled;
    private readonly DispatcherQueueTimer _autoHideTimer;
    private readonly DispatcherQueueTimer _interactionStateTimer;
    private readonly DispatcherQueueTimer _exitAnimationTimer;
    private readonly Grid _hostRoot;
    private readonly Border _pillBorder;
    private readonly SystemBackdropElement _backdropElement;
    private readonly ElementBackdropTargetBridge? _backdropBridge;
    private readonly SolidTintSystemBackdrop _transparentWindowBackdrop;
    private readonly FontIcon _statusIcon;
    private readonly TextBlock _messageText;
    private readonly Button _actionButton;
    private readonly Button _dismissButton;
    private readonly FontIcon _dismissIcon;

    private readonly SolidColorBrush _iconBrush = new(Colors.White);
    private readonly SolidColorBrush _bgBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _borderBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _textBrush = new(Colors.White);
    private readonly SolidColorBrush _actionBgBrush = new(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _actionBorderBrush = new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _dismissBgBrush = new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _dismissIconBrush = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private Func<Task>? _currentActionCallback;
    private Func<Task>? _currentDismissCallback;
    private long _pendingHideSession = -1;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private DesktopAcrylicController? _acrylicController;
    private bool? _lastAcrylicIsDark;
    private bool _acrylicAttachLogged;
    private bool _acrylicAttachFailed;
    private bool _surfacePaintedAcrylic;
    private bool _currentLightTheme;
    private bool _currentHighContrast;
    private bool _hasInteractiveButtons;
    private bool _isClickThroughApplied;
    private bool _wasMouseDownLastTick;
    private bool _pointerGestureStartedInsideHud;
    private bool _autoHidePausedForDrag;
    private ToastLifetimeSpec _currentSpec;
    private bool _isDismissHovered;
    private bool _isActionHovered;
    private bool _isPointerInside;
    private bool _isShown;
    private bool _closed;
    private long _sessionId;
    private MonitorAndBackdropInterop.NativeRect _lastToastWorkArea;
    private double _lastToastScale;

    internal readonly record struct HudPaletteSnapshot(
        Color FallbackBackground,
        Color Border,
        Color PrimaryText,
        Color StatusIcon,
        Color SecondaryGlyph);

    /// <summary>
    /// Pill stroke thickness: zero on regular themes (product call 2026-11-12),
    /// 1 DIP in high contrast only — with acrylic and the DWM shadow both off,
    /// a high-contrast pill over a same-colored wallpaper would otherwise lose
    /// its outline entirely (WCAG 1.4.11 component-boundary contrast).
    /// </summary>
    internal static double ResolveCardBorderThicknessDip(bool highContrast) =>
        highContrast ? 1.0 : 0.0;

    internal enum HudSurfaceKind
    {
        Acrylic = 0,
        Solid = 1,
    }

    /// <summary>
    /// Resolves the pill surface for a show: the fixed frosted-acrylic material
    /// whenever the system backdrop is available, with opaque solid fallback for
    /// machines without desktop acrylic support and for high contrast sessions.
    /// </summary>
    internal static HudSurfaceKind ResolveHudSurfaceKind(
        bool acrylicSupported,
        bool highContrast) =>
        acrylicSupported && !highContrast ? HudSurfaceKind.Acrylic : HudSurfaceKind.Solid;

    public DesktopToastHudWindow()
    {
        Title = "DeskBox Drag HUD";
        WindowHandle = WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);

        // Borderless, shadowless, stroke-less frame for every OS: the frosted
        // pill shape is clipped by the backdrop element's rounded mask, so no
        // DWM frame, border, or corner preference is wanted at all.
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);
        DesktopToastHudInterop.ApplyNoActivateHudWindowStyles(WindowHandle);

        _subclassInstalled = Win32Helper.SetWindowSubclass(
            WindowHandle,
            s_subclassProc,
            SubclassId,
            UIntPtr.Zero);

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.IsRepeating = false;
        _autoHideTimer.Tick += AutoHideTimer_Tick;

        _interactionStateTimer = DispatcherQueue.CreateTimer();
        _interactionStateTimer.Interval = TimeSpan.FromMilliseconds(InteractionPollIntervalMs);
        _interactionStateTimer.IsRepeating = true;
        _interactionStateTimer.Tick += InteractionStateTimer_Tick;

        _exitAnimationTimer = DispatcherQueue.CreateTimer();
        _exitAnimationTimer.IsRepeating = false;
        _exitAnimationTimer.Tick += ExitAnimationTimer_Tick;

        _statusIcon = new FontIcon
        {
            FontSize = StatusIconFontSizeDip,
            Width = StatusIconFontSizeDip,
            Height = StatusIconFontSizeDip,
            Margin = new Thickness(0, 0, IconToMessageSpacingDip, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Glyph = "\uE946",
        };

        _messageText = new TextBlock
        {
            FontFamily = s_hudFontFamily,
            FontSize = MessageFontSizeDip,
            FontWeight = FontWeights.Normal,
            LineHeight = MessageLineHeightDip,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxLines = MessageMaxLines,
            MaxWidth = 440,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Accessibility: the HUD is the sole feedback channel for drag results
        // and never takes focus (WS_EX_NOACTIVATE), so screen readers need the
        // live region plus explicit RaiseNotificationEvent announcements — a
        // LiveSetting-only region never fires when the text is written before
        // the surface becomes visible (WidgetFeedbackPresenter precedent).
        AutomationProperties.SetLiveSetting(_messageText, AutomationLiveSetting.Polite);

        _actionButton = new Button
        {
            FontFamily = s_hudFontFamily,
            FontSize = ActionButtonFontSizeDip,
            FontWeight = FontWeights.Normal,
            CornerRadius = new CornerRadius(ControlCornerRadiusDip),
            Padding = new Thickness(10, 3, 10, 4),
            MinHeight = 28,
            MinWidth = 0,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(MessageToActionSpacingDip, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _actionButton.Click += ActionButton_Click;
        _actionButton.PointerEntered += ActionButton_PointerEntered;
        _actionButton.PointerExited += ActionButton_PointerExited;

        _dismissIcon = new FontIcon
        {
            Glyph = "\uE711",
            FontSize = DismissIconFontSizeDip,
            Foreground = _dismissIconBrush,
        };

        _dismissButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            MinWidth = 28,
            MinHeight = 28,
            CornerRadius = new CornerRadius(ControlCornerRadiusDip),
            Background = _dismissBgBrush,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(MessageToDismissSpacingDip, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = _dismissIcon,
        };
        AutomationProperties.SetName(
            _dismissButton,
            (App.Current as App)?.LocalizationService?.T("Common.Close")
                ?? LocalizationService.DefaultText("Common.Close"));
        _dismissButton.Click += DismissButton_Click;
        _dismissButton.PointerEntered += DismissButton_PointerEntered;
        _dismissButton.PointerExited += DismissButton_PointerExited;

        var layoutGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_statusIcon, 0);
        Grid.SetColumn(_messageText, 1);
        Grid.SetColumn(_actionButton, 2);
        Grid.SetColumn(_dismissButton, 3);
        layoutGrid.Children.Add(_statusIcon);
        layoutGrid.Children.Add(_messageText);
        layoutGrid.Children.Add(_actionButton);
        layoutGrid.Children.Add(_dismissButton);

        _pillBorder = new Border
        {
            CornerRadius = new CornerRadius(CardCornerRadiusDip),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(
                HorizontalPaddingDip,
                VerticalPaddingDip,
                TrailingDismissPaddingDip,
                VerticalPaddingDip),
            MinWidth = MinCardWidthDip,
            MinHeight = MinCardHeightDip,
            MaxWidth = MaxCardWidthDip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = layoutGrid,
        };
        _pillBorder.PointerEntered += PillBorder_PointerEntered;
        _pillBorder.PointerExited += PillBorder_PointerExited;

        _backdropElement = new SystemBackdropElement
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        if (WindowsCompatibilityService.SupportsDesktopAcrylic)
        {
            // Fixed frosted-glass surface (never follows widget material
            // settings). The bridge rounds the element's backdrop target and
            // exposes it for the window-owned controller below.
            _backdropBridge = new ElementBackdropTargetBridge(
                onTargetChanged: OnBackdropTargetChanged,
                onTargetDisconnecting: OnBackdropTargetDisconnecting);
            _backdropBridge.SetCornerRadius(CardCornerRadiusDip);
            _backdropElement.SystemBackdrop = _backdropBridge;
        }

        _hostRoot = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0),
        };
        _hostRoot.Children.Add(_backdropElement);
        _hostRoot.Children.Add(_pillBorder);
        Content = _hostRoot;

        // Per-pixel-alpha transparent window surface so only the rounded pill
        // region is visible; the acrylic lives on the backdrop element inside.
        _transparentWindowBackdrop = new SolidTintSystemBackdrop(Color.FromArgb(0, 0, 0, 0));
        SystemBackdrop = _transparentWindowBackdrop;
    }

    /// <summary>
    /// Formats the per-show diagnostic log line. Deliberately excludes the
    /// message content: HUD messages can embed user note snippets (QuickCapture)
    /// or full file-system paths (drag-out target fallback), and App.Log always
    /// reaches disk in production. HintId plus geometry rebuilds anything the
    /// line is used to diagnose (first-show measure collapse).
    /// </summary>
    internal static string FormatShowLogLine(
        string hintId,
        int messageLength,
        double desiredWidth,
        double desiredHeight,
        double textWidth,
        double textHeight) =>
        $"[DragHud] Show '{hintId}' msgLen={messageLength} " +
        $"desired={desiredWidth:0.#}x{desiredHeight:0.#} " +
        $"text={textWidth:0.#}x{textHeight:0.#}";

    internal readonly record struct FixedAcrylicRecipe(
        Windows.UI.Color TintColor,
        double TintOpacity,
        double LuminosityOpacity);

    /// <summary>
    /// Pure calculator for the fixed frosted-glass recipe (Thin acrylic at the
    /// stack popover's fixed-mode strength), so tests can pin the material.
    /// </summary>
    internal static FixedAcrylicRecipe BuildFixedAcrylicRecipe(bool isDark)
    {
        Windows.UI.Color tint = WidgetMaterialVisualCalculator.BuildContentTintColor(
            isDark,
            AccentColorHelper.DefaultAccentColor);
        WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark,
            useBase: false,
            FixedAcrylicSurfaceOpacity,
            FixedAcrylicMaterialIntensity);
        return new FixedAcrylicRecipe(
            tint,
            profile.TintOpacity,
            profile.LuminosityOpacity);
    }

    internal IntPtr WindowHandle { get; }

    internal bool IsShown => !_closed && _isShown;

    internal bool IsPointerHovered => !_closed && _isShown && _isPointerInside;

    internal static HudPaletteSnapshot ResolveFluentPalette(
        DesktopToastSeverity severity,
        bool light,
        bool highContrast)
    {
        if (highContrast)
        {
            Color hcBg = WindowsCompatibilityService.TryGetUiColor(
                    Windows.UI.ViewManagement.UIColorType.Background)
                ?? Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            Color hcFg = WindowsCompatibilityService.TryGetUiColor(
                    Windows.UI.ViewManagement.UIColorType.Foreground)
                ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            Color hcCaution = severity == DesktopToastSeverity.Warning
                ? Color.FromArgb(0xFF, 0xFF, 0xEB, 0x3B)
                : hcFg;
            return new HudPaletteSnapshot(
                FallbackBackground: hcBg,
                Border: hcFg,
                PrimaryText: hcFg,
                StatusIcon: hcCaution,
                SecondaryGlyph: hcFg);
        }

        // Pure neutral Fluent 2 Flyout palette (matching Windows 11 Volume HUD):
        // #2C2C2C dark / #F9F9F9 light base, crisp monochrome primary glyphs for
        // Info & Success, and Fluent SystemFillColorCaution only for safety warnings.
        Color bg = light
            ? Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9)
            : Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
        Color primaryText = light
            ? Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color statusIcon = severity == DesktopToastSeverity.Warning
            ? (light
                ? Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00)
                : Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00))
            : primaryText;
        Color secondaryGlyph = light
            ? Color.FromArgb(0x8F, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);

        return new HudPaletteSnapshot(bg, Colors.Transparent, primaryText, statusIcon, secondaryGlyph);
    }

    internal void ShowToast(
        DesktopToastRequest request,
        bool preferLightTheme,
        ToastLifetimeSpec spec)
    {
        if (_closed)
        {
            return;
        }

        bool wasShown = _isShown;
        ++_sessionId;
        _currentSpec = spec;
        _isPointerInside = false;
        _isActionHovered = false;
        _isDismissHovered = false;
        _autoHideTimer.Stop();
        _exitAnimationTimer.Stop();

        _currentActionCallback = request.OnActionAsync;
        _currentDismissCallback = request.OnDismissAsync;

        bool hasAction = !string.IsNullOrWhiteSpace(request.ActionButtonText) &&
            request.OnActionAsync is not null;
        if (hasAction)
        {
            _actionButton.Content = request.ActionButtonText;
            _actionButton.Visibility = Visibility.Visible;
        }
        else
        {
            _actionButton.Content = null;
            _actionButton.Visibility = Visibility.Collapsed;
        }

        bool hasDismiss = request.ShowDismissButton;
        _hasInteractiveButtons = hasAction || hasDismiss;
        _dismissButton.Visibility = hasDismiss
            ? Visibility.Visible
            : Visibility.Collapsed;
        _dismissButton.Margin = new Thickness(
            hasAction ? ActionToDismissSpacingDip : MessageToDismissSpacingDip,
            0,
            0,
            0);

        double trailingPaddingDip = hasDismiss
            ? TrailingDismissPaddingDip
            : HorizontalPaddingDip;
        _pillBorder.Padding = new Thickness(
            HorizontalPaddingDip,
            VerticalPaddingDip,
            trailingPaddingDip,
            VerticalPaddingDip);

        ApplyPalette(request.Severity, preferLightTheme);
        _messageText.Text = request.Message;
        AutomationProperties.SetName(_messageText, request.Message);
        if (hasDismiss)
        {
            AutomationProperties.SetName(
                _dismissButton,
                (App.Current as App)?.LocalizationService?.T("Common.Close")
                    ?? LocalizationService.DefaultText("Common.Close"));
        }

        DesktopToastHudInterop.GetCursorMonitorWorkArea(
            out MonitorAndBackdropInterop.NativeRect workArea,
            out double scale);

        bool monitorOrScaleChanged =
            !_isShown ||
            Math.Abs(_lastToastScale - scale) > 0.005 ||
            _lastToastWorkArea.Left != workArea.Left ||
            _lastToastWorkArea.Top != workArea.Top ||
            _lastToastWorkArea.Right != workArea.Right ||
            _lastToastWorkArea.Bottom != workArea.Bottom;
        if (monitorOrScaleChanged)
        {
            DesktopToastHudInterop.SetCloaked(WindowHandle, cloaked: true);
            // Seed the cloaked window onto the target monitor before measuring so
            // XamlRoot adopts that monitor's DPI / rasterization scale first,
            // preventing a 1-frame size jump when transitioning across monitors
            // while an earlier drag-start tip is still visible.
            _appWindow.MoveAndResize(new RectInt32(
                workArea.Left + 16,
                workArea.Top + 16,
                220,
                (int)Math.Ceiling(MinCardHeightDip * scale)));
        }

        _lastToastWorkArea = workArea;
        _lastToastScale = scale;

        double hostPaddingDip = 0.0;
        double workWidthDip = Math.Max(320.0, (workArea.Right - workArea.Left) / scale);
        double workHeightDip = Math.Max(240.0, (workArea.Bottom - workArea.Top) / scale);
        double maxCardHeightDip = Math.Clamp(workHeightDip * 0.35, 96.0, 200.0);
        double effectiveMaxCardWidthDip = Math.Clamp(MaxCardWidthDip, MinCardWidthDip, workWidthDip - 48.0);

        // Show the (still cloaked) window and force a layout pass BEFORE
        // measuring: a freshly created window's XAML tree has never loaded,
        // and WinUI skips manual Measure() on unloaded elements — measuring
        // earlier yields DesiredSize 0x0, collapsing the first toast of a
        // session to MinCardWidth and squeezing the message to one glyph.
        // The window remains cloaked, so the seed-sized frame never flashes.
        if (!_appWindow.IsVisible)
        {
            _ = Win32Helper.ShowWindow(WindowHandle, DesktopToastHudInterop.SwShowNoActivate);
        }

        _hostRoot.UpdateLayout();
        // The backdrop element's target can connect only once the window's XAML
        // tree is live (first show); re-run the attach now that layout ran.
        if (_lastAcrylicIsDark is { } acrylicDark)
        {
            EnsureAcrylicController(acrylicDark);
            if (ShouldRepaintAfterAttachAttempt(_acrylicAttachFailed, _surfacePaintedAcrylic))
            {
                // The post-layout attach attempt failed after ApplyPalette had
                // already painted transparent; repaint solid before the toast
                // becomes visible (this runs before uncloak, so no flash frame
                // escapes — the latched call lands straight in the solid branch).
                ApplyPalette(request.Severity, preferLightTheme);
            }
        }

        // Measure intrinsic content width first so short messages hug cleanly
        // instead of stretching to MaxWidth.
        _pillBorder.HorizontalAlignment = HorizontalAlignment.Left;
        _pillBorder.VerticalAlignment = VerticalAlignment.Top;
        _pillBorder.MaxWidth = effectiveMaxCardWidthDip;

        double nonTextChromeDip =
            HorizontalPaddingDip +
            trailingPaddingDip +
            StatusIconFontSizeDip +
            IconToMessageSpacingDip +
            (hasDismiss ? (28.0 + (hasAction ? ActionToDismissSpacingDip : MessageToDismissSpacingDip)) : 0.0);
        if (hasAction)
        {
            _actionButton.Measure(new Size(Math.Max(96.0, effectiveMaxCardWidthDip * 0.45), maxCardHeightDip));
            nonTextChromeDip += Math.Ceiling(_actionButton.DesiredSize.Width) + MessageToActionSpacingDip;
        }

        _messageText.MaxWidth = Math.Max(140.0, effectiveMaxCardWidthDip - nonTextChromeDip - 4.0);
        _pillBorder.Measure(new Size(effectiveMaxCardWidthDip, maxCardHeightDip));

        double cardWidthDip = Math.Clamp(
            Math.Ceiling(_pillBorder.DesiredSize.Width) + 2.0,
            MinCardWidthDip,
            effectiveMaxCardWidthDip);
        double cardHeightDip = Math.Clamp(
            Math.Ceiling(_pillBorder.DesiredSize.Height),
            MinCardHeightDip,
            maxCardHeightDip);

        _pillBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pillBorder.VerticalAlignment = VerticalAlignment.Stretch;

        double totalWidthDip = cardWidthDip + hostPaddingDip;
        double totalHeightDip = cardHeightDip + hostPaddingDip;

        int widthPx = Math.Max(120, (int)Math.Ceiling(totalWidthDip * scale));
        int heightPx = Math.Max(
            (int)Math.Round(MinCardHeightDip * scale),
            (int)Math.Ceiling(totalHeightDip * scale));
        int bottomOffsetPx = (int)Math.Round((BottomMarginDip - (hostPaddingDip / 2.0)) * scale);

        int x = workArea.Left + Math.Max(8, ((workArea.Right - workArea.Left) - widthPx) / 2);
        int y = Math.Max(workArea.Top + 8, workArea.Bottom - heightPx - Math.Max(8, bottomOffsetPx));

        DesktopToastHudInterop.EnsureNoActivateExtendedStyle(WindowHandle);
        var targetRect = new RectInt32(x, y, widthPx, heightPx);
        _appWindow.MoveAndResize(targetRect);
        if (_appWindow.Size.Width != widthPx ||
            _appWindow.Size.Height != heightPx ||
            _appWindow.Position.X != x ||
            _appWindow.Position.Y != y)
        {
            _appWindow.MoveAndResize(targetRect);
        }

        App.Log(FormatShowLogLine(
            request.HintId,
            request.Message.Length,
            _pillBorder.DesiredSize.Width,
            _pillBorder.DesiredSize.Height,
            _messageText.ActualWidth,
            _messageText.ActualHeight));

        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE);
        DesktopToastHudService.Shared.SetActiveHudWindowHandle(WindowHandle);
        _wasMouseDownLastTick = false;
        _pointerGestureStartedInsideHud = false;
        _autoHidePausedForDrag = false;
        SyncClickThroughAndTimerState(initialShow: true);
        if (!_interactionStateTimer.IsRunning)
        {
            _interactionStateTimer.Start();
        }

        ResetHostVisualMotion();
        DesktopToastHudInterop.SetCloaked(WindowHandle, cloaked: false);
        _isShown = true;

        if (!wasShown)
        {
            PlayEntranceAnimation();
        }

        AnnounceToAutomation(request.Message, request.Severity);
    }

    /// <summary>
    /// Severity to UIA notification mapping: warnings interrupt (Important-
    /// MostRecent), completions announce as activity completed, everything
    /// else queues politely.
    /// </summary>
    internal static (AutomationNotificationKind Kind, AutomationNotificationProcessing Processing)
        ResolveAutomationNotification(DesktopToastSeverity severity) =>
        severity switch
        {
            DesktopToastSeverity.Warning => (
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.ImportantMostRecent),
            DesktopToastSeverity.Success => (
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent),
            _ => (
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.MostRecent),
        };

    /// <summary>
    /// Screen-reader announcement for the toast. The window never gains focus
    /// (WS_EX_NOACTIVATE by design — it must not steal focus during OLE
    /// drags), so Narrator will never read it via focus changes; the
    /// notification event is the only reliable channel (peer fallback pattern
    /// copied from WidgetFeedbackPresenter).
    /// </summary>
    private void AnnounceToAutomation(string message, DesktopToastSeverity severity)
    {
        try
        {
            AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(_messageText) ??
                FrameworkElementAutomationPeer.CreatePeerForElement(_messageText);
            if (peer is null)
            {
                return;
            }

            (AutomationNotificationKind kind, AutomationNotificationProcessing processing) =
                ResolveAutomationNotification(severity);
            peer.RaiseNotificationEvent(
                kind,
                processing,
                message,
                "DeskBox.DragHud");
        }
        catch
        {
        }
    }

    internal void HideImmediate()
    {
        if (_closed)
        {
            return;
        }

        _autoHideTimer.Stop();
        _interactionStateTimer.Stop();
        _isPointerInside = false;
        _isActionHovered = false;
        _isDismissHovered = false;
        _wasMouseDownLastTick = false;
        _pointerGestureStartedInsideHud = false;
        _autoHidePausedForDrag = false;
        _currentActionCallback = null;
        _currentDismissCallback = null;
        DesktopToastHudService.Shared.SetActiveHudWindowHandle(IntPtr.Zero);

        if (!_isShown)
        {
            // A redundant hide (e.g. a direct service Hide() racing the exit
            // slide's ~166ms finalize window) must not cancel the in-flight
            // FinalizeHide: bumping the session here would orphan the pending
            // tick and leave an uncloaked ghost window until the next show.
            return;
        }

        ++_sessionId;
        _isShown = false;

        // The pill stays visible for the exit slide, so stop it from swallowing
        // clicks meant for whatever is behind it during those final frames.
        _isClickThroughApplied = true;
        _hostRoot.IsHitTestVisible = false;
        DesktopToastHudInterop.SetWindowTreeMousePassthrough(WindowHandle, passthrough: true);

        PlayExitAnimation(_sessionId);
    }

    internal void Destroy()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _isShown = false;
        _autoHideTimer.Stop();
        _interactionStateTimer.Stop();
        _exitAnimationTimer.Stop();
        _currentActionCallback = null;
        _currentDismissCallback = null;
        DesktopToastHudService.Shared.SetActiveHudWindowHandle(IntPtr.Zero);
        DisposeAcrylicController();
        _backdropBridge?.Dispose();
        _transparentWindowBackdrop.Dispose();

        if (_subclassInstalled)
        {
            _ = Win32Helper.RemoveWindowSubclass(
                WindowHandle,
                s_subclassProc,
                SubclassId);
        }

        try
        {
            Close();
        }
        catch
        {
        }
    }

    private void ApplyPalette(DesktopToastSeverity severity, bool light)
    {
        bool highContrast = WindowsCompatibilityService.IsHighContrast;
        _currentLightTheme = light;
        _currentHighContrast = highContrast;

        Win32Helper.SetWindowTheme(WindowHandle, isDark: !light && !highContrast);
        _hostRoot.RequestedTheme = highContrast
            ? ElementTheme.Default
            : (light ? ElementTheme.Light : ElementTheme.Dark);

        HudPaletteSnapshot palette = ResolveFluentPalette(severity, light, highContrast);

        _hostRoot.Padding = new Thickness(0);
        _pillBorder.CornerRadius = new CornerRadius(CardCornerRadiusDip);
        _pillBorder.BorderThickness = new Thickness(ResolveCardBorderThicknessDip(highContrast));
        _borderBrush.Color = palette.Border;
        _pillBorder.BorderBrush = _borderBrush;
        ApplySurface(palette);

        _statusIcon.Glyph = severity switch
        {
            DesktopToastSeverity.Warning => "\uE7BA",
            DesktopToastSeverity.Success => "\uE73E",
            _ => "\uE946",
        };

        _iconBrush.Color = palette.StatusIcon;
        _textBrush.Color = palette.PrimaryText;

        _statusIcon.Foreground = _iconBrush;
        _pillBorder.Background = _bgBrush;
        _messageText.Foreground = _textBrush;

        _actionButton.Foreground = _textBrush;
        _actionButton.Background = _actionBgBrush;
        _actionButton.BorderBrush = _actionBorderBrush;
        UpdateInteractiveControlBrushes();
    }

    /// <summary>
    /// Idempotent surface painter: fixed frosted acrylic when supported,
    /// attached, and not latched off; opaque solid otherwise. An attach failure
    /// discovered inside <see cref="EnsureAcrylicController"/> trips the latch
    /// and the follow-up guard repaints solid within the same call, so the
    /// "acrylic selected + attach failed + transparent pill" state (white text
    /// floating on the desktop) is unreachable on every path.
    /// </summary>
    private void ApplySurface(HudPaletteSnapshot palette)
    {
        bool wantAcrylic = ResolveHudSurfaceKind(
            _backdropBridge is not null && !_acrylicAttachFailed,
            _currentHighContrast) == HudSurfaceKind.Acrylic;

        if (wantAcrylic)
        {
            // Fixed frosted-glass surface with no stroke: the transparent pill
            // lets the rounded masked acrylic on the backdrop element show
            // through as the entire HUD background.
            _bgBrush.Color = Colors.Transparent;
            _backdropElement.Visibility = Visibility.Visible;
            EnsureAcrylicController(isDark: !_currentLightTheme);
            if (!_acrylicAttachFailed)
            {
                _surfacePaintedAcrylic = true;
                return;
            }
        }

        // Opaque solid: acrylic unsupported, high contrast, or the attach
        // attempt just failed inside this call.
        _surfacePaintedAcrylic = false;
        _bgBrush.Color = palette.FallbackBackground;
        _backdropElement.Visibility = Visibility.Collapsed;
        DisposeAcrylicController();
    }

    /// <summary>
    /// Attaches or refreshes the fixed acrylic controller on the backdrop
    /// element's target. The configuration deliberately forces
    /// <c>IsInputActive = true</c>: this flyout is <c>WS_EX_NOACTIVATE</c> and
    /// never gains activation, so the default activation-tracked configuration
    /// would keep the controller in its solid fallback color forever (the same
    /// reason <c>SearchPopupWindow</c> and <c>ThemedMenuAcrylicBackdrop</c> pin
    /// the flag on their own no-activate surfaces). A null target (pre-first-
    /// show Pending) never trips the failure latch — the bridge reconnect
    /// callback retries it; only a rejected registration or a thrown exception
    /// latches, and a genuine bridge reconnect (live target) clears it.
    /// </summary>
    private void EnsureAcrylicController(bool isDark)
    {
        _lastAcrylicIsDark = isDark;
        if (_backdropBridge is null || _closed)
        {
            return;
        }

        if (_acrylicAttachFailed)
        {
            // Sticky latch: attach failure is deterministic per target; retrying
            // every show would re-flash a transparent pill before each failure.
            return;
        }

        if (_backdropBridge.Target is not { } target)
        {
            // Element target not connected yet (pre-first-show); the bridge's
            // onTargetChanged callback re-invokes this once it connects.
            return;
        }

        try
        {
            _backdropConfiguration ??= new SystemBackdropConfiguration
            {
                IsInputActive = true,
            };
            _backdropConfiguration.IsInputActive = true;
            _backdropConfiguration.Theme = isDark
                ? SystemBackdropTheme.Dark
                : SystemBackdropTheme.Light;

            if (_acrylicController is null || _acrylicController.IsClosed)
            {
                DisposeAcrylicController();
                var controller = new DesktopAcrylicController();
                controller.SetSystemBackdropConfiguration(_backdropConfiguration);
                if (!controller.AddSystemBackdropTarget(target))
                {
                    controller.Dispose();
                    _acrylicAttachFailed = true;
                    App.Log("[DragHud] Fixed acrylic target registration failed; falling back to solid");
                    return;
                }

                _acrylicController = controller;
            }
            else
            {
                _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
            }

            FixedAcrylicRecipe recipe = BuildFixedAcrylicRecipe(isDark);
            _acrylicController.Kind = DesktopAcrylicKind.Thin;
            _acrylicController.TintColor = recipe.TintColor;
            _acrylicController.FallbackColor = recipe.TintColor;
            _acrylicController.TintOpacity = (float)recipe.TintOpacity;
            _acrylicController.LuminosityOpacity = (float)recipe.LuminosityOpacity;
            if (!_acrylicAttachLogged)
            {
                _acrylicAttachLogged = true;
                App.Log($"[DragHud] Fixed acrylic attached (kind=Thin, tintOpacity={recipe.TintOpacity:0.###}, luminosity={recipe.LuminosityOpacity:0.###})");
            }
        }
        catch (Exception ex)
        {
            _acrylicAttachFailed = true;
            App.Log($"[DragHud] Fixed acrylic unavailable, falling back to solid: {ex.Message}");
            DisposeAcrylicController();
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
        }
    }

    /// <summary>
    /// Whether a surface painted as acrylic needs an immediate solid repaint
    /// because the attach attempt that just ran failed.
    /// </summary>
    internal static bool ShouldRepaintAfterAttachAttempt(bool attachFailedNow, bool paintedAcrylic) =>
        attachFailedNow && paintedAcrylic;

    private void OnBackdropTargetChanged()
    {
        if (_closed)
        {
            return;
        }

        if (_backdropBridge?.Target is not null)
        {
            // A live target means a (re)connect: the reconnect built a fresh
            // masked proxy, which is a new attach universe — clear the failure
            // latch and allow exactly one retry. Disconnects raise this same
            // callback with a null Target and must never clear the latch.
            _acrylicAttachFailed = false;
            _acrylicAttachLogged = false;
        }

        if (_lastAcrylicIsDark is { } isDark)
        {
            EnsureAcrylicController(isDark);
        }

        // Surface repaint (solid→acrylic upgrade after a successful reconnect)
        // is deferred to the next ShowToast: flipping a visible toast mid-
        // display would swap its background under the reader.
    }

    private void OnBackdropTargetDisconnecting(ICompositionSupportsSystemBackdrop target)
    {
        // The masked proxy the controller was added to is going away.
        DisposeAcrylicController();
    }

    private void UpdateInteractiveControlBrushes()
    {
        if (_currentHighContrast)
        {
            Color hcFg = _textBrush.Color;
            _actionBgBrush.Color = Colors.Transparent;
            _actionBorderBrush.Color = hcFg;
            _dismissBgBrush.Color = Colors.Transparent;
            _dismissIconBrush.Color = hcFg;
            return;
        }

        if (_currentLightTheme)
        {
            _actionBgBrush.Color = _isActionHovered
                ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
            _actionBorderBrush.Color = Color.FromArgb(0x14, 0x00, 0x00, 0x00);

            _dismissBgBrush.Color = _isDismissHovered
                ? Color.FromArgb(0x0D, 0x00, 0x00, 0x00)
                : Colors.Transparent;
            _dismissIconBrush.Color = _isDismissHovered
                ? Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x8F, 0x00, 0x00, 0x00);
        }
        else
        {
            _actionBgBrush.Color = _isActionHovered
                ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
            _actionBorderBrush.Color = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);

            _dismissBgBrush.Color = _isDismissHovered
                ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)
                : Colors.Transparent;
            _dismissIconBrush.Color = _isDismissHovered
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        }
    }

    /// <summary>
    /// Resets composition motion base values on the host visual. Exit animations
    /// hold their final values (Opacity 0 / Offset +16 DIP) after completing, so
    /// a later show that plays no entrance (toast replaced while shown, or system
    /// animations disabled mid-session) would inherit an invisible, shifted
    /// pill. Every show resets unconditionally, decoupled from whether an
    /// entrance will play (precedent: WidgetShell StopAnimation-then-park
    /// pattern; stop first — setting a base value under a running animation has
    /// no effect until the animation ends).
    /// </summary>
    private void ResetHostVisualMotion()
    {
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(_hostRoot);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
        }
        catch
        {
        }
    }

    /// <summary>
    /// Linear slide-up + fade-in show motion on the whole host (content and
    /// frosted surface travel together from below the resting position).
    /// </summary>
    private void PlayEntranceAnimation()
    {
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(_hostRoot);
            Compositor compositor = visual.Compositor;
            LinearEasingFunction linear = compositor.CreateLinearEasingFunction();

            ScalarKeyFrameAnimation fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.InsertKeyFrame(0.0f, 0.0f, linear);
            fadeAnim.InsertKeyFrame(1.0f, 1.0f, linear);
            fadeAnim.Duration = TimeSpan.FromMilliseconds(ShowAnimationDurationMs);

            Vector3KeyFrameAnimation offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(
                0.0f,
                new Vector3(0.0f, (float)ShowHideSlideDistanceDip, 0.0f),
                linear);
            offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, linear);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(ShowAnimationDurationMs);

            visual.StartAnimation("Opacity", fadeAnim);
            visual.StartAnimation("Offset", offsetAnim);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Linear slide-down + fade-out hide motion; the window is cloaked only
    /// after the slide completes. A newer show/dismiss bumps <see cref="_sessionId"/>,
    /// which cancels the pending finalize.
    /// </summary>
    private void PlayExitAnimation(long session)
    {
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            FinalizeHide(session);
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(_hostRoot);
            Compositor compositor = visual.Compositor;
            LinearEasingFunction linear = compositor.CreateLinearEasingFunction();

            ScalarKeyFrameAnimation fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.InsertKeyFrame(0.0f, 1.0f, linear);
            fadeAnim.InsertKeyFrame(1.0f, 0.0f, linear);
            fadeAnim.Duration = TimeSpan.FromMilliseconds(HideAnimationDurationMs);

            Vector3KeyFrameAnimation offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(0.0f, Vector3.Zero, linear);
            offsetAnim.InsertKeyFrame(
                1.0f,
                new Vector3(0.0f, (float)ShowHideSlideDistanceDip, 0.0f),
                linear);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(HideAnimationDurationMs);

            visual.StartAnimation("Opacity", fadeAnim);
            visual.StartAnimation("Offset", offsetAnim);
        }
        catch
        {
            FinalizeHide(session);
            return;
        }

        _pendingHideSession = session;
        _exitAnimationTimer.Interval = TimeSpan.FromMilliseconds(HideAnimationDurationMs + 16);
        _exitAnimationTimer.Start();
    }

    private void ExitAnimationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!ExitTickShouldFinalize(_sessionId, _pendingHideSession, _closed))
        {
            return;
        }

        FinalizeHide(_pendingHideSession);
    }

    /// <summary>
    /// Exit-tick guard: only the session that armed the exit may finalize it.
    /// After the HideImmediate fix (redundant hides no longer bump the session)
    /// a mismatch here should be unreachable outside ShowToast/Destroy, which
    /// both stop this timer first — reoccurrence signals a new lifecycle bug.
    /// </summary>
    internal static bool ExitTickShouldFinalize(long sessionId, long pendingSession, bool closed) =>
        !closed && sessionId == pendingSession;

    /// <summary>
    /// Finalize guard: stale session (toast replaced), re-shown, or destroyed
    /// windows must not be cloaked by a late finalize.
    /// </summary>
    internal static bool FinalizeHideShouldRun(long sessionId, long session, bool isShown, bool closed) =>
        !closed && sessionId == session && !isShown;

    private void FinalizeHide(long session)
    {
        if (!FinalizeHideShouldRun(_sessionId, session, _isShown, _closed))
        {
            return;
        }

        try
        {
            DesktopToastHudInterop.SetCloaked(WindowHandle, cloaked: true);
            if (_appWindow.IsVisible)
            {
                _appWindow.Hide();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DragHud] FinalizeHide failed: {ex.Message}");
        }
        finally
        {
            // Service metadata must be released even if cloak/hide throws, or
            // priority gating stays blocked until the lease expires. The
            // generation+hint guard inside the service drops stale notifies,
            // so a superseded toast's cleanup can never clear its successor.
            DesktopToastHudService.Shared.NotifyWindowHidden(
                _currentSpec.Generation,
                _currentSpec.HintId);
        }
    }

    private void ActionButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isActionHovered = true;
        UpdateInteractiveControlBrushes();
    }

    private void ActionButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isActionHovered = false;
        UpdateInteractiveControlBrushes();
    }

    private void DismissButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isDismissHovered = true;
        UpdateInteractiveControlBrushes();
    }

    private void DismissButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isDismissHovered = false;
        UpdateInteractiveControlBrushes();
    }

    private void PillBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = true;
        _autoHideTimer.Stop();
    }

    private void PillBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = false;
        if (_isShown && !_closed && !_autoHidePausedForDrag)
        {
            // The service returns the authoritative remount value — max of the
            // remaining budget and the fixed grace — and extends the priority
            // lease to cover the tail, so a reader is never displaced early.
            int remountMs = DesktopToastHudService.Shared.ReportPointerExited(
                _currentSpec.Generation,
                _currentSpec.HintId);
            if (remountMs > 0)
            {
                _autoHideTimer.Interval = TimeSpan.FromMilliseconds(remountMs);
                _autoHideTimer.Start();
            }
        }
    }

    private void InteractionStateTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_closed || !_isShown)
        {
            sender.Stop();
            return;
        }

        SyncClickThroughAndTimerState(initialShow: false);
    }

    private void SyncClickThroughAndTimerState(bool initialShow)
    {
        // Hard cap first (hover stays exempt so an actively hovered pill
        // never vanishes underneath the cursor): even a KeepVisibleDuringDrag
        // toast frozen by a lost drag entry must eventually disappear.
        if (!_isPointerInside && Environment.TickCount64 >= _currentSpec.HardCapDeadlineTick64)
        {
            HideImmediate();
            return;
        }

        bool shouldBeClickThrough = EvaluateShouldBeClickThrough(out bool dragOrExternalMouseDown);
        if (initialShow || shouldBeClickThrough != _isClickThroughApplied)
        {
            _isClickThroughApplied = shouldBeClickThrough;
            _hostRoot.IsHitTestVisible = !shouldBeClickThrough;
            DesktopToastHudInterop.SetWindowTreeMousePassthrough(WindowHandle, shouldBeClickThrough);
        }

        if (dragOrExternalMouseDown)
        {
            // Only KeepVisibleDuringDrag toasts (drag-out modifier tip, safety
            // warnings) freeze during a drag — their message is about the
            // in-progress interaction. Receipts burn their own budget and
            // expire naturally, so a stale undo button can never linger or be
            // revived by full-budget remounts across serial drags.
            if (_currentSpec.KeepVisibleDuringDrag)
            {
                if (!_autoHidePausedForDrag)
                {
                    DesktopToastHudService.Shared.ReportDragPaused(
                        _currentSpec.Generation,
                        _currentSpec.HintId);
                }

                if (_autoHideTimer.IsRunning)
                {
                    _autoHideTimer.Stop();
                }

                _autoHidePausedForDrag = true;
                return;
            }

            // Non-Keep receipt shown mid-drag: arm its budget right away so
            // the timer is never orphaned by this early return.
            if (initialShow && !_isPointerInside)
            {
                _autoHideTimer.Interval = TimeSpan.FromMilliseconds(_currentSpec.BudgetMs);
                _autoHideTimer.Start();
            }

            return;
        }

        if (_autoHidePausedForDrag)
        {
            _autoHidePausedForDrag = false;
            int remountMs = DesktopToastHudService.Shared.ReportDragResumed(
                _currentSpec.Generation,
                _currentSpec.HintId);
            if (!_isPointerInside)
            {
                if (remountMs > 0)
                {
                    _autoHideTimer.Interval = TimeSpan.FromMilliseconds(remountMs);
                    _autoHideTimer.Start();
                }
                else
                {
                    HideImmediate();
                }
            }

            return;
        }

        if (initialShow && !_isPointerInside)
        {
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(_currentSpec.BudgetMs);
            _autoHideTimer.Start();
        }
    }

    private bool EvaluateShouldBeClickThrough(out bool dragOrExternalMouseDown)
    {
        bool mouseDown = DesktopToastHudInterop.IsAnyMouseButtonDown();
        if (!mouseDown)
        {
            _pointerGestureStartedInsideHud = !_isClickThroughApplied && IsCursorInsideHudBounds();
        }
        else if (!_wasMouseDownLastTick && !_isClickThroughApplied && IsCursorInsideHudBounds())
        {
            _pointerGestureStartedInsideHud = true;
        }

        _wasMouseDownLastTick = mouseDown;
        dragOrExternalMouseDown = ActiveDeskBoxDragRegistry.HasActiveDrag ||
            (mouseDown && !_pointerGestureStartedInsideHud);
        return !_hasInteractiveButtons || dragOrExternalMouseDown;
    }

    private bool IsCursorInsideHudBounds()
    {
        if (WindowHandle == IntPtr.Zero ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT pt) ||
            !Win32Helper.GetWindowRect(WindowHandle, out Win32Helper.RECT rect))
        {
            return false;
        }

        return pt.X >= rect.Left &&
               pt.X < rect.Right &&
               pt.Y >= rect.Top &&
               pt.Y < rect.Bottom;
    }

    private void AutoHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerInside || _autoHidePausedForDrag || _closed || !_isShown)
        {
            return;
        }

        long session = _sessionId;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!_closed && _isShown && session == _sessionId && !_isPointerInside && !_autoHidePausedForDrag)
            {
                HideImmediate();
            }
        });
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        long session = _sessionId;
        Func<Task>? callback = _currentActionCallback;
        if (WindowHandle != IntPtr.Zero)
        {
            // Claim foreground while processing the physical button click so the
            // main process holds foreground rights to grant AllowSetForegroundWindow
            // to the out-of-process settings host before hiding the HUD window.
            _ = Win32Helper.SetForegroundWindow(WindowHandle);
            _ = Win32Helper.AllowSetForegroundWindow(uint.MaxValue);
        }

        if (callback is not null)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Action button handler failed: {ex.Message}");
            }
        }

        UiDispatch.PostAfterCallback(() =>
        {
            if (!_closed && _sessionId == session)
            {
                HideImmediate();
            }
        });
    }

    private async void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        long session = _sessionId;
        Func<Task>? callback = _currentDismissCallback;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!_closed && _sessionId == session)
            {
                HideImmediate();
            }
        });
        if (callback is not null)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Dismiss button handler failed: {ex.Message}");
            }
        }
    }

    private static IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (uMsg == DesktopToastHudInterop.WmMouseActivate)
        {
            return new IntPtr(DesktopToastHudInterop.MaNoActivate);
        }

        if (uMsg == DesktopToastHudInterop.WmGetMinMaxInfo)
        {
            DesktopToastHudInterop.RelaxMinTrackSize(lParam);
            return IntPtr.Zero;
        }

        if (uMsg == DesktopToastHudInterop.WmNcDestroy)
        {
            _ = Win32Helper.RemoveWindowSubclass(hWnd, s_subclassProc, uIdSubclass);
        }

        return Win32Helper.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
