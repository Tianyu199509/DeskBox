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

    private sealed record OnboardingStep(
        string KeyPrefix,
        Action<OnboardingWindow> BuildOptions,
        Action<OnboardingWindow> BuildScene);

    private sealed record IntroMarkTarget(double TranslateX, double TranslateY, double Scale);

    private static readonly OnboardingStep[] Steps =
    [
        new("Onboarding.Step1", window => window.BuildWelcomeOptions(), window => window.BuildWelcomeScene()),
        new("Onboarding.Step2", window => window.BuildStorageFlowOptions(), window => window.BuildStorageFlowScene()),
        new("Onboarding.Step3", window => window.BuildAppearanceOptions(), window => window.BuildAppearanceScene()),
        new("Onboarding.Step4", window => window.BuildFeatureWidgetOptions(), window => window.BuildFeatureWidgetScene()),
        new("Onboarding.Step5", window => window.BuildDailyAccessOptions(), window => window.BuildDailyAccessScene()),
        new("Onboarding.Step6", window => window.BuildReadyOptions(), window => window.BuildReadyScene())
    ];

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private Storyboard? _contentTransitionStoryboard;
    private Storyboard? _introStoryboard;
    private Storyboard? _brandLogoShineStoryboard;
    private Storyboard? _sceneEntranceStoryboard;
    private int _introGeneration;
    private int _sceneAnimationGeneration;
    private int _stepIndex;
    private bool _hasLoaded;
    private bool _isSubclassInstalled;
    private bool _suppressSceneEntranceAnimation;
    private bool _isStepTransitioning;
    private Action? _pendingSceneAnimations;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;

    // Scene state for left-right linkage
    private Border? _storageSidebarItem;
    private TextBlock? _storagePathText;
    private Border? _appearancePreviewWidget;
    private System.Threading.CancellationTokenSource? _hotkeyDemoCts;
    private System.Threading.CancellationTokenSource? _welcomeSceneCts;
    private System.Threading.CancellationTokenSource? _readySceneCts;
    private Button? _hotkeyChangeButton;
    private bool _isRecordingHotkey;

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
            RenderStep(animate: false);
            StartBrandLogoShine();
            PlayIntroSequence();
            DispatcherQueue.TryEnqueue(async () =>
            {
                int introGeneration = _introGeneration;
                await Task.Delay(5200);
                if (introGeneration == _introGeneration &&
                    IntroOverlay.Visibility == Visibility.Visible &&
                    (MainContentGrid.Opacity <= 0.01 ||
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
            RenderStep(animate: false);
        };

        Closed += (_, _) =>
        {
            _introGeneration++;
            _introStoryboard?.Stop();
            _brandLogoShineStoryboard?.Stop();
            StopSceneAnimations();
            IntroMarkHost.Children.Clear();
            DemoScene.Children.Clear();
            StepHintPanel.Children.Clear();
            StepOptionPanel.Children.Clear();
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
        RenderStep(animate: false);
        PlayIntroSequence();
    }

    private void ResizeAndCenterForDisplay(Microsoft.UI.WindowId windowId)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetCurrentDpiScale();
        int desiredWidth = ToPhysicalPixels(DesiredWindowWidth, scale);
        int desiredHeight = ToPhysicalPixels(DesiredWindowHeight, scale);
        int minWidth = ToPhysicalPixels(MinWindowWidth, scale);
        int minHeight = ToPhysicalPixels(MinWindowHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);
        int width = Math.Clamp(
            desiredWidth,
            minWidth,
            Math.Max(minWidth, workArea.Width - workAreaMargin));
        int height = Math.Clamp(
            desiredHeight,
            minHeight,
            Math.Max(minHeight, workArea.Height - workAreaMargin));

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

        if (compact)
        {
            MainContentGrid.ColumnSpacing = 0;
            MainContentGrid.RowSpacing = 20;
            MainContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(StepContentPanel, 0);
            Grid.SetColumn(StepContentPanel, 0);
            StepContentPanel.MaxWidth = double.PositiveInfinity;
            StepContentPanel.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(DemoSceneHost, 1);
            Grid.SetColumn(DemoSceneHost, 0);
            DemoSceneHost.MinHeight = 300;
            DemoSceneHost.VerticalAlignment = VerticalAlignment.Top;
            DemoDesktop.Width = 340;
            DemoDesktop.Height = 268;

            FooterNav.RowSpacing = 14;
            ProgressDots.HorizontalAlignment = HorizontalAlignment.Center;
            FooterButtons.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(ProgressDots, 0);
            Grid.SetColumn(ProgressDots, 0);
            Grid.SetColumnSpan(ProgressDots, 2);
            Grid.SetRow(FooterButtons, 1);
            Grid.SetColumn(FooterButtons, 0);
            Grid.SetColumnSpan(FooterButtons, 2);
        }
        else
        {
            MainContentGrid.ColumnSpacing = 32;
            MainContentGrid.RowSpacing = 0;
            MainContentGrid.ColumnDefinitions[0].Width = new GridLength(1.0, GridUnitType.Star);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(1.0, GridUnitType.Star);
            Grid.SetRow(StepContentPanel, 0);
            Grid.SetColumn(StepContentPanel, 0);
            StepContentPanel.MaxWidth = 420;
            StepContentPanel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(DemoSceneHost, 0);
            Grid.SetColumn(DemoSceneHost, 1);
            DemoSceneHost.MinHeight = 400;
            DemoSceneHost.VerticalAlignment = VerticalAlignment.Center;
            DemoDesktop.Width = 400;
            DemoDesktop.Height = 320;

            FooterNav.RowSpacing = 0;
            ProgressDots.HorizontalAlignment = HorizontalAlignment.Left;
            FooterButtons.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(ProgressDots, 0);
            Grid.SetColumn(ProgressDots, 0);
            Grid.SetColumnSpan(ProgressDots, 1);
            Grid.SetRow(FooterButtons, 0);
            Grid.SetColumn(FooterButtons, 1);
            Grid.SetColumnSpan(FooterButtons, 1);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex <= 0)
        {
            return;
        }

        _stepIndex--;
        RenderStep(animate: true);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex < Steps.Length - 1)
        {
            _stepIndex++;
            RenderStep(animate: true);
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

    private void BuildProgressDots()
    {
        ProgressDots.Children.Clear();
        for (int index = 0; index < Steps.Length; index++)
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

    private void RenderStep(bool animate)
    {
        StopSceneAnimations();
        _pendingSceneAnimations = null;
        _isStepTransitioning = animate;
        var step = Steps[_stepIndex];

        if (animate)
        {
            PrepareContentTransitionStartState();
        }
        else
        {
            ResetContentTransitionState();
        }

        ApplyOnboardingPalette();
        Title = _localizationService.T("Onboarding.WindowTitle");
        StepEyebrowText.Text = _localizationService.T($"{step.KeyPrefix}.Eyebrow");
        StepTitleText.Text = _localizationService.T($"{step.KeyPrefix}.Title");
        StepBodyText.Text = _localizationService.T($"{step.KeyPrefix}.Body");

        StepHintPanel.Children.Clear();
        for (int index = 1; ; index++)
        {
            string hintKey = $"{step.KeyPrefix}.Hint{index}";
            string hint = _localizationService.T(hintKey);
            if (hint == hintKey)
            {
                break;
            }

            StepHintPanel.Children.Add(CreateHintRow(hint));
        }

        StepOptionPanel.Children.Clear();
        step.BuildOptions(this);

        DemoScene.Children.Clear();
        // Never suppress scene entrance — let elements animate in with stagger
        // even during step transitions to avoid the "flash" of all elements appearing at once.
        _suppressSceneEntranceAnimation = false;
        step.BuildScene(this);

        BackButton.IsEnabled = _stepIndex > 0;
        SkipButton.Content = _localizationService.T("Onboarding.Skip");
        BackButton.Content = _localizationService.T("Onboarding.Back");
        NextButton.Content = _stepIndex == Steps.Length - 1
            ? _localizationService.T("Onboarding.Start")
            : _localizationService.T("Onboarding.Next");
        SkipButton.Visibility = _stepIndex == Steps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgressDots();

        if (animate)
        {
            PlayContentTransition(startStatePrepared: true);
        }
        else
        {
            _isStepTransitioning = false;
            _pendingSceneAnimations?.Invoke();
            _pendingSceneAnimations = null;
        }
    }

    private void OnLanguageChanged()
    {
        PrepareIntroContent();
        RenderStep(animate: false);
    }

    private void PrepareIntroContent()
    {
        IntroTitleText.Text = _localizationService.T("Onboarding.Intro.Title");
        IntroBodyText.Text = _localizationService.T("Onboarding.Intro.Body");
    }

    private async void PlayIntroSequence()
    {
        _introStoryboard?.Stop();
        int introGeneration = ++_introGeneration;
        PrepareIntroContent();
        ApplyOnboardingPalette();

        MainContentGrid.IsHitTestVisible = false;
        FooterNav.IsHitTestVisible = false;
        MainContentGrid.Opacity = 0;
        FooterNav.Opacity = 0;
        BrandLogoHost.Opacity = 0;
        IntroOverlay.Opacity = 1;
        IntroOverlay.Visibility = Visibility.Visible;
        IntroMarkHost.Children.Clear();
        SetElementTransform(IntroOverlay);
        SetElementTransform(MainContentGrid, translateY: 8, scale: 0.995);
        SetElementTransform(FooterNav, translateY: 8);
        SetElementTransform(BrandLogoHost);

        var mark = CreateDeskBoxMark(size: 170, layerWidth: 108, layerHeight: 102, cornerRadius: 18, offsetX: 25, offsetY: 20);
        IntroMarkHost.Children.Add(mark);
        SetElementTransform(IntroMarkHost);

        var backLayer = mark.Children[0];
        var middleLayer = mark.Children[1];
        var frontLayer = mark.Children[2];

        SetElementOpacity(backLayer, 0);
        SetElementOpacity(middleLayer, 0);
        SetElementOpacity(frontLayer, 0);
        SetTransformValues(backLayer, translateX: -36, translateY: -18, scale: 0.9);
        SetTransformValues(middleLayer, translateX: -14, translateY: -8, scale: 0.93);
        SetTransformValues(frontLayer, translateX: 28, translateY: 18, scale: 0.95);

        IntroTitleText.Opacity = 0;
        IntroBodyText.Opacity = 0;
        SetElementTransform(IntroTitleText, translateY: 8, scale: 1);
        SetElementTransform(IntroBodyText, translateY: 8, scale: 1);

        try
        {
            var animationTask = RunIntroAnimationAsync(introGeneration, backLayer, middleLayer, frontLayer);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            if (await Task.WhenAny(animationTask, timeoutTask) == timeoutTask)
            {
                App.Log("[Onboarding] Intro animation timed out; showing main content fallback.");
            }
            else
            {
                await animationTask;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Intro animation failed; showing main content fallback. {ex}");
            if (introGeneration == _introGeneration)
            {
                DismissIntro();
                return;
            }
        }

        if (introGeneration == _introGeneration)
        {
            DismissIntro();
        }
    }

    private async Task RunIntroAnimationAsync(
        int introGeneration,
        UIElement backLayer,
        UIElement middleLayer,
        UIElement frontLayer)
    {
        await AnimateIntroLayerAsync(introGeneration, backLayer, -36, -18, 0, 0, 0.9, 1, 380);
        await Task.Delay(70);
        if (introGeneration != _introGeneration) return;
        await AnimateIntroLayerAsync(introGeneration, middleLayer, -14, -8, 0, 0, 0.93, 1, 340);
        await Task.Delay(70);
        if (introGeneration != _introGeneration) return;
        await AnimateIntroLayerAsync(introGeneration, frontLayer, 28, 18, 0, 0, 0.95, 1, 340);
        if (introGeneration != _introGeneration) return;

        await AnimateIntroElementAsync(introGeneration, IntroTitleText, 0, 1, 0, 0, 8, 0, 1, 1, 220);
        await AnimateIntroElementAsync(introGeneration, IntroBodyText, 0, 1, 0, 0, 8, 0, 1, 1, 220);
        await Task.Delay(520);
        if (introGeneration != _introGeneration) return;

        _ = AnimateIntroElementAsync(introGeneration, IntroTitleText, 1, 0, 0, 0, 0, -6, 1, 0.98, 180);
        _ = AnimateIntroElementAsync(introGeneration, IntroBodyText, 1, 0, 0, 0, 0, -6, 1, 0.98, 180);
        _ = AnimateIntroElementAsync(introGeneration, MainContentGrid, 0, 1, 0, 0, 8, 0, 0.995, 1, 360);
        _ = AnimateIntroElementAsync(introGeneration, FooterNav, 0, 1, 0, 0, 8, 0, 1, 1, 320);
        _ = AnimateIntroElementAsync(introGeneration, BrandLogoHost, 0, 1, 0, 0, 0, 0, 1, 1, 240);
        var target = GetIntroMarkTargetTransform();
        await AnimateIntroElementAsync(
            introGeneration,
            IntroMarkHost,
            1,
            0.98,
            0,
            target.TranslateX,
            0,
            target.TranslateY,
            1,
            target.Scale,
            620);
        if (introGeneration != _introGeneration) return;

        await AnimateIntroElementAsync(introGeneration, IntroOverlay, 1, 0, 0, 0, 0, 0, 1, 1, 220);
    }

    private Task AnimateIntroLayerAsync(
        int introGeneration,
        UIElement element,
        double fromX,
        double fromY,
        double toX,
        double toY,
        double fromScale,
        double toScale,
        int milliseconds)
    {
        return AnimateIntroElementAsync(
            introGeneration,
            element,
            0,
            1,
            fromX,
            toX,
            fromY,
            toY,
            fromScale,
            toScale,
            milliseconds);
    }

    private Task AnimateIntroElementAsync(
        int introGeneration,
        UIElement element,
        double fromOpacity,
        double toOpacity,
        double fromX,
        double toX,
        double fromY,
        double toY,
        double fromScale,
        double toScale,
        int milliseconds)
    {
        var transform = GetElementTransform(element);
        transform.TranslateX = fromX;
        transform.TranslateY = fromY;
        transform.ScaleX = fromScale;
        transform.ScaleY = fromScale;
        element.Opacity = fromOpacity;

        var storyboard = new Storyboard();
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var opacityAnim = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(opacityAnim, element);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        storyboard.Children.Add(opacityAnim);

        var translateXAnim = new DoubleAnimation
        {
            From = fromX,
            To = toX,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(translateXAnim, transform);
        Storyboard.SetTargetProperty(translateXAnim, "TranslateX");
        storyboard.Children.Add(translateXAnim);

        var translateYAnim = new DoubleAnimation
        {
            From = fromY,
            To = toY,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(translateYAnim, transform);
        Storyboard.SetTargetProperty(translateYAnim, "TranslateY");
        storyboard.Children.Add(translateYAnim);

        var scaleXAnim = new DoubleAnimation
        {
            From = fromScale,
            To = toScale,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(scaleXAnim, transform);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");
        storyboard.Children.Add(scaleXAnim);

        var scaleYAnim = new DoubleAnimation
        {
            From = fromScale,
            To = toScale,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(scaleYAnim, transform);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");
        storyboard.Children.Add(scaleYAnim);

        var tcs = new TaskCompletionSource<bool>();
        void OnCompleted(object? sender, object e)
        {
            storyboard.Completed -= OnCompleted;
            if (introGeneration == _introGeneration)
            {
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetCanceled();
            }
        }
        storyboard.Completed += OnCompleted;

        _introStoryboard = storyboard;
        storyboard.Begin();

        return tcs.Task;
    }

    private void DismissIntro()
    {
        IntroOverlay.Visibility = Visibility.Collapsed;
        IntroOverlay.Opacity = 0;
        MainContentGrid.Opacity = 1;
        FooterNav.Opacity = 1;
        BrandLogoHost.Opacity = 1;
        MainContentGrid.IsHitTestVisible = true;
        FooterNav.IsHitTestVisible = true;
        SetTransformValues(IntroMarkHost);
        SetTransformValues(MainContentGrid);
        SetTransformValues(FooterNav);
        SetTransformValues(BrandLogoHost);
        _introStoryboard = null;
        PlayContentTransition();
    }

    private IntroMarkTarget GetIntroMarkTargetTransform()
    {
        try
        {
            double introWidth = IntroMarkHost.ActualWidth > 0 ? IntroMarkHost.ActualWidth : IntroMarkHost.Width;
            double introHeight = IntroMarkHost.ActualHeight > 0 ? IntroMarkHost.ActualHeight : IntroMarkHost.Height;
            double brandWidth = BrandLogoHost.ActualWidth > 0 ? BrandLogoHost.ActualWidth : BrandLogoHost.Width;
            double brandHeight = BrandLogoHost.ActualHeight > 0 ? BrandLogoHost.ActualHeight : BrandLogoHost.Height;
            var introCenter = IntroMarkHost
                .TransformToVisual(RootGrid)
                .TransformPoint(new Windows.Foundation.Point(introWidth / 2, introHeight / 2));
            var brandCenter = BrandLogoHost
                .TransformToVisual(RootGrid)
                .TransformPoint(new Windows.Foundation.Point(brandWidth / 2, brandHeight / 2));

            return new IntroMarkTarget(
                brandCenter.X - introCenter.X,
                brandCenter.Y - introCenter.Y,
                Math.Clamp(brandWidth / Math.Max(1, introWidth), 0.18, 0.28));
        }
        catch
        {
            return new IntroMarkTarget(-304, -172, 0.22);
        }
    }

    private void StartBrandLogoShine()
    {
        _brandLogoShineStoryboard?.Stop();
        BrandLogoShineTransform.TranslateX = -44;

        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        AddTransformAnimation(
            storyboard,
            BrandLogoShineTransform,
            "TranslateX",
            -44,
            66,
            1450,
            beginMs: 700,
            EasingMode.EaseInOut);
        _brandLogoShineStoryboard = storyboard;
        storyboard.Begin();
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
            dot.Fill = active
                ? AccentBrush()
                : SubtleDotBrush();
        }
    }

    // ─────────────── Step 1: Welcome ───────────────

    private void BuildWelcomeOptions()
    {
        // No interactive options — hints only
    }

    // ─────────────── Step 2: File storage & Quick Access ───────────────

    private void BuildStorageFlowOptions()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var changeButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 112,
            Content = CreateButtonContent("\uE8DA", _localizationService.T("Onboarding.Storage.ChangePath"))
        };
        changeButton.Click += ChangeStoragePathButton_Click;
        StepOptionPanel.Children.Add(CreateStoragePathActionCard(path, changeButton));

        // Pin to Quick Access toggle
        var pinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = pinState == QuickAccessPinState.Pinned;
        var pinToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = isPinned
        };
        pinToggle.Toggled += async (_, _) =>
        {
            if (pinToggle.IsOn)
            {
                string storagePath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
                var result = await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(storagePath);
                if (!result.Succeeded && RootGrid.XamlRoot is not null)
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = _localizationService.T("Onboarding.Step2.PinTitle"),
                        CloseButtonText = _localizationService.T("Common.Ok"),
                        DefaultButton = ContentDialogButton.Close,
                        Content = new TextBlock
                        {
                            Text = _localizationService.T("Onboarding.Step2.PinDescription"),
                            TextWrapping = TextWrapping.Wrap
                        }
                    };
                    await dialog.ShowAsync();
                }
            }
            else
            {
                string storagePath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
                await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(storagePath);
            }
            UpdateStorageSidebarHighlight(pinToggle.IsOn);
        };
        StepOptionPanel.Children.Add(CreateSettingToggleCard(
            "\uE718",
            _localizationService.T("Onboarding.Step2.PinTitle"),
            _localizationService.T("Onboarding.Step2.PinDescription"),
            pinToggle));
    }

    // ─────────────── Step 3: Appearance ───────────────

    private void BuildAppearanceOptions()
    {
        // Theme selector
        var themePanel = new StackPanel { Spacing = 8 };
        themePanel.Children.Add(new TextBlock
        {
            Text = _localizationService.T("Onboarding.Step3.ThemeSection"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = SecondaryTextBrush()
        });
        var themeSelector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        string[] themeKeys = { "System", "Light", "Dark" };
        string[] themeLabels = {
            _localizationService.T("Onboarding.Step3.ThemeSystem"),
            _localizationService.T("Onboarding.Step3.ThemeLight"),
            _localizationService.T("Onboarding.Step3.ThemeDark")
        };
        string currentTheme = _settingsService.Settings.Theme;
        for (int i = 0; i < themeKeys.Length; i++)
        {
            string key = themeKeys[i];
            var rb = new RadioButton
            {
                Content = themeLabels[i],
                IsChecked = string.Equals(currentTheme, key, StringComparison.OrdinalIgnoreCase),
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 4)
            };
            string capturedKey = key;
            rb.Checked += (_, _) =>
            {
                if (App.Current.ThemeService is { } ts)
                {
                    ts.SetTheme(capturedKey);
                }
                else
                {
                    _settingsService.Settings.Theme = capturedKey;
                    _settingsService.SaveDebounced();
                }
                UpdateAppearancePreview();
            };
            themeSelector.Children.Add(rb);
        }
        themePanel.Children.Add(themeSelector);
        StepOptionPanel.Children.Add(themePanel);

        // Accent color selector
        var accentPanel = new StackPanel { Spacing = 8 };
        accentPanel.Children.Add(new TextBlock
        {
            Text = _localizationService.T("Onboarding.Step3.AccentSection"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = SecondaryTextBrush()
        });
        var accentSelector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // System accent option
        bool useSystemAccent = !string.Equals(_settingsService.Settings.AccentColorMode, ThemeService.AccentModeCustom, StringComparison.OrdinalIgnoreCase);
        var systemAccentRb = new RadioButton
        {
            Content = _localizationService.T("Onboarding.Step3.UseSystemAccent"),
            IsChecked = useSystemAccent,
            MinWidth = 0,
            Padding = new Thickness(10, 4, 10, 4)
        };
        systemAccentRb.Checked += (_, _) =>
        {
            if (App.Current.ThemeService is { } ts)
            {
                ts.SetAccentMode(ThemeService.AccentModeSystem);
            }
            else
            {
                _settingsService.Settings.AccentColorMode = ThemeService.AccentModeSystem;
                _settingsService.SaveDebounced();
            }
            UpdateAppearancePreview();
        };
        accentSelector.Children.Add(systemAccentRb);

        // Preset colors
        string[] presetColors = { "#0078D4", "#E81123", "#107C10", "#5D2E9B", "#FF8C00", "#0099BC" };
        for (int i = 0; i < presetColors.Length; i++)
        {
            string colorHex = presetColors[i];
            var colorBtn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF,
                    System.Convert.ToByte(colorHex.Substring(1, 2), 16),
                    System.Convert.ToByte(colorHex.Substring(3, 2), 16),
                    System.Convert.ToByte(colorHex.Substring(5, 2), 16))),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = null
            };
            string captured = colorHex;
            colorBtn.Click += (_, _) =>
            {
                var color = Microsoft.UI.ColorHelper.FromArgb(0xFF,
                    System.Convert.ToByte(captured.Substring(1, 2), 16),
                    System.Convert.ToByte(captured.Substring(3, 2), 16),
                    System.Convert.ToByte(captured.Substring(5, 2), 16));
                if (App.Current.ThemeService is { } ts)
                {
                    ts.SetCustomAccentColor(color);
                }
                else
                {
                    _settingsService.Settings.AccentColorMode = ThemeService.AccentModeCustom;
                    _settingsService.Settings.CustomAccentColor = captured;
                    _settingsService.SaveDebounced();
                }
                systemAccentRb.IsChecked = false;
                UpdateAppearancePreview();
            };
            accentSelector.Children.Add(colorBtn);
        }
        accentPanel.Children.Add(accentSelector);
        StepOptionPanel.Children.Add(accentPanel);

        // Material selector
        var materialPanel = new StackPanel { Spacing = 8 };
        materialPanel.Children.Add(new TextBlock
        {
            Text = _localizationService.T("Onboarding.Step3.MaterialSection"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = SecondaryTextBrush()
        });
        var materialSelector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        string[] materialKeys = { "Mica", "Acrylic", "Solid" };
        string[] materialLabels = {
            _localizationService.T("Onboarding.Step3.MaterialMica"),
            _localizationService.T("Onboarding.Step3.MaterialAcrylic"),
            _localizationService.T("Onboarding.Step3.MaterialSolid")
        };
        string currentMaterial = _settingsService.Settings.WidgetMaterialType;
        for (int i = 0; i < materialKeys.Length; i++)
        {
            string key = materialKeys[i];
            var rb = new RadioButton
            {
                Content = materialLabels[i],
                IsChecked = string.Equals(currentMaterial, key, StringComparison.OrdinalIgnoreCase),
                MinWidth = 0,
                Padding = new Thickness(10, 4, 10, 4)
            };
            string capturedKey = key;
            rb.Checked += (_, _) =>
            {
                _settingsService.Settings.WidgetMaterialType = capturedKey;
                _settingsService.SaveDebounced();
                if (App.Current.ThemeService is { } ts)
                {
                    ts.RefreshAppearance();
                }
                UpdateAppearancePreview();
            };
            materialSelector.Children.Add(rb);
        }
        materialPanel.Children.Add(materialSelector);
        StepOptionPanel.Children.Add(materialPanel);
    }

    // ─────────────── Step 4: Feature widgets ───────────────

    private void BuildFeatureWidgetOptions()
    {
        BuildFeatureToggleCard(
            "\uE8FD",
            _localizationService.T("Onboarding.Step4.TodoTitle"),
            _localizationService.T("Onboarding.Step4.TodoDescription"),
            WidgetKind.Todo);

        BuildFeatureToggleCard(
            "\uE70B",
            _localizationService.T("Onboarding.Step4.QuickCaptureTitle"),
            _localizationService.T("Onboarding.Step4.QuickCaptureDescription"),
            WidgetKind.QuickCapture);

        BuildFeatureToggleCard(
            "\uE8D6",
            _localizationService.T("Onboarding.Step4.MusicTitle"),
            _localizationService.T("Onboarding.Step4.MusicDescription"),
            WidgetKind.Music);

        BuildFeatureToggleCard(
            "\uE928",
            _localizationService.T("Onboarding.Step4.WeatherTitle"),
            _localizationService.T("Onboarding.Step4.WeatherDescription"),
            WidgetKind.Weather);
    }

    private void BuildFeatureToggleCard(
        string glyph,
        string title,
        string description,
        WidgetKind kind)
    {
        bool isEnabled = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = isEnabled
        };

        var card = CreateSettingToggleCard(glyph, title, description, toggle);
        StepOptionPanel.Children.Add(card);

        toggle.Toggled += (_, _) =>
        {
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, toggle.IsOn);
            _settingsService.SaveDebounced();
            UpdateFeatureWidgetScene();
        };
    }

    // ─────────────── Step 5: Daily use ───────────────

    private void BuildDailyAccessOptions()
    {
        var hotkeyToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = _settingsService.Settings.GlobalHotkeyEnabled
        };

        // Hotkey display + change button
        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);

        _hotkeyChangeButton = new Button
        {
            MinWidth = 76,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE765", FontSize = 12, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = hotkeyText,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        _hotkeyChangeButton.Click += (_, _) => BeginHotkeyRecording();
        _hotkeyChangeButton.IsEnabled = hotkeyToggle.IsOn;

        hotkeyToggle.Toggled += (_, _) =>
        {
            if (App.Current.GlobalHotkeyService is { } globalHotkeyService)
            {
                globalHotkeyService.SetEnabled(hotkeyToggle.IsOn);
            }
            else
            {
                _settingsService.Settings.GlobalHotkeyEnabled = hotkeyToggle.IsOn;
                _settingsService.SaveDebounced();
            }
            _hotkeyChangeButton.IsEnabled = hotkeyToggle.IsOn;
            RestartHotkeyDemoAnimation(hotkeyToggle.IsOn);
        };

        var hotkeyCard = CreateSettingToggleCardWithExtra(
            "\uE765",
            _localizationService.T("Onboarding.Step5.HotkeyTitle"),
            _localizationService.T("Onboarding.Step5.HotkeyDescription"),
            hotkeyToggle,
            _hotkeyChangeButton);

        StepOptionPanel.Children.Add(hotkeyCard);

        var startupToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = StartupService.IsEnabled()
        };
        startupToggle.Toggled += (_, _) =>
        {
            StartupService.SetEnabled(startupToggle.IsOn);
            _settingsService.Settings.AutoStart = startupToggle.IsOn;
            _settingsService.SaveDebounced();
        };

        StepOptionPanel.Children.Add(CreateSettingToggleCard(
            "\uE7F4",
            _localizationService.T("Onboarding.Step5.StartupTitle"),
            _localizationService.T("Onboarding.Step5.StartupDescription"),
            startupToggle));
    }

    private void BeginHotkeyRecording()
    {
        if (_hotkeyChangeButton is null)
        {
            return;
        }

        _isRecordingHotkey = true;
        _hotkeyChangeButton.Content = _localizationService.T("Onboarding.Step5.HotkeyRecording");
        _hotkeyChangeButton.Focus(FocusState.Programmatic);
    }

    private void EndHotkeyRecording()
    {
        _isRecordingHotkey = false;
        RefreshHotkeyChangeButton();
    }

    private void RefreshHotkeyChangeButton()
    {
        if (_hotkeyChangeButton is null || _isRecordingHotkey)
        {
            return;
        }

        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);

        _hotkeyChangeButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = "\uE765", FontSize = 12, Foreground = AccentBrush() },
                new TextBlock
                {
                    Text = hotkeyText,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private async Task ApplyRecordedHotkeyAsync(DeskBox.Models.GlobalHotkeyGesture gesture)
    {
        EndHotkeyRecording();
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        if (!hotkeyService.TryApplyGesture(gesture, out string? error))
        {
            if (RootGrid.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                await dialog.ShowAsync();
            }
        }

        RefreshHotkeyChangeButton();
    }

    private static DeskBox.Models.HotkeyModifierKeys GetPressedHotkeyModifiers()
    {
        var modifiers = DeskBox.Models.HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Control;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Alt;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Shift;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Windows.System.VirtualKey key)
    {
        return key is
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift;
    }

    private void OnHotkeyKeyDown(Windows.System.VirtualKey key)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        if (key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyRecording();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var gesture = new DeskBox.Models.GlobalHotkeyGesture(
            GetPressedHotkeyModifiers(),
            (int)key);
        _ = ApplyRecordedHotkeyAsync(gesture);
    }

    // ─────────────── Step 6: Ready ───────────────

    private void BuildReadyOptions()
    {
        // Config summary
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var summaryPinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = summaryPinState == QuickAccessPinState.Pinned;

        // Row 1: Storage + Pin status
        var summaryGrid1 = CreateOptionGrid(2,
            CreateCompactInfoCard(
                "\uE8B7",
                _localizationService.T("Onboarding.Step6.SummaryStorage"),
                $"{_localizationService.T("Onboarding.Step6.SummaryPath")}: {System.IO.Path.GetFileName(path)}"),
            CreateCompactInfoCard(
                "\uE718",
                _localizationService.T("Onboarding.Step6.SummaryPinned"),
                isPinned ? _localizationService.T("Onboarding.Step6.SummaryPinned") : _localizationService.T("Onboarding.Step6.SummaryNotPinned")));
        StepOptionPanel.Children.Add(summaryGrid1);

        // Row 2: Appearance summary
        string themeLabel = _settingsService.Settings.Theme switch
        {
            "Light" => _localizationService.T("Onboarding.Step3.ThemeLight"),
            "Dark" => _localizationService.T("Onboarding.Step3.ThemeDark"),
            _ => _localizationService.T("Onboarding.Step3.ThemeSystem")
        };
        string materialLabel = _settingsService.Settings.WidgetMaterialType switch
        {
            "Acrylic" => _localizationService.T("Onboarding.Step3.MaterialAcrylic"),
            "Solid" => _localizationService.T("Onboarding.Step3.MaterialSolid"),
            _ => _localizationService.T("Onboarding.Step3.MaterialMica")
        };

        StepOptionPanel.Children.Add(CreateOptionGrid(2,
            CreateCompactInfoCard("\uE7F4", _localizationService.T("Onboarding.Step6.SummaryAppearance"), themeLabel),
            CreateCompactInfoCard("\uE7F4", _localizationService.T("Onboarding.Step6.SummaryMaterial"), materialLabel)));

        // Row 3: Feature widgets + Daily use
        var enabledWidgets = new List<string>();
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.TodoTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.QuickCaptureTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Music))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.MusicTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Weather))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step4.WeatherTitle"));
        }
        string widgetSummary = enabledWidgets.Count > 0
            ? string.Join(" · ", enabledWidgets)
            : _localizationService.T("Onboarding.Step6.NoWidgets");

        string hotkeySummary = _settingsService.Settings.GlobalHotkeyEnabled
            ? _localizationService.T("Onboarding.Step6.SummaryHotkeyOn")
            : _localizationService.T("Onboarding.Step6.SummaryHotkeyOff");
        string startupSummary = StartupService.IsEnabled()
            ? _localizationService.T("Onboarding.Step6.SummaryStartupOn")
            : _localizationService.T("Onboarding.Step6.SummaryStartupOff");

        StepOptionPanel.Children.Add(CreateOptionGrid(2,
            CreateCompactInfoCard("\uE713", _localizationService.T("Onboarding.Step6.SummaryWidgets"), widgetSummary),
            CreateCompactInfoCard("\uE765", _localizationService.T("Onboarding.Step6.SummaryDaily"), $"{hotkeySummary} · {startupSummary}")));
    }

    private async void ChangeStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        string? folderPath = FolderPickerService.PickFolder(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folderPath);
        string currentPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0 && RootGrid.XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = _localizationService.T("Settings.Dialog.MigrateTitle"),
                PrimaryButtonText = _localizationService.T("Settings.Dialog.MigrateButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.Dialog.MigrateBody",
                        affectedCount,
                        currentPath,
                        normalizedPath),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (App.Current.WidgetManager is not null)
        {
            try
            {
                await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
            }
            catch (Exception ex)
            {
                if (RootGrid.XamlRoot is not null)
                {
                    var errorDialog = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle"),
                        CloseButtonText = _localizationService.T("Common.Ok"),
                        DefaultButton = ContentDialogButton.Close,
                        Content = new TextBlock
                        {
                            Text = _localizationService.Format("Settings.Dialog.MigrateFailedBody", ex.Message),
                            TextWrapping = TextWrapping.Wrap
                        }
                    };

                    await errorDialog.ShowAsync();
                }

                return;
            }
        }

        _settingsService.Settings.DefaultManagedStorageRootPath = normalizedPath;
        _settingsService.SaveDebounced();
        RenderStep(animate: false);
    }

    // ─────────────── Scene Builders ───────────────

    private void BuildWelcomeScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        // Decorative mini files on the desktop surface
        var decorFile1 = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE8A5");
        decorFile1.Opacity = 0.5;
        decorFile1.Scale = new System.Numerics.Vector3(0.7f, 0.7f, 1);
        AddToScene(decorFile1, 16, 14);

        var decorFile2 = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE7C3");
        decorFile2.Opacity = 0.45;
        decorFile2.Scale = new System.Numerics.Vector3(0.65f, 0.65f, 1);
        AddToScene(decorFile2, 300, 18);

        // Centered widget preview with floating animation
        var fileWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody(),
            width: 168,
            height: 128);
        AddToScene(fileWidget, 116, 46);

        // Badge below the widget
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.LightLayer"));
        AddToScene(badge, 147, 196);

        PlaySceneEntrance(decorFile1, decorFile2, fileWidget, badge);

        // Gentle floating animation using Translation
        _welcomeSceneCts = new System.Threading.CancellationTokenSource();
        var token = _welcomeSceneCts.Token;
        _ = Task.Run(async () =>
        {
            int gen = _sceneAnimationGeneration;
            while (!token.IsCancellationRequested && gen == _sceneAnimationGeneration)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (gen == _sceneAnimationGeneration && fileWidget.XamlRoot is not null)
                    {
                        float y = (float)(Math.Sin(DateTime.Now.Ticks / 3000000.0) * 4);
                        fileWidget.Translation = new System.Numerics.Vector3(0, y, 0);
                    }
                });
                await Task.Delay(33, token);
            }
        }, token);
    }

    private void BuildStorageFlowScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        string path = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);

        // Stage 1: Desktop file (top center)
        var fileIcon = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE8A5");
        AddToScene(fileIcon, 159, 10);

        // Down arrow 1
        var arrow1 = CreateVerticalArrow();
        AddToScene(arrow1, 193, 70);

        // Stage 2: Widget (middle)
        var widget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody(),
            width: 160,
            height: 84);
        AddToScene(widget, 120, 98);

        // Down arrow 2
        var arrow2 = CreateVerticalArrow();
        AddToScene(arrow2, 193, 188);

        // Stage 3: Storage folder + sidebar (bottom, side by side)
        var storageCard = CreateScenePathCard(path);
        AddToScene(storageCard, 20, 222);

        // Explorer sidebar mock
        var sidebarPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Padding = new Thickness(8, 8, 8, 8)
        };
        sidebarPanel.Children.Add(new TextBlock
        {
            Text = _localizationService.T("Onboarding.Scene.QuickAccess"),
            FontSize = 10,
            Foreground = SecondaryTextBrush()
        });

        _storageSidebarItem = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x20, 0, 120, 212)),
            BorderBrush = AccentBrush(),
            BorderThickness = new Thickness(1),
            Opacity = 1.0,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "\uE718",
                        FontSize = 11,
                        Foreground = AccentBrush()
                    },
                    new TextBlock
                    {
                        Text = "DeskBox",
                        FontSize = 11,
                        Foreground = PrimaryTextBrush()
                    }
                }
            }
        };
        sidebarPanel.Children.Add(_storageSidebarItem);
        var sidebar = new Border
        {
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Width = 165,
            Child = sidebarPanel
        };
        AddToScene(sidebar, 210, 222);

        // Set initial sidebar state
        var initialPinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        UpdateStorageSidebarHighlight(initialPinState == QuickAccessPinState.Pinned);

        PlaySceneEntrance(fileIcon, arrow1, widget, arrow2, storageCard, sidebar);
    }

    private void BuildAppearanceScene()
    {
        UpdateAppearancePreview();
    }

    private void BuildFeatureWidgetScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        // File widget always shown (centered top)
        var fileWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.FileWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody(),
            width: 160,
            height: 84);
        fileWidget.Tag = "FileWidget";
        AddToScene(fileWidget, 120, 16);

        PlaySceneEntrance(fileWidget);

        // Feature widget toggle cards
        UpdateFeatureWidgetScene();
    }

    private void BuildDailyAccessScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        // Widget preview (centered)
        var widget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody(),
            width: 148,
            height: 92);
        widget.Tag = "DailyAccessWidget";
        AddToScene(widget, 126, 24);

        // Hotkey keycap
        var hotkey = CreateHotkeyKeycap();
        AddToScene(hotkey, 140, 130);

        // Show/hide badge
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.ShowHide"));
        AddToScene(badge, 143, 178);

        // Taskbar at bottom
        var taskbar = CreateTaskbar();
        AddToScene(taskbar, 0, 278);

        // Tray icon on taskbar
        var tray = CreateTrayGlyph();
        AddToScene(tray, 340, 284);

        PlaySceneEntrance(widget, hotkey, badge, taskbar, tray);

        RestartHotkeyDemoAnimation(_settingsService.Settings.GlobalHotkeyEnabled);
    }

    private void BuildReadyScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        // Empty widget with pulsing dashed border + "drag files here" hint
        var emptyBody = new Grid
        {
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "\uE8B7",
                            FontSize = 24,
                            Foreground = AccentBrush(),
                            Opacity = 0.7
                        },
                        new TextBlock
                        {
                            Text = _localizationService.T("Onboarding.Step6.EmptyWidgetHint"),
                            FontSize = 11,
                            Foreground = SecondaryTextBrush(),
                            Opacity = 0.7,
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    }
                }
            }
        };

        var emptyWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            emptyBody,
            width: 200,
            height: 128);
        AddToScene(emptyWidget, 100, 40);

        // Animated drag arrow floating toward the widget
        var dragArrow = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = AccentBrush(),
            Opacity = 0.9,
            Child = new FontIcon
            {
                Glyph = "\uE8A5",
                FontSize = 18,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AddToScene(dragArrow, 310, 70);

        PlaySceneEntrance(emptyWidget, dragArrow);

        // Pulsing drag arrow animation
        _readySceneCts = new System.Threading.CancellationTokenSource();
        var token = _readySceneCts.Token;
        _ = Task.Run(async () =>
        {
            int gen = _sceneAnimationGeneration;
            double t = 0;
            while (!token.IsCancellationRequested && gen == _sceneAnimationGeneration)
            {
                t += 0.03;
                double cycle = (Math.Sin(t) + 1) / 2; // 0..1
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (gen == _sceneAnimationGeneration && dragArrow.XamlRoot is not null)
                    {
                        // Move arrow left toward widget, then reset
                        float x = (float)(-cycle * 12);
                        dragArrow.Translation = new System.Numerics.Vector3(x, 0, 0);
                        dragArrow.Opacity = 0.5 + cycle * 0.4;
                    }
                });
                await Task.Delay(33, token);
            }
        }, token);
    }

    // ─────────────── Scene Linkage Helpers ───────────────

    private void UpdateStorageSidebarHighlight(bool isPinned)
    {
        if (_storageSidebarItem is null)
        {
            return;
        }

        if (isPinned)
        {
            _storageSidebarItem.Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0x28, 0, 120, 212));
            _storageSidebarItem.BorderBrush = AccentBrush();
            _storageSidebarItem.BorderThickness = new Thickness(1);
            _storageSidebarItem.Opacity = 1.0;
        }
        else
        {
            _storageSidebarItem.Background = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0x10, 128, 128, 128));
            _storageSidebarItem.BorderBrush = new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0x20, 128, 128, 128));
            _storageSidebarItem.BorderThickness = new Thickness(1);
            _storageSidebarItem.Opacity = 0.5;
        }
    }

    private void UpdateAppearancePreview()
    {
        if (_appearancePreviewWidget is null)
        {
            return;
        }

        ApplyOnboardingPalette();
        DemoScene.Children.Clear();
        DemoScene.Children.Add(CreateDesktopSurface());

        _appearancePreviewWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody(),
            width: 180,
            height: 136);
        AddToScene(_appearancePreviewWidget, 110, 30);

        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(5),
            Background = AccentBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1)
        };
        AddToScene(swatch, 110, 190);

        var previewLabel = new TextBlock
        {
            Text = _localizationService.T("Onboarding.Scene.PreviewHint"),
            FontSize = 11,
            Foreground = SecondaryTextBrush()
        };
        AddToScene(previewLabel, 132, 192);

        var dotRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        string[] dotColors = { "#0078D4", "#E81123", "#107C10", "#5D2E9B", "#FF8C00" };
        foreach (string c in dotColors)
        {
            dotRow.Children.Add(new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF,
                    System.Convert.ToByte(c.Substring(1, 2), 16),
                    System.Convert.ToByte(c.Substring(3, 2), 16),
                    System.Convert.ToByte(c.Substring(5, 2), 16)))
            });
        }
        AddToScene(dotRow, 130, 225);

        PlaySceneEntrance(_appearancePreviewWidget, swatch, previewLabel, dotRow);
    }

    private void UpdateFeatureWidgetScene()
    {
        // Remove existing feature widget cards (keep desktop surface and file widget)
        var toRemove = DemoScene.Children.OfType<Border>()
            .Where(b => b.Tag is string tag && tag.StartsWith("FeatureWidget:"))
            .ToList();
        foreach (var item in toRemove)
        {
            DemoScene.Children.Remove(item);
        }

        // 2x2 grid of feature toggle indicator cards
        var features = new[]
        {
            (WidgetKind.Todo, "\uE8FD", _localizationService.T("Onboarding.Step4.TodoTitle"), 36, 116),
            (WidgetKind.Music, "\uE8D6", _localizationService.T("Onboarding.Step4.MusicTitle"), 216, 116),
            (WidgetKind.QuickCapture, "\uE70B", _localizationService.T("Onboarding.Step4.QuickCaptureTitle"), 36, 192),
            (WidgetKind.Weather, "\uE928", _localizationService.T("Onboarding.Step4.WeatherTitle"), 216, 192)
        };

        var newElements = new List<UIElement>();

        foreach (var (kind, glyph, title, x, y) in features)
        {
            bool isEnabled = FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
            var card = CreateFeatureToggleIndicator(glyph, title, isEnabled);
            card.Tag = $"FeatureWidget:{kind}";
            AddToScene(card, x, y);
            newElements.Add(card);
        }

        if (newElements.Count > 0)
        {
            PlaySceneEntrance(newElements.ToArray());
        }
    }

    private void RestartHotkeyDemoAnimation(bool enabled)
    {
        _hotkeyDemoCts?.Cancel();
        if (!enabled)
        {
            return;
        }

        _hotkeyDemoCts = new System.Threading.CancellationTokenSource();
        var token = _hotkeyDemoCts.Token;
        _ = Task.Run(async () =>
        {
            int gen = _sceneAnimationGeneration;
            while (!token.IsCancellationRequested && gen == _sceneAnimationGeneration)
            {
                // Find the widget tagged "DailyAccessWidget" on the UI thread
                Border? target = null;
                bool found = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (gen == _sceneAnimationGeneration)
                    {
                        target = DemoScene.Children.OfType<Border>()
                            .FirstOrDefault(b => b.Tag is string t && t == "DailyAccessWidget");
                        found = target is not null;
                    }
                });
                await Task.Delay(16, token);

                if (!found || gen != _sceneAnimationGeneration)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                var capturedTarget = target;
                if (capturedTarget is null)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                // Fade out (hide)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (gen == _sceneAnimationGeneration && capturedTarget.XamlRoot is not null)
                    {
                        capturedTarget.Opacity = 0.15;
                        capturedTarget.Translation = new System.Numerics.Vector3(0, 6, 0);
                    }
                });

                await Task.Delay(500, token);
                if (gen != _sceneAnimationGeneration) break;

                // Fade in (show)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (gen == _sceneAnimationGeneration && capturedTarget.XamlRoot is not null)
                    {
                        capturedTarget.Opacity = 1.0;
                        capturedTarget.Translation = System.Numerics.Vector3.Zero;
                    }
                });

                await Task.Delay(1500, token);
            }
        }, token);
    }

    private UIElement CreateHintRow(string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 13,
            Foreground = AccentBrush()
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = SecondaryTextBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        return panel;
    }

    private Border CreateInfoCard(string glyph, string title, string description, bool wrapDescription = true)
    {
        return CreateOptionCard(CreateInlineOptionContent(glyph, title, description, wrapDescription));
    }

    private Border CreateCompactInfoCard(string glyph, string title, string description)
    {
        return CreateOptionCard(
            CreateInlineOptionContent(
                glyph,
                title,
                description,
                wrapDescription: true,
                compact: true,
                maxDescriptionLines: 2),
            compact: true);
    }

    private Border CreateCompactTileCard(string glyph, string title, string description)
    {
        var content = new StackPanel
        {
            Spacing = 5,
            MinHeight = 72
        };

        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 18,
            Foreground = AccentBrush(),
            HorizontalAlignment = HorizontalAlignment.Left
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = SecondaryTextBrush(),
            LineHeight = 16,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.WrapWholeWords
        });

        return CreateOptionCard(content, compact: true);
    }

    private Grid CreateOptionGrid(int columnCount, params Border[] cards)
    {
        var grid = new Grid
        {
            ColumnSpacing = columnCount >= 3 ? 8 : 10,
            RowSpacing = 8
        };

        for (int index = 0; index < columnCount; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int rowCount = (int)Math.Ceiling(cards.Length / (double)columnCount);
        for (int index = 0; index < rowCount; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int index = 0; index < cards.Length; index++)
        {
            cards[index].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(cards[index], index % columnCount);
            Grid.SetRow(cards[index], index / columnCount);
            grid.Children.Add(cards[index]);
        }

        return grid;
    }

    private Border CreateStoragePathActionCard(string path, Button actionButton)
    {
        actionButton.VerticalAlignment = VerticalAlignment.Center;

        var content = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(24) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        content.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 16,
            Foreground = AccentBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = _localizationService.T("Onboarding.Storage.CurrentPath"),
                    FontSize = 12.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = path,
                    FontSize = 11.5,
                    Foreground = SecondaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                }
            }
        };
        Grid.SetColumn(textStack, 1);
        content.Children.Add(textStack);

        Grid.SetColumn(actionButton, 2);
        content.Children.Add(actionButton);

        return CreateOptionCard(content, compact: true);
    }

    private Border CreateSettingToggleCard(string glyph, string title, string description, ToggleSwitch toggle, bool compact = false)
    {
        return CreateSettingToggleCardWithExtra(glyph, title, description, toggle, null, compact);
    }

    private Border CreateSettingToggleCardWithExtra(
        string glyph,
        string title,
        string description,
        ToggleSwitch toggle,
        UIElement? extra,
        bool compact = false)
    {
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        toggle.MinWidth = 86;

        var content = new Grid
        {
            ColumnSpacing = compact ? 10 : 12,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(compact ? 22 : 26) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var iconHost = new Grid
        {
            Width = compact ? 22 : 26,
            Height = compact ? 22 : 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = compact ? 15 : 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentBrush()
        });
        content.Children.Add(iconHost);

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = compact ? 13 : 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                },
                new TextBlock
                {
                    Text = description,
                    FontSize = compact ? 11.5 : 12.5,
                    Foreground = SecondaryTextBrush(),
                    MaxLines = compact ? 1 : 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.WrapWholeWords
                }
            }
        };
        Grid.SetColumn(textStack, 1);
        content.Children.Add(textStack);

        if (extra is not null)
        {
            if (extra is FrameworkElement fe)
            {
                Grid.SetColumn(fe, 2);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            content.Children.Add(extra);
        }

        Grid.SetColumn(toggle, 3);
        content.Children.Add(toggle);

        return CreateOptionCard(content, compact);
    }

    private Border CreateOptionCard(UIElement content, bool compact = false)
    {
        return new Border
        {
            Padding = compact ? new Thickness(10) : new Thickness(12),
            Background = OptionCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
    }

    private Grid CreateInlineOptionContent(
        string glyph,
        string title,
        string description,
        bool wrapDescription = true,
        bool compact = false,
        int maxDescriptionLines = 0)
    {
        var textStack = new StackPanel
        {
            Spacing = compact ? 1 : 2,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = compact ? 170 : 330
        };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = compact ? 13 : 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = compact ? 11.5 : 12.5,
            Foreground = SecondaryTextBrush(),
            LineHeight = compact ? 16 : 18,
            MaxLines = maxDescriptionLines > 0 ? maxDescriptionLines : 0,
            TextTrimming = wrapDescription ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = wrapDescription ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap
        });

        var content = new Grid
        {
            ColumnSpacing = compact ? 8 : 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(compact ? 22 : 26) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };

        var iconHost = new Grid
        {
            Width = compact ? 22 : 26,
            Height = compact ? 22 : 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = compact ? 16 : 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentBrush()
        });
        content.Children.Add(iconHost);

        Grid.SetColumn(textStack, 1);
        content.Children.Add(textStack);
        return content;
    }

    private StackPanel CreateButtonContent(string glyph, string text)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 14 },
                new TextBlock { Text = text }
            }
        };
    }

    private Border CreateDesktopSurface()
    {
        var palette = GetPalette();
        var surfaceColor = palette.SceneSurface;
        // Subtle vertical gradient for depth
        var gradientBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        gradientBrush.GradientStops.Add(new GradientStop
        {
            Offset = 0,
            Color = Microsoft.UI.ColorHelper.FromArgb(
                surfaceColor.A,
                (byte)Math.Min(255, surfaceColor.R + 6),
                (byte)Math.Min(255, surfaceColor.G + 6),
                (byte)Math.Min(255, surfaceColor.B + 8))
        });
        gradientBrush.GradientStops.Add(new GradientStop
        {
            Offset = 1,
            Color = surfaceColor
        });

        return new Border
        {
            Width = 400,
            Height = 320,
            Background = gradientBrush,
            CornerRadius = new CornerRadius(10)
        };
    }

    private Canvas CreateDeskBoxMark(
        double size = 130,
        double layerWidth = 82,
        double layerHeight = 78,
        double cornerRadius = 14,
        double offsetX = 18,
        double offsetY = 14)
    {
        var canvas = new Canvas
        {
            Width = size,
            Height = size
        };

        canvas.Children.Add(CreateDeskBoxMarkLayer(0, layerWidth, layerHeight, cornerRadius, offsetX, offsetY));
        canvas.Children.Add(CreateDeskBoxMarkLayer(1, layerWidth, layerHeight, cornerRadius, offsetX, offsetY));
        canvas.Children.Add(CreateDeskBoxMarkLayer(2, layerWidth, layerHeight, cornerRadius, offsetX, offsetY));
        return canvas;
    }

    private Border CreateDeskBoxMarkLayer(
        int index,
        double layerWidth,
        double layerHeight,
        double cornerRadius,
        double offsetX,
        double offsetY)
    {
        Color[] colors =
        [
            ColorHelper.FromArgb(0xFF, 0x0B, 0x64, 0xBF),
            ColorHelper.FromArgb(0xFF, 0x16, 0x91, 0xE8),
            ColorHelper.FromArgb(0xFF, 0x58, 0xAA, 0xFE)
        ];

        var layer = new Border
        {
            Width = layerWidth,
            Height = layerHeight,
            Background = BrushFromColor(colors[index]),
            BorderBrush = BrushFromColor(ColorHelper.FromArgb(0x42, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };

        layer.RenderTransform = new CompositeTransform
        {
            SkewY = -12
        };

        Canvas.SetLeft(layer, 14 + index * offsetX);
        Canvas.SetTop(layer, 14 + index * offsetY);
        return layer;
    }

    private Border CreateHotkeyKeycap()
    {
        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);

        return new Border
        {
            MinWidth = 108,
            Height = 38,
            Padding = new Thickness(12, 0, 12, 0),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = "\uE765", FontSize = 15, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = hotkeyText,
                        FontSize = 13,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateMiniWidgetPreview(string title, string glyph, UIElement body, double width = 148, double height = 128)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid
        {
            Margin = new Thickness(12, 0, 12, 0),
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = AccentBrush(),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);
        grid.Children.Add(header);

        var bodyHost = new Grid
        {
            Padding = new Thickness(12, 2, 12, 12),
            Children = { body }
        };
        Grid.SetRow(bodyHost, 1);
        grid.Children.Add(bodyHost);

        return new Border
        {
            Width = width,
            Height = height,
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = grid
        };
    }

    private Grid CreateFileWidgetPreviewBody()
    {
        var files = new Grid
        {
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        for (int row = 0; row < 2; row++)
        {
            files.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (int column = 0; column < 3; column++)
        {
            files.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        string[] glyphs = ["\uE8A5", "\uE8B7", "\uE7C3", "\uE8A5", "\uE8B7", "\uE7C3"];
        for (int index = 0; index < glyphs.Length; index++)
        {
            var icon = CreateTinyIcon(glyphs[index]);
            Grid.SetRow(icon, index / 3);
            Grid.SetColumn(icon, index % 3);
            files.Children.Add(icon);
        }

        return files;
    }

    private StackPanel CreateTinyIcon(string glyph)
    {
        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 3,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 18, Foreground = PrimaryTextBrush() },
                new Border { Width = 24, Height = 4, Background = TertiaryTextBrush(), CornerRadius = new CornerRadius(2) }
            }
        };
    }

    private Border CreateMiniFile(string label, string glyph)
    {
        return new Border
        {
            Width = 82,
            Height = 58,
            Padding = new Thickness(8),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 21, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 10.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateVerticalArrow()
    {
        return new Border
        {
            Width = 16,
            Height = 32,
            Opacity = 0.6,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 2,
                Children =
                {
                    new Border
                    {
                        Width = 2,
                        Height = 20,
                        Background = AccentBrush(),
                        CornerRadius = new CornerRadius(1)
                    },
                    new FontIcon
                    {
                        Glyph = "\uE70D",
                        FontSize = 11,
                        Foreground = AccentBrush(),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
    }

    private Border CreateFeatureToggleIndicator(string glyph, string title, bool isEnabled)
    {
        var accentBrush = AccentBrush();
        var strokeBrush = isEnabled ? accentBrush : StrokeBrush();
        double opacity = isEnabled ? 1.0 : 0.45;

        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 12.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                },
                new TextBlock
                {
                    Text = isEnabled ? "✓" : "—",
                    FontSize = 10.5,
                    Foreground = isEnabled ? accentBrush : SecondaryTextBrush()
                }
            }
        };
        Grid.SetColumn(textPanel, 1);

        return new Border
        {
            Width = 148,
            Height = 60,
            Padding = new Thickness(10, 6, 10, 6),
            Background = isEnabled
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x20, 0, 120, 212))
                : SceneCardBrush(),
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Opacity = opacity,
            Child = new Grid
            {
                ColumnSpacing = 10,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children =
                {
                    new Border
                    {
                        Width = 28,
                        Height = 28,
                        CornerRadius = new CornerRadius(7),
                        Background = isEnabled
                            ? accentBrush
                            : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x10, 128, 128, 128)),
                        Child = new FontIcon
                        {
                            Glyph = glyph,
                            FontSize = 16,
                            Foreground = isEnabled
                                ? new SolidColorBrush(Colors.White)
                                : SecondaryTextBrush(),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    },
                    textPanel
                }
            }
        };
    }

    private Border CreatePathCard(string path)
    {
        return new Border
        {
            Width = 280,
            Height = 54,
            Padding = new Thickness(12, 8, 12, 8),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = _localizationService.T("Onboarding.Storage.CurrentPath"),
                        FontSize = 11,
                        Foreground = SecondaryTextBrush()
                    },
                    new TextBlock
                    {
                        Text = path,
                        FontSize = 12.5,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateScenePathCard(string path)
    {
        return new Border
        {
            Width = 165,
            Height = 56,
            Padding = new Thickness(10, 8, 10, 8),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = _localizationService.T("Onboarding.Storage.CurrentPath"),
                        FontSize = 9.5,
                        Foreground = SecondaryTextBrush()
                    },
                    new TextBlock
                    {
                        Text = path,
                        FontSize = 10.5,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap
                    }
                }
            }
        };
    }

    private Border CreateBadge(string text)
    {
        return new Border
        {
            MinWidth = 92,
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Background = AccentBrush(),
            CornerRadius = new CornerRadius(16),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
    }

    private Border CreateTaskbar()
    {
        return new Border
        {
            Width = 400,
            Height = 42,
            Background = TaskbarBrush(),
            CornerRadius = new CornerRadius(0, 0, 10, 10)
        };
    }

    private Border CreateTrayGlyph()
    {
        return new Border
        {
            Width = 34,
            Height = 26,
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new FontIcon
            {
                Glyph = "\uE77B",
                FontSize = 15,
                Foreground = AccentBrush()
            }
        };
    }

    private void AddToScene(UIElement element, double left, double top)
    {
        element.SetValue(Canvas.LeftProperty, left);
        element.SetValue(Canvas.TopProperty, top);
        if (DemoScene is Canvas canvas)
        {
            canvas.Children.Add(element);
            return;
        }

        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        DemoScene.Children.Add(new Canvas
        {
            Width = 400,
            Height = 320,
            Children = { element }
        });
    }

    private void StopSceneAnimations()
    {
        _sceneAnimationGeneration++;
        _contentTransitionStoryboard?.Stop();
        _contentTransitionStoryboard = null;
        _sceneEntranceStoryboard?.Stop();
        _sceneEntranceStoryboard = null;
        _hotkeyDemoCts?.Cancel();
        _hotkeyDemoCts?.Dispose();
        _hotkeyDemoCts = null;
        _welcomeSceneCts?.Cancel();
        _welcomeSceneCts?.Dispose();
        _welcomeSceneCts = null;
        _readySceneCts?.Cancel();
        _readySceneCts?.Dispose();
        _readySceneCts = null;
    }

    private void PlaySceneEntrance(params UIElement[] elements)
    {
        if (_suppressSceneEntranceAnimation)
        {
            CompleteSceneEntrance(elements);
            return;
        }

        int animationGeneration = _sceneAnimationGeneration;

        try
        {
            for (int index = 0; index < elements.Length; index++)
            {
                var element = elements[index];
                int beginMs = 100 + (index * 90);
                SetElementOpacity(element, 0);
                element.Translation = new System.Numerics.Vector3(22, 20, 0);
                element.Scale = new System.Numerics.Vector3(0.92f, 0.92f, 1);

                AnimationBuilder.Create()
                    .Opacity(
                        1,
                        from: 0,
                        delay: TimeSpan.FromMilliseconds(beginMs),
                        duration: TimeSpan.FromMilliseconds(480),
                        easingType: EasingType.Cubic,
                        easingMode: EasingMode.EaseOut)
                    .Translation(
                        new System.Numerics.Vector3(0, 0, 0),
                        from: new System.Numerics.Vector3(22, 20, 0),
                        delay: TimeSpan.FromMilliseconds(beginMs),
                        duration: TimeSpan.FromMilliseconds(580),
                        easingType: EasingType.Cubic,
                        easingMode: EasingMode.EaseOut)
                    .Scale(
                        new System.Numerics.Vector3(1, 1, 1),
                        from: new System.Numerics.Vector3(0.92f, 0.92f, 1),
                        delay: TimeSpan.FromMilliseconds(beginMs),
                        duration: TimeSpan.FromMilliseconds(580),
                        easingType: EasingType.Cubic,
                        easingMode: EasingMode.EaseOut)
                    .Start(element);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Scene entrance animation failed; showing scene fallback. {ex}");
            CompleteSceneEntrance(elements);
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            int timeoutMs = Math.Min(1500, 200 + (elements.Length * 90) + 600);
            await Task.Delay(timeoutMs);
            if (animationGeneration == _sceneAnimationGeneration &&
                elements.Any(element => element.Opacity <= 0.01))
            {
                App.Log("[Onboarding] Scene entrance animation timed out; showing scene fallback.");
                CompleteSceneEntrance(elements);
            }
        });
    }

    private static void CompleteSceneEntrance(IEnumerable<UIElement> elements)
    {
        foreach (var element in elements)
        {
            SetElementOpacity(element, 1);
            element.Translation = System.Numerics.Vector3.Zero;
            element.Scale = System.Numerics.Vector3.One;
            SetTransformValues(element);
        }
    }

    private static void CompleteSceneTransition(
        IEnumerable<UIElement> visibleElements,
        IEnumerable<UIElement>? hiddenElements)
    {
        foreach (var element in visibleElements)
        {
            SetElementOpacity(element, 1);
            SetTransformValues(element);
        }

        if (hiddenElements is null)
        {
            return;
        }

        foreach (var element in hiddenElements)
        {
            SetElementOpacity(element, 0);
            SetTransformValues(element);
        }
    }

    private static KeySpline CreateSceneEntranceEase()
    {
        return new KeySpline
        {
            ControlPoint1 = new Windows.Foundation.Point(0.0, 0.0),
            ControlPoint2 = new Windows.Foundation.Point(0.58, 1.0)
        };
    }

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


    private void PrepareContentTransitionStartState()
    {
        SetElementOpacity(StepTitleText, 0);
        SetElementOpacity(StepBodyText, 0);
        SetElementOpacity(StepHintPanel, 0);
        SetElementOpacity(StepOptionPanel, 0);
        // Don't hide DemoScene/DemoDesktop — scene entrance handles element animations
        SetElementOpacity(DemoDesktop, 1);
        SetElementOpacity(DemoScene, 1);

        SetElementTransform(DemoDesktop, translateX: 0, translateY: 0, scale: 1);
        SetElementTransform(StepTitleText, translateY: 8, scale: 1);
        SetElementTransform(StepBodyText, translateY: 8, scale: 1);
        SetElementTransform(StepHintPanel, translateY: 8, scale: 1);
        SetElementTransform(StepOptionPanel, translateY: 10, scale: 0.99);
        SetCanvasTranslateX(DemoScene, 0);
    }

    private void ResetContentTransitionState()
    {
        SetElementOpacity(StepTitleText, 1);
        SetElementOpacity(StepBodyText, 1);
        SetElementOpacity(StepHintPanel, 1);
        SetElementOpacity(StepOptionPanel, 1);
        SetElementOpacity(DemoDesktop, 1);
        SetElementOpacity(DemoScene, 1);

        SetTransformValues(DemoDesktop);
        SetTransformValues(StepTitleText);
        SetTransformValues(StepBodyText);
        SetTransformValues(StepHintPanel);
        SetTransformValues(StepOptionPanel);
        SetCanvasTranslateX(DemoScene, 0);
    }

    private void PlayContentTransition(bool startStatePrepared = false)
    {
        _contentTransitionStoryboard?.Stop();
        if (!startStatePrepared)
        {
            PrepareContentTransitionStartState();
        }

        // Only animate left panel — right side is handled by scene entrance
        var storyboard = new Storyboard();
        AddOpacityAnimation(storyboard, StepTitleText, from: 0, to: 1, milliseconds: 260);
        AddTranslateYAnimation(storyboard, GetElementTransform(StepTitleText), from: 8, to: 0, milliseconds: 340);
        AddOpacityAnimation(storyboard, StepBodyText, from: 0, to: 1, milliseconds: 280, beginMs: 45);
        AddTranslateYAnimation(storyboard, GetElementTransform(StepBodyText), from: 8, to: 0, milliseconds: 360, beginMs: 45);
        AddOpacityAnimation(storyboard, StepHintPanel, from: 0, to: 1, milliseconds: 260, beginMs: 80);
        AddTranslateYAnimation(storyboard, GetElementTransform(StepHintPanel), from: 8, to: 0, milliseconds: 360, beginMs: 80);
        AddOpacityAnimation(storyboard, StepOptionPanel, from: 0, to: 1, milliseconds: 310, beginMs: 115);
        AddTranslateYAnimation(storyboard, GetElementTransform(StepOptionPanel), from: 10, to: 0, milliseconds: 430, beginMs: 115);
        AddScaleAnimation(storyboard, GetElementTransform(StepOptionPanel), from: 0.99, to: 1, milliseconds: 430, beginMs: 115);
        _contentTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_contentTransitionStoryboard, storyboard))
            {
                ResetContentTransitionState();
                _contentTransitionStoryboard = null;
                _isStepTransitioning = false;
                _pendingSceneAnimations?.Invoke();
                _pendingSceneAnimations = null;
            }
        };
        storyboard.Begin();
    }

    private static void AddOpacityAnimation(
        Storyboard storyboard,
        DependencyObject target,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        bool autoReverse = false,
        EasingMode easingMode = EasingMode.EaseOut,
        bool useSceneEntranceEase = false)
    {
        Timeline animation;
        if (useSceneEntranceEase)
        {
            var keyFrame = new SplineDoubleKeyFrame
            {
                KeySpline = CreateSceneEntranceEase(),
                KeyTime = TimeSpan.FromMilliseconds(milliseconds)
            };
            keyFrame.Value = to;
            animation = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                AutoReverse = autoReverse,
                KeyFrames = { new EasingDoubleKeyFrame { Value = from, KeyTime = TimeSpan.Zero }, keyFrame }
            };
        }
        else
        {
            animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                AutoReverse = autoReverse,
                EasingFunction = new CubicEase { EasingMode = easingMode }
            };
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private static void AddTranslateXAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        EasingMode easingMode = EasingMode.EaseOut,
        bool autoReverse = false,
        bool useSceneEntranceEase = false)
    {
        AddTransformAnimation(storyboard, transform, "TranslateX", from, to, milliseconds, beginMs, easingMode, autoReverse, useSceneEntranceEase);
    }

    private static void AddTranslateYAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        EasingMode easingMode = EasingMode.EaseOut,
        bool autoReverse = false,
        bool useSceneEntranceEase = false)
    {
        AddTransformAnimation(storyboard, transform, "TranslateY", from, to, milliseconds, beginMs, easingMode, autoReverse, useSceneEntranceEase);
    }

    private static void AddScaleAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        bool autoReverse = false,
        EasingMode easingMode = EasingMode.EaseOut,
        bool useSceneEntranceEase = false)
    {
        AddTransformAnimation(storyboard, transform, "ScaleX", from, to, milliseconds, beginMs, easingMode, autoReverse, useSceneEntranceEase);
        AddTransformAnimation(storyboard, transform, "ScaleY", from, to, milliseconds, beginMs, easingMode, autoReverse, useSceneEntranceEase);
    }

    private static void AddTransformAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        string property,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        EasingMode easingMode = EasingMode.EaseOut,
        bool autoReverse = false,
        bool useSceneEntranceEase = false)
    {
        Timeline animation;
        if (useSceneEntranceEase)
        {
            var keyFrame = new SplineDoubleKeyFrame
            {
                KeySpline = CreateSceneEntranceEase(),
                KeyTime = TimeSpan.FromMilliseconds(milliseconds)
            };
            keyFrame.Value = to;
            animation = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                AutoReverse = autoReverse,
                KeyFrames = { new EasingDoubleKeyFrame { Value = from, KeyTime = TimeSpan.Zero }, keyFrame }
            };
        }
        else
        {
            animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                AutoReverse = autoReverse,
                EasingFunction = new CubicEase { EasingMode = easingMode }
            };
        }

        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static void SetElementOpacity(UIElement element, double opacity)
    {
        element.Opacity = opacity;
    }

    private static void SetCanvasTranslateX(UIElement element, double translateX)
    {
        var transform = GetElementTransform(element);
        transform.TranslateX = translateX;
        transform.TranslateY = 0;
        transform.ScaleX = 1;
        transform.ScaleY = 1;
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

    private void ApplyOnboardingPalette()
    {
        var palette = GetPalette();
        DemoDesktop.Background = BrushFromColor(palette.SceneSurface);
        DemoDesktop.BorderBrush = BrushFromColor(palette.Stroke);
    }

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

    private Brush PrimaryTextBrush() => BrushFromColor(GetPalette().PrimaryText);

    private Brush SecondaryTextBrush() => BrushFromColor(GetPalette().SecondaryText);

    private Brush TertiaryTextBrush() => BrushFromColor(GetPalette().TertiaryText);

    private Brush OptionCardBrush() => BrushFromColor(GetPalette().OptionCard);

    private Brush SceneSurfaceBrush() => BrushFromColor(GetPalette().SceneSurface);

    private Brush SceneCardBrush() => BrushFromColor(GetPalette().SceneCard);

    private Brush StrokeBrush() => BrushFromColor(GetPalette().Stroke);

    private Brush TaskbarBrush() => BrushFromColor(GetPalette().Taskbar);

    private Brush SubtleDotBrush() => BrushFromColor(GetPalette().SubtleDot);

    private Brush AccentBrush()
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        return BrushFromColor(accentColor);
    }

    private OnboardingPalette GetPalette()
    {
        return IsDarkTheme()
            ? new OnboardingPalette(
                ColorHelper.FromArgb(0xFF, 0xF6, 0xF7, 0xFB),
                ColorHelper.FromArgb(0xFF, 0xC8, 0xCF, 0xDA),
                ColorHelper.FromArgb(0xFF, 0x83, 0x8B, 0x98),
                ColorHelper.FromArgb(0xFF, 0x22, 0x25, 0x2D),
                ColorHelper.FromArgb(0xFF, 0x18, 0x1B, 0x22),
                ColorHelper.FromArgb(0xFF, 0x26, 0x2A, 0x33),
                ColorHelper.FromArgb(0xFF, 0x3A, 0x40, 0x4A),
                ColorHelper.FromArgb(0xFF, 0x20, 0x23, 0x2B),
                ColorHelper.FromArgb(0xFF, 0x68, 0x72, 0x80))
            : new OnboardingPalette(
                ColorHelper.FromArgb(0xFF, 0x1B, 0x1F, 0x27),
                ColorHelper.FromArgb(0xFF, 0x55, 0x5E, 0x6E),
                ColorHelper.FromArgb(0xFF, 0xA7, 0xAF, 0xBC),
                ColorHelper.FromArgb(0xFF, 0xF7, 0xF9, 0xFC),
                ColorHelper.FromArgb(0xFF, 0xFC, 0xFD, 0xFF),
                ColorHelper.FromArgb(0xFF, 0xF1, 0xF4, 0xF8),
                ColorHelper.FromArgb(0xFF, 0xD8, 0xDF, 0xEA),
                ColorHelper.FromArgb(0xFF, 0xEA, 0xEF, 0xF6),
                ColorHelper.FromArgb(0xFF, 0xC6, 0xD0, 0xDE));
    }

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

    private sealed record OnboardingPalette(
        Color PrimaryText,
        Color SecondaryText,
        Color TertiaryText,
        Color OptionCard,
        Color SceneSurface,
        Color SceneCard,
        Color Stroke,
        Color Taskbar,
        Color SubtleDot);
}
