using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow : Window
{
    internal const int CurrentOnboardingVersion = 3;
    private const int DesiredWindowWidth = 1040;
    private const int DesiredWindowHeight = 700;
    private const int MinWindowWidth = 700;
    private const int MinWindowHeight = 560;
    private const int WindowWorkAreaMargin = 80;
    private const int CompactLayoutThreshold = 860;
    private const int StepCount = 5;

    /// <summary>The move/path safety step; skipping is offered only after it.</summary>
    private const int FirstSkippableStep = 2;

    /// <summary>Per-child delay of the copy column's staggered reveal.</summary>
    private const int TextRevealStaggerMs = 45;
    private const double TextRevealTravel = 18;
    private static readonly UIntPtr OnboardingWindowSubclassId = new(0xD05C0B01);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;

    private int _stepIndex;
    private bool _hasLoaded;
    private bool _isClosed;
    private bool _isAnimating;
    private bool _hasInitializedFeatureToggles;
    private bool _isSynchronizingFeatureToggles;
    private bool _hasToggledDuringSummonStep;
    private bool _hasCompletedSummonPractice;
    private bool _isCompactLayout;
    private Task _featureWidgetSelectionUpdateTask = Task.CompletedTask;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private Storyboard? _textRevealStoryboard;
    private Panel? _textRevealPanel;

    public OnboardingWindow(
        SettingsService settingsService,
        LocalizationService localizationService,
        int initialStep = 0)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _stepIndex = Math.Clamp(initialStep, 0, StepCount - 1);
        _windowSubclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
        InitializeComponent();
        _localizationService.LanguageChanged += OnLanguageChanged;
        _settingsService.SettingsChanged += OnFeatureWidgetSettingsChanged;
        App.Current.OnboardingWidgetsVisibilityChanged += OnOnboardingWidgetsVisibilityChanged;

        WindowsCompatibilityService.ApplySafeBackdrop(this);

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
        RootGrid.Loaded += (_, _) =>
        {
            _hasLoaded = true;
            ApplyResponsiveLayout();
            StepPips.SelectedPageIndex = _stepIndex;
            SetupStep(animate: false);
            UpdateFooterState();
            ApplySceneState(_stepIndex, animate: false);
            StartSceneLoops(_stepIndex);
            PlaySceneEntrance();
            PlayTextReveal(GetStepPanel(_stepIndex), direction: 1, initialDelayMs: 220);
        };

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            if (!_hasLoaded)
            {
                return;
            }

            SetupStep(animate: false);
            ApplySceneState(_stepIndex, animate: false);
            StartSceneLoops(_stepIndex);
        };

        Closed += (_, _) =>
        {
            _isClosed = true;
            WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
            StopAllSceneAnimations();
            FinishTextReveal();
            RemoveMinimumSizeHook();
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _settingsService.SettingsChanged -= OnFeatureWidgetSettingsChanged;
            App.Current.OnboardingWidgetsVisibilityChanged -= OnOnboardingWidgetsVisibilityChanged;
        };
    }

    public void RestartIntro(int stepIndex = 0)
    {
        if (!_hasLoaded)
        {
            return;
        }

        _stepIndex = Math.Clamp(stepIndex, 0, StepCount - 1);
        SetupStep(animate: false);
        UpdateFooterState();
        ApplySceneState(_stepIndex, animate: false);
        StartSceneLoops(_stepIndex);
        PlaySceneEntrance();
        PlayTextReveal(GetStepPanel(_stepIndex), direction: 1, initialDelayMs: 120);
    }

    // ════════════════════════════════════════════════════════════
    //  Window setup
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
        double height = RootGrid.ActualHeight;
        if (width <= 0)
        {
            return;
        }

        bool compact = width < CompactLayoutThreshold || height < 560;
        _isCompactLayout = compact;

        // Narrower scene canvas column + tighter copy margins on small windows.
        RootGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 0.36 : 0.42, GridUnitType.Star);
        var sideMargin = compact ? 26 : 44;
        TextScrollViewer.Margin = new Thickness(sideMargin, 0, sideMargin, 0);
        FooterNav.Margin = new Thickness(compact ? 26 : 44, 0, compact ? 26 : 44, compact ? 14 : 24);
    }

    // ════════════════════════════════════════════════════════════
    //  Step navigation
    // ════════════════════════════════════════════════════════════

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

        NavigateToStep(_stepIndex - 1);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnimating)
        {
            return;
        }

        if (_stepIndex < StepCount - 1)
        {
            NavigateToStep(_stepIndex + 1);
            return;
        }

        await CompleteOnboardingAsync();
    }

    private async void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex < FirstSkippableStep)
        {
            return;
        }

        await CompleteOnboardingAsync();
    }

    private async Task CompleteOnboardingAsync()
    {
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        SetFeatureTogglesEnabled(false);
        await _featureWidgetSelectionUpdateTask;
        _settingsService.Settings.HasCompletedOnboarding = true;
        _settingsService.Settings.CompletedOnboardingVersion = CurrentOnboardingVersion;
        _settingsService.Settings.OnboardingStepIndex = 0;
        await _settingsService.SaveAsync();
        Close();
    }

    private void NavigateToStep(int newStep)
    {
        if (newStep < 0 || newStep >= StepCount || newStep == _stepIndex || _isAnimating)
        {
            return;
        }

        _isAnimating = true;
        int direction = newStep > _stepIndex ? 1 : -1;
        var currentPanel = GetStepPanel(_stepIndex);
        var newPanel = GetStepPanel(newStep);

        _stepIndex = newStep;
        _settingsService.Settings.OnboardingStepIndex = newStep;
        _settingsService.SaveDebounced(notifySubscribers: false);

        // Step data first (drop mode, hotkey text, values), then the stage
        // hand-off so each scene starts from the right story.
        SetupStep(animate: true);
        ApplySceneState(newStep, animate: true, direction);
        StartSceneLoops(newStep);
        if (newStep == 4)
        {
            PlayFeatureStepEntrance();
        }

        PlayTextColumnSwap(currentPanel, newPanel, direction);
        UpdateFooterState();
    }

    /// <summary>
    /// Swaps the copy column in a single layout pass: the outgoing panel
    /// collapses immediately so the incoming one lays out straight at its
    /// final vertically-centered position (a cross-dissolve kept both panels
    /// laid out and made the incoming copy jump when the cell re-centered).
    /// The incoming children then reveal in a short stagger — title, body,
    /// controls — each sliding a few pixels in from the navigation direction
    /// on the Fluent decelerate curve. Storyboards (not composition) because
    /// the panel was just uncollapsed and its visuals can still be rebuilt
    /// under StartAnimation, which would silently drop the animation.
    /// </summary>
    private void PlayTextColumnSwap(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        int direction)
    {
        outgoing.Visibility = Visibility.Collapsed;
        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 1;

        if (!WindowsCompatibilityService.AreAnimationsEnabled)
        {
            FinishTextReveal();
            _isAnimating = false;
            return;
        }

        PlayTextReveal(incoming, direction, initialDelayMs: 60);
        int generation = ++_transitionGeneration;
        _ = ReleaseNavigationGateAsync(generation);
    }

    private int _transitionGeneration;

    /// <summary>
    /// Navigation re-opens shortly after a swap starts (it only debounces
    /// double clicks); a newer swap simply supersedes the running reveal.
    /// </summary>
    private async Task ReleaseNavigationGateAsync(int generation)
    {
        await Task.Delay(200);
        if (_isClosed || generation != _transitionGeneration)
        {
            return;
        }

        _isAnimating = false;
    }

    private void PlayTextReveal(FrameworkElement panel, int direction, int initialDelayMs)
    {
        FinishTextReveal();
        panel.Opacity = 1;
        if (!WindowsCompatibilityService.AreAnimationsEnabled || panel is not Panel host)
        {
            return;
        }

        var storyboard = new Storyboard();
        int index = 0;
        foreach (UIElement child in host.Children)
        {
            if (child.Visibility != Visibility.Visible)
            {
                continue;
            }

            double travel = direction * TextRevealTravel;
            var transform = new TranslateTransform { X = travel };
            child.RenderTransform = transform;
            child.Opacity = 0;
            TimeSpan begin = TimeSpan.FromMilliseconds(initialDelayMs + (index * TextRevealStaggerMs));
            storyboard.Children.Add(CreateDecelerateAnimation(child, nameof(UIElement.Opacity), 0, 1, 260, begin));
            storyboard.Children.Add(CreateDecelerateAnimation(transform, nameof(TranslateTransform.X), travel, 0, 500, begin));
            index++;
        }

        _textRevealStoryboard = storyboard;
        _textRevealPanel = host;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_textRevealStoryboard, storyboard))
            {
                FinishTextReveal();
            }
        };
        storyboard.Begin();
    }

    /// <summary>
    /// Lands base values first, then releases the storyboard's hold so the
    /// copy settles without a post-animation snap.
    /// </summary>
    private void FinishTextReveal()
    {
        if (_textRevealPanel is { } host)
        {
            foreach (UIElement child in host.Children)
            {
                child.Opacity = 1;
                if (child.RenderTransform is TranslateTransform transform)
                {
                    transform.X = 0;
                }
            }
        }

        _textRevealStoryboard?.Stop();
        _textRevealStoryboard = null;
        _textRevealPanel = null;
    }

    private static DoubleAnimationUsingKeyFrames CreateDecelerateAnimation(
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMs,
        TimeSpan begin)
    {
        var animation = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from
        });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)),
            Value = to,
            // Fluent "decelerate max": fast departure, long soft landing.
            KeySpline = new KeySpline
            {
                ControlPoint1 = new Windows.Foundation.Point(0.1, 0.9),
                ControlPoint2 = new Windows.Foundation.Point(0.2, 1.0)
            }
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    // ════════════════════════════════════════════════════════════
    //  Step setup dispatch
    // ════════════════════════════════════════════════════════════

    private void SetupStep(bool animate)
    {
        if (!animate)
        {
            FinishTextReveal();
            for (int index = 0; index < StepCount; index++)
            {
                var panel = GetStepPanel(index);
                panel.Visibility = index == _stepIndex
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                panel.Opacity = 1;
                panel.RenderTransform = null;
                panel.IsHitTestVisible = true;
            }
        }

        switch (_stepIndex)
        {
            case 1:
                SetupDropStep();
                break;
            case 2:
                SetupSummonStep();
                break;
            case 3:
                SetupTrayStep();
                break;
            case 4:
                SetupFeatureStep();
                break;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Footer
    // ════════════════════════════════════════════════════════════

    private void UpdateFooterState()
    {
        bool canGoBack = _stepIndex > 0;
        BackButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = canGoBack;
        ToolTipService.SetToolTip(BackButton, _localizationService.T("Onboarding.Back"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            BackButton, _localizationService.T("Onboarding.Back"));
        NextButton.Content = _stepIndex == StepCount - 1
            ? _localizationService.T("Onboarding.Start")
            : _localizationService.T("Onboarding.Next");
        NextButton.IsEnabled = true;
        SkipButton.Visibility = _stepIndex >= FirstSkippableStep && _stepIndex < StepCount - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateProgressDots();
    }

    private void UpdateProgressDots()
    {
        if (StepPips.SelectedPageIndex != _stepIndex)
        {
            StepPips.SelectedPageIndex = _stepIndex;
        }
    }

    private void StepPips_SelectionChanged(
        PipsPager sender,
        PipsPagerSelectedIndexChangedEventArgs args)
    {
        int target = sender.SelectedPageIndex;
        if (target == _stepIndex || _isAnimating)
        {
            if (target != _stepIndex)
            {
                sender.SelectedPageIndex = _stepIndex;
            }

            return;
        }

        NavigateToStep(target);
    }

    // ════════════════════════════════════════════════════════════
    //  Localization
    // ════════════════════════════════════════════════════════════

    private void OnLanguageChanged()
    {
        Title = _localizationService.T("Onboarding.WindowTitle");
        Localized.RefreshAll(_localizationService);
        SetupStep(animate: false);
        UpdateFooterState();
        RefreshSummonStatus();
    }

    // ════════════════════════════════════════════════════════════
    //  Visual helpers
    // ════════════════════════════════════════════════════════════

    internal static Microsoft.UI.Composition.Visual GetVisual(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        // Scale/Rotation animations pivot around CenterPoint, whose default
        // (0,0) makes press/pop keyframes drift toward the top-left — anchor
        // every scene element at its own center.
        var size = element.ActualSize;
        visual.CenterPoint = new Vector3(size.X / 2f, size.Y / 2f, 0f);
        return visual;
    }

    // ════════════════════════════════════════════════════════════
    //  Minimum-size window subclass
    // ════════════════════════════════════════════════════════════

    private void InstallMinimumSizeHook()
    {
        Win32Helper.SetWindowSubclass(_hWnd, _windowSubclassProc, OnboardingWindowSubclassId, UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        Win32Helper.RemoveWindowSubclass(_hWnd, _windowSubclassProc, OnboardingWindowSubclassId);
    }

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
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
        return (int)Math.Round(logicalPixels * scale);
    }
}
