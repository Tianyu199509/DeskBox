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
    private const int DesiredWindowWidth = 960;
    private const int DesiredWindowHeight = 700;
    private const int MinWindowWidth = 620;
    private const int MinWindowHeight = 500;
    private const int WindowWorkAreaMargin = 96;
    private const int CompactLayoutThreshold = 820;
    private static readonly UIntPtr OnboardingWindowSubclassId = new(0xD05C0B01);

    private sealed record OnboardingStep(
        string KeyPrefix,
        Action<OnboardingWindow> BuildOptions,
        Action<OnboardingWindow> BuildScene);

    private sealed record IntroMarkTarget(double TranslateX, double TranslateY, double Scale);

    private static readonly OnboardingStep[] Steps =
    [
        new("Onboarding.Step1", window => window.BuildOverviewOptions(), window => window.BuildOverviewScene()),
        new("Onboarding.Step2", window => window.BuildFileWidgetOptions(), window => window.BuildFileWidgetScene()),
        new("Onboarding.Step3", window => window.BuildFeatureWidgetOptions(), window => window.BuildFeatureWidgetScene()),
        new("Onboarding.Step4", window => window.BuildDailyAccessOptions(), window => window.BuildDailyAccessScene()),
        new("Onboarding.Step5", window => window.BuildSafetyOptions(), window => window.BuildSafetyScene())
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
    private readonly Win32Helper.SubclassProc _windowSubclassProc;

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
            DemoSceneHost.MinHeight = 260;
            DemoSceneHost.VerticalAlignment = VerticalAlignment.Top;
            DemoDesktop.Width = 300;
            DemoDesktop.Height = 234;

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
            MainContentGrid.ColumnDefinitions[0].Width = new GridLength(1.05, GridUnitType.Star);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0.95, GridUnitType.Star);
            Grid.SetRow(StepContentPanel, 0);
            Grid.SetColumn(StepContentPanel, 0);
            StepContentPanel.MaxWidth = 410;
            StepContentPanel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(DemoSceneHost, 0);
            Grid.SetColumn(DemoSceneHost, 1);
            DemoSceneHost.MinHeight = 330;
            DemoSceneHost.VerticalAlignment = VerticalAlignment.Center;
            DemoDesktop.Width = 320;
            DemoDesktop.Height = 250;

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
        for (int index = 1; index <= 2; index++)
        {
            StepHintPanel.Children.Add(CreateHintRow(_localizationService.T($"{step.KeyPrefix}.Hint{index}")));
        }

        StepOptionPanel.Children.Clear();
        step.BuildOptions(this);

        DemoScene.Children.Clear();
        _suppressSceneEntranceAnimation = animate;
        try
        {
            step.BuildScene(this);
        }
        finally
        {
            _suppressSceneEntranceAnimation = false;
        }

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

    private async Task AnimateIntroElementAsync(
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
        const int FrameMs = 16;
        int frameCount = Math.Max(1, milliseconds / FrameMs);
        for (int frame = 0; frame <= frameCount; frame++)
        {
            if (introGeneration != _introGeneration)
            {
                return;
            }

            double progress = frame / (double)frameCount;
            double eased = EaseInOutCubic(progress);
            element.Opacity = Lerp(fromOpacity, toOpacity, eased);
            SetTransformValues(
                element,
                Lerp(fromX, toX, eased),
                Lerp(fromY, toY, eased),
                Lerp(fromScale, toScale, eased));
            await Task.Delay(FrameMs);
        }
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

    private static double Lerp(double from, double to, double progress)
    {
        return from + ((to - from) * progress);
    }

    private static double EaseInOutCubic(double progress)
    {
        return progress < 0.5
            ? 4 * progress * progress * progress
            : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
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

    private void BuildOverviewOptions()
    {
        StepOptionPanel.Children.Add(CreateOptionGrid(
            2,
            CreateCompactInfoCard(
                "\uE8B7",
                _localizationService.T("Onboarding.Overview.FileWidgetsTitle"),
                _localizationService.T("Onboarding.Overview.FileWidgetsDescription")),
            CreateCompactInfoCard(
                "\uE713",
                _localizationService.T("Onboarding.Overview.FeatureWidgetsTitle"),
                _localizationService.T("Onboarding.Overview.FeatureWidgetsDescription"))));
    }

    private void BuildFileWidgetOptions()
    {
        StepOptionPanel.Children.Add(CreateOptionGrid(
            2,
            CreateCompactInfoCard(
                "\uE8AB",
                _localizationService.T("Onboarding.File.ManagedTitle"),
                _localizationService.T("Onboarding.File.ManagedDescription")),
            CreateCompactInfoCard(
                "\uE8A5",
                _localizationService.T("Onboarding.File.MappedTitle"),
                _localizationService.T("Onboarding.File.MappedDescription"))));

        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var changeButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 112,
            Content = CreateButtonContent("\uE8DA", _localizationService.T("Onboarding.Storage.ChangePath"))
        };
        changeButton.Click += ChangeStoragePathButton_Click;
        StepOptionPanel.Children.Add(CreateStoragePathActionCard(path, changeButton));
    }

    private void BuildFeatureWidgetOptions()
    {
        StepOptionPanel.Children.Add(CreateOptionGrid(
            3,
            CreateCompactTileCard(
                "\uE8FD",
                _localizationService.T("Onboarding.Feature.TodoTitle"),
                _localizationService.T("Onboarding.Feature.TodoDescription")),
            CreateCompactTileCard(
                "\uE70B",
                _localizationService.T("Onboarding.Feature.QuickCaptureTitle"),
                _localizationService.T("Onboarding.Feature.QuickCaptureDescription")),
            CreateCompactTileCard(
                "\uE8D6",
                _localizationService.T("Onboarding.Feature.MusicTitle"),
                _localizationService.T("Onboarding.Feature.MusicDescription"))));

        var clipboardToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = _settingsService.Settings.QuickCaptureClipboardEnabled
        };

        clipboardToggle.Toggled += (_, _) =>
        {
            if (clipboardToggle.IsOn)
            {
                FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, true);
            }
            else
            {
                _settingsService.Settings.QuickCaptureImageClipboardEnabled = false;
            }

            _settingsService.Settings.QuickCaptureClipboardEnabled = clipboardToggle.IsOn;
            _settingsService.SaveDebounced();
        };

        StepOptionPanel.Children.Add(CreateSettingToggleCard(
            "\uE8C8",
            _localizationService.T("Onboarding.Feature.ClipboardTitle"),
            _localizationService.T("Onboarding.Feature.ClipboardDescription"),
            clipboardToggle,
            compact: true));
    }

    private void BuildDailyAccessOptions()
    {
        var hotkeyToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = _settingsService.Settings.GlobalHotkeyEnabled
        };
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
        };

        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = StartupService.IsEnabled()
        };
        toggle.Toggled += (_, _) =>
        {
            StartupService.SetEnabled(toggle.IsOn);
            _settingsService.Settings.AutoStart = toggle.IsOn;
            _settingsService.SaveDebounced();
            RenderStep(animate: false);
        };

        StepOptionPanel.Children.Add(CreateSettingToggleCard(
            "\uE765",
            _localizationService.T("Onboarding.Daily.HotkeyToggleTitle"),
            _localizationService.T("Onboarding.Daily.HotkeyToggleDescription"),
            hotkeyToggle));
        StepOptionPanel.Children.Add(CreateSettingToggleCard(
            "\uE7F4",
            _localizationService.T("Onboarding.Startup.Title"),
            _localizationService.T("Onboarding.Startup.Description"),
            toggle));
    }

    private void BuildSafetyOptions()
    {
        StepOptionPanel.Children.Add(CreateOptionGrid(
            3,
            CreateCompactTileCard(
                "\uE8B7",
                _localizationService.T("Onboarding.Safety.FilesTitle"),
                _localizationService.T("Onboarding.Safety.FilesDescription")),
            CreateCompactTileCard(
                "\uE72E",
                _localizationService.T("Onboarding.Safety.LocalDataTitle"),
                _localizationService.T("Onboarding.Safety.LocalDataDescription")),
            CreateCompactTileCard(
                "\uE713",
                _localizationService.T("Onboarding.Safety.SettingsTitle"),
                _localizationService.T("Onboarding.Safety.SettingsDescription"))));
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

    private void BuildOverviewScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        var fileWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody());
        var todoWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.Todo"),
            "\uE8FD",
            CreateTodoPreviewBody(compact: true));
        var musicWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.Music"),
            "\uE8D6",
            CreateMusicPreviewBody(compact: true));
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.NativeDesktop"));
        var layerBadge = CreateBadge(_localizationService.T("Onboarding.Scene.LightLayer"));

        AddToScene(fileWidget, 28, 48);
        AddToScene(todoWidget, 172, 44);
        AddToScene(musicWidget, 96, 140);
        AddToScene(badge, 32, 202);
        AddToScene(layerBadge, 176, 190);

        PlaySceneEntrance(fileWidget, todoWidget, musicWidget, badge, layerBadge);
    }

    private void BuildFileWidgetScene()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);

        DemoScene.Children.Add(CreateDesktopSurface());
        var managedWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody());
        var mappedWidget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.MappedWidget"),
            "\uE8A5",
            CreateMappedWidgetPreviewBody());
        var connector = CreateConnectorLine(width: 72, dashed: true);
        var pathCard = CreatePathCard(path);
        var moveBadge = CreateBadge(_localizationService.T("Onboarding.Scene.MoveBadge"));
        var viewBadge = CreateBadge(_localizationService.T("Onboarding.Scene.ViewOnly"));

        AddToScene(managedWidget, 24, 44);
        AddToScene(mappedWidget, 180, 44);
        AddToScene(connector, 130, 94);
        AddToScene(pathCard, 34, 184);
        AddToScene(moveBadge, 54, 146);
        AddToScene(viewBadge, 192, 146);

        PlaySceneEntrance(managedWidget, mappedWidget, connector, moveBadge, viewBadge, pathCard);
    }

    private void BuildFeatureWidgetScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        var todo = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.Todo"),
            "\uE8FD",
            CreateTodoPreviewBody(compact: false),
            width: 126,
            height: 112);
        var quickCapture = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.QuickCapture"),
            "\uE70B",
            CreateQuickCapturePreviewBody(),
            width: 126,
            height: 112);
        var music = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.Music"),
            "\uE8D6",
            CreateMusicPreviewBody(compact: false),
            width: 154,
            height: 104);

        AddToScene(todo, 24, 40);
        AddToScene(quickCapture, 170, 40);
        AddToScene(music, 82, 154);

        PlaySceneEntrance(todo, quickCapture, music);
        PlayMusicBarsPulse(music);
    }

    private void BuildDailyAccessScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());
        var widget = CreateMiniWidgetPreview(
            _localizationService.T("Onboarding.Scene.ManagedWidget"),
            "\uE8B7",
            CreateFileWidgetPreviewBody());
        var taskbar = CreateTaskbar();
        var tray = CreateTrayGlyph();
        var hotkey = CreateHotkeyKeycap();
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.ShowHide"));

        AddToScene(widget, 96, 44);
        AddToScene(taskbar, 0, 208);
        AddToScene(tray, 248, 214);
        AddToScene(hotkey, 34, 160);
        AddToScene(badge, 148, 162);

        PlaySceneEntrance(taskbar, tray, hotkey, widget, badge);
    }

    private void BuildSafetyScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        var storage = CreatePathCard(SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath));
        var localData = CreateSecurityCard(
            "\uE72E",
            _localizationService.T("Onboarding.Scene.LocalOnly"),
            _localizationService.T("Onboarding.Scene.LocalOnlyDescription"));
        var settings = CreateSecurityCard(
            "\uE713",
            _localizationService.T("Onboarding.Scene.AdjustLater"),
            _localizationService.T("Onboarding.Scene.AdjustLaterDescription"));
        var folder = CreateFolderCard(_localizationService.T("Onboarding.Scene.OriginalFolder"));

        AddToScene(folder, 34, 46);
        AddToScene(storage, 34, 128);
        AddToScene(localData, 178, 46);
        AddToScene(settings, 178, 128);

        PlaySceneEntrance(folder, localData, storage, settings);
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

        Grid.SetColumn(toggle, 2);
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
        return new Border
        {
            Width = 320,
            Height = 250,
            Background = SceneSurfaceBrush(),
            CornerRadius = new CornerRadius(8)
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
            MinWidth = 94,
            Height = 34,
            Padding = new Thickness(10, 0, 10, 0),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = "\uE765", FontSize = 14, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = hotkeyText,
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateWidgetCard(string title, string glyph)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Margin = new Thickness(12, 0, 12, 0),
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 14, Foreground = AccentBrush() },
                new TextBlock { Text = title, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = PrimaryTextBrush() }
            }
        };
        grid.Children.Add(header);

        var files = new Grid
        {
            Padding = new Thickness(12, 4, 12, 12)
        };
        Grid.SetRow(files, 1);
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
        grid.Children.Add(files);

        return new Border
        {
            Width = 126,
            Height = 124,
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private Border CreateMiniWidgetPreview(string title, string glyph, UIElement body, double width = 126, double height = 124)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid
        {
            Margin = new Thickness(10, 0, 10, 0),
            ColumnSpacing = 7,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 13,
            Foreground = AccentBrush(),
            VerticalAlignment = VerticalAlignment.Center
        });

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 11.5,
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
            Padding = new Thickness(10, 2, 10, 10),
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
            CornerRadius = new CornerRadius(8),
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

    private Grid CreateMappedWidgetPreviewBody()
    {
        var grid = CreateFileWidgetPreviewBody();
        grid.Children.Add(new Border
        {
            Width = 66,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = AccentBrush(),
            Opacity = 0.9,
            CornerRadius = new CornerRadius(11),
            Child = new TextBlock
            {
                Text = _localizationService.T("Onboarding.Scene.ViewOnly"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        });
        return grid;
    }

    private StackPanel CreateTodoPreviewBody(bool compact)
    {
        var panel = new StackPanel
        {
            Spacing = compact ? 6 : 7
        };
        panel.Children.Add(CreateTodoPreviewRow(_localizationService.T("Onboarding.Scene.TodoItem1"), completed: false));
        panel.Children.Add(CreateTodoPreviewRow(_localizationService.T("Onboarding.Scene.TodoItem2"), completed: true));
        if (!compact)
        {
            panel.Children.Add(CreateTodoPreviewRow(_localizationService.T("Onboarding.Scene.TodoItem3"), completed: false));
        }

        return panel;
    }

    private Grid CreateTodoPreviewRow(string text, bool completed)
    {
        var row = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };

        row.Children.Add(new Border
        {
            Width = 13,
            Height = 13,
            BorderBrush = completed ? AccentBrush() : StrokeBrush(),
            BorderThickness = new Thickness(1.5),
            Background = completed ? AccentBrush() : new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(6.5),
            Child = completed
                ? new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Colors.White)
                }
                : null
        });

        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = completed ? SecondaryTextBrush() : PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(textBlock, 1);
        row.Children.Add(textBlock);
        return row;
    }

    private StackPanel CreateQuickCapturePreviewBody()
    {
        return new StackPanel
        {
            Spacing = 7,
            Children =
            {
                CreateQuickCapturePreviewItem("\uE8C8", _localizationService.T("Onboarding.Scene.QuickCaptureText")),
                CreateQuickCapturePreviewItem("\uE8A5", _localizationService.T("Onboarding.Scene.QuickCaptureImage")),
                CreateQuickCapturePreviewItem("\uE71B", _localizationService.T("Onboarding.Scene.QuickCaptureLink"))
            }
        };
    }

    private Border CreateQuickCapturePreviewItem(string glyph, string text)
    {
        return new Border
        {
            Height = 21,
            Padding = new Thickness(7, 0, 7, 0),
            Background = OptionCardBrush(),
            CornerRadius = new CornerRadius(5),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 10.5, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = text,
                        FontSize = 10.3,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
    }

    private Grid CreateMusicPreviewBody(bool compact)
    {
        var grid = new Grid
        {
            RowSpacing = compact ? 7 : 8
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = _localizationService.T("Onboarding.Scene.MusicTrack"),
            FontSize = compact ? 10.4 : 11,
            Foreground = PrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        grid.Children.Add(title);

        var progress = new Grid
        {
            Height = 4
        };
        progress.Children.Add(new Border
        {
            Height = 4,
            Background = StrokeBrush(),
            CornerRadius = new CornerRadius(2)
        });
        progress.Children.Add(new Border
        {
            Width = compact ? 42 : 64,
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = AccentBrush(),
            CornerRadius = new CornerRadius(2)
        });
        Grid.SetRow(progress, 1);
        grid.Children.Add(progress);

        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = compact ? 3 : 4,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = "music-bars"
        };
        double[] heights = compact ? [10, 18, 13, 24, 15, 20] : [12, 25, 16, 31, 20, 28, 14, 22];
        foreach (double height in heights)
        {
            bars.Children.Add(new Border
            {
                Width = compact ? 4 : 5,
                Height = height,
                Background = AccentBrush(),
                Opacity = 0.72,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        Grid.SetRow(bars, 2);
        grid.Children.Add(bars);
        return grid;
    }

    private Border CreateSecurityCard(string glyph, string title, string description)
    {
        return new Border
        {
            Width = 112,
            Height = 74,
            Padding = new Thickness(10),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new FontIcon { Glyph = glyph, FontSize = 18, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = description,
                        FontSize = 9.5,
                        Foreground = SecondaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
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

    private Border CreateFolderCard(string title)
    {
        return new Border
        {
            Width = 112,
            Height = 74,
            Padding = new Thickness(10),
            Background = SceneCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = "\uE8B7", FontSize = 24, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateConnectorLine(double width, bool dashed = false)
    {
        var line = new Border
        {
            Width = width,
            Height = 2,
            Background = AccentBrush(),
            CornerRadius = new CornerRadius(1),
            Opacity = dashed ? 0.5 : 0.8
        };

        if (!dashed)
        {
            return line;
        }

        var panel = new StackPanel
        {
            Width = width,
            Height = 6,
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        for (int index = 0; index < 5; index++)
        {
            panel.Children.Add(new Border
            {
                Width = 7,
                Height = 2,
                Background = AccentBrush(),
                CornerRadius = new CornerRadius(1)
            });
        }

        return new Border
        {
            Width = width,
            Height = 8,
            Child = panel,
            Opacity = 0.5
        };
    }

    private Border CreatePathCard(string path)
    {
        return new Border
        {
            Width = 252,
            Height = 50,
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
                        FontSize = 10.5,
                        Foreground = SecondaryTextBrush()
                    },
                    new TextBlock
                    {
                        Text = path,
                        FontSize = 11.5,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
    }

    private Border CreateBadge(string text)
    {
        return new Border
        {
            MinWidth = 82,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Background = AccentBrush(),
            CornerRadius = new CornerRadius(15),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
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
            Width = 320,
            Height = 42,
            Background = TaskbarBrush(),
            CornerRadius = new CornerRadius(0, 0, 8, 8)
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
            Width = 320,
            Height = 250,
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
                int beginMs = 90 + (index * 85);
                SetElementOpacity(element, 0);
                element.Translation = new System.Numerics.Vector3(18, 16, 0);
                element.Scale = new System.Numerics.Vector3(0.94f, 0.94f, 1);

                AnimationBuilder.Create()
                    .Opacity(
                        1,
                        from: 0,
                        delay: TimeSpan.FromMilliseconds(beginMs),
                        duration: TimeSpan.FromMilliseconds(430),
                        easingType: EasingType.Cubic,
                        easingMode: EasingMode.EaseOut)
                    .Translation(
                        new System.Numerics.Vector3(0, 0, 0),
                        from: new System.Numerics.Vector3(18, 16, 0),
                        delay: TimeSpan.FromMilliseconds(beginMs),
                        duration: TimeSpan.FromMilliseconds(520),
                        easingType: EasingType.Cubic,
                        easingMode: EasingMode.EaseOut)
                    .Scale(
                        new System.Numerics.Vector3(1, 1, 1),
                        from: new System.Numerics.Vector3(0.94f, 0.94f, 1),
                        delay: TimeSpan.FromMilliseconds(beginMs),
                        duration: TimeSpan.FromMilliseconds(520),
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
            await Task.Delay(1200);
            if (animationGeneration == _sceneAnimationGeneration &&
                elements.Any(element => element.Opacity <= 0.01))
            {
                App.Log("[Onboarding] Scene entrance animation timed out; showing scene fallback.");
                CompleteSceneEntrance(elements);
            }
        });
    }

    private void PlayMusicBarsPulse(UIElement root)
    {
        if (root is not FrameworkElement frameworkElement)
        {
            return;
        }

        var bars = FindTaggedStackPanel(frameworkElement, "music-bars");
        if (bars is null)
        {
            return;
        }

        for (int index = 0; index < bars.Children.Count; index++)
        {
            if (bars.Children[index] is not UIElement bar)
            {
                continue;
            }

            bar.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1);
            AnimationBuilder.Create()
                .Scale(
                    new System.Numerics.Vector3(1, index % 2 == 0 ? 0.66f : 1.22f, 1),
                    from: new System.Numerics.Vector3(1, 1, 1),
                    delay: TimeSpan.FromMilliseconds(220 + (index * 35)),
                    duration: TimeSpan.FromMilliseconds(520),
                    repeat: RepeatOption.Forever,
                    easingType: EasingType.Sine,
                    easingMode: EasingMode.EaseInOut)
                .Start(bar);
        }
    }

    private static StackPanel? FindTaggedStackPanel(DependencyObject root, string tag)
    {
        int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is StackPanel panel &&
                string.Equals(panel.Tag as string, tag, StringComparison.Ordinal))
            {
                return panel;
            }

            var result = FindTaggedStackPanel(child, tag);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private void PlaySceneStoryboard(
        Storyboard storyboard,
        int timeoutMs,
        IReadOnlyCollection<UIElement> visibleElements,
        IReadOnlyCollection<UIElement>? hiddenElements = null)
    {
        int animationGeneration = _sceneAnimationGeneration;

        try
        {
            _sceneEntranceStoryboard = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (animationGeneration == _sceneAnimationGeneration &&
                    ReferenceEquals(_sceneEntranceStoryboard, storyboard))
                {
                    CompleteSceneTransition(visibleElements, hiddenElements);
                    _sceneEntranceStoryboard = null;
                }
            };
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Scene storyboard failed; showing scene fallback. {ex}");
            CompleteSceneTransition(visibleElements, hiddenElements);
            _sceneEntranceStoryboard = null;
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(timeoutMs);
            if (animationGeneration == _sceneAnimationGeneration &&
                ReferenceEquals(_sceneEntranceStoryboard, storyboard) &&
                visibleElements.Any(element => element.Opacity <= 0.01))
            {
                App.Log("[Onboarding] Scene storyboard timed out; showing scene fallback.");
                CompleteSceneTransition(visibleElements, hiddenElements);
                _sceneEntranceStoryboard = null;
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
        double xamlScale = RootGrid.XamlRoot?.RasterizationScale ?? 0;
        if (xamlScale > 0)
        {
            return xamlScale;
        }

        uint dpi = GetDpiForWindow(_hWnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        return Math.Max(1, (int)Math.Round(logicalPixels * scale, MidpointRounding.AwayFromZero));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void PrepareContentTransitionStartState()
    {
        SetElementOpacity(StepTitleText, 0);
        SetElementOpacity(StepBodyText, 0);
        SetElementOpacity(StepHintPanel, 0);
        SetElementOpacity(StepOptionPanel, 0);
        SetElementOpacity(DemoDesktop, 0);
        SetElementOpacity(DemoScene, 0);

        SetElementTransform(DemoDesktop, translateX: 26, translateY: 0, scale: 0.965);
        SetElementTransform(StepTitleText, translateY: 8, scale: 1);
        SetElementTransform(StepBodyText, translateY: 8, scale: 1);
        SetElementTransform(StepHintPanel, translateY: 8, scale: 1);
        SetElementTransform(StepOptionPanel, translateY: 10, scale: 0.99);
        SetCanvasTranslateX(DemoScene, 28);
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
        AddOpacityAnimation(storyboard, DemoDesktop, from: 0, to: 1, milliseconds: 420, beginMs: 60);
        AddTranslateXAnimation(storyboard, GetElementTransform(DemoDesktop), from: 26, to: 0, milliseconds: 520, beginMs: 60);
        AddScaleAnimation(storyboard, GetElementTransform(DemoDesktop), from: 0.965, to: 1, milliseconds: 520, beginMs: 60);
        AddOpacityAnimation(storyboard, DemoScene, from: 0, to: 1, milliseconds: 420, beginMs: 100);
        AddTranslateXAnimation(storyboard, GetElementTransform(DemoScene), from: 28, to: 0, milliseconds: 540, beginMs: 100);
        _contentTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_contentTransitionStoryboard, storyboard))
            {
                ResetContentTransitionState();
                _contentTransitionStoryboard = null;
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

    private static void AddOpacityKeyFrameAnimation(
        Storyboard storyboard,
        DependencyObject target,
        params (int TimeMs, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (timeMs, value) in frames)
        {
            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(timeMs),
                KeySpline = CreateSceneEntranceEase(),
                Value = value
            });
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
