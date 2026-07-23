using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow : Window
{
    private const int DesiredWindowWidth = 1040;
    private const int DesiredWindowHeight = 740;
    private const int MinWindowWidth = 660;
    private const int MinWindowHeight = 540;
    private const int WindowWorkAreaMargin = 96;
    private const int CompactLayoutThreshold = 880;
    private static readonly UIntPtr OnboardingWindowSubclassId = new(0xD05C0B01);

    private sealed record IntroMarkTarget(double TranslateX, double TranslateY, double Scale);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;

    private Storyboard? _introStoryboard;
    private Storyboard? _brandLogoShineStoryboard;
    private Storyboard? _stepTransitionStoryboard;
    private Storyboard? _keycapPulseStoryboard;
    private System.Threading.CancellationTokenSource? _hotkeyDemoCts;
    private int _introGeneration;
    private int _stepIndex;
    private bool _hasLoaded;
    private bool _isSubclassInstalled;
    private bool _isAnimating;
    private bool _isRecordingHotkey;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;

    // Accent color preset list
    private static readonly string[] PresetAccentColors = { "#0078D4", "#E81123", "#107C10", "#5D2E9B", "#FF8C00", "#0099BC" };

    public OnboardingWindow(SettingsService settingsService, LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _windowSubclassProc = WindowSubclassProc;
        InitializeComponent();
        _localizationService.LanguageChanged += OnLanguageChanged;

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarHost);

        Title = localizationService.T("Onboarding.WindowTitle");
        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        ResizeAndCenterForDisplay(windowId);
        InstallMinimumSizeHook();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = false;
        }

        SizeChanged += (_, _) => ApplyResponsiveLayout();
        RootGrid.KeyDown += (_, e) => OnHotkeyKeyDown(e.Key);
        RootGrid.Loaded += (_, _) =>
        {
            _hasLoaded = true;
            ApplyResponsiveLayout();
            ApplyTitleBarButtonColors();
            BuildProgressDots();
            SetupStep(animate: false);
            StartBrandLogoShine();
            PlayIntroSequence();

            DispatcherQueue.TryEnqueue(async () =>
            {
                int introGeneration = _introGeneration;
                await Task.Delay(5200);
                if (introGeneration == _introGeneration &&
                    IntroOverlay.Visibility == Visibility.Visible &&
                    (StepContainer.Opacity <= 0.01 ||
                     FooterNav.Opacity <= 0.01))
                {
                    App.Log("[Onboarding] First paint fallback restored hidden main content.");
                    DismissIntro();
                }
            });
        };

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplyTitleBarButtonColors();
            PrepareIntroContent();
            SetupStep(animate: false);
        };

        Closed += (_, _) =>
        {
            _introGeneration++;
            _introStoryboard?.Stop();
            _brandLogoShineStoryboard?.Stop();
            _stepTransitionStoryboard?.Stop();
            _keycapPulseStoryboard?.Stop();
            _hotkeyDemoCts?.Cancel();
            _hotkeyDemoCts?.Dispose();
            _hotkeyDemoCts = null;
            IntroMarkHost.Children.Clear();
            RemoveMinimumSizeHook();
            _localizationService.LanguageChanged -= OnLanguageChanged;
        };
    }

    public void RestartIntro()
    {
        if (!_hasLoaded)
        {
            return;
        }

        _stepIndex = 0;
        SetupStep(animate: false);
        PlayIntroSequence();
    }

    // ════════════════════════════════════════════════════════════
    //  Window Setup
    // ════════════════════════════════════════════════════════════

    private void ResizeAndCenterForDisplay(Microsoft.UI.WindowId windowId)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetCurrentDpiScale();
        int desiredWidth = ToPhysicalPixels(DesiredWindowWidth, scale);
        int desiredHeight = ToPhysicalPixels(DesiredWindowHeight, scale);
        int minWidth = ToPhysicalPixels(MinWindowWidth, scale);
        int minHeight = ToPhysicalPixels(MinWindowHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);
        int width = Math.Clamp(desiredWidth, minWidth, Math.Max(minWidth, workArea.Width - workAreaMargin));
        int height = Math.Clamp(desiredHeight, minHeight, Math.Max(minHeight, workArea.Height - workAreaMargin));

        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
    }

    private void ApplyResponsiveLayout()
    {
        double width = RootGrid.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        bool compact = width < CompactLayoutThreshold;
        RootGrid.Padding = compact ? new Thickness(28) : new Thickness(40);
        TitleBarHost.Margin = compact
            ? new Thickness(-28, -28, -28, 6)
            : new Thickness(-40, -40, -40, 6);
        IntroOverlay.Margin = compact ? new Thickness(-28) : new Thickness(-40);
        IntroOverlay.Padding = compact ? new Thickness(28) : new Thickness(40);
        FooterNav.Margin = compact ? new Thickness(0, 18, 0, 0) : new Thickness(0, 24, 0, 0);

        // Step 3: stack columns vertically in compact mode
        if (Step3Panel.ColumnDefinitions.Count > 0)
        {
            if (compact)
            {
                Step3Panel.ColumnSpacing = 0;
                Step3Panel.RowSpacing = 20;
                Step3Panel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                Step3Panel.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(Step3PreviewHost, 1);
                Grid.SetColumn(Step3PreviewHost, 0);
                Step3PreviewHost.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                Step3Panel.ColumnSpacing = 32;
                Step3Panel.RowSpacing = 0;
                Step3Panel.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                Step3Panel.ColumnDefinitions[1].Width = GridLength.Auto;
                Grid.SetRow(Step3PreviewHost, 0);
                Grid.SetColumn(Step3PreviewHost, 1);
                Step3PreviewHost.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }

        ProgressDots.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    // ════════════════════════════════════════════════════════════
    //  Step Navigation
    // ════════════════════════════════════════════════════════════

    private static readonly int StepCount = 5;

    private FrameworkElement GetStepPanel(int index) => index switch
    {
        0 => Step1Panel,
        1 => Step2Panel,
        2 => Step3Panel,
        3 => Step4Panel,
        4 => Step5Panel,
        _ => Step1Panel
    };

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0 || _isAnimating)
        {
            return;
        }

        _ = NavigateToStepAsync(_stepIndex - 1, forward: false);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnimating)
        {
            return;
        }

        if (_stepIndex < StepCount - 1)
        {
            await NavigateToStepAsync(_stepIndex + 1, forward: true);
            return;
        }

        await CompleteOnboardingAsync();
    }

    private async void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        await CompleteOnboardingAsync();
    }

    private async Task CompleteOnboardingAsync()
    {
        _settingsService.Settings.HasCompletedOnboarding = true;
        await _settingsService.SaveAsync();
        Close();
    }

    private async Task NavigateToStepAsync(int newStep, bool forward)
    {
        if (newStep == _stepIndex || _isAnimating)
        {
            return;
        }

        _isAnimating = true;
        StopStepAnimations();

        var currentPanel = GetStepPanel(_stepIndex);
        var newPanel = GetStepPanel(newStep);

        // Prepare new panel start state
        newPanel.Visibility = Visibility.Visible;
        double enterFromX = forward ? 40 : -40;
        SetElementTransform(newPanel, translateX: enterFromX, translateY: 0, scale: 0.99);
        newPanel.Opacity = 0;

        // Build transition storyboard
        _stepTransitionStoryboard?.Stop();
        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // ── Animate current panel out ──
        var curOpacityOut = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = easing
        };
        Storyboard.SetTarget(curOpacityOut, currentPanel);
        Storyboard.SetTargetProperty(curOpacityOut, "Opacity");
        storyboard.Children.Add(curOpacityOut);

        var curTransform = GetElementTransform(currentPanel);
        var curXOut = new DoubleAnimation
        {
            From = 0,
            To = forward ? -40 : 40,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = easing
        };
        Storyboard.SetTarget(curXOut, curTransform);
        Storyboard.SetTargetProperty(curXOut, "TranslateX");
        storyboard.Children.Add(curXOut);

        // ── Animate new panel in (delayed) ──
        int inDelay = 140;
        var newOpacityIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(340)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newOpacityIn, newPanel);
        Storyboard.SetTargetProperty(newOpacityIn, "Opacity");
        storyboard.Children.Add(newOpacityIn);

        var newTransform = GetElementTransform(newPanel);
        var newXIn = new DoubleAnimation
        {
            From = enterFromX,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newXIn, newTransform);
        Storyboard.SetTargetProperty(newXIn, "TranslateX");
        storyboard.Children.Add(newXIn);

        var newScaleInX = new DoubleAnimation
        {
            From = 0.99,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newScaleInX, newTransform);
        Storyboard.SetTargetProperty(newScaleInX, "ScaleX");
        storyboard.Children.Add(newScaleInX);

        var newScaleInY = new DoubleAnimation
        {
            From = 0.99,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = TimeSpan.FromMilliseconds(inDelay),
            EasingFunction = easing
        };
        Storyboard.SetTarget(newScaleInY, newTransform);
        Storyboard.SetTargetProperty(newScaleInY, "ScaleY");
        storyboard.Children.Add(newScaleInY);

        _stepTransitionStoryboard = storyboard;
        int previousStep = _stepIndex;
        _stepIndex = newStep;

        storyboard.Completed += (_, _) =>
        {
            currentPanel.Visibility = Visibility.Collapsed;
            SetElementTransform(currentPanel);
            currentPanel.Opacity = 1;
            _isAnimating = false;

            // Start step-specific ambient animations
            StartStepAmbientAnimation(newStep);
        };

        UpdateFooterState();
        SetupStep(animate: true);
        storyboard.Begin();
    }

    /// <summary>
    /// Sets up the current step's dynamic content and wires up events.
    /// Called on initial load (animate=false) and during transitions (animate=true).
    /// </summary>
    private void SetupStep(bool animate)
    {
        switch (_stepIndex)
        {
            case 0:
                if (animate) StartStep1CardAnimation();
                break;
            case 1:
                SetupStep2Features();
                break;
            case 2:
                SetupStep3();
                break;
            case 3:
                SetupStep4();
                break;
            case 4:
                SetupStep5();
                break;
        }
    }

    private void StopStepAnimations()
    {
        _keycapPulseStoryboard?.Stop();
        _keycapPulseStoryboard = null;
        _hotkeyDemoCts?.Cancel();
        _hotkeyDemoCts?.Dispose();
        _hotkeyDemoCts = null;
        _searchDemoCts?.Cancel();
        _searchDemoCts?.Dispose();
        _searchDemoCts = null;
    }

    // ════════════════════════════════════════════════════════════
    //  Step 1: Value Card Stagger Animation
    // ════════════════════════════════════════════════════════════

    private void StartStep1CardAnimation()
    {
        Border[] cards = [Step1Card1, Step1Card2, Step1Card3];
        for (int i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var transform = GetElementTransform(card);
            transform.TranslateY = 14;
            card.Opacity = 0;

            var storyboard = new Storyboard();
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            int delay = 100 + i * 120;

            var opacityAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(360)),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacityAnim, card);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            storyboard.Children.Add(opacityAnim);

            var translateAnim = new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = easing
            };
            Storyboard.SetTarget(translateAnim, transform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateY");
            storyboard.Children.Add(translateAnim);

            storyboard.Begin();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 5: Search Demo Typewriter Animation
    // ════════════════════════════════════════════════════════════

    private System.Threading.CancellationTokenSource? _searchDemoCts;

    private void StartSearchDemoAnimation()
    {
        _searchDemoCts?.Cancel();
        _searchDemoCts?.Dispose();
        var cts = new System.Threading.CancellationTokenSource();
        _searchDemoCts = cts;
        _ = RunSearchDemoAsync(cts.Token);
    }

    private async Task RunSearchDemoAsync(System.Threading.CancellationToken ct)
    {
        try
        {
            string demoText = "\u5468\u62a5.docx";
            Step5SearchDemoText.Text = "";
            Step5SearchResults.Opacity = 0;

            await Task.Delay(600, ct);

            // Typewriter effect
            for (int i = 0; i < demoText.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                Step5SearchDemoText.Text = demoText[..(i + 1)];
                await Task.Delay(110, ct);
            }

            await Task.Delay(400, ct);

            // Fade in results
            var transform = GetElementTransform(Step5SearchResults);
            transform.TranslateY = 8;
            var storyboard = new Storyboard();
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var opacityAnim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(380)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(opacityAnim, Step5SearchResults);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            storyboard.Children.Add(opacityAnim);

            var translateAnim = new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                EasingFunction = easing
            };
            Storyboard.SetTarget(translateAnim, transform);
            Storyboard.SetTargetProperty(translateAnim, "TranslateY");
            storyboard.Children.Add(translateAnim);

            storyboard.Begin();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Starts ambient (looping) animations specific to a step.
    /// </summary>
    private void StartStepAmbientAnimation(int step)
    {
        switch (step)
        {
            case 0:
                StartStep1CardAnimation();
                break;
            case 3:
                // Step 4: Start keycap pulse if hotkey is enabled
                if (Step4HotkeyToggle.IsOn)
                {
                    StartKeycapPulse();
                }
                break;
            case 4:
                // Step 5: Start search demo animation
                StartSearchDemoAnimation();
                break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Footer State
    // ════════════════════════════════════════════════════════════

    private void BuildProgressDots()
    {
        ProgressDots.Children.Clear();
        for (int index = 0; index < StepCount; index++)
        {
            ProgressDots.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Opacity = 0.42,
                Fill = SubtleDotBrush()
            });
        }
    }

    private void UpdateProgressDots()
    {
        for (int index = 0; index < ProgressDots.Children.Count; index++)
        {
            if (ProgressDots.Children[index] is not Ellipse dot)
            {
                continue;
            }

            bool active = index == _stepIndex;
            dot.Width = 8;
            dot.Height = 8;
            dot.Opacity = active ? 1 : 0.42;
            dot.Fill = active ? AccentBrush() : SubtleDotBrush();
        }
    }

    private void UpdateFooterState()
    {
        BackButton.IsEnabled = _stepIndex > 0;
        SkipButton.Content = _localizationService.T("Onboarding.Skip");
        BackButton.Content = _localizationService.T("Onboarding.Back");
        NextButton.Content = _stepIndex == StepCount - 1
            ? _localizationService.T("Onboarding.Start")
            : _localizationService.T("Onboarding.Next");
        SkipButton.Visibility = _stepIndex == StepCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgressDots();
    }

    // ════════════════════════════════════════════════════════════
    //  Step 2: Storage & Quick Access
    // ════════════════════════════════════════════════════════════

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(_hWnd, _windowSubclassProc, OnboardingWindowSubclassId, UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _windowSubclassProc, OnboardingWindowSubclassId);
        _isSubclassInstalled = false;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        const uint WmGetMinMaxInfo = 0x0024;
        const uint WmNcDestroy = 0x0082;

        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = GetCurrentDpiScale();
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, ToPhysicalPixels(MinWindowWidth, scale));
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, ToPhysicalPixels(MinWindowHeight, scale));
            System.Runtime.InteropServices.Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private double GetCurrentDpiScale()
    {
        return Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        return Math.Max(1, (int)Math.Round(logicalPixels * scale, MidpointRounding.AwayFromZero));
    }

    // ════════════════════════════════════════════════════════════
    //  Title Bar
    // ════════════════════════════════════════════════════════════

    private void ApplyTitleBarButtonColors()
    {
        bool isDark = IsDarkTheme();
        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
    }

    // ════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════

    private bool IsDarkTheme()
    {
        return RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };
    }

    private static SolidColorBrush BrushFromColor(Color color)
    {
        return new SolidColorBrush(color);
    }

    private SolidColorBrush AccentBrush()
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        return BrushFromColor(accentColor);
    }

    private SolidColorBrush SubtleDotBrush()
    {
        return IsDarkTheme()
            ? BrushFromColor(ColorHelper.FromArgb(0xFF, 0x68, 0x72, 0x80))
            : BrushFromColor(ColorHelper.FromArgb(0xFF, 0xC6, 0xD0, 0xDE));
    }

    private static void SetElementOpacity(UIElement element, double opacity)
    {
        element.Opacity = opacity;
    }

    private static void SetElementTransform(
        UIElement element,
        double translateX = 0,
        double translateY = 0,
        double scale = 1)
    {
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        SetTransformValues(element, translateX, translateY, scale);
    }

    private static void SetTransformValues(
        UIElement element,
        double translateX = 0,
        double translateY = 0,
        double scale = 1)
    {
        var transform = GetElementTransform(element);
        transform.TranslateX = translateX;
        transform.TranslateY = translateY;
        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    private static CompositeTransform GetElementTransform(UIElement element)
    {
        if (element.RenderTransform is CompositeTransform transform)
        {
            return transform;
        }

        transform = new CompositeTransform();
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        element.RenderTransform = transform;
        return transform;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}
