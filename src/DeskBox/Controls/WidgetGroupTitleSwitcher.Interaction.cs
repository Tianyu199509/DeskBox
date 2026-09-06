using DeskBox.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace DeskBox.Controls;

public sealed partial class WidgetGroupTitleSwitcher
{
    private DateTimeOffset _pointerEnteredAt;
    private string? _pendingWheelTargetId;
    private double _wheelAccumulator;
    private DateTimeOffset _lastObservedWheelInputAt;
    private int _wheelGestureDirection;
    private bool _wheelGestureCommitted;
    private bool _pickerOpen;
    private bool _isPointerOverSelector;
    private bool _isSelectorPressed;
    private Storyboard? _identityStoryboard;
    private Storyboard? _interactionChromeStoryboard;
    private Storyboard? _wheelFeedbackStoryboard;
    private Storyboard? _scrollSurfaceStoryboard;
    private Storyboard? _boundaryReboundStoryboard;
    private DispatcherTimer? _wheelFeedbackFallbackTimer;
    private double _pendingIdentityWidth;

    private void RegisterKeyboardAccelerators()
    {
        var openPicker = new KeyboardAccelerator
        {
            Key = VirtualKey.Down,
            Modifiers = VirtualKeyModifiers.Menu
        };
        openPicker.Invoked += (_, args) =>
        {
            if (_presentation is null || _pickerOpen)
            {
                return;
            }

            OpenPicker();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(openPicker);
    }

    private void SelectorButton_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        CoreVirtualKeyStates menuState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Menu);
        bool menu =
            menuState.HasFlag(CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Down && menu)
        {
            OpenPicker(SelectorButton);
            e.Handled = true;
        }

        // Enter and Space intentionally use Button's native invocation path,
        // preserving standard focus, pressed-state and accessibility behavior.
    }

    internal bool TryHandleKeyboardNavigation(KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Tab ||
            !IsVirtualKeyDown(VirtualKey.Control) ||
            _presentation is null ||
            _presentation.Members.Count < 2 ||
            _pickerOpen)
        {
            return false;
        }

        if (IsTabInteractionBusy || _draggingMemberId is not null)
        {
            return true;
        }

        return SwitchRelative(
            IsVirtualKeyDown(VirtualKey.Shift) ? -1 : 1,
            WidgetGroupSwitchOrigin.Keyboard);
    }

    private static bool IsVirtualKeyDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);
    }

    private void SelectorButton_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isPointerOverSelector = true;
        _pointerEnteredAt = DateTimeOffset.UtcNow;
        ResetWheelGesture();
        UpdateInteractionChrome();
    }

    private void SelectorButton_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isPointerOverSelector = false;
        _isSelectorPressed = false;
        ResetWheelGesture();
        CancelWheelFeedback();
        UpdateInteractionChrome();
    }

    private void SelectorButton_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SelectorButton).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isSelectorPressed = true;
        BeginDetachLongPress(SelectorButton, e);
        UpdateInteractionChrome();
    }

    private void SelectorButton_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isSelectorPressed = false;
        GroupTitle_PointerReleased(sender, e);
        UpdateInteractionChrome();
    }

    internal void HandleTitleBarPointerWheel(
        UIElement source,
        PointerRoutedEventArgs e)
    {
        ProcessPointerWheel(source, e);
    }

    private void TabsPanel_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        _pointerEnteredAt = DateTimeOffset.UtcNow;
        ResetWheelGesture();
    }

    private void TabsPanel_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        ResetWheelGesture();
        CancelWheelFeedback();
    }

    private void ProcessPointerWheel(
        UIElement source,
        PointerRoutedEventArgs e)
    {
        double delta =
            e.GetCurrentPoint(source).Properties.MouseWheelDelta;
        if (ProcessWheelDelta(delta))
        {
            e.Handled = true;
        }
    }

    internal bool HandleNativeWheel(int delta)
    {
        return ProcessWheelDelta(delta);
    }

    private bool ProcessWheelDelta(double delta)
    {
        if (!WheelSwitchEnabled || IsTabInteractionBusy || _draggingMemberId is not null ||
            _pickerOpen ||
            _presentation is null ||
            _presentation.Members.Count < 2 ||
            delta == 0)
        {
            return false;
        }

        bool consumedStep =
            WidgetGroupNavigationInteractionPolicy.TryConsumeWheelGestureStep(
                ref _wheelAccumulator,
                ref _lastObservedWheelInputAt,
                ref _wheelGestureDirection,
                ref _wheelGestureCommitted,
                delta,
                DateTimeOffset.UtcNow,
                out int direction);
        if (!consumedStep)
        {
            return true;
        }

        // Animate only committed wheel steps. Precision touchpads can emit a
        // dense stream of sub-threshold deltas; restarting storyboards for
        // every raw delta adds work without communicating another navigation.
        try
        {
            AnimateWheelDirectionFeedback(scrollsUp: direction < 0);
            AnimateScrollSurfaceFeedback();
        }
        catch (Exception ex)
        {
            // Visual feedback must never prevent the actual navigation. WinUI
            // can reject a storyboard at runtime when a theme/animation state
            // changes while the wheel event is being processed.
            _wheelFeedbackStoryboard = null;
            UpWheelFeedback.Opacity = 0;
            DownWheelFeedback.Opacity = 0;
            UpWheelFeedbackTransform.ScaleY = 1;
            DownWheelFeedbackTransform.ScaleY = 1;
            App.Log($"[WidgetGroup] Wheel feedback animation failed: {ex}");
        }

        if (SwitchRelative(direction, WidgetGroupSwitchOrigin.Wheel))
        {
            CancelBoundaryRebound();
        }
        else
        {
            AnimateBoundaryRebound(direction);
        }

        // The pointer is over the explicit switcher hot zone. Consume even an
        // edge attempt so it cannot accidentally scroll member content.
        return true;
    }

    private void ResetWheelGesture()
    {
        _wheelAccumulator = 0;
        _lastObservedWheelInputAt = default;
        _wheelGestureDirection = 0;
        _wheelGestureCommitted = false;
    }

    private void AnimateWheelDirectionFeedback(bool scrollsUp)
    {
        if (XamlRoot is null)
        {
            return;
        }

        StopWheelFeedbackStoryboard();
        UpWheelFeedback.Opacity = 0;
        DownWheelFeedback.Opacity = 0;
        UpWheelFeedbackTransform.ScaleY = 1;
        DownWheelFeedbackTransform.ScaleY = 1;

        Border target = scrollsUp
            ? UpWheelFeedback
            : DownWheelFeedback;
        ScaleTransform transform = scrollsUp
            ? UpWheelFeedbackTransform
            : DownWheelFeedbackTransform;
        const double peakOpacity = 0.25;

        if (!AreSystemAnimationsEnabled())
        {
            target.Opacity = peakOpacity;
            _wheelFeedbackFallbackTimer ??= CreateWheelFeedbackFallbackTimer();
            _wheelFeedbackFallbackTimer.Stop();
            _wheelFeedbackFallbackTimer.Start();
            return;
        }

        var storyboard = new Storyboard();
        var opacityAnimation = new DoubleAnimationUsingKeyFrames();
        opacityAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0
        });
        opacityAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)),
            Value = peakOpacity,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        opacityAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            Value = 0,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        });
        Storyboard.SetTarget(opacityAnimation, target);
        Storyboard.SetTargetProperty(opacityAnimation, nameof(UIElement.Opacity));
        storyboard.Children.Add(opacityAnimation);

        var scaleAnimation = new DoubleAnimationUsingKeyFrames();
        scaleAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0.84
        });
        scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(115)),
            Value = 1.035,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        scaleAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            Value = 1,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(scaleAnimation, transform);
        Storyboard.SetTargetProperty(scaleAnimation, nameof(ScaleTransform.ScaleY));
        storyboard.Children.Add(scaleAnimation);

        storyboard.Completed += WheelFeedbackStoryboard_Completed;
        _wheelFeedbackStoryboard = storyboard;
        storyboard.Begin();
    }

    private void WheelFeedbackStoryboard_Completed(object? sender, object args)
    {
        if (sender is not Storyboard storyboard)
        {
            return;
        }

        storyboard.Completed -= WheelFeedbackStoryboard_Completed;
        storyboard.Stop();
        storyboard.Children.Clear();
        if (!ReferenceEquals(_wheelFeedbackStoryboard, storyboard))
        {
            return;
        }

        _wheelFeedbackStoryboard = null;
        UpWheelFeedback.Opacity = 0;
        DownWheelFeedback.Opacity = 0;
        UpWheelFeedbackTransform.ScaleY = 1;
        DownWheelFeedbackTransform.ScaleY = 1;
    }

    private void StopWheelFeedbackStoryboard()
    {
        if (_wheelFeedbackStoryboard is not { } storyboard)
        {
            return;
        }

        _wheelFeedbackStoryboard = null;
        storyboard.Completed -= WheelFeedbackStoryboard_Completed;
        storyboard.Stop();
        storyboard.Children.Clear();
    }

    private void CancelWheelFeedback()
    {
        StopWheelFeedbackStoryboard();
        _wheelFeedbackFallbackTimer?.Stop();
        UpWheelFeedback.Opacity = 0;
        DownWheelFeedback.Opacity = 0;
        UpWheelFeedbackTransform.ScaleY = 1;
        DownWheelFeedbackTransform.ScaleY = 1;
        CancelBoundaryRebound();
        StopScrollSurfaceStoryboard();
        TitleInteractionChromeTransform.ScaleX = 1;
        TitleInteractionChromeTransform.ScaleY = 1;
    }

    private DispatcherTimer CreateWheelFeedbackFallbackTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpWheelFeedback.Opacity = 0;
            DownWheelFeedback.Opacity = 0;
        };
        return timer;
    }

    private void AnimateScrollSurfaceFeedback()
    {
        StopAndClearStoryboard(ref _interactionChromeStoryboard);
        StopScrollSurfaceStoryboard();

        if (UsesTabs)
        {
            return;
        }

        double restingOpacity = ResolveInteractionSurfaceOpacity();
        if (!AreSystemAnimationsEnabled() || XamlRoot is null)
        {
            TitleInteractionChrome.Opacity = Math.Max(restingOpacity, 0.3);
            return;
        }

        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65)),
            Value = Math.Max(restingOpacity, 0.34),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)),
            Value = restingOpacity,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
        Storyboard.SetTarget(opacity, TitleInteractionChrome);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));

        var scaleX = CreateSurfaceScaleAnimation();
        var scaleY = CreateSurfaceScaleAnimation();
        Storyboard.SetTarget(scaleX, TitleInteractionChromeTransform);
        Storyboard.SetTargetProperty(scaleX, nameof(ScaleTransform.ScaleX));
        Storyboard.SetTarget(scaleY, TitleInteractionChromeTransform);
        Storyboard.SetTargetProperty(scaleY, nameof(ScaleTransform.ScaleY));

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Completed += ScrollSurfaceStoryboard_Completed;
        _scrollSurfaceStoryboard = storyboard;
        storyboard.Begin();

        static DoubleAnimationUsingKeyFrames CreateSurfaceScaleAnimation()
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)),
                Value = 1.012,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)),
                Value = 1,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
            return animation;
        }
    }

    private void ScrollSurfaceStoryboard_Completed(object? sender, object args)
    {
        if (sender is not Storyboard storyboard)
        {
            return;
        }

        storyboard.Completed -= ScrollSurfaceStoryboard_Completed;
        storyboard.Stop();
        storyboard.Children.Clear();
        if (!ReferenceEquals(_scrollSurfaceStoryboard, storyboard))
        {
            return;
        }

        _scrollSurfaceStoryboard = null;
        TitleInteractionChrome.Opacity = ResolveInteractionSurfaceOpacity();
        TitleInteractionChromeTransform.ScaleX = 1;
        TitleInteractionChromeTransform.ScaleY = 1;
    }

    private void StopScrollSurfaceStoryboard()
    {
        if (_scrollSurfaceStoryboard is not { } storyboard)
        {
            return;
        }

        _scrollSurfaceStoryboard = null;
        storyboard.Completed -= ScrollSurfaceStoryboard_Completed;
        storyboard.Stop();
        storyboard.Children.Clear();
    }

    private void AnimateBoundaryRebound(int direction)
    {
        CancelBoundaryRebound();
        if (!AreSystemAnimationsEnabled() || XamlRoot is null || direction == 0)
        {
            return;
        }

        double attemptedOffset = -Math.Sign(direction) * 4.5;
        var transform = new TranslateTransform();
        CurrentIdentityLayer.RenderTransform = transform;
        var motion = new DoubleAnimationUsingKeyFrames();
        motion.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)),
            Value = attemptedOffset,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        motion.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(245)),
            Value = 0,
            EasingFunction = new BackEase
            {
                Amplitude = 0.24,
                EasingMode = EasingMode.EaseOut
            }
        });
        Storyboard.SetTarget(motion, transform);
        Storyboard.SetTargetProperty(motion, nameof(TranslateTransform.Y));
        var storyboard = new Storyboard();
        storyboard.Children.Add(motion);
        storyboard.Completed += BoundaryReboundStoryboard_Completed;
        _boundaryReboundStoryboard = storyboard;
        storyboard.Begin();
    }

    private void BoundaryReboundStoryboard_Completed(object? sender, object args)
    {
        if (sender is not Storyboard storyboard)
        {
            return;
        }

        storyboard.Completed -= BoundaryReboundStoryboard_Completed;
        storyboard.Stop();
        storyboard.Children.Clear();
        if (!ReferenceEquals(_boundaryReboundStoryboard, storyboard))
        {
            return;
        }

        _boundaryReboundStoryboard = null;
        CurrentIdentityLayer.RenderTransform = null;
    }

    private void CancelBoundaryRebound()
    {
        if (_boundaryReboundStoryboard is { } storyboard)
        {
            _boundaryReboundStoryboard = null;
            storyboard.Completed -= BoundaryReboundStoryboard_Completed;
            storyboard.Stop();
            storyboard.Children.Clear();
        }
        if (_identityStoryboard is null)
        {
            CurrentIdentityLayer.RenderTransform = null;
        }
    }

    private bool SwitchRelative(
        int delta,
        WidgetGroupSwitchOrigin origin)
    {
        if (_presentation is null || delta == 0 || _pickerOpen ||
            IsTabInteractionBusy || _draggingMemberId is not null)
        {
            return false;
        }

        int activeIndex = FindMemberIndex(
            _presentation.Members,
            _presentation.ActiveMemberId);
        if (origin == WidgetGroupSwitchOrigin.Wheel &&
            !string.IsNullOrWhiteSpace(_pendingWheelTargetId))
        {
            int pendingIndex = FindMemberIndex(
                _presentation.Members,
                _pendingWheelTargetId);
            if (pendingIndex >= 0)
            {
                activeIndex = pendingIndex;
            }
        }
        if (activeIndex < 0)
        {
            activeIndex = FindActiveFlagIndex(_presentation.Members);
        }
        if (!WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex,
                _presentation.Members.Count,
                delta,
                out int targetIndex,
                wrap: origin is WidgetGroupSwitchOrigin.Keyboard or
                    WidgetGroupSwitchOrigin.Wheel))
        {
            return false;
        }

        string targetId = _presentation.Members[targetIndex].WidgetId;
        if (origin == WidgetGroupSwitchOrigin.Wheel)
        {
            // The manager commits ActiveMemberId only after preparation,
            // persistence and the content transition. Keep the optimistic
            // target until the manager commits or rejects it so slow members
            // cannot repeatedly restart the same request.
            _pendingWheelTargetId = targetId;
        }
        else
        {
            _pendingWheelTargetId = null;
        }

        MemberInvoked?.Invoke(
            this,
            new WidgetGroupMemberEventArgs(
                targetId,
                origin));
        return true;
    }

    internal void NotifyMemberInvocationCompleted(
        string widgetId,
        bool succeeded,
        long? tabSelectionRequestVersion = null)
    {
        if (tabSelectionRequestVersion is { } version)
        {
            CompleteTabInvocation(widgetId, succeeded, version);
        }

        if (!string.Equals(
                _pendingWheelTargetId,
                widgetId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (succeeded &&
            !string.Equals(
                _presentation?.ActiveMemberId,
                widgetId,
                StringComparison.Ordinal))
        {
            // A coalesced duplicate reports success while the original
            // request is still preparing. Keep its optimistic cursor until
            // the committed presentation catches up.
            return;
        }

        _pendingWheelTargetId = null;
        _wheelAccumulator = 0;
    }

    private void ReconcilePendingWheelTarget(
        WidgetGroupPresentation? presentation)
    {
        if (presentation is null ||
            string.IsNullOrWhiteSpace(_pendingWheelTargetId))
        {
            _pendingWheelTargetId = null;
            return;
        }

        if (string.Equals(
                presentation.ActiveMemberId,
                _pendingWheelTargetId,
                StringComparison.Ordinal))
        {
            _pendingWheelTargetId = null;
        }
    }

    private void AnimateIdentityTransition(
        IdentitySnapshot previous,
        IdentitySnapshot next,
        WidgetGroupSwitchOrigin origin,
        bool forward)
    {
        CancelIdentityAnimation();
        SetIdentity(
            OutgoingIcon,
            OutgoingTitle,
            previous);
        SetIdentity(
            CurrentIcon,
            CurrentTitle,
            next);
        SetPositionRail(OutgoingPositionRailLayer, previous);
        SetPositionRail(CurrentPositionRailLayer, next);
        OutgoingIdentityLayer.Opacity = 1;
        CurrentIdentityLayer.Opacity = 0;
        OutgoingPositionRailLayer.Opacity = 1;
        CurrentPositionRailLayer.Opacity = 0;
        double currentIdentityWidth = IdentityViewport.Width;
        _pendingIdentityWidth = MeasureIdentityWidth(next);

        bool animationsEnabled = AreSystemAnimationsEnabled();
        bool directional =
            animationsEnabled &&
            origin is not WidgetGroupSwitchOrigin.Programmatic;
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled,
                directional);
        if (profile.DurationMilliseconds <= 0)
        {
            IdentityViewport.Width = _pendingIdentityWidth;
            CancelIdentityAnimation();
            SetIdentity(
                OutgoingIcon,
                OutgoingTitle,
                null);
            SetPositionRail(OutgoingPositionRailLayer, null);
            CurrentPositionRailLayer.Opacity = 1;
            return;
        }

        int durationMs = profile.DurationMilliseconds;
        int outgoingDurationMs = profile.OutgoingDurationMilliseconds;
        int incomingBeginTimeMs =
            outgoingDurationMs + profile.SwapGapMilliseconds;
        int incomingDurationMs = profile.IncomingDurationMilliseconds;
        double sign = forward ? 1 : -1;
        double distance = profile.TranslationDistance;
        double railDistance = Math.Min(4, distance);
        var outgoingTransform = new TranslateTransform();
        var incomingTransform = new TranslateTransform
        {
            Y = directional ? distance * sign : 0
        };
        var outgoingRailTransform = new TranslateTransform();
        var incomingRailTransform = new TranslateTransform
        {
            Y = directional ? railDistance * sign : 0
        };
        OutgoingIdentityLayer.RenderTransform = outgoingTransform;
        CurrentIdentityLayer.RenderTransform = incomingTransform;
        OutgoingPositionRailLayer.RenderTransform = outgoingRailTransform;
        CurrentPositionRailLayer.RenderTransform = incomingRailTransform;

        var outgoingFade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(outgoingDurationMs),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseIn
            }
        };
        Storyboard.SetTarget(outgoingFade, OutgoingIdentityLayer);
        Storyboard.SetTargetProperty(outgoingFade, nameof(Opacity));

        var incomingFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(incomingBeginTimeMs),
            Duration = TimeSpan.FromMilliseconds(incomingDurationMs),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        Storyboard.SetTarget(incomingFade, CurrentIdentityLayer);
        Storyboard.SetTargetProperty(incomingFade, nameof(Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(outgoingFade);
        storyboard.Children.Add(incomingFade);

        var iconScaleX = CreateIncomingIconScaleAnimation();
        var iconScaleY = CreateIncomingIconScaleAnimation();
        Storyboard.SetTarget(iconScaleX, CurrentIconScaleTransform);
        Storyboard.SetTargetProperty(
            iconScaleX,
            nameof(ScaleTransform.ScaleX));
        Storyboard.SetTarget(iconScaleY, CurrentIconScaleTransform);
        Storyboard.SetTargetProperty(
            iconScaleY,
            nameof(ScaleTransform.ScaleY));
        storyboard.Children.Add(iconScaleX);
        storyboard.Children.Add(iconScaleY);

        var outgoingRailFade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(outgoingDurationMs),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseIn
            }
        };
        Storyboard.SetTarget(
            outgoingRailFade,
            OutgoingPositionRailLayer);
        Storyboard.SetTargetProperty(
            outgoingRailFade,
            nameof(Opacity));
        storyboard.Children.Add(outgoingRailFade);

        var incomingRailFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(incomingBeginTimeMs),
            Duration = TimeSpan.FromMilliseconds(incomingDurationMs),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        Storyboard.SetTarget(
            incomingRailFade,
            CurrentPositionRailLayer);
        Storyboard.SetTargetProperty(
            incomingRailFade,
            nameof(Opacity));
        storyboard.Children.Add(incomingRailFade);
        var widthAnimation = new DoubleAnimation
        {
            From = currentIdentityWidth,
            To = _pendingIdentityWidth,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        Storyboard.SetTarget(widthAnimation, IdentityViewport);
        Storyboard.SetTargetProperty(
            widthAnimation,
            nameof(FrameworkElement.Width));
        storyboard.Children.Add(widthAnimation);
        if (directional)
        {
            var outgoingMotion = new DoubleAnimation
            {
                From = 0,
                To = -distance * sign,
                Duration = TimeSpan.FromMilliseconds(outgoingDurationMs),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            };
            Storyboard.SetTarget(outgoingMotion, outgoingTransform);
            Storyboard.SetTargetProperty(outgoingMotion, nameof(TranslateTransform.Y));
            storyboard.Children.Add(outgoingMotion);

            var incomingMotion = new DoubleAnimation
            {
                From = distance * sign,
                To = 0,
                BeginTime = TimeSpan.FromMilliseconds(incomingBeginTimeMs),
                Duration = TimeSpan.FromMilliseconds(incomingDurationMs),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(incomingMotion, incomingTransform);
            Storyboard.SetTargetProperty(incomingMotion, nameof(TranslateTransform.Y));
            storyboard.Children.Add(incomingMotion);

            var outgoingRailMotion = new DoubleAnimation
            {
                From = 0,
                To = -railDistance * sign,
                Duration = TimeSpan.FromMilliseconds(outgoingDurationMs),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            };
            Storyboard.SetTarget(
                outgoingRailMotion,
                outgoingRailTransform);
            Storyboard.SetTargetProperty(
                outgoingRailMotion,
                nameof(TranslateTransform.Y));
            storyboard.Children.Add(outgoingRailMotion);

            var incomingRailMotion = new DoubleAnimation
            {
                From = railDistance * sign,
                To = 0,
                BeginTime = TimeSpan.FromMilliseconds(incomingBeginTimeMs),
                Duration = TimeSpan.FromMilliseconds(incomingDurationMs),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(
                incomingRailMotion,
                incomingRailTransform);
            Storyboard.SetTargetProperty(
                incomingRailMotion,
                nameof(TranslateTransform.Y));
            storyboard.Children.Add(incomingRailMotion);
        }
        storyboard.Completed += IdentityStoryboard_Completed;
        _identityStoryboard = storyboard;
        storyboard.Begin();

        DoubleAnimationUsingKeyFrames CreateIncomingIconScaleAnimation()
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(
                    TimeSpan.FromMilliseconds(incomingBeginTimeMs)),
                Value = 0.9
            });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(
                    incomingBeginTimeMs + (incomingDurationMs * 0.62))),
                Value = 1.055,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(
                    incomingBeginTimeMs + incomingDurationMs)),
                Value = 1,
                EasingFunction = new BackEase
                {
                    Amplitude = 0.18,
                    EasingMode = EasingMode.EaseOut
                }
            });
            return animation;
        }
    }

    private void UpdateInteractionChrome(bool animate = true)
    {
        StopAndClearStoryboard(ref _interactionChromeStoryboard);
        StopScrollSurfaceStoryboard();
        TitleInteractionChromeTransform.ScaleX = 1;
        TitleInteractionChromeTransform.ScaleY = 1;

        CapsuleChrome.Background = ResolveThemeBrush(
            _isSelectorPressed
                ? "SubtleFillColorTertiaryBrush"
                : "SubtleFillColorSecondaryBrush",
            new SolidColorBrush(Colors.Transparent));
        double targetSurfaceOpacity = ResolveInteractionSurfaceOpacity();
        CapsuleChrome.Opacity = 0;
        bool animationsEnabled = animate && AreSystemAnimationsEnabled();
        if (!animationsEnabled || XamlRoot is null)
        {
            TitleInteractionChrome.Opacity = targetSurfaceOpacity;
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(
            WidgetMotion.FeedbackMilliseconds);
        var storyboard = new Storyboard();
        AddOpacityAnimation(
            TitleInteractionChrome,
            targetSurfaceOpacity);
        _interactionChromeStoryboard = storyboard;
        storyboard.Begin();

        void AddOpacityAnimation(UIElement target, double opacity)
        {
            var animation = new DoubleAnimation
            {
                To = opacity,
                Duration = duration,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(
                animation,
                nameof(UIElement.Opacity));
            storyboard.Children.Add(animation);
        }
    }

    private double ResolveInteractionSurfaceOpacity()
    {
        if (UsesTabs)
        {
            return 0;
        }

        return _pickerOpen
            ? 0.34
            : _isSelectorPressed
                ? 0.4
                : _isPointerOverSelector
                    ? 0.26
                    : 0;
    }

    private void IdentityStoryboard_Completed(object? sender, object e)
    {
        if (sender is Storyboard storyboard)
        {
            storyboard.Completed -= IdentityStoryboard_Completed;
            storyboard.Stop();
            storyboard.Children.Clear();
        }

        OutgoingIdentityLayer.Opacity = 0;
        CurrentIdentityLayer.Opacity = 1;
        OutgoingIdentityLayer.RenderTransform = null;
        CurrentIdentityLayer.RenderTransform = null;
        OutgoingPositionRailLayer.Opacity = 0;
        CurrentPositionRailLayer.Opacity = 1;
        OutgoingPositionRailLayer.RenderTransform = null;
        CurrentPositionRailLayer.RenderTransform = null;
        CurrentIconScaleTransform.ScaleX = 1;
        CurrentIconScaleTransform.ScaleY = 1;
        IdentityViewport.Width = _pendingIdentityWidth;
        SetIdentity(
            OutgoingIcon,
            OutgoingTitle,
            null);
        SetPositionRail(OutgoingPositionRailLayer, null);
        _identityStoryboard = null;
    }

    private void CancelIdentityAnimation()
    {
        if (_identityStoryboard is not null)
        {
            _identityStoryboard.Completed -= IdentityStoryboard_Completed;
            _identityStoryboard.Stop();
            _identityStoryboard.Children.Clear();
            _identityStoryboard = null;
        }

        OutgoingIdentityLayer.Opacity = 0;
        CurrentIdentityLayer.Opacity = 1;
        OutgoingIdentityLayer.RenderTransform = null;
        CurrentIdentityLayer.RenderTransform = null;
        OutgoingPositionRailLayer.Opacity = 0;
        CurrentPositionRailLayer.Opacity = 1;
        OutgoingPositionRailLayer.RenderTransform = null;
        CurrentPositionRailLayer.RenderTransform = null;
        CurrentIconScaleTransform.ScaleX = 1;
        CurrentIconScaleTransform.ScaleY = 1;
        SetPositionRail(OutgoingPositionRailLayer, null);
    }

    private static void StopAndClearStoryboard(ref Storyboard? storyboard)
    {
        Storyboard? active = storyboard;
        storyboard = null;
        active?.Stop();
        active?.Children.Clear();
    }

    private bool CanAnimateIdentity()
    {
        return XamlRoot is not null;
    }

    private static bool AreSystemAnimationsEnabled()
    {
        return DeskBox.Services.WindowsCompatibilityService.ShouldAnimate;
    }
}
