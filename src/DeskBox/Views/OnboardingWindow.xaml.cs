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
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow : Window
{
    internal const int CurrentOnboardingVersion = 3;
    private const int DesiredWindowWidth = 1040;
    private const int DesiredWindowHeight = 680;
    private const int MinWindowWidth = 700;
    private const int MinWindowHeight = 560;
    private const int WindowWorkAreaMargin = 80;
    private const int CompactLayoutThreshold = 860;
    private const int StepCount = 5;
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

    public OnboardingWindow(
        SettingsService settingsService,
        LocalizationService localizationService,
        int initialStep = 0)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _stepIndex = Math.Clamp(initialStep, 0, StepCount - 1);
        _windowSubclassProc = WindowSubclassProc;
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
            ApplySceneState(_stepIndex, animate: false);
            StartSceneLoops(_stepIndex);
            PlaySceneEntrance();
        };

        RootGrid.ActualThemeChanged += (_, _) =>
        {
            SetupStep(animate: false);
            ApplySceneState(_stepIndex, animate: false);
        };

        Closed += (_, _) =>
        {
            _isClosed = true;
            WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
            StopSceneLoops();
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
        ApplySceneState(_stepIndex, animate: false);
        StartSceneLoops(_stepIndex);
        PlaySceneEntrance();
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

        NavigateToStep(_stepIndex - 1, forward: false);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isAnimating)
        {
            return;
        }

        if (_stepIndex < StepCount - 1)
        {
            NavigateToStep(_stepIndex + 1, forward: true);
            return;
        }

        await CompleteOnboardingAsync();
    }

    private async Task CompleteOnboardingAsync()
    {
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        SetFeatureTogglesEnabled(false);
        await _featureWidgetSelectionUpdateTask;
        _settingsService.Settings.HasCompletedOnboarding = true;
        _settingsService.Settings.CompletedOnboardingVersion = CurrentOnboardingVersion;
        _settingsService.Settings.OnboardingStepIndex = 0;
        await _settingsService.SaveAsync();
        Close();
    }

    private void NavigateToStep(int newStep, bool forward)
    {
        if (newStep < 0 || newStep >= StepCount || newStep == _stepIndex || _isAnimating)
        {
            return;
        }

        _isAnimating = true;
        var currentPanel = GetStepPanel(_stepIndex);
        var newPanel = GetStepPanel(newStep);

        newPanel.Visibility = Visibility.Visible;

        // The scene stage stays mounted and morphs; only the text column swaps.
        ApplySceneState(newStep, animate: true);
        StartSceneLoops(newStep);
        PlayTextColumnSwap(currentPanel, newPanel, forward);

        _stepIndex = newStep;
        _settingsService.Settings.OnboardingStepIndex = newStep;
        _settingsService.SaveDebounced(notifySubscribers: false);

        UpdateFooterState();
        SetupStep(animate: true);
    }

    /// <summary>
    /// Crossfades the copy column: the outgoing panel just fades, the incoming
    /// one fades in with a soft 6px rise — no lateral motion, matching the
    /// OOBE-style page cadence.
    /// </summary>
    private void PlayTextColumnSwap(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        bool forward)
    {
        if (!WindowsCompatibilityService.AreAnimationsEnabled)
        {
            outgoing.Visibility = Visibility.Collapsed;
            ResetVisual(incoming);
            _isAnimating = false;
            return;
        }

        var outVisual = GetVisual(outgoing);
        var inVisual = GetVisual(incoming);
        var compositor = outVisual.Compositor;

        outVisual.StopAnimation("Opacity");
        outVisual.StopAnimation("Translation");
        inVisual.StopAnimation("Opacity");
        inVisual.StopAnimation("Translation");

        var exitEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.7f, 0f), new Vector2(1f, 1f));
        var enterEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(130);
        fadeOut.InsertKeyFrame(0f, 1f);
        fadeOut.InsertKeyFrame(1f, 0f, exitEase);
        outVisual.StartAnimation("Opacity", fadeOut);

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(300);
        fadeIn.DelayTime = TimeSpan.FromMilliseconds(90);
        fadeIn.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, enterEase);
        var riseIn = compositor.CreateVector3KeyFrameAnimation();
        riseIn.Duration = TimeSpan.FromMilliseconds(340);
        riseIn.DelayTime = TimeSpan.FromMilliseconds(90);
        riseIn.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        riseIn.InsertKeyFrame(0f, new Vector3(0, 6, 0));
        riseIn.InsertKeyFrame(1f, Vector3.Zero, enterEase);

        inVisual.StartAnimation("Opacity", fadeIn);
        inVisual.StartAnimation("Translation", riseIn);

        int generation = ++_transitionGeneration;
        _ = CompleteSwapAsync(outgoing, incoming, generation);
    }

    private int _transitionGeneration;

    private async Task CompleteSwapAsync(
        FrameworkElement outgoing,
        FrameworkElement incoming,
        int generation)
    {
        await Task.Delay(540);
        if (_isClosed || generation != _transitionGeneration)
        {
            return;
        }

        outgoing.Visibility = Visibility.Collapsed;
        ResetVisual(outgoing);
        incoming.Opacity = 1;
        incoming.Translation = Vector3.Zero;
        _isAnimating = false;
    }

    // ════════════════════════════════════════════════════════════
    //  Step setup dispatch
    // ════════════════════════════════════════════════════════════

    private void SetupStep(bool animate)
    {
        if (!animate)
        {
            for (int index = 0; index < StepCount; index++)
            {
                GetStepPanel(index).Visibility = index == _stepIndex
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        switch (_stepIndex)
        {
            case 2:
                SetupSummonStep();
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
            return;
        }

        NavigateToStep(target, forward: target > _stepIndex);
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
        return ElementCompositionPreview.GetElementVisual(element);
    }

    internal static void ResetVisual(UIElement element)
    {
        var visual = GetVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.StopAnimation("Scale");
        element.Opacity = 1;
        element.Translation = Vector3.Zero;
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
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
