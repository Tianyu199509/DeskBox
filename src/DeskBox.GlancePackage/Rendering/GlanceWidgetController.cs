using System.Globalization;
using DeskBox.GlancePackage.Services;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
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
    private readonly CultureInfo _culture;
    private readonly GlanceData _data;
    private GlanceWidgetData Settings => _data.Settings;
    private readonly GlanceRuntimeState _runtimeState;

    private readonly FrameworkElement _content;
    private readonly CalendarDecorationState _decoration;
    private readonly Border _backgroundA;
    private readonly Border _backgroundB;
    private readonly string[] _images;
    private readonly Stretch _imageStretch;
    private bool _showingA;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _rotationTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resizeTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clockTimer;

    private bool _visible = true;
    private bool _longHidden;
    private bool _collapsed;
    private bool _applying; // toggle revert suppression
    private bool _disposed;
    private DateOnly _renderedDate;

    private double _width;
    private double _height;
    // Last pipeline outputs, kept so clock ticks can refresh the
    // presentation without recomputing the month.
    private GlanceCalendarMonth _month;
    private bool _isCompact;
    private double _panelHeight;
    private double _panelWidth;
    private GlanceTraditionalCalendarMode _restoreMode;

    internal FrameworkElement View => _content;
    internal int EventsReceived { get; private set; }

    public GlanceWidgetController(string packageRoot, string contributionId, string instanceId, string instanceDataRoot)
    {
        _packageRoot = packageRoot;
        _instanceId = instanceId;
        _instanceDataRoot = instanceDataRoot;
        _culture = HostConfig.TryGetCulture() ?? CultureInfo.CurrentUICulture;
        PackageStrings.Configure(_culture, packageRoot);

        _data = GlanceDataFile.Load(instanceDataRoot) ?? new GlanceData(new GlanceWidgetData(), default);
        _runtimeState = GlanceRuntimeState.LoadOrCreate(instanceDataRoot);
        bool showFestivals = Settings.ShowChineseFestivals;

        _width = GlanceMonthPipeline.DefaultWidth;
        _height = GlanceMonthPipeline.DefaultHeight;
        (_month, _isCompact, _panelHeight, _panelWidth, double dayItemHeight, bool showSecondary, GlanceTraditionalCalendarMode effectiveMode) =
            GlanceMonthPipeline.Build(showFestivals, Settings.TraditionalCalendarMode, _culture, _width, _height);
        bool showTraditional = effectiveMode != GlanceTraditionalCalendarMode.None;
        _restoreMode = showTraditional ? effectiveMode : GlanceTraditionalCalendarMode.ChineseLunar;

        _content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(packageRoot, "glance.xaml")));
        _content.DataContext = GlanceMonthPipeline.CreatePresentation(_month, _isCompact, _panelHeight, _panelWidth, _culture, _width, _height);

        var calendarView = _content.FindName("NativeCalendarView").As<CalendarView>();
        _decoration = new CalendarDecorationState(_month, dayItemHeight, showTraditional, showFestivals, showSecondary);
        GlanceViewBuilder.SubscribeDayDecoration(calendarView, _decoration, _culture);

        _images = GlanceViewBuilder.LoadImages(Settings, packageRoot);
        _imageStretch = Settings.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;
        _backgroundA = _content.FindName("BackgroundA").As<Border>();
        _backgroundB = _content.FindName("BackgroundB").As<Border>();
        if (_images.Length == 0)
        {
            GlanceViewBuilder.ShowGradientFallback(_backgroundA);
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
        nextItem.Click += (_, _) => Show(_runtimeState.ImageIndex + 1);
        var pauseItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuPauseRotation", "暂停轮播") };
        pauseItem.Click += (_, _) => TogglePause();
        var settingsItem = new MenuFlyoutItem { Text = PackageStrings.Get("menuSettings", "设置") };
        settingsItem.Click += (_, _) =>
        {
            settingsLayer.Visibility = settingsLayer.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        };
        menu.Items.Add(nextItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(settingsItem);
        _content.ContextFlyout = menu;

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _rotationTimer = dispatcher.CreateTimer();
        _rotationTimer.Tick += (_, _) => { if (!_runtimeState.Paused) Show(_runtimeState.ImageIndex + 1); };
        _clockTimer = dispatcher.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += (_, _) => ClockTimerTick();
        _resizeTimer = dispatcher.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(120);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => RebuildMonth();
        _renderedDate = DateOnly.FromDateTime(DateTime.Today);

        // Visual pause ONLY (audit round 20): Unloaded fires during host
        // group transitions that can ROLL BACK, so it must never permanently
        // dispose the controller - a rolled-back view would come back with
        // dead timers. Real teardown happens exclusively on
        // deskbox_widget_destroy.
        _content.Unloaded += (_, _) => StopVisualResources();
        _content.Loaded += (_, _) =>
        {
            EnsureCurrentDate();
            UpdateTimers();
        };

        UpdateTimers();
    }

    // ---- Host lifecycle events (ABI v4 kinds routed by GlanceWidgetHandle) ----

    internal void RefreshRequested()
    {
        EventsReceived++;
        if (_disposed) return;
        EnsureCurrentDate();
        RebuildMonth();
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
            EnsureCurrentDate();
        }
        UpdateTimers();
    }

    internal void OnCompactStateChanged(bool collapsed)
    {
        EventsReceived++;
        if (_disposed) return;
        _collapsed = collapsed;
        UpdateTimers();
    }

    internal void OnLongHidden()
    {
        EventsReceived++;
        if (_disposed) return;
        _longHidden = true;
        UpdateTimers();
    }

    internal void OnViewportChanged(double width, double height)
    {
        EventsReceived++;
        if (_disposed) return;
        if (Math.Abs(width - _width) < 1 && Math.Abs(height - _height) < 1) return;
        _width = width;
        _height = height;
        // Debounce: the host reports viewport changes continuously during a
        // resize; the pipeline re-runs once per settled size.
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    // ---- Timers ----

    private void UpdateTimers()
    {
        if (_disposed) return;
        GlanceLifecyclePolicy.Activity activity = GlanceLifecyclePolicy.Compute(
            _visible, _longHidden, _collapsed, _runtimeState.Paused,
            Settings.RotationIntervalMinutes > 0, _images.Length > 1);

        _clockTimer.Stop();
        if (activity.ClockRunning)
        {
            _clockTimer.Interval = GlanceLifecyclePolicy.DelayToNextMinute(DateTime.Now);
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
        _clockTimer.Stop();
        _clockTimer.Interval = GlanceLifecyclePolicy.DelayToNextMinute(DateTime.Now);
        _clockTimer.Start();
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
        _renderedDate = today;
        RebuildMonth();
    }

    private void UpdateClockText()
    {
        _content.DataContext = GlanceMonthPipeline.CreatePresentation(
            _month, _isCompact, _panelHeight, _panelWidth, _culture, _width, _height);
    }

    // ---- Settings (host-authoritative write-through) ----

    private bool CommitSettings(string patch)
    {
        if (HostConfig.TryPushInstanceConfig(_instanceId, patch))
        {
            // Local cache for continuity until the next host sync; the
            // authoritative copy lives in the built-in store.
            GlanceDataFile.Save(_data, _instanceDataRoot);
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

    private void RebuildMonth()
    {
        (_month, _isCompact, _panelHeight, _panelWidth, double itemHeight, bool secondary, GlanceTraditionalCalendarMode mode) =
            GlanceMonthPipeline.Build(Settings.ShowChineseFestivals, Settings.TraditionalCalendarMode, _culture, _width, _height);
        _decoration.Update(_month, itemHeight, mode != GlanceTraditionalCalendarMode.None, Settings.ShowChineseFestivals, secondary);
        _renderedDate = DateOnly.FromDateTime(DateTime.Today);
        UpdateClockText();
    }

    private void Show(int index)
    {
        if (_images.Length == 0) return;
        _runtimeState.ImageIndex = ((index % _images.Length) + _images.Length) % _images.Length;
        var brush = new ImageBrush
        {
            ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_images[_runtimeState.ImageIndex])),
            Stretch = _imageStretch,
        };
        Border next = _showingA ? _backgroundB : _backgroundA;
        Border fadeOut = _showingA ? _backgroundA : _backgroundB;
        next.Background = brush;
        next.Opacity = 1;
        fadeOut.Opacity = 0;
        _showingA = !_showingA;
    }

    private void TogglePause()
    {
        _runtimeState.Paused = !_runtimeState.Paused;
        UpdateTimers();
        // Persist the pause at the change point: teardown no longer saves on
        // Unloaded, and destroy persistence must be able to stay no-throw.
        GlanceRuntimeState.TrySave(_runtimeState, _instanceDataRoot);
    }

    // ---- Teardown ----

    private void StopVisualResources()
    {
        if (_disposed) return;
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
        _clockTimer.Stop();
        _rotationTimer.Stop();
        _resizeTimer.Stop();
        GlanceRuntimeState.TrySave(_runtimeState, _instanceDataRoot);
    }
}
