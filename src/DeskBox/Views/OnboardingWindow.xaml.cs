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
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow : Window
{
    internal const int CurrentOnboardingVersion = 2;
    private const int DesiredWindowWidth = 1040;
    private const int DesiredWindowHeight = 740;
    private const int MinWindowWidth = 660;
    private const int MinWindowHeight = 540;
    private const int WindowWorkAreaMargin = 96;
    private const int CompactLayoutThreshold = 880;
    private const uint WmReservedHotkeyCapture = 0x8443;
    private static readonly UIntPtr OnboardingWindowSubclassId = new(0xD05C0B01);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;

    private Storyboard? _introStoryboard;
    private Storyboard? _brandLogoShineStoryboard;
    private Storyboard? _stepTransitionStoryboard;
    private Storyboard? _keycapPulseStoryboard;
    private Storyboard? _stepAmbientStoryboard;
    private Storyboard? _statusFeedbackStoryboard;
    private System.Threading.CancellationTokenSource? _hotkeyDemoCts;
    private int _introGeneration;
    private int _stepIndex;
    private bool _hasLoaded;
    private bool _isSubclassInstalled;
    private bool _isAnimating;
    private bool _isRecordingHotkey;
    private bool _hasInitializedFeatureToggles;
    private bool _isSynchronizingFeatureToggles;
    private Task _featureWidgetSelectionUpdateTask = Task.CompletedTask;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private readonly ReservedHotkeyHookService _hotkeyRecordingHook = new();

    // Accent color preset list
    private static readonly string[] PresetAccentColors = { "#0078D4", "#E81123", "#107C10", "#5D2E9B", "#FF8C00", "#0099BC" };

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
        App.Current.OnboardingFileImportCompleted += OnOnboardingFileImportCompleted;
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
        Activated += OnboardingWindow_Activated;
        RootGrid.KeyDown += (_, e) => OnHotkeyKeyDown(e.Key);
        RootGrid.Loaded += (_, _) =>
        {
            _hasLoaded = true;
            ApplyResponsiveLayout();
            ApplyTitleBarButtonColors();
            BuildProgressDots();
            SetupStep(animate: false);
            StartStepAmbientAnimation(_stepIndex);
            StartBrandLogoShine();
            PlayIntroSequence();

            DispatcherQueue.TryEnqueue(async () =>
            {
                int introGeneration = _introGeneration;
                await Task.Delay(IntroAnimationTargetMilliseconds + 1500);
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
            Activated -= OnboardingWindow_Activated;
            _introGeneration++;
            _introStoryboard?.Stop();
            _brandLogoShineStoryboard?.Stop();
            _stepTransitionStoryboard?.Stop();
            _keycapPulseStoryboard?.Stop();
            _stepAmbientStoryboard?.Stop();
            _statusFeedbackStoryboard?.Stop();
            _hotkeyDemoCts?.Cancel();
            _hotkeyDemoCts?.Dispose();
            _hotkeyDemoCts = null;
            _isRecordingHotkey = false;
            _hotkeyRecordingHook.Dispose();
            ReleaseFilePracticeWidget();
            DetachDesktopOrganizationWindow();
            IntroMarkHost.Children.Clear();
            RemoveMinimumSizeHook();
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _settingsService.SettingsChanged -= OnFeatureWidgetSettingsChanged;
            App.Current.OnboardingFileImportCompleted -= OnOnboardingFileImportCompleted;
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

        ApplyTaskFlowResponsiveLayout(compact, width);

        ProgressDots.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    private void ApplyTaskFlowResponsiveLayout(bool compact, double availableWidth)
    {
        ApplyTwoColumnTaskLayout(TaskStep3Layout, TaskStep3VisualStage, compact);
        ApplyTwoColumnTaskLayout(TaskStep4Layout, TaskStep4VisualStage, compact);
        ApplyTwoColumnTaskLayout(TaskStep2Layout, TaskStep2VisualStage, compact);

        Border[] cards =
        [
            TaskStep5TodoCard,
            TaskStep5QuickCaptureCard,
            TaskStep5SearchCard,
            TaskStep5WeatherCard,
            TaskStep5MusicCard,
            TaskStep5GlanceCard,
            TaskStep5PomodoroCard
        ];

        TaskStep5FeatureGrid.ColumnDefinitions.Clear();
        TaskStep5FeatureGrid.RowDefinitions.Clear();
        TaskStep5FeatureSection.Width = compact
            ? Math.Min(720, Math.Max(520, availableWidth - 56))
            : 720;
        int columnCount = compact ? 2 : 3;
        int rowCount = (cards.Length + columnCount - 1) / columnCount;
        for (int index = 0; index < columnCount; index++)
        {
            TaskStep5FeatureGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        }

        for (int index = 0; index < rowCount; index++)
        {
            TaskStep5FeatureGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
        }

        for (int index = 0; index < cards.Length; index++)
        {
            Grid.SetRow(cards[index], index / columnCount);
            Grid.SetColumn(cards[index], index % columnCount);
        }
    }

    private static void ApplyTwoColumnTaskLayout(
        Grid layout,
        FrameworkElement visualStage,
        bool compact)
    {
        if (layout.ColumnDefinitions.Count < 2)
        {
            return;
        }

        layout.ColumnDefinitions[0].Width = new GridLength(
            compact ? 1 : 0.88,
            GridUnitType.Star);
        layout.ColumnDefinitions[1].Width = compact
            ? new GridLength(0)
            : new GridLength(1.12, GridUnitType.Star);
        layout.ColumnSpacing = compact ? 0 : 36;
        layout.RowSpacing = compact ? 22 : 0;
        Grid.SetRow(visualStage, compact ? 1 : 0);
        Grid.SetColumn(visualStage, compact ? 0 : 1);
    }

    // ════════════════════════════════════════════════════════════
    //  Step Navigation
    // ════════════════════════════════════════════════════════════

    private static readonly int StepCount = 4;

    private FrameworkElement GetStepPanel(int index) => index switch
    {
        0 => TaskStep3Panel,
        1 => TaskStep4Panel,
        2 => TaskStep2Panel,
        3 => TaskStep5Panel,
        _ => TaskStep3Panel
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
        TaskStep5TodoToggle.IsEnabled = false;
        TaskStep5QuickCaptureToggle.IsEnabled = false;
        TaskStep5SearchToggle.IsEnabled = false;
        TaskStep5WeatherToggle.IsEnabled = false;
        TaskStep5MusicToggle.IsEnabled = false;
        TaskStep5GlanceToggle.IsEnabled = false;
        TaskStep5PomodoroToggle.IsEnabled = false;
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        await _featureWidgetSelectionUpdateTask;
        _settingsService.Settings.HasCompletedOnboarding = true;
        _settingsService.Settings.CompletedOnboardingVersion = CurrentOnboardingVersion;
        _settingsService.Settings.OnboardingStepIndex = 0;
        await _settingsService.SaveAsync();
        ReleaseFilePracticeWidget();
        Close();
    }

    private async Task NavigateToStepAsync(int newStep, bool forward)
    {
        if (newStep < 0 ||
            newStep >= StepCount ||
            newStep == _stepIndex ||
            _isAnimating)
        {
            return;
        }

        if (_isRecordingHotkey)
        {
            EndHotkeyRecording();
        }

        if (_stepIndex == 0 && newStep != 0)
        {
            ReleaseFilePracticeWidget();
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
        _stepIndex = newStep;
        _settingsService.Settings.OnboardingStepIndex = newStep;
        _settingsService.SaveDebounced(notifySubscribers: false);

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
            case 0:
                SetupTaskStep3();
                break;
            case 1:
                SetupTaskStep4();
                break;
            case 2:
                SetupTaskStep2();
                break;
            case 3:
                SetupTaskStep5();
                break;
        }
    }

    private void StopStepAnimations()
    {
        _keycapPulseStoryboard?.Stop();
        _keycapPulseStoryboard = null;
        _stepAmbientStoryboard?.Stop();
        _stepAmbientStoryboard = null;
        _statusFeedbackStoryboard?.Stop();
        _statusFeedbackStoryboard = null;
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
        _stepAmbientStoryboard?.Stop();
        _stepAmbientStoryboard = step switch
        {
            0 => CreateFilePracticeAmbientStoryboard(),
            1 => CreateVisibilityPracticeAmbientStoryboard(),
            2 => CreateTrayAmbientStoryboard(),
            3 => CreateFeatureCardEntranceStoryboard(),
            _ => null
        };
        _stepAmbientStoryboard?.Begin();
    }

    private Storyboard CreateFilePracticeAmbientStoryboard()
    {
        var storyboard = new Storyboard();
        var fileTransform = GetElementTransform(TaskStep3FileToken);
        var dropTransform = GetElementTransform(TaskStep3DropHalo);
        AddStepAnimation(
            storyboard,
            fileTransform,
            "TranslateY",
            4,
            -5,
            durationMilliseconds: 1500,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            TaskStep3DropHalo,
            "Opacity",
            0.10,
            0.24,
            durationMilliseconds: 1050,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            dropTransform,
            "ScaleX",
            0.96,
            1.03,
            durationMilliseconds: 1050,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            dropTransform,
            "ScaleY",
            0.96,
            1.03,
            durationMilliseconds: 1050,
            autoReverse: true,
            repeat: true);
        return storyboard;
    }

    private Storyboard CreateVisibilityPracticeAmbientStoryboard()
    {
        var storyboard = new Storyboard();
        var widgetsTransform = GetElementTransform(TaskStep4PreviewWidgets);
        AddStepAnimation(
            storyboard,
            widgetsTransform,
            "TranslateY",
            5,
            -2,
            durationMilliseconds: 1800,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            TaskStep4PreviewWidgets,
            "Opacity",
            0.82,
            1,
            durationMilliseconds: 1800,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            TaskStep4HotkeyHalo,
            "Opacity",
            0.12,
            0.34,
            durationMilliseconds: 1100,
            autoReverse: true,
            repeat: true);
        return storyboard;
    }

    private Storyboard CreateTrayAmbientStoryboard()
    {
        var storyboard = new Storyboard();
        var haloTransform = GetElementTransform(TaskStep2TrayHalo);
        AddStepAnimation(
            storyboard,
            TaskStep2TrayHalo,
            "Opacity",
            0.26,
            0.08,
            durationMilliseconds: 1150,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            haloTransform,
            "ScaleX",
            0.88,
            1.12,
            durationMilliseconds: 1150,
            autoReverse: true,
            repeat: true);
        AddStepAnimation(
            storyboard,
            haloTransform,
            "ScaleY",
            0.88,
            1.12,
            durationMilliseconds: 1150,
            autoReverse: true,
            repeat: true);
        return storyboard;
    }

    private Storyboard CreateFeatureCardEntranceStoryboard()
    {
        var storyboard = new Storyboard();
        FrameworkElement[] cards =
        [
            TaskStep5TodoCard,
            TaskStep5QuickCaptureCard,
            TaskStep5SearchCard,
            TaskStep5WeatherCard,
            TaskStep5MusicCard,
            TaskStep5GlanceCard,
            TaskStep5PomodoroCard,
            TaskStep5OptionalHint
        ];
        for (int index = 0; index < cards.Length; index++)
        {
            var card = cards[index];
            var transform = GetElementTransform(card);
            transform.TranslateY = 12;
            card.Opacity = 0;
            int delay = 70 + index * 55;
            AddStepAnimation(
                storyboard,
                card,
                "Opacity",
                0,
                1,
                durationMilliseconds: 280,
                beginMilliseconds: delay);
            AddStepAnimation(
                storyboard,
                transform,
                "TranslateY",
                12,
                0,
                durationMilliseconds: 340,
                beginMilliseconds: delay);
        }

        return storyboard;
    }

    private static void AddStepAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMilliseconds,
        int beginMilliseconds = 0,
        bool autoReverse = false,
        bool repeat = false)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMilliseconds)),
            BeginTime = TimeSpan.FromMilliseconds(beginMilliseconds),
            AutoReverse = autoReverse,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        if (repeat)
        {
            animation.RepeatBehavior = RepeatBehavior.Forever;
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void AnimateStatusFeedback(Border badge)
    {
        if (!_hasLoaded)
        {
            return;
        }

        _statusFeedbackStoryboard?.Stop();
        var transform = GetElementTransform(badge);
        transform.TranslateY = 4;
        transform.ScaleX = 0.985;
        transform.ScaleY = 0.985;
        badge.Opacity = 0.58;

        var storyboard = new Storyboard();
        AddStepAnimation(storyboard, badge, "Opacity", 0.58, 1, 210);
        AddStepAnimation(storyboard, transform, "TranslateY", 4, 0, 240);
        AddStepAnimation(storyboard, transform, "ScaleX", 0.985, 1, 240);
        AddStepAnimation(storyboard, transform, "ScaleY", 0.985, 1, 240);
        _statusFeedbackStoryboard = storyboard;
        storyboard.Begin();
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
                Height = 6,
                Opacity = 0.34,
                Fill = SubtleDotBrush()
            });
        }

        UpdateProgressDots();
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
            dot.Width = active ? 8 : 6;
            dot.Height = active ? 8 : 6;
            dot.Opacity = active ? 1 : 0.34;
            dot.Fill = active ? AccentBrush() : SubtleDotBrush();
        }

        StepCounterText.Text = $"{_stepIndex + 1:00} / {StepCount:00}";
    }

    private void UpdateFooterState()
    {
        BackButton.IsEnabled = _stepIndex > 0;
        SkipButton.Content = _localizationService.T("Onboarding.SkipAll");
        BackButton.Content = _localizationService.T("Onboarding.Back");
        NextButton.Content = _stepIndex switch
        {
            _ when _stepIndex == StepCount - 1 => _localizationService.T("Onboarding.Start"),
            0 when !_hasCompletedFilePractice => _localizationService.T("Onboarding.Task.SkipPractice"),
            1 when !_hasCompletedVisibilityPractice => _localizationService.T("Onboarding.Task.SkipPractice"),
            0 or 1 => _localizationService.T("Onboarding.Task.Continue"),
            _ => _localizationService.T("Onboarding.Next")
        };
        NextButton.IsEnabled = true;
        SkipButton.Visibility = _stepIndex == StepCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgressDots();
    }

    // ════════════════════════════════════════════════════════════
    //  Window sizing and title-bar plumbing
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

        if (message == WmReservedHotkeyCapture)
        {
            if (_isRecordingHotkey)
            {
                _ = ApplyRecordedHotkeyAsync(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Windows,
                    (int)VirtualKey.Space));
            }

            return IntPtr.Zero;
        }

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
