using DeskBox.Helpers;
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
    private const int SceneLoopIntervalMs = 3600;
    private const int SceneLoopInitialDelayMs = 900;
    private const int DesiredWindowWidth = 960;
    private const int DesiredWindowHeight = 700;
    private const int MinWindowWidth = 620;
    private const int MinWindowHeight = 500;
    private const int WindowWorkAreaMargin = 96;
    private const int CompactLayoutThreshold = 820;
    private static readonly UIntPtr OnboardingWindowSubclassId = new(0xD05C0B01);

    private sealed record OnboardingStep(
        string KeyPrefix,
        string WidgetGlyph,
        Action<OnboardingWindow> BuildOptions,
        Action<OnboardingWindow> BuildScene);

    private sealed record IntroMarkTarget(double TranslateX, double TranslateY, double Scale);

    private static readonly OnboardingStep[] Steps =
    [
        new("Onboarding.Step1", "\uE8B7", window => window.BuildWelcomeOptions(), window => window.BuildWelcomeScene()),
        new("Onboarding.Step2", "\uE8B7", window => window.BuildFirstWidgetOptions(), window => window.BuildFirstWidgetScene()),
        new("Onboarding.Step3", "\uE8A5", window => window.BuildDropActionOptions(), window => window.BuildDropActionScene()),
        new("Onboarding.Step4", "\uE8B7", window => window.BuildMappingOptions(), window => window.BuildMappingScene()),
        new("Onboarding.Step5", "\uE77B", window => window.BuildDailyAccessOptions(), window => window.BuildDailyAccessScene())
    ];

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private DispatcherQueueTimer? _sceneLoopTimer;
    private Storyboard? _contentTransitionStoryboard;
    private Storyboard? _introStoryboard;
    private Storyboard? _brandLogoShineStoryboard;
    private Storyboard? _sceneLoopStoryboard;
    private Func<Storyboard>? _sceneLoopStoryboardFactory;
    private int _sceneAnimationGeneration;
    private int _introGeneration;
    private int _stepIndex;
    private bool _hasLoaded;
    private bool _isSubclassInstalled;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;

    public OnboardingWindow(SettingsService settingsService, LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _windowSubclassProc = WindowSubclassProc;
        InitializeComponent();
        _localizationService.LanguageChanged += OnLanguageChanged;

        SystemBackdrop = new MicaBackdrop
        {
            Kind = MicaKind.BaseAlt
        };

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
        RootGrid.Padding = compact ? new Thickness(28) : new Thickness(48);
        TitleBarHost.Margin = compact
            ? new Thickness(-28, -28, -28, 8)
            : new Thickness(-48, -48, -48, 8);
        IntroOverlay.Margin = compact ? new Thickness(-28) : new Thickness(-48);
        IntroOverlay.Padding = compact ? new Thickness(28) : new Thickness(48);
        FooterNav.Margin = compact ? new Thickness(0, 20, 0, 0) : new Thickness(0, 32, 0, 0);

        if (compact)
        {
            MainContentGrid.ColumnSpacing = 0;
            MainContentGrid.RowSpacing = 24;
            MainContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetRow(StepContentPanel, 0);
            Grid.SetColumn(StepContentPanel, 0);
            StepContentPanel.MaxWidth = double.PositiveInfinity;
            StepContentPanel.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(DemoSceneHost, 1);
            Grid.SetColumn(DemoSceneHost, 0);
            DemoSceneHost.MinHeight = 280;
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
            MainContentGrid.ColumnSpacing = 40;
            MainContentGrid.RowSpacing = 0;
            MainContentGrid.ColumnDefinitions[0].Width = new GridLength(1.05, GridUnitType.Star);
            MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0.95, GridUnitType.Star);
            Grid.SetRow(StepContentPanel, 0);
            Grid.SetColumn(StepContentPanel, 0);
            StepContentPanel.MaxWidth = 430;
            StepContentPanel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(DemoSceneHost, 0);
            Grid.SetColumn(DemoSceneHost, 1);
            DemoSceneHost.MinHeight = 360;
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
        DemoScene.Opacity = 1;
        SetCanvasTranslateX(DemoScene, 0);
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
            PlayContentTransition();
        }

        StartSceneLoop(afterTransition: animate);
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
            await RunIntroAnimationAsync(introGeneration, backLayer, middleLayer, frontLayer);
        }
        catch
        {
            if (introGeneration == _introGeneration)
            {
                await Task.Delay(900);
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

    private void BuildWelcomeOptions()
    {
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE8B7",
            _localizationService.T("Onboarding.Welcome.ManagedTitle"),
            _localizationService.T("Onboarding.Welcome.ManagedDescription")));
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE8A5",
            _localizationService.T("Onboarding.Welcome.MappedTitle"),
            _localizationService.T("Onboarding.Welcome.MappedDescription")));
    }

    private void BuildFirstWidgetOptions()
    {
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE710",
            _localizationService.T("Onboarding.FirstWidget.CreateTitle"),
            _localizationService.T("Onboarding.FirstWidget.CreateDescription")));
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE8A5",
            _localizationService.T("Onboarding.FirstWidget.DropTitle"),
            _localizationService.T("Onboarding.FirstWidget.DropDescription")));
    }

    private void BuildDropActionOptions()
    {
        var moveButton = CreateDropActionRadio(
            SettingsService.ManagedDropActionMove,
            _localizationService.T("Onboarding.DropAction.MoveTitle"),
            _localizationService.T("Onboarding.DropAction.MoveDescription"),
            "\uE8AB");
        var copyButton = CreateDropActionRadio(
            SettingsService.ManagedDropActionCopy,
            _localizationService.T("Onboarding.DropAction.CopyTitle"),
            _localizationService.T("Onboarding.DropAction.CopyDescription"),
            "\uE8C8");

        StepOptionPanel.Children.Add(new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children =
            {
                moveButton,
                copyButton
            }
        });
        Grid.SetColumn(copyButton, 1);

        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE8B7",
            _localizationService.T("Onboarding.Storage.CurrentPath"),
            path,
            wrapDescription: false));

        var changeButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 120,
            Content = CreateButtonContent("\uE8DA", _localizationService.T("Onboarding.Storage.ChangePath"))
        };
        changeButton.Click += ChangeStoragePathButton_Click;
        StepOptionPanel.Children.Add(changeButton);
    }

    private RadioButton CreateDropActionRadio(string action, string title, string description, string glyph)
    {
        var radio = new RadioButton
        {
            GroupName = "ManagedDropAction",
            IsChecked = string.Equals(_settingsService.Settings.ManagedDropAction, action, StringComparison.OrdinalIgnoreCase),
            Padding = new Thickness(8),
            MinHeight = 62,
            Content = CreateCompactDropActionContent(glyph, title, description)
        };

        radio.Checked += (_, _) =>
        {
            _settingsService.Settings.ManagedDropAction = action;
            _settingsService.SaveDebounced();
            RenderStep(animate: false);
        };

        return radio;
    }

    private Grid CreateCompactDropActionContent(string glyph, string title, string description)
    {
        var content = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            }
        };

        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 18,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentBrush()
        });

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 13.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = description,
                    FontSize = 11.5,
                    Foreground = SecondaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                }
            }
        };
        Grid.SetColumn(textStack, 1);
        content.Children.Add(textStack);
        return content;
    }

    private void BuildMappingOptions()
    {
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE8B7",
            _localizationService.T("Onboarding.Mapping.ManagedTitle"),
            _localizationService.T("Onboarding.Mapping.ManagedDescription")));
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE8A5",
            _localizationService.T("Onboarding.Mapping.MappedTitle"),
            _localizationService.T("Onboarding.Mapping.MappedDescription")));
    }

    private void BuildDailyAccessOptions()
    {
        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);

        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE77B",
            _localizationService.T("Onboarding.Daily.TrayTitle"),
            _localizationService.T("Onboarding.Daily.TrayDescription")));
        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE765",
            _localizationService.T("Onboarding.Daily.HotkeyTitle"),
            _localizationService.Format("Onboarding.Daily.HotkeyDescription", hotkeyText)));

        var toggle = new ToggleSwitch
        {
            Header = _localizationService.T("Onboarding.Startup.Title"),
            OnContent = _localizationService.T("Common.On"),
            OffContent = _localizationService.T("Common.Off"),
            IsOn = StartupService.IsEnabled()
        };
        toggle.Toggled += (_, _) =>
        {
            StartupService.SetEnabled(toggle.IsOn);
            _settingsService.Settings.AutoStart = toggle.IsOn;
            _settingsService.SaveDebounced();
            RenderStep(animate: false);
        };

        StepOptionPanel.Children.Add(toggle);
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

    private void BuildWelcomeScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        var file = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE8A5");
        var folder = CreateMiniFile(_localizationService.T("Onboarding.Scene.ProjectFolder"), "\uE8B7");
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.NativeDesktop"));
        var layerBadge = CreateBadge(_localizationService.T("Onboarding.Scene.LightLayer"));

        AddToScene(file, 34, 56);
        AddToScene(folder, 34, 142);
        AddToScene(widget, 172, 58);
        AddToScene(badge, 32, 202);
        AddToScene(layerBadge, 176, 190);

        SetElementOpacity(file, 1);
        SetElementOpacity(folder, 1);
        SetElementOpacity(widget, 0.92);
        SetElementOpacity(badge, 0.84);
        SetElementOpacity(layerBadge, 1);
        SetElementTransform(file);
        SetElementTransform(folder);
        SetElementTransform(widget);
        SetElementTransform(badge);
        SetElementTransform(layerBadge);

        SetSceneLoop(() =>
        {
            SetElementOpacity(file, 1);
            SetElementOpacity(folder, 1);
            SetElementOpacity(widget, 0.42);
            SetElementOpacity(badge, 0.84);
            SetElementOpacity(layerBadge, 0);
            SetTransformValues(file);
            SetTransformValues(folder);
            SetTransformValues(widget, translateX: 28, translateY: 0, scale: 0.96);
            SetTransformValues(badge);
            SetTransformValues(layerBadge, translateY: 8, scale: 0.96);

            var storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, widget, 0.42, 0.92, 420, beginMs: 120);
            AddTranslateXAnimation(storyboard, GetElementTransform(widget), 28, 0, 480, beginMs: 120);
            AddScaleAnimation(storyboard, GetElementTransform(widget), 0.96, 1, 480, beginMs: 120);
            AddScaleAnimation(storyboard, GetElementTransform(file), 1, 1.045, 180, beginMs: 620, autoReverse: true);
            AddScaleAnimation(storyboard, GetElementTransform(folder), 1, 1.045, 180, beginMs: 760, autoReverse: true);
            AddOpacityAnimation(storyboard, layerBadge, 0, 1, 220, beginMs: 920);
            AddTranslateYAnimation(storyboard, GetElementTransform(layerBadge), 8, 0, 220, beginMs: 920);
            AddScaleAnimation(storyboard, GetElementTransform(layerBadge), 0.96, 1, 220, beginMs: 920);
            return storyboard;
        });
    }

    private void BuildFirstWidgetScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());

        var addButton = CreateAddWidgetButton();
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var sourceFile = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE8A5");
        var movingFile = CreateMiniFile(string.Empty, "\uE8A5");
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.FileAdded"));

        AddToScene(addButton, 34, 54);
        AddToScene(sourceFile, 36, 144);
        AddToScene(movingFile, 36, 144);
        AddToScene(widget, 172, 58);
        AddToScene(badge, 180, 190);

        SetElementOpacity(widget, 1);
        SetElementOpacity(movingFile, 0);
        SetElementOpacity(badge, 1);
        SetElementTransform(addButton);
        SetElementTransform(widget);
        SetElementTransform(movingFile);
        SetElementTransform(badge);

        SetSceneLoop(() =>
        {
            SetElementOpacity(widget, 0);
            SetElementOpacity(sourceFile, 1);
            SetElementOpacity(movingFile, 0);
            SetElementOpacity(badge, 0);
            SetTransformValues(addButton);
            SetTransformValues(widget, translateX: 22, translateY: 8, scale: 0.94);
            SetTransformValues(movingFile);
            SetTransformValues(badge, translateY: 8, scale: 0.96);

            var storyboard = new Storyboard();
            AddScaleAnimation(storyboard, GetElementTransform(addButton), 1, 1.08, 180, beginMs: 80, autoReverse: true);
            AddOpacityAnimation(storyboard, widget, 0, 1, 360, beginMs: 260);
            AddTranslateXAnimation(storyboard, GetElementTransform(widget), 22, 0, 420, beginMs: 260);
            AddTranslateYAnimation(storyboard, GetElementTransform(widget), 8, 0, 420, beginMs: 260);
            AddScaleAnimation(storyboard, GetElementTransform(widget), 0.94, 1, 420, beginMs: 260);
            AddOpacityAnimation(storyboard, movingFile, 0, 1, 120, beginMs: 740);
            AddTranslateXAnimation(storyboard, GetElementTransform(movingFile), 0, 145, 720, beginMs: 820, EasingMode.EaseInOut);
            AddTranslateYAnimation(storyboard, GetElementTransform(movingFile), 0, -56, 720, beginMs: 820, EasingMode.EaseInOut);
            AddOpacityAnimation(storyboard, movingFile, 1, 0, 160, beginMs: 1430);
            AddOpacityAnimation(storyboard, sourceFile, 1, 0.35, 220, beginMs: 1280);
            AddScaleAnimation(storyboard, GetElementTransform(widget), 1, 1.035, 180, beginMs: 1460, autoReverse: true);
            AddOpacityAnimation(storyboard, badge, 0, 1, 220, beginMs: 1580);
            AddTranslateYAnimation(storyboard, GetElementTransform(badge), 8, 0, 220, beginMs: 1580);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 0.96, 1, 220, beginMs: 1580);
            return storyboard;
        });
    }

    private void BuildDropActionScene()
    {
        string badgeKey = string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? "Onboarding.Scene.CopyBadge"
            : "Onboarding.Scene.MoveBadge";
        bool isCopy = string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase);
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);

        DemoScene.Children.Add(CreateDesktopSurface());
        var sourceFile = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE8A5");
        var movingFile = CreateMiniFile(string.Empty, "\uE8A5");
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var pathCard = CreatePathCard(path);
        var badge = CreateBadge(_localizationService.T(badgeKey));

        AddToScene(sourceFile, 34, 76);
        AddToScene(movingFile, 34, 76);
        AddToScene(widget, 174, 52);
        AddToScene(pathCard, 34, 184);
        AddToScene(badge, 204, 184);

        SetElementOpacity(sourceFile, isCopy ? 1 : 0.38);
        SetElementOpacity(movingFile, 0);
        SetElementOpacity(pathCard, 1);
        SetElementOpacity(badge, 1);
        SetElementTransform(sourceFile);
        SetElementTransform(movingFile);
        SetElementTransform(pathCard);
        SetElementTransform(badge);
        SetElementTransform(widget);

        SetSceneLoop(() =>
        {
            SetElementOpacity(sourceFile, 1);
            SetElementOpacity(movingFile, 0);
            SetElementOpacity(pathCard, 0.92);
            SetElementOpacity(badge, 0);
            SetTransformValues(sourceFile);
            SetTransformValues(movingFile);
            SetTransformValues(pathCard);
            SetTransformValues(badge, translateY: 8, scale: 0.96);
            SetTransformValues(widget);

            var storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, movingFile, 0, 1, 120, beginMs: 80);
            AddTranslateXAnimation(storyboard, GetElementTransform(movingFile), 0, 148, 760, beginMs: 160, EasingMode.EaseInOut);
            AddTranslateYAnimation(storyboard, GetElementTransform(movingFile), 0, -10, 760, beginMs: 160, EasingMode.EaseInOut);
            AddOpacityAnimation(storyboard, movingFile, 1, 0, 180, beginMs: 790);
            if (!isCopy)
            {
                AddOpacityAnimation(storyboard, sourceFile, 1, 0.28, 260, beginMs: 620);
            }

            AddScaleAnimation(storyboard, GetElementTransform(widget), 1, 1.035, 180, beginMs: 820, autoReverse: true);
            AddOpacityAnimation(storyboard, pathCard, 0.92, 1, 220, beginMs: 1010, autoReverse: true);
            AddScaleAnimation(storyboard, GetElementTransform(pathCard), 1, 1.012, 220, beginMs: 1010, autoReverse: true);
            AddOpacityAnimation(storyboard, badge, 0, 1, 220, beginMs: 1120);
            AddTranslateYAnimation(storyboard, GetElementTransform(badge), 8, 0, 220, beginMs: 1120);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 0.96, 1, 220, beginMs: 1120);
            return storyboard;
        });
    }

    private void BuildMappingScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());
        var sourceFolder = CreateFolderCard(_localizationService.T("Onboarding.Scene.OriginalFolder"));
        var connector = CreateConnectorLine(width: 62, dashed: true);
        var mappedWidget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.MappedWidget"), "\uE8A5");
        var mirrorFile = CreateMiniFile(string.Empty, "\uE8A5");
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.ViewOnly"));

        AddToScene(sourceFolder, 26, 84);
        AddToScene(connector, 136, 119);
        AddToScene(mappedWidget, 190, 62);
        AddToScene(mirrorFile, 210, 116);
        AddToScene(badge, 82, 184);

        SetElementOpacity(connector, 0.78);
        SetElementOpacity(mirrorFile, 0.9);
        SetElementTransform(sourceFolder);
        SetElementTransform(mappedWidget);
        SetElementTransform(mirrorFile);
        SetElementTransform(badge);

        SetSceneLoop(() =>
        {
            SetElementOpacity(connector, 0.38);
            SetElementOpacity(mirrorFile, 0);
            SetTransformValues(sourceFolder);
            SetTransformValues(mappedWidget);
            SetTransformValues(mirrorFile, translateX: -18, scale: 0.94);
            SetTransformValues(badge);

            var storyboard = new Storyboard();
            AddScaleAnimation(storyboard, GetElementTransform(sourceFolder), 1, 1.045, 260, beginMs: 80, autoReverse: true);
            AddOpacityAnimation(storyboard, connector, 0.38, 1, 260, beginMs: 250, autoReverse: true);
            AddOpacityAnimation(storyboard, mirrorFile, 0, 0.92, 260, beginMs: 420);
            AddTranslateXAnimation(storyboard, GetElementTransform(mirrorFile), -18, 0, 320, beginMs: 420);
            AddScaleAnimation(storyboard, GetElementTransform(mirrorFile), 0.94, 1, 320, beginMs: 420);
            AddScaleAnimation(storyboard, GetElementTransform(mappedWidget), 1, 1.035, 260, beginMs: 620, autoReverse: true);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 1, 1.035, 210, beginMs: 820, autoReverse: true);
            return storyboard;
        });
    }

    private void BuildDailyAccessScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());
        bool autoStart = StartupService.IsEnabled();
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var taskbar = CreateTaskbar();
        var tray = CreateTrayGlyph();
        var hotkey = CreateHotkeyKeycap();
        var badge = CreateBadge(_localizationService.T(autoStart ? "Onboarding.Scene.Startup" : "Onboarding.Scene.StartupOff"));

        AddToScene(widget, 96, 48);
        AddToScene(taskbar, 0, 208);
        AddToScene(tray, 248, 214);
        AddToScene(hotkey, 34, 160);
        AddToScene(badge, 148, 162);

        SetElementOpacity(widget, 1);
        SetElementOpacity(hotkey, 1);
        SetElementOpacity(badge, 1);
        SetElementTransform(widget);
        SetElementTransform(tray);
        SetElementTransform(hotkey);
        SetElementTransform(badge);

        SetSceneLoop(() =>
        {
            SetElementOpacity(widget, autoStart ? 0.52 : 1);
            SetElementOpacity(hotkey, 1);
            SetElementOpacity(badge, 0);
            SetTransformValues(widget, translateX: autoStart ? 24 : 0, scale: autoStart ? 0.98 : 1);
            SetTransformValues(tray);
            SetTransformValues(hotkey);
            SetTransformValues(badge, translateY: 8, scale: 0.96);

            var storyboard = new Storyboard();
            AddScaleAnimation(storyboard, GetElementTransform(tray), 1, 1.14, 220, beginMs: 120, autoReverse: true);
            if (autoStart)
            {
                AddOpacityAnimation(storyboard, widget, 0.52, 1, 520, beginMs: 320);
                AddTranslateXAnimation(storyboard, GetElementTransform(widget), 24, 0, 520, beginMs: 320);
                AddScaleAnimation(storyboard, GetElementTransform(widget), 0.98, 1, 520, beginMs: 320);
            }

            AddScaleAnimation(storyboard, GetElementTransform(hotkey), 1, 1.07, 180, beginMs: 820, autoReverse: true);
            AddOpacityAnimation(storyboard, widget, 1, 1, 260, beginMs: 960);
            AddTranslateXAnimation(storyboard, GetElementTransform(widget), autoStart ? 0 : 0, 0, 420, beginMs: 960);
            AddScaleAnimation(storyboard, GetElementTransform(widget), 1, 1.03, 170, beginMs: 1040, autoReverse: true);
            AddOpacityAnimation(storyboard, badge, 0, 1, 220, beginMs: 1240);
            AddTranslateYAnimation(storyboard, GetElementTransform(badge), 8, 0, 220, beginMs: 1240);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 0.96, 1, 220, beginMs: 1240);
            return storyboard;
        });
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

    private Border CreateOptionCard(UIElement content)
    {
        return new Border
        {
            Padding = new Thickness(12),
            Background = OptionCardBrush(),
            BorderBrush = StrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
    }

    private StackPanel CreateInlineOptionContent(string glyph, string title, string description, bool wrapDescription = true)
    {
        var textStack = new StackPanel
        {
            Spacing = 2,
            MaxWidth = 330
        };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12.5,
            Foreground = SecondaryTextBrush(),
            TextTrimming = wrapDescription ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = wrapDescription ? TextWrapping.Wrap : TextWrapping.NoWrap
        });

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new FontIcon
                {
                    Glyph = glyph,
                    Width = 22,
                    FontSize = 18,
                    Foreground = AccentBrush()
                },
                textStack
            }
        };
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

    private Border CreateAddWidgetButton()
    {
        return new Border
        {
            Width = 86,
            Height = 58,
            Padding = new Thickness(8),
            Background = SceneCardBrush(),
            BorderBrush = AccentBrush(),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = "\uE710", FontSize = 20, Foreground = AccentBrush() },
                    new TextBlock
                    {
                        Text = _localizationService.T("Onboarding.Scene.NewWidget"),
                        FontSize = 10.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = PrimaryTextBrush(),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };
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

    private void SetSceneLoop(Func<Storyboard> storyboardFactory)
    {
        _sceneLoopStoryboardFactory = storyboardFactory;
    }

    private void StartSceneLoop(bool afterTransition)
    {
        if (_sceneLoopStoryboardFactory is null)
        {
            return;
        }

        int animationGeneration = _sceneAnimationGeneration;
        _sceneLoopTimer = DispatcherQueue.CreateTimer();
        _sceneLoopTimer.IsRepeating = true;
        _sceneLoopTimer.Interval = TimeSpan.FromMilliseconds(SceneLoopIntervalMs);
        _sceneLoopTimer.Tick += (_, _) =>
        {
            if (animationGeneration != _sceneAnimationGeneration)
            {
                return;
            }

            PlaySceneLoopOnce();
        };

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(afterTransition ? SceneLoopInitialDelayMs + 260 : SceneLoopInitialDelayMs);
            if (animationGeneration != _sceneAnimationGeneration || _sceneLoopTimer is null)
            {
                return;
            }

            PlaySceneLoopOnce();
            _sceneLoopTimer.Start();
        });
    }

    private void PlaySceneLoopOnce()
    {
        if (_sceneLoopStoryboardFactory is null)
        {
            return;
        }

        _sceneLoopStoryboard?.Stop();
        _sceneLoopStoryboard = _sceneLoopStoryboardFactory();
        _sceneLoopStoryboard.Begin();
    }

    private void StopSceneAnimations()
    {
        _sceneAnimationGeneration++;
        _sceneLoopTimer?.Stop();
        _sceneLoopTimer = null;
        _contentTransitionStoryboard?.Stop();
        _contentTransitionStoryboard = null;
        _sceneLoopStoryboard?.Stop();
        _sceneLoopStoryboard = null;
        _sceneLoopStoryboardFactory = null;
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

    private void PlayContentTransition()
    {
        _contentTransitionStoryboard?.Stop();
        SetCanvasTranslateX(DemoScene, 18);
        var storyboard = new Storyboard();
        AddOpacityAnimation(storyboard, StepTitleText, from: 0, to: 1, milliseconds: 220);
        AddOpacityAnimation(storyboard, StepBodyText, from: 0, to: 1, milliseconds: 240, beginMs: 30);
        AddOpacityAnimation(storyboard, StepOptionPanel, from: 0, to: 1, milliseconds: 250, beginMs: 60);
        AddOpacityAnimation(storyboard, DemoScene, from: 0, to: 1, milliseconds: 320, beginMs: 40);
        AddTranslateXAnimation(storyboard, GetElementTransform(DemoScene), from: 18, to: 0, milliseconds: 360, beginMs: 40);
        _contentTransitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_contentTransitionStoryboard, storyboard))
            {
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
        EasingMode easingMode = EasingMode.EaseOut)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            AutoReverse = autoReverse,
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
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
        bool autoReverse = false)
    {
        AddTransformAnimation(storyboard, transform, "TranslateX", from, to, milliseconds, beginMs, easingMode, autoReverse);
    }

    private static void AddTranslateYAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        EasingMode easingMode = EasingMode.EaseOut,
        bool autoReverse = false)
    {
        AddTransformAnimation(storyboard, transform, "TranslateY", from, to, milliseconds, beginMs, easingMode, autoReverse);
    }

    private static void AddScaleAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        bool autoReverse = false,
        EasingMode easingMode = EasingMode.EaseOut)
    {
        AddTransformAnimation(storyboard, transform, "ScaleX", from, to, milliseconds, beginMs, easingMode, autoReverse);
        AddTransformAnimation(storyboard, transform, "ScaleY", from, to, milliseconds, beginMs, easingMode, autoReverse);
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
        bool autoReverse = false)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            AutoReverse = autoReverse,
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
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

    private static void AddLayerSettleAnimation(
        Storyboard storyboard,
        UIElement element,
        double fromX,
        double fromY,
        double toX,
        double toY,
        int milliseconds,
        int beginMs)
    {
        AddOpacityAnimation(storyboard, element, 0, 1, milliseconds, beginMs);
        AddTranslateXAnimation(storyboard, GetElementTransform(element), fromX, toX, milliseconds, beginMs);
        AddTranslateYAnimation(storyboard, GetElementTransform(element), fromY, toY, milliseconds, beginMs);
        AddScaleAnimation(storyboard, GetElementTransform(element), 0.94, 1, milliseconds, beginMs);
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
