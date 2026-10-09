using System.Diagnostics;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Services;

public sealed record WidgetTrayAnimationProfile(
    double ShowOffsetX,
    double ShowOffsetY,
    double HideOffsetX,
    double HideOffsetY,
    float ShowStartOpacity,
    float HideEndOpacity,
    float ShowStartScale,
    float HideEndScale,
    int DurationMs,
    bool IsEnabled,
    float ShowStartRotation = 0f,
    float HideEndRotation = 0f,
    double CenterAnchorX = 0.5,
    double CenterAnchorY = 0.5,
    string? WipeDirection = null);

public sealed class WidgetTrayAnimationController : IDisposable
{
    public const float RestingOpacity = 1.0f;
    public const float SoftOpacity = 0.0f;
    public const float RestingScale = 1.0f;
    public const float SoftScale = 0.985f;

    private const double MinWidgetSlideOffset = 1.0;
    private const double OffscreenSlidePadding = 16.0;
    private const float TiltDegreesHorizontal = 4f;
    private const float TiltDegreesVertical = 3f;

    private readonly AppWindow _appWindow;
    private readonly FrameworkElement _rootElement;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IntPtr _windowHandle;
    private readonly Func<Windows.Foundation.Rect> _getAnimationBounds;
    private readonly Action<string> _log;

    private PointInt32? _targetPosition;
    private double? _offsetOverrideX;
    private double? _offsetOverrideY;
    private bool _useCrossingFade;
    private double _crossingFadeStartOffset;
    private double _crossingFadeEndOffset;
    private bool _crossingFadeAxisIsX;
    private double _centerAnchorX = 0.5;
    private double _centerAnchorY = 0.5;
    private float _showStartRotation;
    private float _hideEndRotation;
    private string? _wipeDirection;
    private Microsoft.UI.Composition.InsetClip? _cachedRootClip;
    private Microsoft.UI.Composition.Visual? _cachedRootVisual;
    private bool _isWindowCloakedForTrayShow;
    private double _preparedOffsetX;
    private double _preparedOffsetY;
    private float _preparedOpacity = RestingOpacity;
    private float _preparedScale = RestingScale;
    private int _preparedRefreshRateHz = WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
    private int _preparedRefreshAnchorX;
    private int _preparedRefreshAnchorY;
    private EventHandler<object>? _contentReadyRenderingHandler;
    private int _contentReadyFrameCount;
    private long _contentReadyGeneration;
    private Action? _contentReadyAction;

    private bool _isRendering;
    private Stopwatch? _renderStopwatch;
    private double _renderDurationMs;
    private double _renderFromOffsetX;
    private double _renderFromOffsetY;
    private double _renderToOffsetX;
    private double _renderToOffsetY;
    private bool _renderIsShowing;
    private long _renderGeneration;
    private string _renderEasingIntensity = string.Empty;
    private Action? _renderCompleted;
    private Action? _renderFailed;
    private IDisposable? _renderFrameRegistration;
    private readonly WidgetAnimationFramePacingPolicy _renderPacing = new();
    private PointInt32? _lastCommittedPosition;
    private WidgetTrayAnimationFrameTracker? _renderFrameTracker;
    private Microsoft.UI.Composition.Compositor? _cachedCompositor;
    private readonly WidgetCompositionResources _compositionResources = new();

    public WidgetTrayAnimationController(
        AppWindow appWindow,
        FrameworkElement rootElement,
        DispatcherQueue dispatcherQueue,
        IntPtr windowHandle,
        Func<Windows.Foundation.Rect> getAnimationBounds,
        Action<string> log)
    {
        _appWindow = appWindow;
        _rootElement = rootElement;
        _dispatcherQueue = dispatcherQueue;
        _windowHandle = windowHandle;
        _getAnimationBounds = getAnimationBounds;
        _log = log;
    }

    public long Generation { get; private set; }

    public bool IsApplyingBounds { get; private set; }

    public bool IsPositionTransitionActive => _targetPosition.HasValue;

    public long NextGeneration()
    {
        return ++Generation;
    }

    public void SetOffsetOverride(double? offsetX, double? offsetY)
    {
        _offsetOverrideX = offsetX;
        _offsetOverrideY = offsetY;
    }

    public void CloakWindowForTrayShow()
    {
        if (_isWindowCloakedForTrayShow)
        {
            return;
        }

        // Disable DWM's built-in show/hide transition so it doesn't
        // interfere with our custom animation.
        int forceDisabled = 1;
        Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref forceDisabled,
            sizeof(int));

        int cloaked = 1;
        int result = Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_CLOAK,
            ref cloaked,
            sizeof(int));
        if (result == 0)
        {
            _isWindowCloakedForTrayShow = true;
        }
        else
        {
            _log($"CloakWindow failed hresult=0x{result:X8}");
        }
    }

    public void RevealWindowForTrayShow()
    {
        if (!_isWindowCloakedForTrayShow)
        {
            return;
        }

        int cloaked = 0;
        int result = Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_CLOAK,
            ref cloaked,
            sizeof(int));
        if (result == 0)
        {
            _isWindowCloakedForTrayShow = false;
        }
        else
        {
            _log($"RevealWindow failed hresult=0x{result:X8}");
        }
    }

    private void RestoreDwmTransitions()
    {
        int forceDisabled = 0;
        Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref forceDisabled,
            sizeof(int));
    }

    public void PlayAfterContentReady(Action action)
    {
        CancelContentReadyCallback();
        _contentReadyGeneration = Generation;
        _contentReadyFrameCount = 0;
        _contentReadyAction = action;
        _contentReadyRenderingHandler = OnContentReadyRenderingFrame;
        CompositionTarget.Rendering += _contentReadyRenderingHandler;
    }

    /// <summary>
    /// Waits until two composition frames have elapsed for a newly shown
    /// window. The first frame commits its XAML surface; the second confirms
    /// that the surface can replace an already-visible group member.
    /// </summary>
    public async Task WaitForContentReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Action readyAction = () => completion.TrySetResult();
        PlayAfterContentReady(readyAction);
        try
        {
            await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(_contentReadyAction, readyAction))
            {
                CancelContentReadyCallback();
            }
            throw;
        }
    }

    public WidgetTrayAnimationProfile CreateProfile(WidgetAnimationOptions options)
    {
        string effect = options.Effect;
        int durationMs = options.DurationMs;
        string effectiveDirection = WidgetAnimationSettings.GetEffectiveSlideDirection(
            effect, options.SlideDirection);
        var slideOffsets = GetOffscreenSlideOffsets();
        ResolveCrossingFadeState(effectiveDirection, GetDirectionalTravel(effectiveDirection, slideOffsets));
        var (dirX, dirY) = WidgetAnimationSettings.GetDirectionalOffset(options.SlideDirection, slideOffsets);

        ResetVisualMotionChannels(effect, effectiveDirection);

        var profile = effect switch
        {
            SettingsService.WidgetAnimationEffectNone => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                1, false),
            SettingsService.WidgetAnimationEffectFade => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeft => new WidgetTrayAnimationProfile(
                -slideOffsets.Left, 0, -slideOffsets.Left, 0,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUp => new WidgetTrayAnimationProfile(
                0, -slideOffsets.Up, 0, -slideOffsets.Up,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDown => new WidgetTrayAnimationProfile(
                0, slideOffsets.Down, 0, slideOffsets.Down,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectScaleFade => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                SoftScale, SoftScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRight => new WidgetTrayAnimationProfile(
                slideOffsets.Right, 0, slideOffsets.Right, 0,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectZoom => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                0.5f, 0.5f,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUpFade => new WidgetTrayAnimationProfile(
                0, -slideOffsets.Up, 0, -slideOffsets.Up,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDownFade => new WidgetTrayAnimationProfile(
                0, slideOffsets.Down, 0, slideOffsets.Down,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeftFade => new WidgetTrayAnimationProfile(
                -slideOffsets.Left, 0, -slideOffsets.Left, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRightFade => new WidgetTrayAnimationProfile(
                slideOffsets.Right, 0, slideOffsets.Right, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideFade => new WidgetTrayAnimationProfile(
                dirX, dirY, dirX, dirY,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectScaleSlide => new WidgetTrayAnimationProfile(
                dirX, dirY, dirX, dirY,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            // Stationary effects: the window never moves, so they can never
            // cross into an adjacent display.
            SettingsService.WidgetAnimationEffectEdgeScale => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                0f, 0f,
                durationMs, true),
            SettingsService.WidgetAnimationEffectTilt => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectWipe => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            _ => new WidgetTrayAnimationProfile(
                dirX, dirY, dirX, dirY,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true)
        };

        // Crossing fade owns opacity only for effects that would otherwise
        // stay fully opaque while sliding; fade-bearing effects already dim
        // the crossing sliver with their own curve.
        _useCrossingFade &= profile.ShowOffsetX != 0 || profile.ShowOffsetY != 0;
        if (_useCrossingFade &&
            profile.ShowStartOpacity == RestingOpacity &&
            profile.HideEndOpacity == RestingOpacity)
        {
            // Endpoints anchor at transparent: the parked show state must be
            // invisible on the adjacent display, and the per-frame boundary
            // formula drives everything in between.
            profile = profile with
            {
                ShowStartOpacity = SoftOpacity,
                HideEndOpacity = SoftOpacity
            };
        }
        else
        {
            _useCrossingFade = false;
        }

        return profile;
    }

    private static double GetDirectionalTravel(
        string effectiveDirection,
        (double Left, double Right, double Up, double Down) slideOffsets)
    {
        return effectiveDirection switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => slideOffsets.Left,
            SettingsService.WidgetAnimationSlideDirectionUp => slideOffsets.Up,
            SettingsService.WidgetAnimationSlideDirectionDown => slideOffsets.Down,
            SettingsService.WidgetAnimationSlideDirectionRight => slideOffsets.Right,
            _ => 0
        };
    }

    /// <summary>
    /// Refreshes the effect-specific Composition channels (scale anchor,
    /// tilt rotation, wipe clip side) for the profile being created. These
    /// are consumed later by Animate/BeginSharedAnimate via cached fields,
    /// which avoids widening their public signatures.
    /// </summary>
    private void ResetVisualMotionChannels(string effect, string effectiveDirection)
    {
        _centerAnchorX = 0.5;
        _centerAnchorY = 0.5;
        _showStartRotation = 0f;
        _hideEndRotation = 0f;
        _wipeDirection = null;

        string direction = WidgetAnimationSettings.NormalizeSlideDirection(effectiveDirection);
        switch (effect)
        {
            case SettingsService.WidgetAnimationEffectEdgeScale:
                (_centerAnchorX, _centerAnchorY) = direction switch
                {
                    SettingsService.WidgetAnimationSlideDirectionLeft => (0.0, 0.5),
                    SettingsService.WidgetAnimationSlideDirectionUp => (0.5, 0.0),
                    SettingsService.WidgetAnimationSlideDirectionDown => (0.5, 1.0),
                    _ => (1.0, 0.5)
                };
                break;

            case SettingsService.WidgetAnimationEffectTilt:
                float tiltDegrees = direction switch
                {
                    SettingsService.WidgetAnimationSlideDirectionLeft => -TiltDegreesHorizontal,
                    SettingsService.WidgetAnimationSlideDirectionUp => -TiltDegreesVertical,
                    SettingsService.WidgetAnimationSlideDirectionDown => TiltDegreesVertical,
                    _ => TiltDegreesHorizontal
                };
                _hideEndRotation = tiltDegrees;
                _showStartRotation = -tiltDegrees;
                break;

            case SettingsService.WidgetAnimationEffectWipe:
                _wipeDirection = direction == SettingsService.WidgetAnimationSlideDirectionNone
                    ? SettingsService.WidgetAnimationSlideDirectionRight
                    : direction;
                break;
        }
    }

    public void PrepareVisualState(double offsetX, double offsetY, float opacity, float scale)
    {
        _lastCommittedPosition = null;
        // Capture the destination monitor while the HWND still rests there.
        // Show animations move the native window off-screen during preparation,
        // where a later monitor lookup could otherwise return the wrong display.
        _preparedRefreshRateHz = Win32Helper.GetDisplayRefreshRateForWindow(_windowHandle);
        _preparedOffsetX = offsetX;
        _preparedOffsetY = offsetY;
        _preparedOpacity = opacity;
        _preparedScale = scale;
        var bounds = _getAnimationBounds();
        _preparedRefreshAnchorX = (int)Math.Round(bounds.X + bounds.Width / 2);
        _preparedRefreshAnchorY = (int)Math.Round(bounds.Y + bounds.Height / 2);
        _targetPosition = new PointInt32(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y));
        ApplyWindowOffset(offsetX, offsetY);

        _rootElement.Opacity = 1;
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);
        ClearWipeClip(visual);
        visual.CenterPoint = GetVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.RotationAngleInDegrees = _showStartRotation;
        visual.Opacity = RestingOpacity;
        visual.Scale = new Vector3(scale, scale, 1.0f);
        visual.Opacity = Math.Clamp(opacity, 0.0f, 1.0f);
    }

    public void PrepareHiddenState()
    {
        // PrepareTrayShowAnimation already established the effect-specific start state.
        // Do not replace it with an empty XAML surface after the native window is shown.
        if (_targetPosition.HasValue)
        {
            ApplyWindowOffset(_preparedOffsetX, _preparedOffsetY);
            var v = GetCachedRootVisual();
            StopVisualAnimations(v);
            ClearWipeClip(v);
            v.Opacity = Math.Clamp(_preparedOpacity, 0.0f, 1.0f);
            v.CenterPoint = GetVisualCenterPoint();
            v.RotationAngleInDegrees = _showStartRotation;
            v.Scale = new Vector3(_preparedScale, _preparedScale, 1.0f);
            return;
        }

        _rootElement.Opacity = 1;
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);
        ClearWipeClip(visual);
        visual.Offset = Vector3.Zero;
        visual.RotationAngleInDegrees = 0f;
        visual.Opacity = RestingOpacity;
        visual.Scale = new Vector3(RestingScale, RestingScale, 1.0f);
        visual.Opacity = SoftOpacity;
    }

    public void Animate(
        double fromOffsetX,
        double fromOffsetY,
        double toOffsetX,
        double toOffsetY,
        float fromOpacity,
        float toOpacity,
        float fromScale,
        float toScale,
        int durationMs,
        bool isShowing,
        long generation,
        string easingIntensity,
        Action completed,
        Action? failed = null)
    {
        try
        {
        _log(
            $"AnimateStart mode={(isShowing ? "show" : "hide")} gen={generation} durationMs={durationMs} " +
            $"windowOffset=({fromOffsetX:F0},{fromOffsetY:F0})->({toOffsetX:F0},{toOffsetY:F0}) " +
            $"windowOpacity={fromOpacity:F2}->{toOpacity:F2}");
        Stop();
        if (_targetPosition is null)
        {
            PrepareVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);
        }
        else
        {
            ApplyWindowOffset(fromOffsetX, fromOffsetY);
        }

        // Ensure visual is ready and any previous animations are stopped.
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);

        if (durationMs <= 1)
        {
            // Ensure final visual state is applied for instant transitions.
            visual.Opacity = toOpacity;
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Scale = new Vector3(toScale, toScale, 1);
            visual.RotationAngleInDegrees = isShowing ? 0f : _hideEndRotation;
            ClearWipeClip(visual);
            CompleteAnimation(toOffsetX, toOffsetY, isShowing, generation, completed);
            return;
        }

        // ── Opacity & Scale: Composition KeyFrame animations (GPU-driven) ──
        var compositor = GetCachedCompositor(visual);
        var easing = _compositionResources.GetTrayEasing(compositor, easingIntensity, isShowing);
        var duration = TimeSpan.FromMilliseconds(durationMs);

        if (_useCrossingFade)
        {
            // Crossing fade replaces the opacity keyframe animation: opacity
            // tracks the boundary-relative displacement of the window frame
            // by frame. Anchor the first presented frame here so the parked
            // state is already correct.
            visual.Opacity = ComputeCrossingFadeOpacity(fromOffsetX, fromOffsetY);
        }
        else
        {
            StartOpacityAnimation(visual, compositor, easing, duration, fromOpacity, toOpacity);
        }

        // Scale animation
        if (Math.Abs(fromScale - toScale) > 0.001f)
        {
            visual.CenterPoint = GetVisualCenterPoint();
            var scaleAnim = _compositionResources.GetVector3(compositor, WidgetAnimationTemplate.TrayScale);
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1));
            scaleAnim.InsertKeyFrame(1, new Vector3(toScale, toScale, 1), easing);
            visual.Scale = new Vector3(fromScale, fromScale, 1);
            visual.StartAnimation("Scale", scaleAnim);
        }
        else
        {
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Scale = new Vector3(toScale, toScale, 1);
        }

        StartTiltAnimation(visual, compositor, easing, duration, isShowing);
        StartWipeAnimation(visual, compositor, easing, duration, isShowing);

        // Native movement shares the interaction clock; opacity/scale remain
        // compositor animations and do not require CPU property updates.
        _renderFromOffsetX = fromOffsetX;
        _renderFromOffsetY = fromOffsetY;
        _renderToOffsetX = toOffsetX;
        _renderToOffsetY = toOffsetY;
        _renderDurationMs = durationMs;
        _renderIsShowing = isShowing;
        _renderGeneration = generation;
        _renderCompleted = completed;
        _renderFailed = failed;
        _renderStopwatch = Stopwatch.StartNew();
        _isRendering = true;
        long startedTimestamp = Stopwatch.GetTimestamp();
        _renderFrameTracker = new WidgetTrayAnimationFrameTracker(
            startedTimestamp,
            [_preparedRefreshRateHz]);
        _renderPacing.Reset(0, GetAnimationFrameBudgetMilliseconds());

        // Use the same easing for window position interpolation.
        _renderEasingIntensity = easingIntensity;

        _renderFrameRegistration = WidgetCompactAnimationCoordinator.Register(
            OnRenderingFrame, GetAnimationFrameBudgetMilliseconds);
        }
        catch (Exception ex)
        {
            _log($"Animation start failed: {ex.Message}");
            try { AbortPositionAnimation(generation, failed); }
            catch (Exception cleanupError) { _log($"Animation failure cleanup: {cleanupError.Message}"); }
        }
    }

    /// <summary>
    /// Shared-clock variant of <see cref="Animate"/>: prepares state and starts
    /// the GPU-driven opacity/scale Composition animations, but leaves window
    /// position interpolation to the batch driver (single clock + atomic
    /// DeferWindowPos commit for all windows in lockstep).
    /// Returns null when the transition completes instantly (duration &lt;= 1ms).
    /// </summary>
    public WidgetTrayBatchAnimationEntry? BeginSharedAnimate(
        double fromOffsetX,
        double fromOffsetY,
        double toOffsetX,
        double toOffsetY,
        float fromOpacity,
        float toOpacity,
        float fromScale,
        float toScale,
        int durationMs,
        bool isShowing,
        long generation,
        string easingIntensity,
        Action completed,
        Action? failed = null)
    {
        try
        {
        _log(
            $"SharedAnimateStart mode={(isShowing ? "show" : "hide")} gen={generation} durationMs={durationMs} " +
            $"windowOffset=({fromOffsetX:F0},{fromOffsetY:F0})->({toOffsetX:F0},{toOffsetY:F0})");
        Stop();
        if (_targetPosition is null)
        {
            PrepareVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);
        }
        else
        {
            ApplyWindowOffset(fromOffsetX, fromOffsetY);
        }

        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);

        if (durationMs <= 1)
        {
            visual.Opacity = toOpacity;
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Scale = new Vector3(toScale, toScale, 1);
            visual.RotationAngleInDegrees = isShowing ? 0f : _hideEndRotation;
            ClearWipeClip(visual);
            CompleteAnimation(toOffsetX, toOffsetY, isShowing, generation, completed);
            return null;
        }

        // ── Opacity & Scale: Composition KeyFrame animations (GPU-driven) ──
        var compositor = GetCachedCompositor(visual);
        var easing = _compositionResources.GetTrayEasing(compositor, easingIntensity, isShowing);
        var duration = TimeSpan.FromMilliseconds(durationMs);

        if (_useCrossingFade)
        {
            // Crossing fade replaces the opacity keyframe animation: opacity
            // tracks the boundary-relative displacement of the window frame
            // by frame. Anchor the first presented frame here so the parked
            // state is already correct.
            visual.Opacity = ComputeCrossingFadeOpacity(fromOffsetX, fromOffsetY);
        }
        else
        {
            StartOpacityAnimation(visual, compositor, easing, duration, fromOpacity, toOpacity);
        }

        if (Math.Abs(fromScale - toScale) > 0.001f)
        {
            visual.CenterPoint = GetVisualCenterPoint();
            var scaleAnim = _compositionResources.GetVector3(compositor, WidgetAnimationTemplate.TrayScale);
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1));
            scaleAnim.InsertKeyFrame(1, new Vector3(toScale, toScale, 1), easing);
            visual.Scale = new Vector3(fromScale, fromScale, 1);
            visual.StartAnimation("Scale", scaleAnim);
        }
        else
        {
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Scale = new Vector3(toScale, toScale, 1);
        }

        StartTiltAnimation(visual, compositor, easing, duration, isShowing);
        StartWipeAnimation(visual, compositor, easing, duration, isShowing);

        // Position frames are owned by the batch driver from here on.
        var basePosition = _targetPosition ?? GetCurrentBasePosition();
        long capturedGeneration = generation;

        return new WidgetTrayBatchAnimationEntry
        {
            WindowHandle = _windowHandle,
            BaseX = basePosition.X,
            BaseY = basePosition.Y,
            FromOffsetX = fromOffsetX,
            FromOffsetY = fromOffsetY,
            ToOffsetX = toOffsetX,
            ToOffsetY = toOffsetY,
            RefreshRateHz = _preparedRefreshRateHz,
            RefreshAnchorX = _preparedRefreshAnchorX,
            RefreshAnchorY = _preparedRefreshAnchorY,
            FrameVisual = _useCrossingFade
                ? progress =>
                {
                    // Crossing fade on the shared clock: opacity follows this
                    // window's own boundary-relative displacement, so lockstep
                    // group slides still dissolve each window as it crosses.
                    if (capturedGeneration != Generation)
                    {
                        return;
                    }

                    GetCachedRootVisual().Opacity = ComputeCrossingFadeOpacity(
                        Lerp(fromOffsetX, toOffsetX, progress),
                        Lerp(fromOffsetY, toOffsetY, progress));
                }
                : null,
            IsValid = () => capturedGeneration == Generation,
            Completed = () => CompleteAnimation(toOffsetX, toOffsetY, isShowing, capturedGeneration, completed,
                positionAlreadyCommitted: true),
            Failed = () => AbortPositionAnimation(capturedGeneration, failed)
        };
        }
        catch
        {
            try { AbortPositionAnimation(generation, failed); }
            catch (Exception cleanupError) { _log($"Animation failure cleanup: {cleanupError.Message}"); }
            throw;
        }
    }

    private PointInt32 GetCurrentBasePosition()
    {
        var bounds = _getAnimationBounds();
        return new PointInt32(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y));
    }

    /// <summary>
    /// The bounds the window rests at when no tray-animation offset is applied.
    /// While a position transition is prepared/running the HWND is physically
    /// displaced offscreen, so group-offset math must use this resting position
    /// instead of the displaced physical one.
    /// </summary>
    public Windows.Foundation.Rect GetRestingAnimationBounds()
    {
        var current = _getAnimationBounds();
        if (_targetPosition is { } target)
        {
            return new Windows.Foundation.Rect(target.X, target.Y, current.Width, current.Height);
        }

        return current;
    }

    private void OnRenderingFrame()
    {
        try // ✅ 添加异常保护防止渲染线程崩溃
        {
            if (!_isRendering || _renderGeneration != Generation)
            {
                StopRendering("superseded");
                return;
            }

            var stopwatch = _renderStopwatch;
            if (stopwatch is null)
            {
                StopRendering("invalid-clock");
                return;
            }

            long timestamp = Stopwatch.GetTimestamp();
            _renderFrameTracker?.RecordFrame(timestamp);

            double nowMs = stopwatch.Elapsed.TotalMilliseconds;
            double rawProgress = Math.Clamp(nowMs / _renderDurationMs, 0.0, 1.0);
            bool finalFrame = rawProgress >= 1.0;
            double budgetMs = GetAnimationFrameBudgetMilliseconds();
            if (!_renderPacing.ShouldSubmit(nowMs, budgetMs, force: finalFrame))
            {
                return;
            }
            double easedProgress = WidgetAnimationSettings.Ease(rawProgress, _renderEasingIntensity, _renderIsShowing);
            double currentOffsetX = Lerp(_renderFromOffsetX, _renderToOffsetX, easedProgress);
            double currentOffsetY = Lerp(_renderFromOffsetY, _renderToOffsetY, easedProgress);

            // Move the window; opacity/scale stay GPU-driven by Composition
            // animations — except the crossing fade, which rides this very
            // position clock so it stays pixel-exact against the monitor
            // boundary regardless of easing.
            if (_useCrossingFade)
            {
                GetCachedRootVisual().Opacity = ComputeCrossingFadeOpacity(currentOffsetX, currentOffsetY);
            }

            long started = Stopwatch.GetTimestamp();
            bool submitted = ApplyWindowOffset(currentOffsetX, currentOffsetY, force: finalFrame);
            if (submitted)
            {
                _renderPacing.RecordSubmission(nowMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                _renderFrameTracker?.RecordPositionSubmission(timestamp,
                    (int)Math.Round(1000.0 / Math.Max(0.1, budgetMs)));
            }

            if (rawProgress < 1.0)
            {
                return;
            }

            StopRendering("completed");

            CompleteAnimation(
                _renderToOffsetX,
                _renderToOffsetY,
                _renderIsShowing,
                _renderGeneration,
                _renderCompleted,
                positionAlreadyCommitted: true);
        }
        catch (Exception ex)
        {
            // 记录异常日志但不让渲染线程崩溃
            App.Log($"[WidgetTrayAnimationController] Frame exception: {ex.Message}\n{ex.StackTrace}");
            StopRendering("failed");
            try { AbortPositionAnimation(_renderGeneration, _renderFailed); }
            catch (Exception cleanupError) { _log($"Animation failure cleanup: {cleanupError.Message}"); }
        }
    }

    private void StopRendering(string outcome = "cancelled")
    {
        if (!_isRendering)
        {
            return;
        }

        _isRendering = false;
        _renderStopwatch = null;
        _renderFrameRegistration?.Dispose();
        _renderFrameRegistration = null;
        WidgetTrayAnimationFrameTracker? tracker = _renderFrameTracker;
        _renderFrameTracker = null;
        WidgetTrayAnimationDiagnostics.Report(
            tracker,
            Stopwatch.GetTimestamp(),
            _renderIsShowing,
            outcome,
            $"window:0x{_windowHandle.ToInt64():X}",
            _log);
    }

    public void Stop()
    {
        CancelContentReadyCallback();
        StopRendering();
        _lastCommittedPosition = null;
        RestoreDwmTransitions();

        if (_cachedRootVisual is { } visual)
        {
            try
            {
                StopVisualAnimations(visual);
                ClearWipeClip(visual);
            }
            catch
            {
                // The Composition Visual may be invalid if the window is
                // being torn down. Swallow to avoid stowed WinRT exceptions.
            }
        }
    }

    private void OnContentReadyRenderingFrame(object? sender, object e)
    {
        if (_contentReadyGeneration != Generation)
        {
            CancelContentReadyCallback();
            return;
        }

        // The first frame commits the newly shown XAML surface while the HWND
        // is still outside the work area. Start moving on the following frame.
        if (++_contentReadyFrameCount < 2)
        {
            return;
        }

        Action? action = _contentReadyAction;
        CancelContentReadyCallback();
        action?.Invoke();
    }

    private void CancelContentReadyCallback()
    {
        if (_contentReadyRenderingHandler is not null)
        {
            CompositionTarget.Rendering -= _contentReadyRenderingHandler;
            _contentReadyRenderingHandler = null;
        }

        _contentReadyAction = null;
        _contentReadyFrameCount = 0;
    }

    public void StopAndRestoreWindowPosition()
    {
        Stop();
        RestoreWindowPosition();
    }

    private double GetAnimationFrameBudgetMilliseconds() =>
        WidgetCompactAnimationCoordinator.GetFrameBudgetMillisecondsForPoint(
            _preparedRefreshAnchorX, _preparedRefreshAnchorY);

    public void Dispose()
    {
        NextGeneration();
        try
        {
            Stop();
        }
        finally
        {
            _compositionResources.Dispose();
            _cachedRootVisual = null;
            _cachedCompositor = null;
        }
    }

    public void RestoreVisualState()
    {
        try
        {
            _rootElement.Opacity = 1;
            var visual = GetCachedRootVisual();
            StopVisualAnimations(visual);
            ClearWipeClip(visual);
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Offset = Vector3.Zero;
            visual.RotationAngleInDegrees = 0f;
            visual.Opacity = RestingOpacity;
            visual.Scale = new Vector3(RestingScale, RestingScale, 1.0f);
        }
        catch
        {
            // The Composition Visual may be invalid if the window is
            // being torn down. Swallow to avoid stowed WinRT exceptions.
        }
    }

    public void RestoreWindowPosition()
    {
        if (_targetPosition is { } target)
        {
            IsApplyingBounds = true;
            try
            {
                if (_lastCommittedPosition is not { } committed ||
                    committed.X != target.X || committed.Y != target.Y)
                {
                    MoveNativeWindow(target);
                }
            }
            finally
            {
                IsApplyingBounds = false;
            }
        }

        _targetPosition = null;
        _lastCommittedPosition = null;
    }

    private void AbortPositionAnimation(long generation, Action? failed)
    {
        if (generation != Generation)
        {
            return;
        }
        try
        {
            Stop();
            SetOffsetOverride(null, null);
            RestoreWindowPosition();
        }
        catch (Exception ex)
        {
            _log($"Animation failure position restore: {ex.Message}");
        }
        finally
        {
            RestoreVisualState();
            try { RevealWindowForTrayShow(); }
            finally { failed?.Invoke(); }
        }
    }

    private void CompleteAnimation(
        double finalOffsetX,
        double finalOffsetY,
        bool isShowing,
        long generation,
        Action? completed,
        bool positionAlreadyCommitted = false)
    {
        if (generation != Generation)
        {
            return;
        }

        if (!positionAlreadyCommitted)
        {
            ApplyWindowOffset(finalOffsetX, finalOffsetY, force: true);
        }
        else
        {
            var target = _targetPosition ?? GetCurrentBasePosition();
            _lastCommittedPosition = new PointInt32(
                target.X + (int)Math.Round(finalOffsetX),
                target.Y + (int)Math.Round(finalOffsetY));
        }
        SetOffsetOverride(null, null);
        RestoreDwmTransitions();
        if (_cachedRootVisual is { } visual)
        {
            try { ClearWipeClip(visual); }
            catch
            {
                // Visual teardown races are non-fatal here; Stop() also clears.
            }
        }
        _log($"AnimateCompleted mode={(isShowing ? "show" : "hide")} gen={generation}");
        completed?.Invoke();
    }

    private bool ApplyWindowOffset(double offsetX, double offsetY, bool force = false)
    {
        // Skip the GetWindowRect round-trip when the resting position is
        // already known — it only matters as a fallback.
        var target = _targetPosition ?? GetCurrentBasePosition();
        var nextPosition = new PointInt32(
            target.X + (int)Math.Round(offsetX),
            target.Y + (int)Math.Round(offsetY));

        if (!force && _lastCommittedPosition is { } previous &&
            previous.X == nextPosition.X && previous.Y == nextPosition.Y)
        {
            return false;
        }

        IsApplyingBounds = true;
        try
        {
            MoveNativeWindow(nextPosition);
            _lastCommittedPosition = nextPosition;
        }
        finally
        {
            IsApplyingBounds = false;
        }
        return true;
    }

    private void MoveNativeWindow(PointInt32 position)
    {
        // Direct P/Invoke SetWindowPos — bypasses AppWindow.Move() WinRT
        // marshalling overhead for lower per-frame latency.
        if (!Win32Helper.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            position.X,
            position.Y,
            0, 0,
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not commit tray animation window position.");
        }
    }

    private Microsoft.UI.Composition.Compositor GetCachedCompositor(Microsoft.UI.Composition.Visual visual)
    {
        return _cachedCompositor ??= visual.Compositor;
    }

    private void StartOpacityAnimation(
        Microsoft.UI.Composition.Visual visual,
        Microsoft.UI.Composition.Compositor compositor,
        Microsoft.UI.Composition.CompositionEasingFunction easing,
        TimeSpan duration,
        float fromOpacity,
        float toOpacity)
    {
        if (Math.Abs(fromOpacity - toOpacity) <= 0.001f)
        {
            visual.Opacity = toOpacity;
            return;
        }

        var opacityAnim = _compositionResources.GetScalar(compositor, WidgetAnimationTemplate.TrayOpacity);
        opacityAnim.Duration = duration;
        opacityAnim.InsertKeyFrame(0, fromOpacity);
        opacityAnim.InsertKeyFrame(1, toOpacity, easing);
        visual.Opacity = fromOpacity;
        visual.StartAnimation("Opacity", opacityAnim);
    }

    private void StartTiltAnimation(
        Microsoft.UI.Composition.Visual visual,
        Microsoft.UI.Composition.Compositor compositor,
        Microsoft.UI.Composition.CompositionEasingFunction easing,
        TimeSpan duration,
        bool isShowing)
    {
        float fromRotation = isShowing ? _showStartRotation : 0f;
        float toRotation = isShowing ? 0f : _hideEndRotation;
        if (Math.Abs(fromRotation - toRotation) <= 0.01f)
        {
            visual.RotationAngleInDegrees = toRotation;
            return;
        }

        var rotationAnim = compositor.CreateScalarKeyFrameAnimation();
        rotationAnim.Duration = duration;
        rotationAnim.InsertKeyFrame(0, fromRotation);
        rotationAnim.InsertKeyFrame(1, toRotation, easing);
        visual.RotationAngleInDegrees = fromRotation;
        visual.StartAnimation("RotationAngleInDegrees", rotationAnim);
    }

    private void StartWipeAnimation(
        Microsoft.UI.Composition.Visual visual,
        Microsoft.UI.Composition.Compositor compositor,
        Microsoft.UI.Composition.CompositionEasingFunction easing,
        TimeSpan duration,
        bool isShowing)
    {
        if (_wipeDirection is null)
        {
            ClearWipeClip(visual);
            return;
        }

        var clip = _cachedRootClip ??= compositor.CreateInsetClip();
        visual.Clip = clip;
        (string insetProperty, float fullInset) = GetWipeInsetTarget();
        float fromInset = isShowing ? fullInset : 0f;
        float toInset = isShowing ? 0f : fullInset;

        var wipeAnim = compositor.CreateScalarKeyFrameAnimation();
        wipeAnim.Duration = duration;
        wipeAnim.InsertKeyFrame(0, fromInset);
        wipeAnim.InsertKeyFrame(1, toInset, easing);
        ApplyWipeInset(clip, fromInset);
        clip.StartAnimation(insetProperty, wipeAnim);
    }

    private (string InsetProperty, float FullInset) GetWipeInsetTarget()
    {
        return _wipeDirection switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft =>
                ("RightInset", (float)Math.Max(1, _rootElement.ActualWidth)),
            SettingsService.WidgetAnimationSlideDirectionUp =>
                ("BottomInset", (float)Math.Max(1, _rootElement.ActualHeight)),
            SettingsService.WidgetAnimationSlideDirectionDown =>
                ("TopInset", (float)Math.Max(1, _rootElement.ActualHeight)),
            _ => ("LeftInset", (float)Math.Max(1, _rootElement.ActualWidth))
        };
    }

    private void ApplyWipeInset(Microsoft.UI.Composition.InsetClip clip, float value)
    {
        clip.LeftInset = 0f;
        clip.RightInset = 0f;
        clip.TopInset = 0f;
        clip.BottomInset = 0f;
        switch (_wipeDirection)
        {
            case SettingsService.WidgetAnimationSlideDirectionLeft:
                clip.RightInset = value;
                break;
            case SettingsService.WidgetAnimationSlideDirectionUp:
                clip.BottomInset = value;
                break;
            case SettingsService.WidgetAnimationSlideDirectionDown:
                clip.TopInset = value;
                break;
            default:
                clip.LeftInset = value;
                break;
        }
    }

    private void ClearWipeClip(Microsoft.UI.Composition.Visual visual)
    {
        Microsoft.UI.Composition.InsetClip? clip = _cachedRootClip;
        if (clip is null)
        {
            return;
        }

        clip.StopAnimation("LeftInset");
        clip.StopAnimation("RightInset");
        clip.StopAnimation("TopInset");
        clip.StopAnimation("BottomInset");
        visual.Clip = null;
        _cachedRootClip = null;
    }

    private (double Left, double Right, double Up, double Down) GetOffscreenSlideOffsets()
    {
        if (_offsetOverrideX.HasValue || _offsetOverrideY.HasValue)
        {
            double horizontal = Math.Abs(_offsetOverrideX.GetValueOrDefault());
            double vertical = Math.Abs(_offsetOverrideY.GetValueOrDefault());
            return (
                horizontal > 0 ? horizontal : MinWidgetSlideOffset,
                horizontal > 0 ? horizontal : MinWidgetSlideOffset,
                vertical > 0 ? vertical : MinWidgetSlideOffset,
                vertical > 0 ? vertical : MinWidgetSlideOffset);
        }

        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        DisplayArea? displayArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            App.Log(
                $"[TrayAnimation] Display area unavailable for " +
                $"hwnd=0x{_windowHandle.ToInt64():X}; using minimum slide offsets");
            return (
                MinWidgetSlideOffset,
                MinWidgetSlideOffset,
                MinWidgetSlideOffset,
                MinWidgetSlideOffset);
        }

        RectInt32 workArea = displayArea.WorkArea;
        var bounds = _getAnimationBounds();
        double x = bounds.X;
        double y = bounds.Y;
        double width = Math.Max(MinWidgetSlideOffset, bounds.Width);
        double height = Math.Max(MinWidgetSlideOffset, bounds.Height);

        double left = Math.Max(MinWidgetSlideOffset, (x + width) - workArea.X + OffscreenSlidePadding);
        double right = Math.Max(MinWidgetSlideOffset, (workArea.X + workArea.Width) - x + OffscreenSlidePadding);
        double up = Math.Max(MinWidgetSlideOffset, (y + height) - workArea.Y + OffscreenSlidePadding);
        double down = Math.Max(MinWidgetSlideOffset, (workArea.Y + workArea.Height) - y + OffscreenSlidePadding);
        return (left, right, up, down);
    }

    /// <summary>
    /// Arms the per-window crossing fade when the slide direction leads onto
    /// an adjacent display. The fade span is derived from this window's own
    /// resting geometry, so lockstep group slides still dissolve each window
    /// as it individually crosses the monitor boundary.
    /// </summary>
    private void ResolveCrossingFadeState(string effectiveDirection, double unconfinedTravel)
    {
        _useCrossingFade = false;
        _crossingFadeStartOffset = 0;
        _crossingFadeEndOffset = 0;
        _crossingFadeAxisIsX = false;

        if (unconfinedTravel <= 0 ||
            effectiveDirection == SettingsService.WidgetAnimationSlideDirectionNone)
        {
            return;
        }

        // Resting geometry, not physical: show profiles are re-created after
        // prepare has already parked the HWND on the adjacent display, and the
        // fade span must describe the resting position it animates back to.
        Windows.Foundation.Rect rest = GetRestingAnimationBounds();
        var displayArea = DisplayArea.GetFromPoint(
            new PointInt32(
                (int)Math.Round(rest.X),
                (int)Math.Round(rest.Y)),
            DisplayAreaFallback.Primary);

        RectInt32 workArea = displayArea.WorkArea;
        RectInt32 outerBounds = displayArea.OuterBounds;
        double x = rest.X;
        double y = rest.Y;
        double width = Math.Max(MinWidgetSlideOffset, rest.Width);
        double height = Math.Max(MinWidgetSlideOffset, rest.Height);

        WidgetSlideBoundaryPolicy.CrossingFadeDecision decision = effectiveDirection switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft =>
                WidgetSlideBoundaryPolicy.ResolveCrossingFade(
                    unconfinedTravel,
                    farEdge: x,
                    nearEdge: x + width,
                    workAreaEdge: workArea.X,
                    WidgetSlideBoundaryPolicy.HasAdjacentDisplayBeyondEdge(
                        outerBounds, SettingsService.WidgetAnimationSlideDirectionLeft)),
            SettingsService.WidgetAnimationSlideDirectionUp =>
                WidgetSlideBoundaryPolicy.ResolveCrossingFade(
                    unconfinedTravel,
                    farEdge: y,
                    nearEdge: y + height,
                    workAreaEdge: workArea.Y,
                    WidgetSlideBoundaryPolicy.HasAdjacentDisplayBeyondEdge(
                        outerBounds, SettingsService.WidgetAnimationSlideDirectionUp)),
            SettingsService.WidgetAnimationSlideDirectionDown =>
                WidgetSlideBoundaryPolicy.ResolveCrossingFade(
                    unconfinedTravel,
                    farEdge: y + height,
                    nearEdge: y,
                    workAreaEdge: workArea.Y + workArea.Height,
                    WidgetSlideBoundaryPolicy.HasAdjacentDisplayBeyondEdge(
                        outerBounds, SettingsService.WidgetAnimationSlideDirectionDown)),
            SettingsService.WidgetAnimationSlideDirectionRight =>
                WidgetSlideBoundaryPolicy.ResolveCrossingFade(
                    unconfinedTravel,
                    farEdge: x + width,
                    nearEdge: x,
                    workAreaEdge: workArea.X + workArea.Width,
                    WidgetSlideBoundaryPolicy.HasAdjacentDisplayBeyondEdge(
                        outerBounds, SettingsService.WidgetAnimationSlideDirectionRight)),
            _ => default
        };

        if (!decision.UseCrossingFade)
        {
            return;
        }

        _useCrossingFade = true;
        _crossingFadeStartOffset = decision.StartOffset;
        _crossingFadeEndOffset = decision.EndOffset;
        _crossingFadeAxisIsX = effectiveDirection is
            SettingsService.WidgetAnimationSlideDirectionLeft or
            SettingsService.WidgetAnimationSlideDirectionRight;
    }

    /// <summary>
    /// Opacity implied by the window's current slide displacement: fully
    /// opaque until the leading edge reaches the monitor boundary, then a
    /// linear fade to invisible as the window crosses onto the adjacent
    /// display. Symmetric for show (displacement shrinks) and hide.
    /// </summary>
    private float ComputeCrossingFadeOpacity(double offsetX, double offsetY)
    {
        double travel = Math.Abs(_crossingFadeAxisIsX ? offsetX : offsetY);
        double span = _crossingFadeEndOffset - _crossingFadeStartOffset;
        if (span <= 0)
        {
            return travel >= _crossingFadeEndOffset ? SoftOpacity : RestingOpacity;
        }

        double progress = (travel - _crossingFadeStartOffset) / span;
        return (float)(1.0 - Math.Clamp(progress, 0.0, 1.0));
    }

    private Microsoft.UI.Composition.Visual GetCachedRootVisual()
    {
        return _cachedRootVisual ??= ElementCompositionPreview.GetElementVisual(_rootElement);
    }

    private Vector3 GetVisualCenterPoint()
    {
        // EdgeScale anchors scaling on the slide-side edge midpoint so the
        // widget appears to be absorbed into / ejected from that edge;
        // every other effect scales around the visual center.
        return new Vector3(
            (float)Math.Max(0, _rootElement.ActualWidth * _centerAnchorX),
            (float)Math.Max(0, _rootElement.ActualHeight * _centerAnchorY),
            0);
    }

    private static void StopVisualAnimations(Microsoft.UI.Composition.Visual visual)
    {
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.StopAnimation("RotationAngleInDegrees");
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }
}
