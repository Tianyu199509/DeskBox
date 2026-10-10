using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace DeskBox.Controls;

public sealed partial class WidgetFeedbackPresenter : UserControl
{
    private CancellationTokenSource? _dismissCancellation;
    private WidgetFeedbackRequest? _current;
    private long _generation;

    public WidgetFeedbackPresenter()
    {
        InitializeComponent();
        string? closeLabel = App.Current?.LocalizationService?.T("Common.Close");
        if (!string.IsNullOrWhiteSpace(closeLabel))
        {
            AutomationProperties.SetName(DismissButton, closeLabel);
        }

        Unloaded += (_, _) => Clear();
    }

    public bool IsCompact { get; set; }

    public WidgetFeedbackRequest? Current => _current;

    public void Show(WidgetFeedbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Show(request));
            return;
        }

        _dismissCancellation?.Cancel();
        _dismissCancellation?.Dispose();
        _dismissCancellation = new CancellationTokenSource();
        _current = request;
        long generation = ++_generation;

        MessageText.Text = request.Message;
        MessageText.MaxLines = IsCompact ? 1 : 2;
        MessageText.TextWrapping = IsCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        ApplySeverity(request.Severity);

        bool hasAction =
            !string.IsNullOrWhiteSpace(request.ActionText) &&
            request.Action is not null;
        ActionButton.Content = request.ActionText;
        ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        DismissButton.Visibility = request.DismissAction is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        FeedbackSurface.IsHitTestVisible = hasAction || request.DismissAction is not null;
        FeedbackSurface.Visibility = Visibility.Visible;

        // Announce through UIA rather than relying on the live region alone:
        // the text is written before the surface becomes visible, so a
        // LiveSetting-only region never fires for Narrator, and an identical
        // follow-up message would stay silent either way.
        AnnounceToAutomation(request.Message, request.Severity);

        AnimateIn();
        _ = DismissAfterDelayAsync(
            generation,
            request.DisplayDuration,
            _dismissCancellation.Token);
    }

    /// <summary>
    /// Raises a UIA notification for the toast so screen readers announce the
    /// result. Success maps to ActionCompleted; warnings and errors use
    /// ImportantMostRecent so they are not swallowed by queued speech.
    /// </summary>
    private void AnnounceToAutomation(string message, WidgetFeedbackSeverity severity)
    {
        // CreatePeerForElement rather than FromElement alone: the peer may not
        // exist yet on the first toast (or right after Narrator activates),
        // and a missing peer would silently drop the announcement.
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(MessageText) ??
                                FrameworkElementAutomationPeer.CreatePeerForElement(MessageText);
        if (peer is null)
        {
            return;
        }

        AutomationNotificationKind kind = severity == WidgetFeedbackSeverity.Success
            ? AutomationNotificationKind.ActionCompleted
            : AutomationNotificationKind.Other;
        AutomationNotificationProcessing processing = severity is WidgetFeedbackSeverity.Warning or WidgetFeedbackSeverity.Error
            ? AutomationNotificationProcessing.ImportantMostRecent
            : AutomationNotificationProcessing.MostRecent;
        peer.RaiseNotificationEvent(kind, processing, message, "DeskBox.Feedback");
    }

    public void Clear(string? deduplicationKey = null)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Clear(deduplicationKey));
            return;
        }

        if (deduplicationKey is not null &&
            !string.Equals(
                _current?.DeduplicationKey,
                deduplicationKey,
                StringComparison.Ordinal))
        {
            return;
        }

        _dismissCancellation?.Cancel();
        _dismissCancellation?.Dispose();
        _dismissCancellation = null;
        _current = null;
        ++_generation;
        AnimateOut();
    }

    private async Task DismissAfterDelayAsync(
        long generation,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_generation == generation)
            {
                Clear();
            }
        });
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        Func<Task>? action = _current?.Action;
        if (action is null)
        {
            return;
        }

        ActionButton.IsEnabled = false;
        try
        {
            await action();
            Clear();
        }
        catch (Exception ex)
        {
            App.Log($"[Feedback] Action failed: {ex}");
        }
        finally
        {
            ActionButton.IsEnabled = true;
        }
    }

    // The X both hides this tip and runs the requester's dismissal side effect
    // (for the drag-out receipt, switching the result-hint setting off), so the
    // dismissal outlives the tip instead of reappearing on the next drop.
    private async void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        Func<Task>? dismiss = _current?.DismissAction;
        DismissButton.IsEnabled = false;
        try
        {
            if (dismiss is not null)
            {
                await dismiss();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Feedback] Dismiss failed: {ex}");
        }
        finally
        {
            DismissButton.IsEnabled = true;
        }

        Clear();
    }

    private void ApplySeverity(WidgetFeedbackSeverity severity)
    {
        SeverityIcon.Glyph = severity switch
        {
            WidgetFeedbackSeverity.Success => "\uE73E",
            WidgetFeedbackSeverity.Warning => "\uE7BA",
            WidgetFeedbackSeverity.Error => "\uEA39",
            _ => "\uE946"
        };
        SeverityIcon.Foreground = severity switch
        {
            WidgetFeedbackSeverity.Success => ResolveThemedBrush(
                "SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(255, 16, 124, 65)),
            WidgetFeedbackSeverity.Warning => ResolveThemedBrush(
                "SystemFillColorCautionBrush", Windows.UI.Color.FromArgb(255, 157, 93, 0)),
            WidgetFeedbackSeverity.Error => ResolveThemedBrush(
                "SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            _ => ResolveThemedBrush(
                "AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 0, 120, 212))
        };
    }

    // Element-scoped resolution follows the presenter's own theme override
    // (NeutralInteractionBrush), not the system theme; the fallbacks are the
    // Fluent light-theme values.
    private Brush ResolveThemedBrush(string resourceKey, Windows.UI.Color fallback) =>
        DeskBox.Helpers.NeutralInteractionBrush.ResolveThemedBrush(resourceKey, SeverityIcon) ??
        new SolidColorBrush(fallback);

    private void AnimateIn()
    {
        if (!AnimationsEnabled())
        {
            FeedbackSurface.Opacity = 1;
            FeedbackTransform.Y = 0;
            return;
        }

        FeedbackSurface.Opacity = 0;
        FeedbackTransform.Y = 2;
        var storyboard = new Storyboard();
        AddAnimation(storyboard, FeedbackSurface, nameof(Opacity), 0, 1, 167);
        AddAnimation(storyboard, FeedbackTransform, nameof(TranslateTransform.Y), 2, 0, 167);
        storyboard.Begin();
    }

    private void AnimateOut()
    {
        if (FeedbackSurface.Visibility == Visibility.Collapsed)
        {
            return;
        }

        if (!AnimationsEnabled())
        {
            FinishHide();
            return;
        }

        var storyboard = new Storyboard();
        AddAnimation(storyboard, FeedbackSurface, nameof(Opacity), FeedbackSurface.Opacity, 0, 83);
        storyboard.Completed += (_, _) => FinishHide();
        storyboard.Begin();
    }

    private void FinishHide()
    {
        FeedbackSurface.Opacity = 0;
        FeedbackSurface.Visibility = Visibility.Collapsed;
        FeedbackSurface.IsHitTestVisible = false;
        FeedbackTransform.Y = 2;
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMilliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds)
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static bool AnimationsEnabled()
    {
        return DeskBox.Services.WindowsCompatibilityService.ShouldAnimate;
    }
}
