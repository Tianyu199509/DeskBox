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

    private sealed record OnboardingStep(
        string KeyPrefix,
        string WidgetGlyph,
        Action<OnboardingWindow> BuildOptions,
        Action<OnboardingWindow> BuildScene);

    private static readonly OnboardingStep[] Steps =
    [
        new("Onboarding.Step1", "\uE8B7", window => window.BuildWelcomeOptions(), window => window.BuildWelcomeScene()),
        new("Onboarding.Step2", "\uE8A5", window => window.BuildDropActionOptions(), window => window.BuildDropActionScene()),
        new("Onboarding.Step3", "\uE8B7", window => window.BuildStoragePathOptions(), window => window.BuildStoragePathScene()),
        new("Onboarding.Step4", "\uE8B7", window => window.BuildMappingOptions(), window => window.BuildMappingScene()),
        new("Onboarding.Step5", "\uE77B", window => window.BuildStartupOptions(), window => window.BuildStartupScene())
    ];

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private DispatcherQueueTimer? _sceneLoopTimer;
    private Storyboard? _contentTransitionStoryboard;
    private Storyboard? _sceneLoopStoryboard;
    private Func<Storyboard>? _sceneLoopStoryboardFactory;
    private int _sceneAnimationGeneration;
    private int _stepIndex;

    public OnboardingWindow(SettingsService settingsService, LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
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
        _appWindow.Resize(new Windows.Graphics.SizeInt32(900, 640));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - _appWindow.Size.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - _appWindow.Size.Height) / 2)));

        RootGrid.Loaded += (_, _) =>
        {
            ApplyTitleBarButtonColors();
            BuildProgressDots();
            RenderStep(animate: false);
        };
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplyTitleBarButtonColors();
            RenderStep(animate: false);
        };

        Closed += (_, _) =>
        {
            StopSceneAnimations();
            _localizationService.LanguageChanged -= OnLanguageChanged;
        };
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
        RenderStep(animate: false);
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

        StepOptionPanel.Children.Add(moveButton);
        StepOptionPanel.Children.Add(copyButton);
    }

    private RadioButton CreateDropActionRadio(string action, string title, string description, string glyph)
    {
        var radio = new RadioButton
        {
            GroupName = "ManagedDropAction",
            IsChecked = string.Equals(_settingsService.Settings.ManagedDropAction, action, StringComparison.OrdinalIgnoreCase),
            Content = CreateInlineOptionContent(glyph, title, description)
        };

        radio.Checked += (_, _) =>
        {
            _settingsService.Settings.ManagedDropAction = action;
            _settingsService.SaveDebounced();
            RenderStep(animate: false);
        };

        return radio;
    }

    private void BuildStoragePathOptions()
    {
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

    private void BuildStartupOptions()
    {
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

        StepOptionPanel.Children.Add(CreateInfoCard(
            "\uE7F4",
            _localizationService.T("Onboarding.Startup.Title"),
            _localizationService.T("Onboarding.Startup.Description")));
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

        AddToScene(file, 34, 66);
        AddToScene(folder, 34, 138);
        AddToScene(widget, 172, 58);
        AddToScene(badge, 170, 192);

        SetElementOpacity(widget, 0.42);
        SetElementOpacity(badge, 0);
        SetElementTransform(widget, translateX: 34, scale: 0.96);
        SetElementTransform(badge, translateY: 8, scale: 0.96);

        SetSceneLoop(() =>
        {
            SetElementOpacity(widget, 0.42);
            SetElementOpacity(badge, 0);
            SetTransformValues(widget, translateX: 34, scale: 0.96);
            SetTransformValues(badge, translateY: 8, scale: 0.96);

            var storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, widget, 0.42, 1, 520, beginMs: 0);
            AddTranslateXAnimation(storyboard, GetElementTransform(widget), 34, 0, 520, beginMs: 0);
            AddScaleAnimation(storyboard, GetElementTransform(widget), 0.96, 1, 520, beginMs: 0);
            AddOpacityAnimation(storyboard, badge, 0, 1, 260, beginMs: 360);
            AddTranslateYAnimation(storyboard, GetElementTransform(badge), 8, 0, 260, beginMs: 360);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 0.96, 1, 260, beginMs: 360);
            return storyboard;
        });
    }

    private void BuildDropActionScene()
    {
        string badgeKey = string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase)
            ? "Onboarding.Scene.CopyBadge"
            : "Onboarding.Scene.MoveBadge";
        bool isCopy = string.Equals(_settingsService.Settings.ManagedDropAction, SettingsService.ManagedDropActionCopy, StringComparison.OrdinalIgnoreCase);

        DemoScene.Children.Add(CreateDesktopSurface());
        var sourceFile = CreateMiniFile(_localizationService.T("Onboarding.Scene.DesktopFile"), "\uE8A5");
        var movingFile = CreateMiniFile(string.Empty, "\uE8A5");
        var arrow = CreateArrow();
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var folder = CreateFolderCard(_localizationService.T("Onboarding.Scene.DefaultStorage"));
        var badge = CreateBadge(_localizationService.T(badgeKey));

        AddToScene(sourceFile, 26, 92);
        AddToScene(movingFile, 26, 92);
        AddToScene(arrow, 108, 104);
        AddToScene(widget, 166, 54);
        AddToScene(folder, 174, 168);
        AddToScene(badge, 28, 172);

        SetElementOpacity(movingFile, 0);
        SetElementOpacity(badge, 0);
        SetElementTransform(movingFile);
        SetElementTransform(badge, translateY: 8, scale: 0.96);
        SetElementTransform(widget);

        SetSceneLoop(() =>
        {
            SetElementOpacity(sourceFile, 1);
            SetElementOpacity(movingFile, 0);
            SetElementOpacity(badge, 0);
            SetTransformValues(movingFile);
            SetTransformValues(badge, translateY: 8, scale: 0.96);
            SetTransformValues(widget);

            var storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, movingFile, 0, 1, 120, beginMs: 80);
            AddTranslateXAnimation(storyboard, GetElementTransform(movingFile), 0, 142, 850, beginMs: 160, EasingMode.EaseInOut);
            AddTranslateYAnimation(storyboard, GetElementTransform(movingFile), 0, -28, 850, beginMs: 160, EasingMode.EaseInOut);
            AddOpacityAnimation(storyboard, movingFile, 1, 0, 180, beginMs: 880);
            if (!isCopy)
            {
                AddOpacityAnimation(storyboard, sourceFile, 1, 0.28, 260, beginMs: 620);
            }

            AddScaleAnimation(storyboard, GetElementTransform(widget), 1, 1.035, 180, beginMs: 840, autoReverse: true);
            AddOpacityAnimation(storyboard, badge, 0, 1, 220, beginMs: 960);
            AddTranslateYAnimation(storyboard, GetElementTransform(badge), 8, 0, 220, beginMs: 960);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 0.96, 1, 220, beginMs: 960);
            return storyboard;
        });
    }

    private void BuildStoragePathScene()
    {
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);

        DemoScene.Children.Add(CreateDesktopSurface());
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var movingFile = CreateMiniFile(string.Empty, "\uE8A5");
        var connector = CreateConnectorLine(width: 54);
        var arrow = CreateArrow();
        var folder = CreateFolderCard(_localizationService.T("Onboarding.Scene.DefaultStorage"));
        var pathCard = CreatePathCard(path);

        AddToScene(widget, 26, 54);
        AddToScene(movingFile, 58, 96);
        AddToScene(connector, 132, 111);
        AddToScene(arrow, 146, 98);
        AddToScene(folder, 190, 62);
        AddToScene(pathCard, 34, 174);

        SetElementOpacity(movingFile, 0);
        SetElementTransform(movingFile);
        SetElementTransform(folder);
        SetElementTransform(pathCard);

        SetSceneLoop(() =>
        {
            SetElementOpacity(movingFile, 0);
            SetTransformValues(movingFile);
            SetTransformValues(folder);
            SetTransformValues(pathCard);

            var storyboard = new Storyboard();
            AddOpacityAnimation(storyboard, movingFile, 0, 1, 120, beginMs: 80);
            AddTranslateXAnimation(storyboard, GetElementTransform(movingFile), 0, 150, 760, beginMs: 140, EasingMode.EaseInOut);
            AddTranslateYAnimation(storyboard, GetElementTransform(movingFile), 0, -18, 760, beginMs: 140, EasingMode.EaseInOut);
            AddOpacityAnimation(storyboard, movingFile, 1, 0, 160, beginMs: 800);
            AddScaleAnimation(storyboard, GetElementTransform(folder), 1, 1.045, 210, beginMs: 820, autoReverse: true);
            AddScaleAnimation(storyboard, GetElementTransform(pathCard), 1, 1.018, 220, beginMs: 980, autoReverse: true);
            return storyboard;
        });
    }

    private void BuildMappingScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());
        var sourceFolder = CreateFolderCard(_localizationService.T("Onboarding.Scene.OriginalFolder"));
        var connector = CreateConnectorLine(width: 62, dashed: true);
        var mappedWidget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.MappedWidget"), "\uE8A5");
        var badge = CreateBadge(_localizationService.T("Onboarding.Scene.ViewOnly"));

        AddToScene(sourceFolder, 26, 84);
        AddToScene(connector, 136, 119);
        AddToScene(mappedWidget, 190, 62);
        AddToScene(badge, 82, 184);

        SetElementOpacity(connector, 0.38);
        SetElementTransform(sourceFolder);
        SetElementTransform(mappedWidget);
        SetElementTransform(badge);

        SetSceneLoop(() =>
        {
            SetElementOpacity(connector, 0.38);
            SetTransformValues(sourceFolder);
            SetTransformValues(mappedWidget);
            SetTransformValues(badge);

            var storyboard = new Storyboard();
            AddScaleAnimation(storyboard, GetElementTransform(sourceFolder), 1, 1.045, 260, beginMs: 80, autoReverse: true);
            AddOpacityAnimation(storyboard, connector, 0.38, 1, 260, beginMs: 250, autoReverse: true);
            AddScaleAnimation(storyboard, GetElementTransform(mappedWidget), 1, 1.035, 260, beginMs: 420, autoReverse: true);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 1, 1.035, 210, beginMs: 680, autoReverse: true);
            return storyboard;
        });
    }

    private void BuildStartupScene()
    {
        DemoScene.Children.Add(CreateDesktopSurface());
        bool autoStart = StartupService.IsEnabled();
        var widget = CreateWidgetCard(_localizationService.T("Onboarding.Scene.ManagedWidget"), "\uE8B7");
        var taskbar = CreateTaskbar();
        var tray = CreateTrayGlyph();
        var badge = CreateBadge(_localizationService.T(autoStart ? "Onboarding.Scene.Startup" : "Onboarding.Scene.StartupOff"));

        AddToScene(widget, 96, 50);
        AddToScene(taskbar, 0, 208);
        AddToScene(tray, 248, 214);
        AddToScene(badge, 70, 162);

        SetElementOpacity(widget, autoStart ? 0.38 : 0.82);
        SetElementOpacity(badge, 0);
        SetElementTransform(widget, translateX: autoStart ? 24 : 0, scale: autoStart ? 0.98 : 1);
        SetElementTransform(tray);
        SetElementTransform(badge, translateY: 8, scale: 0.96);

        SetSceneLoop(() =>
        {
            SetElementOpacity(widget, autoStart ? 0.38 : 0.82);
            SetElementOpacity(badge, 0);
            SetTransformValues(widget, translateX: autoStart ? 24 : 0, scale: autoStart ? 0.98 : 1);
            SetTransformValues(tray);
            SetTransformValues(badge, translateY: 8, scale: 0.96);

            var storyboard = new Storyboard();
            AddScaleAnimation(storyboard, GetElementTransform(tray), 1, 1.14, 220, beginMs: 120, autoReverse: true);
            if (autoStart)
            {
                AddOpacityAnimation(storyboard, widget, 0.38, 1, 520, beginMs: 320);
                AddTranslateXAnimation(storyboard, GetElementTransform(widget), 24, 0, 520, beginMs: 320);
                AddScaleAnimation(storyboard, GetElementTransform(widget), 0.98, 1, 520, beginMs: 320);
            }

            AddOpacityAnimation(storyboard, badge, 0, 1, 220, beginMs: 820);
            AddTranslateYAnimation(storyboard, GetElementTransform(badge), 8, 0, 220, beginMs: 820);
            AddScaleAnimation(storyboard, GetElementTransform(badge), 0.96, 1, 220, beginMs: 820);
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

    private TextBlock CreateSceneLabel(string key)
    {
        return new TextBlock
        {
            Text = _localizationService.T(key),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryTextBrush()
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
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
    }

    private FontIcon CreateArrow()
    {
        return new FontIcon
        {
            Glyph = "\uE72A",
            Width = 36,
            Height = 36,
            FontSize = 22,
            Foreground = AccentBrush()
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
        EasingMode easingMode = EasingMode.EaseOut)
    {
        AddTransformAnimation(storyboard, transform, "TranslateX", from, to, milliseconds, beginMs, easingMode);
    }

    private static void AddTranslateYAnimation(
        Storyboard storyboard,
        CompositeTransform transform,
        double from,
        double to,
        int milliseconds,
        int beginMs = 0,
        EasingMode easingMode = EasingMode.EaseOut)
    {
        AddTransformAnimation(storyboard, transform, "TranslateY", from, to, milliseconds, beginMs, easingMode);
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
