using System.Globalization;
using DeskBox.GlancePackage.Services;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Owns one live glance widget instance: view, settings, runtime state,
/// image rotation, clock, resize debounce, and the host lifecycle events.
/// This is the package-side counterpart of the built-in widget's view model
/// (audit rounds 18-19): events arriving over ABI v4 actually stop/start
/// work instead of tweaking probe visuals, the clock advances on minute
/// boundaries and rolls the month at midnight, and deskbox_widget_destroy
/// disposes the controller explicitly instead of betting on Unloaded.
/// </summary>
internal sealed class GlanceWidgetController : IDisposable
{
    private readonly string _packageRoot;
    private readonly string _instanceId;
    private readonly string _instanceDataRoot;
    private CultureInfo _culture;
    private GlanceData _data;
    private GlanceWidgetData Settings => _data.Settings;
    private readonly GlanceRuntimeState _runtimeState;

    private readonly FrameworkElement _content;
    private readonly CalendarDecorationState _decoration;
    private readonly CalendarView _calendarView;
    private readonly GlanceCalendarNavigationController _calendarNavigation;
    private readonly long _calendarModeToken;
    private DateOnly _displayedMonth;
    private readonly Border _backgroundA;
    private readonly Border _backgroundB;
    private readonly Grid _backgroundLayer;
    private readonly Border _readabilityLayer;
    private readonly Border _calendarReadabilityLayer;
    private readonly Border _gradientLayer;
    private readonly Border _actionLayer;
    private bool _pointerInside;
    private bool _keyboardFocus;
    private string[] _images;
    private Stretch _imageStretch;
    private bool _showingA;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _transitionStoryboard;
    private bool _transitionInFlight;
    private Border? _transitionIncoming;
    private long _transitionGeneration;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _rotationTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resizeTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;

    private bool _visible = true;
    private bool _longHidden;
    private bool _collapsed;
    private bool _applying; // toggle revert suppression
    private bool _disposed;
    private bool _loaded;
    private readonly GlanceAppearanceController _appearance;
    private PackageAppearance _hostAppearance;
    private bool? _fallbackIsDark;
    private PackagePerformancePolicy _performance;
    private readonly GlanceImageLoadCoordinator _imageLoader;
    private readonly GlanceImageDecodeCoordinator _imageDecoder;
    private readonly HashSet<string> _failedImages = new(StringComparer.OrdinalIgnoreCase);
    private string? _displayedImagePath;
    private ImageSource? _displayedImageSource;
    private int _requestedImageIndex;
    private int _requestedDecodeWidth;
    private int _displayedDecodeWidth;
    private string[] _catalogPaths = [];
    private bool _catalogRandomOrder;
    private bool _needsImageLoad = true;
    private bool _decodePending;
    private bool _needsImageDecode;
    private bool _forceImageReload;
    private DateOnly _renderedDate;
    private MenuFlyoutItem _nextItem = null!;
    private MenuFlyoutItem _pauseItem = null!;
    private MenuFlyoutItem _settingsItem = null!;

    private readonly GlanceViewportCoordinator _viewport = new(GlanceMonthPipeline.DefaultWidth, GlanceMonthPipeline.DefaultHeight);
    private double _width => _viewport.Width;
    private double _height => _viewport.Height;
    private bool _interactiveResize;
    // Last pipeline outputs, kept so clock ticks can refresh the
    // presentation without recomputing the month.
    private GlanceCalendarMonth _month;
    private bool _isCompact;
    private double _panelHeight;
    private double _panelWidth;
    private GlanceTraditionalCalendarMode _restoreMode;

    internal FrameworkElement View => _content;
    internal double CompactBackgroundOpacity => 1 - Settings.BackgroundImageTransparency;
    internal ImageSource? CompactBackgroundImage => _disposed || CompactBackgroundOpacity <= 0.001 ? null : _displayedImageSource;
    internal int EventsReceived { get; private set; }

    public GlanceWidgetController(string packageRoot, string contributionId, string instanceId, string instanceDataRoot, GlanceImageRepository imageRepository)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        _displayedMonth = new(today.Year, today.Month, 1);
        _renderedDate = today;
        _packageRoot = packageRoot;
        _instanceId = instanceId;
        _instanceDataRoot = instanceDataRoot;
        _culture = HostConfig.TryGetCulture() ?? CultureInfo.CurrentUICulture;
        _hostAppearance = HostConfig.ReadAppearance();
        _performance = HostConfig.ReadPerformancePolicy();
        PackageStrings.Configure(_culture, packageRoot);

        _data = GlanceDataFile.Load(instanceDataRoot) ?? new GlanceData(new GlanceWidgetData(), default);
        _runtimeState = GlanceRuntimeState.LoadOrCreate(instanceDataRoot);
        bool showFestivals = Settings.ShowChineseFestivals;

        (_month, _isCompact, _panelHeight, _panelWidth, double dayItemHeight, bool showSecondary, GlanceTraditionalCalendarMode effectiveMode) =
            GlanceMonthPipeline.Build(showFestivals, Settings.TraditionalCalendarMode, _culture, _width, _height,
                _displayedMonth, today);
        bool showTraditional = effectiveMode != GlanceTraditionalCalendarMode.None;
        _restoreMode = showTraditional ? effectiveMode : GlanceTraditionalCalendarMode.ChineseLunar;

        _content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(packageRoot, "glance.xaml")));
        _content.Language = _culture.Name;
        _appearance = new GlanceAppearanceController(_content);
        _content.DataContext = GlanceMonthPipeline.CreatePresentation(_month, _isCompact, _panelHeight, _panelWidth, Settings, _culture, _width, _height);

        var calendarView = _content.FindName("NativeCalendarView").As<CalendarView>();
        _calendarView = calendarView;
        _decoration = new CalendarDecorationState(_month, dayItemHeight, showTraditional, showFestivals, showSecondary, _culture);
        GlanceViewBuilder.SubscribeDayDecoration(calendarView, _decoration);
        _calendarNavigation = new GlanceCalendarNavigationController(calendarView, month =>
        {
            if (_disposed) return;
            _displayedMonth = month;
            RebuildMonth();
        }, _displayedMonth);
        _displayedMonth = _calendarNavigation.DisplayedMonth;
        _calendarNavigation.SetCulture(_culture);
        _calendarModeToken = calendarView.RegisterPropertyChangedCallback(CalendarView.DisplayModeProperty,
            (_, _) => UpdateTraditionalTitleVisibility());

        _images = [];
        _imageStretch = Settings.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;
        _backgroundA = _content.FindName("BackgroundA").As<Border>();
        _backgroundB = _content.FindName("BackgroundB").As<Border>();
        _backgroundLayer = _content.FindName("BackgroundImageLayer").As<Grid>();
        _readabilityLayer = _content.FindName("ReadabilityLayer").As<Border>();
        _calendarReadabilityLayer = _content.FindName("CalendarReadabilityLayer").As<Border>();
        _gradientLayer = _content.FindName("NonCalendarGradientLayer").As<Border>();
        _actionLayer = _content.FindName("ActionLayer").As<Border>();
        _content.PointerEntered += (_, _) => { _pointerInside = true; UpdateActionLayer(); };
        _content.PointerExited += (_, _) => { _pointerInside = false; UpdateActionLayer(); };
        _content.GotFocus += (_, e) =>
        {
            _keyboardFocus = e.OriginalSource is Control control && control.FocusState == FocusState.Keyboard;
            UpdateActionLayer();
        };
        _content.LostFocus += (_, _) => { _keyboardFocus = false; UpdateActionLayer(); };
        UpdateActionLayer();
        if (_images.Length == 0)
        {
            GlanceViewBuilder.ShowGradientFallback(_backgroundA, _hostAppearance.IsDark);
            _showingA = false;
        }
        else
        {
            Show(_runtimeState.ImageIndex);
        }

        var pauseButton = _content.FindName("PauseButton").As<Button>();
        var nextButton = _content.FindName("NextButton").As<Button>();
        if (!Settings.ShowPhotoControls)
        {
            pauseButton.Visibility = Visibility.Collapsed;
            nextButton.Visibility = Visibility.Collapsed;
        }
        pauseButton.Click += (_, _) => TogglePause();
        nextButton.Click += (_, _) => Show(_runtimeState.ImageIndex + 1);

        var settingsLayer = _content.FindName("SettingsLayer").As<FrameworkElement>();
        var festivalToggle = _content.FindName("FestivalToggle").As<ToggleSwitch>();
        var traditionalToggle = _content.FindName("TraditionalToggle").As<ToggleSwitch>();
        festivalToggle.IsOn = showFestivals;
        traditionalToggle.IsOn = showTraditional;
        festivalToggle.Toggled += (_, _) =>
        {
            if (_applying) return;
            Settings.ShowChineseFestivals = festivalToggle.IsOn;
            // Minimal patch (audit round 20): send ONLY the mutated field -
            // a full owned snapshot would overwrite concurrent host-side
            // setting changes with this widget's stale cache.
            if (CommitSettings(GlanceDataFile.BuildOwnedPatch(Settings, "showChineseFestivals")))
            {
                RebuildMonth();
                return;
            }
            RevertToggle(festivalToggle, value => Settings.ShowChineseFestivals = value);
        };
        traditionalToggle.Toggled += (_, _) =>
        {
            if (_applying) return;
            Settings.TraditionalCalendarMode = traditionalToggle.IsOn ? _restoreMode : GlanceTraditionalCalendarMode.None;
            if (CommitSettings(GlanceDataFile.BuildOwnedPatch(Settings, "traditionalCalendarMode")))
            {
                RebuildMonth();
                return;
            }
            RevertToggle(traditionalToggle, value => Settings.TraditionalCalendarMode = value ? _restoreMode : GlanceTraditionalCalendarMode.None);
        };

        var menu = new MenuFlyout();
        var nextItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuNextBackground", "下一张背景") };
        var pauseItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuPauseRotation", "暂停轮播") };
        var settingsItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuSettings", "设置") };
        _nextItem = nextItem;
        _pauseItem = pauseItem;
        _settingsItem = settingsItem;
        nextItem.Click += (_, _) => Show(_runtimeState.ImageIndex + 1);
        pauseItem.Click += (_, _) => TogglePause();
        settingsItem.Click += (_, _) =>
        {
            settingsLayer.Visibility = settingsLayer.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        };
        menu.Items.Add(nextItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(settingsItem);
        _content.ContextFlyout = menu;
        UpdateSettingsStrings();

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _imageDecoder = new GlanceImageDecodeCoordinator(dispatcher, OnImageDecoded, OnImageDecodeFailed);
        _imageLoader = new GlanceImageLoadCoordinator(
            imageRepository.GetAvailableAsync,
            imageRepository.RefreshOnlineAsync,
            action => dispatcher.TryEnqueue(() => action()), ApplyImages);
        _rotationTimer = dispatcher.CreateTimer();
        _rotationTimer.Tick += (_, _) => { if (!_runtimeState.Paused) Show(_runtimeState.ImageIndex + 1); };
        _clockTimer = dispatcher.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += (_, _) => ClockTimerTick();
        _resizeTimer = dispatcher.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(120);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => RebuildMonth();
        // Visual pause ONLY (audit round 20): Unloaded fires during host
        // group transitions that can ROLL BACK, so it must never permanently
        // dispose the controller - a rolled-back view would come back with
        // dead timers. Real teardown happens exclusively on
        // deskbox_widget_destroy.
        _loaded = _content.IsLoaded;
        _content.Unloaded += (_, _) =>
        {
            _loaded = false;
            _interactiveResize = false;
            _pointerInside = _keyboardFocus = false;
            UpdateActionLayer();
            _appearance.SetLoaded(false);
            _calendarNavigation.SetLoaded(false);
            if (!_collapsed) SuspendImageLoading();
            StopVisualResources();
            if (_collapsed) UpdateTimers();
        };
        _content.Loaded += (_, _) =>
        {
            if (_disposed) return;
            _loaded = true;
            _appearance.SetLoaded(true);
            _appearance.SetActive(_visible && !_longHidden);
            _calendarNavigation.SetLoaded(true);
            RequestImagesIfNeeded();
            RequestImageDecodeIfNeeded();
            EnsureCurrentDate();
            UpdateTimers();
        };

        _appearance.SetLoaded(_loaded);
        _appearance.SetActive(_visible && !_longHidden);
        _calendarNavigation.SetLoaded(_loaded);
        UpdateAppearance();
        UpdateTimers();
        ApplyLayerEffects((GlancePresentation)_content.DataContext);
        RequestImagesIfNeeded();
    }

    // ---- Host lifecycle events (ABI v4 kinds routed by GlanceWidgetHandle) ----

    internal void RefreshRequested(bool refreshImages = true)
    {
        EventsReceived++;
        if (_disposed) return;
        ReloadSettings();
        if (refreshImages)
        {
            _needsImageLoad = true;
            _forceImageReload = true;
            RequestImagesIfNeeded();
        }
        RebuildMonth();
        UpdateTimers();
    }

    internal void OnVisibilityChanged(bool visible)
    {
        EventsReceived++;
        if (_disposed) return;
        _visible = visible;
        if (visible)
        {
            // Built-in parity: the clock text refreshes immediately on
            // reveal instead of showing the hidden-time snapshot, and a
            // widget hidden across midnight/month rolls its grid forward
            // (the minute tick cannot detect a date change while stopped).
            _longHidden = false;
            _appearance.SetActive(true);
            EnsureCurrentDate();
            RequestImagesIfNeeded();
            RequestImageDecodeIfNeeded();
        }
        else
        {
            _appearance.SetActive(false);
            StopImageTransition();
            SuspendImageLoading();
        }
        UpdateTimers();
    }

    internal void OnCompactStateChanged(bool collapsed)
    {
        EventsReceived++;
        if (_disposed) return;
        _collapsed = collapsed;
        RequestImagesIfNeeded();
        RequestImageDecodeIfNeeded();
        UpdateTimers();
    }

    internal void OnLongHidden()
    {
        EventsReceived++;
        if (_disposed) return;
        _longHidden = true;
        _appearance.SetActive(false);
        StopImageTransition();
        SuspendImageLoading();
        UpdateTimers();
    }

    internal void OnViewportChanged(double width, double height)
    {
        EventsReceived++;
        if (_disposed) return;
        if (!_viewport.Resize(width, height)) return;
        // Debounce: the host reports viewport changes continuously during a
        // resize; the pipeline re-runs once per settled size.
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    internal void BeginInteractiveResize()
    {
        EventsReceived++;
        if (_disposed) return;
        _interactiveResize = true;
        StopImageTransition();
        UpdateTimers();
    }

    internal void CompleteInteractiveResize(double width, double height)
    {
        EventsReceived++;
        if (_disposed) return;
        _interactiveResize = false;
        _viewport.Resize(width, height);
        _resizeTimer.Stop();
        RebuildMonth();
        UpdateTimers();
    }

    internal void BeginResponsiveLayoutTransition(double width, double height)
    {
        EventsReceived++;
        if (_disposed) return;
        StopImageTransition();
        _viewport.Begin(width, height);
        _resizeTimer.Stop();
        RebuildMonth();
        UpdateTimers();
    }

    internal void CompleteResponsiveLayoutTransition(double width, double height)
    {
        EventsReceived++;
        if (_disposed) return;
        _viewport.Complete(width, height);
        _resizeTimer.Stop();
        RebuildMonth();
        UpdateTimers();
    }

    internal void CancelResponsiveLayoutTransition()
    {
        EventsReceived++;
        if (_disposed) return;
        _viewport.Cancel();
        _resizeTimer.Stop();
        RebuildMonth();
        UpdateTimers();
    }

    internal void OnRevealCompleted()
    {
        EventsReceived++;
        if (_disposed) return;
        EnsureCurrentDate();
        RequestImagesIfNeeded();
        RequestImageDecodeIfNeeded();
        UpdateTimers();
    }

    private void UpdateActionLayer()
    {
        bool shown = Settings.ShowPhotoControls && (_pointerInside || _keyboardFocus);
        _actionLayer.Visibility = Settings.ShowPhotoControls ? Visibility.Visible : Visibility.Collapsed;
        _actionLayer.Opacity = shown ? 1 : 0;
        _actionLayer.IsHitTestVisible = shown;
    }

    /// <summary>
    /// Live config push (HostApi v4): the host re-fired the config-changed
    /// callback after a language or appearance change. Re-derives
    /// culture, month data, presentation, and menu texts in place - the
    /// user sees the widget switch language without recreation.
    /// </summary>
    internal void ApplyConfigChange(CultureInfo culture)
    {
        if (_disposed) return;
        _culture = culture;
        _content.Language = culture.Name;
        _calendarNavigation.SetCulture(culture);
        _hostAppearance = HostConfig.ReadAppearance();
        ApplyPerformancePolicy();
        _nextItem.Text = PackageStrings.Get("menuNextBackground", "下一张背景");
        _pauseItem.Text = PackageStrings.Get("menuPauseRotation", "暂停轮播");
        _settingsItem.Text = PackageStrings.Get("menuSettings", "设置");
        UpdateSettingsStrings();
        RebuildMonth();
    }

    internal void OnPerformanceSettingsChanged()
    {
        EventsReceived++;
        if (_disposed) return;
        ApplyPerformancePolicy();
    }

    private void ApplyPerformancePolicy()
    {
        _performance = HostConfig.ReadPerformancePolicy();
        if (!_performance.AllowDecorativeAnimations)
        {
            StopImageTransition();
        }
        UpdateTimers();
    }

    private void UpdateSettingsStrings()
    {
        _content.FindName("SettingsTitle").As<TextBlock>().Text = PackageStrings.Get("menuSettings", "Settings");
        var festivals = _content.FindName("FestivalToggle").As<ToggleSwitch>();
        var traditional = _content.FindName("TraditionalToggle").As<ToggleSwitch>();
        festivals.Header = PackageStrings.Get("festivalToggle", "Festivals");
        traditional.Header = PackageStrings.Get("traditionalToggle", "Traditional calendar");
        foreach (var toggle in new[] { festivals, traditional })
        {
            toggle.OffContent = PackageStrings.Get("toggleOff", "Off");
            toggle.OnContent = PackageStrings.Get("toggleOn", "On");
        }
    }

    internal void OnAppearanceChanged()
    {
        EventsReceived++;
        if (_disposed) return;
        _hostAppearance = HostConfig.ReadAppearance();
        UpdateAppearance();
    }

    private void UpdateAppearance()
    {
        double opacity = 1 - Settings.BackgroundImageTransparency;
        string? image = _displayedImagePath;
        _appearance.Update(_hostAppearance, Settings, image, image is not null && opacity > 0.001);
        if (image is null && _fallbackIsDark != _hostAppearance.IsDark)
        {
            GlanceViewBuilder.ShowGradientFallback(_backgroundA, _hostAppearance.IsDark);
            _fallbackIsDark = _hostAppearance.IsDark;
        }
    }

    // ---- Timers ----

    /// <summary>
    /// Built-in UpdateClockTimer cadence: a time display ticks per minute;
    /// date/weekday/calendar/traditional-only ticks once per midnight;
    /// nothing that displays time or dates needs no clock.
    /// </summary>
    private ClockCadence CurrentClockCadence() =>
        GlanceDisplayPolicy.ComputeClockCadence(
            Settings.ShowTime, Settings.ShowDate, Settings.ShowWeekday,
            // Raw setting (built-in parity): the responsive floor only gates
            // the surface, not the clock's midnight obligation.
            Settings.ShowCalendar,
            Settings.TraditionalCalendarMode);

    private void UpdateTimers()
    {
        if (_disposed) return;
        ClockCadence cadence = CurrentClockCadence();
        GlanceLifecyclePolicy.Activity activity = GlanceLifecyclePolicy.Compute(
            _visible && (_loaded || _collapsed), _longHidden, _collapsed, _runtimeState.Paused,
            cadence, Settings.RotationIntervalMinutes > 0 && _performance.AllowImageAutoRotation &&
                !_interactiveResize && !_viewport.IsTransitionActive,
            _images.Count(path => !_failedImages.Contains(path)) > 1);

        _clockTimer.Stop();
        if (activity.Cadence != ClockCadence.None)
        {
            _clockTimer.Interval = GlanceDisplayPolicy.DelayToBoundary(activity.Cadence, DateTime.Now);
            _clockTimer.Start();
        }

        _rotationTimer.Stop();
        if (activity.RotationRunning)
        {
            _rotationTimer.Interval = TimeSpan.FromMinutes(
                Math.Clamp(Settings.RotationIntervalMinutes, 0.1, 1440));
            _rotationTimer.Start();
        }
    }

    private void ClockTimerTick()
    {
        if (_disposed) return;
        // A running clock catches the midnight rollover here; a HIDDEN
        // widget catches it in EnsureCurrentDate on reveal (audit 20).
        EnsureCurrentDate();
        // Re-arm the one-shot clock only; restarting the rotation timer here
        // would reset its progress.
        ClockCadence cadence = CurrentClockCadence();
        _clockTimer.Stop();
        if (cadence != ClockCadence.None)
        {
            _clockTimer.Interval = GlanceDisplayPolicy.DelayToBoundary(cadence, DateTime.Now);
            _clockTimer.Start();
        }
    }

    /// <summary>
    /// Brings the rendered date (and month grid) forward to today. Cheap
    /// when nothing changed; rebuilds the month on any date rollover.
    /// </summary>
    private void EnsureCurrentDate()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        if (today == _renderedDate)
        {
            UpdateClockText();
            return;
        }
        // Follow midnight/month rollover only when the user was viewing the
        // current month; explicit calendar browsing keeps its context.
        if (_displayedMonth.Year == _renderedDate.Year && _displayedMonth.Month == _renderedDate.Month)
        {
            _displayedMonth = new(today.Year, today.Month, 1);
            _calendarNavigation.SetMonth(_displayedMonth);
        }
        _renderedDate = today;
        RebuildMonth();
    }

    private void UpdateClockText()
    {
        var presentation = GlanceMonthPipeline.CreatePresentation(
            _month, _isCompact, _panelHeight, _panelWidth, Settings, _culture, _width, _height);
        // Built-in parity: the action-bar affordance follows the runtime
        // pause state - paused shows the play triangle to resume.
        presentation.PlayIconVisibility = _runtimeState.Paused ? Visibility.Visible : Visibility.Collapsed;
        presentation.PauseIconVisibility = _runtimeState.Paused ? Visibility.Collapsed : Visibility.Visible;
        _content.DataContext = presentation;
        ApplyLayerEffects(presentation);
        UpdateAppearance();
        UpdateTraditionalTitleVisibility();
    }

    private void UpdateTraditionalTitleVisibility()
    {
        if (_disposed) return;
        _content.FindName("TraditionalCalendarTitlePresenter").As<TextBlock>().Visibility =
            _loaded && _calendarView.DisplayMode == CalendarViewDisplayMode.Month &&
            _decoration.ShowTraditional && _decoration.ShowSecondary
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Built-in parity: the background container carries the user's
    /// transparency (opacity = 1 - transparency), and the readability
    /// layers (black for non-calendar foreground, theme fill for the
    /// calendar surface, bottom gradient) appear only when an image is
    /// actually visible behind them.
    /// </summary>
    private void ApplyLayerEffects(GlancePresentation presentation)
    {
        double imageOpacity = 1.0 - Math.Clamp(Settings.BackgroundImageTransparency, 0.0, 1.0);
        _backgroundLayer.Opacity = imageOpacity;
        bool calendar = presentation.CalendarSurfaceVisibility == Visibility.Visible;
        // Built-in parity: HasVisibleCurrentImage = has image AND the image
        // is actually visible (opacity > 0.001). A fully transparent
        // background must not show darkening layers (audit 21 R2).
        bool hasVisibleImage = _displayedImagePath is not null && imageOpacity > 0.001;
        bool nonCalendarForeground = hasVisibleImage &&
            presentation.ForegroundVisibility == Visibility.Visible && !calendar;
        _readabilityLayer.Opacity = presentation.ReadabilityOpacity;
        _readabilityLayer.Visibility = nonCalendarForeground ? Visibility.Visible : Visibility.Collapsed;
        _gradientLayer.Visibility = nonCalendarForeground ? Visibility.Visible : Visibility.Collapsed;
        _calendarReadabilityLayer.Opacity = presentation.ReadabilityOpacity;
        _calendarReadabilityLayer.Visibility = hasVisibleImage && calendar ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Settings (host-authoritative write-through) ----

    private bool CommitSettings(string patch)
    {
        if (HostConfig.TryPushInstanceConfig(_instanceId, patch))
        {
            // Accepted is not committed. The host refreshes the snapshot
            // after persistence succeeds; never write an optimistic value
            // over a newer authoritative snapshot from another change.
            return true;
        }
        PackageLogger.LogVerbose("[GlancePackage] settings write-through unavailable; reverting toggle");
        return false;
    }

    private void RevertToggle(ToggleSwitch toggle, Action<bool> apply)
    {
        _applying = true;
        try
        {
            toggle.IsOn = !toggle.IsOn;
            apply(toggle.IsOn);
        }
        finally
        {
            _applying = false;
        }
    }

    // ---- View updates ----

    private void ReloadSettings()
    {
        GlanceData? next = GlanceDataFile.Load(_instanceDataRoot);
        if (next is null) return;

        bool sourcesChanged = !GlanceDataFile.SameImageSources(Settings, next.Settings);
        bool imageStyleChanged = Settings.ImageFit != next.Settings.ImageFit ||
            Settings.ImageFocus != next.Settings.ImageFocus;
        _data = next;
        _imageStretch = Settings.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;

        bool showTraditional = new GlanceTraditionalCalendarService().ResolveMode(
            Settings.TraditionalCalendarMode, _culture.Name) != GlanceTraditionalCalendarMode.None;
        if (showTraditional) _restoreMode = Settings.TraditionalCalendarMode;
        _applying = true;
        try
        {
            _content.FindName("FestivalToggle").As<ToggleSwitch>().IsOn = Settings.ShowChineseFestivals;
            _content.FindName("TraditionalToggle").As<ToggleSwitch>().IsOn = showTraditional;
        }
        finally { _applying = false; }

        Visibility controls = Settings.ShowPhotoControls ? Visibility.Visible : Visibility.Collapsed;
        _content.FindName("PauseButton").As<Button>().Visibility = controls;
        _content.FindName("NextButton").As<Button>().Visibility = controls;
        UpdateActionLayer();

        if (sourcesChanged)
        {
            _imageDecoder.Cancel();
            _needsImageLoad = true;
            RequestImagesIfNeeded();
        }
        if (imageStyleChanged)
        {
            _imageDecoder.Cancel();
            _decodePending = false;
            _needsImageDecode = true;
            RequestImageDecodeIfNeeded();
        }
    }

    private void RequestImagesIfNeeded()
    {
        if (!_needsImageLoad || (!_loaded && !_collapsed) || !_visible || _longHidden || _disposed) return;
        _needsImageLoad = false;
        _ = _imageLoader.RequestAsync(Settings);
    }

    private void SuspendImageLoading()
    {
        _imageLoader.Cancel();
        _imageDecoder.Cancel();
        if (_decodePending) _needsImageDecode = true;
        _decodePending = false;
        _needsImageLoad = true;
    }

    private void RequestImageDecodeIfNeeded()
    {
        if (!_needsImageDecode || _images.Length == 0 || _disposed ||
            (!_loaded && !_collapsed) || !_visible || _longHidden) return;
        _needsImageDecode = false;
        Show(_runtimeState.ImageIndex);
    }

    private void ApplyImages(IReadOnlyList<GlanceImageInfo> images)
    {
        if (_disposed) return;
        string[] paths = images.Select(image => image.LocalPath).ToArray();
        if (!_forceImageReload && _catalogRandomOrder == Settings.RandomOrder &&
            _catalogPaths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
        {
            // Unload may cancel the first decode after discovery has finished.
            if (_displayedImagePath is null && _failedImages.Count == 0 && _images.Length > 0)
                Show(_runtimeState.ImageIndex);
            else RequestImageDecodeIfNeeded();
            return;
        }
        bool forceDecode = _forceImageReload;
        _forceImageReload = false;
        string? selected = _displayedImagePath;
        _imageDecoder.Cancel();
        _failedImages.Clear();
        _catalogPaths = paths;
        _catalogRandomOrder = Settings.RandomOrder;
        _images = [.. paths];
        if (_catalogRandomOrder && _images.Length > 1) Random.Shared.Shuffle(_images);
        int retained = Array.FindIndex(_images, path => string.Equals(path, selected, StringComparison.OrdinalIgnoreCase));
        _runtimeState.ImageIndex = retained >= 0 ? retained : Math.Clamp(_runtimeState.ImageIndex, 0, Math.Max(0, _images.Length - 1));
        if (_images.Length == 0) ClearImages();
        else if (retained < 0 || forceDecode || _needsImageDecode)
        {
            _needsImageDecode = false;
            Show(_runtimeState.ImageIndex);
        }
        UpdateClockText();
        UpdateTimers();
    }

    private void RebuildMonth()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        (_month, _isCompact, _panelHeight, _panelWidth, double itemHeight, bool secondary, GlanceTraditionalCalendarMode mode) =
            GlanceMonthPipeline.Build(Settings.ShowChineseFestivals, Settings.TraditionalCalendarMode, _culture, _width, _height,
                _displayedMonth, today);
        _decoration.Update(_month, itemHeight, mode != GlanceTraditionalCalendarMode.None, Settings.ShowChineseFestivals, secondary, _culture);
        _renderedDate = today;
        UpdateClockText();
        if (_displayedImagePath is not null && _loaded && _visible && !_collapsed &&
            !_interactiveResize && !_viewport.IsTransitionActive &&
            GlanceImageDecodeSizeCalculator.NeedsRefresh(_displayedDecodeWidth, RequiredDecodeWidth()))
            Show(_runtimeState.ImageIndex);
    }

    private void Show(int index)
    {
        if (_images.Length == 0 || _disposed) return;
        for (int offset = 0; offset < _images.Length; offset++)
        {
            int candidate = ((index + offset) % _images.Length + _images.Length) % _images.Length;
            if (_failedImages.Contains(_images[candidate])) continue;
            _requestedImageIndex = candidate;
            _requestedDecodeWidth = RequiredDecodeWidth();
            var (alignmentX, alignmentY) = GlanceDisplayPolicy.ResolveImageFocus(Settings.ImageFocus);
            _decodePending = true;
            _imageDecoder.Request(_images[candidate], _imageStretch, alignmentX, alignmentY, _requestedDecodeWidth);
            return;
        }
        // Every candidate failed decoding. Keep the last good image, or the
        // existing gradient when no image has ever opened successfully.
        UpdateClockText();
        UpdateTimers();
    }

    private int RequiredDecodeWidth() => GlanceImageDecodeSizeCalculator.Calculate(
        _width, _height, _content.XamlRoot?.RasterizationScale ?? 1);

    private void OnImageDecoded(string path, ImageBrush brush)
    {
        _decodePending = false;
        if (_disposed) return;
        if ((!_loaded && !_collapsed) || !_visible || _longHidden)
        {
            _needsImageDecode = true;
            return;
        }
        int index = Array.FindIndex(_images, item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _runtimeState.ImageIndex = index;
        _displayedImagePath = path;
        _displayedImageSource = brush.ImageSource;
        _displayedDecodeWidth = _requestedDecodeWidth;
        Border incoming = _showingA ? _backgroundB : _backgroundA;
        Border outgoing = _showingA ? _backgroundA : _backgroundB;
        RunTransition(incoming, outgoing, brush);
        UpdateClockText();
        UpdateTimers();
    }

    private void OnImageDecodeFailed(string path)
    {
        _decodePending = false;
        if (_disposed) return;
        if ((!_loaded && !_collapsed) || !_visible || _longHidden)
        {
            _needsImageDecode = true;
            return;
        }
        _failedImages.Add(path);
        PackageLogger.LogVerbose($"[GlancePackage] skipped image that could not be decoded: {path}");
        Show(_requestedImageIndex + 1);
    }

    private void ClearImages()
    {
        _imageDecoder.Cancel();
        _decodePending = false;
        _needsImageDecode = false;
        StopImageTransition();
        _backgroundA.Background = _backgroundB.Background = null;
        _backgroundA.Opacity = _backgroundB.Opacity = 0;
        ResetTransform(_backgroundA);
        ResetTransform(_backgroundB);
        _displayedImagePath = null;
        _displayedImageSource = null;
        _fallbackIsDark = null;
        _showingA = true;
    }

    /// <summary>
    /// Verbatim port of the built-in RunTransition: per-mode opacity/slide/
    /// zoom animation with the speed table (Fast 170 / Standard 300 /
    /// Relaxed 520 ms, CubicEase EaseOut); instant swap for None or when
    /// there is no outgoing image.
    /// </summary>
    private void RunTransition(Border incoming, Border outgoing, ImageBrush brush)
    {
        // The OS animation event invalidates the host's cache but does not
        // require an app-settings change. Observe it before each transition.
        _performance = HostConfig.ReadPerformancePolicy();
        // A previously in-flight transition is finalized first so its
        // half-faded opacities never leak into this run.
        StopImageTransition();
        ResetTransform(incoming);
        ResetTransform(outgoing);

        bool animate = _loaded && !_collapsed && !_interactiveResize && !_viewport.IsTransitionActive &&
            _performance.AllowDecorativeAnimations && Settings.Transition != GlanceTransitionMode.None && outgoing.Background is not null;
        if (!animate)
        {
            incoming.Background = brush;
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            outgoing.Background = null;
            _showingA = ReferenceEquals(incoming, _backgroundA);
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(Settings.TransitionSpeed switch
        {
            GlanceTransitionSpeed.Fast => 170,
            GlanceTransitionSpeed.Relaxed => 520,
            _ => 300,
        });
        incoming.Background = brush;
        incoming.Opacity = 0;
        outgoing.Opacity = 1;
        _transitionInFlight = true;
        _transitionIncoming = incoming;

        var storyboard = new Storyboard();
        AddAnimation(storyboard, incoming, "Opacity", 0, 1, duration);
        AddAnimation(storyboard, outgoing, "Opacity", 1, 0, duration);

        if (Settings.Transition == GlanceTransitionMode.SlideFade && incoming.RenderTransform is CompositeTransform slide)
        {
            slide.TranslateY = 16;
            AddAnimation(storyboard, slide, "TranslateY", 16, 0, duration);
        }
        else if (Settings.Transition == GlanceTransitionMode.ZoomFade && incoming.RenderTransform is CompositeTransform zoom)
        {
            zoom.ScaleX = 1.035;
            zoom.ScaleY = 1.035;
            AddAnimation(storyboard, zoom, "ScaleX", 1.035, 1, duration);
            AddAnimation(storyboard, zoom, "ScaleY", 1.035, 1, duration);
        }

        long generation = ++_transitionGeneration;
        storyboard.Completed += (_, _) =>
        {
            if (generation != _transitionGeneration || !ReferenceEquals(_transitionStoryboard, storyboard)) return;
            _transitionInFlight = false;
            _transitionIncoming = null;
            _transitionStoryboard = null;
            incoming.Opacity = 1;
            outgoing.Background = null;
            outgoing.Opacity = 0;
            ResetTransform(incoming);
            _showingA = ReferenceEquals(incoming, _backgroundA);
        };
        _transitionStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>
    /// Snaps an in-flight transition to its completed state (self-audit:
    /// stopping a storyboard mid-fade would otherwise strand two
    /// half-visible images and an unflipped active-buffer flag).
    /// </summary>
    private void FinalizeInFlightTransition()
    {
        if (!_transitionInFlight) return;
        _transitionInFlight = false;
        Border incoming = _transitionIncoming!;
        Border outgoing = ReferenceEquals(incoming, _backgroundA) ? _backgroundB : _backgroundA;
        incoming.Opacity = 1;
        outgoing.Opacity = 0;
        outgoing.Background = null;
        ResetTransform(incoming);
        _showingA = ReferenceEquals(incoming, _backgroundA);
        _transitionIncoming = null;
    }

    private void StopImageTransition()
    {
        _transitionGeneration++;
        FinalizeInFlightTransition();
        Microsoft.UI.Xaml.Media.Animation.Storyboard? storyboard = _transitionStoryboard;
        _transitionStoryboard = null;
        storyboard?.Stop();
    }

    private static void ResetTransform(Border border)
    {
        if (border.RenderTransform is CompositeTransform transform)
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void TogglePause()
    {
        _runtimeState.Paused = !_runtimeState.Paused;
        UpdateTimers();
        UpdateClockText();
        // Persist the pause at the change point: teardown no longer saves on
        // Unloaded, and destroy persistence must be able to stay no-throw.
        GlanceRuntimeState.TrySave(_runtimeState, _instanceDataRoot);
    }

    // ---- Teardown ----

    private void StopVisualResources()
    {
        if (_disposed) return;
        StopImageTransition();
        _clockTimer.Stop();
        _rotationTimer.Stop();
        _resizeTimer.Stop();
    }

    /// <summary>
    /// Total no-throw teardown (audit round 20): deskbox_widget_destroy
    /// calls this BEFORE removing the handle, so a runtime-state save
    /// failure must never turn into an ABI destroy failure - that would
    /// leave the host lease alive against an already-gone package handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _displayedImageSource = null;
        try { _calendarNavigation.Dispose(); }
        catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] calendar cleanup failed: {error.Message}"); }
        try { _calendarView.UnregisterPropertyChangedCallback(CalendarView.DisplayModeProperty, _calendarModeToken); }
        catch (Exception error) { PackageLogger.LogVerbose($"[GlancePackage] calendar callback cleanup failed: {error.Message}"); }
        _appearance.Dispose();
        _imageLoader.Dispose();
        _imageDecoder.Dispose();
        StopImageTransition();
        _clockTimer.Stop();
        _rotationTimer.Stop();
        _resizeTimer.Stop();
        GlanceRuntimeState.TrySave(_runtimeState, _instanceDataRoot);
    }
}
