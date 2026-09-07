using DeskBox.Models;
using Microsoft.UI.Xaml;
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
        FeedbackSurface.IsHitTestVisible = hasAction;
        FeedbackSurface.Visibility = Visibility.Visible;

        AnimateIn();
        _ = DismissAfterDelayAsync(
            generation,
            request.DisplayDuration,
            _dismissCancellation.Token);
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

    private void ApplySeverity(WidgetFeedbackSeverity severity)
    {
        SeverityIcon.Glyph = severity switch
        {
            WidgetFeedbackSeverity.Success => "\uE73E",
            WidgetFeedbackSeverity.Warning => "\uE7BA",
            WidgetFeedbackSeverity.Error => "\uEA39",
            _ => "\uE946"
        };
        SeverityIcon.Foreground = new SolidColorBrush(severity switch
        {
            WidgetFeedbackSeverity.Success => Windows.UI.Color.FromArgb(255, 16, 124, 65),
            WidgetFeedbackSeverity.Warning => Windows.UI.Color.FromArgb(255, 157, 93, 0),
            WidgetFeedbackSeverity.Error => Windows.UI.Color.FromArgb(255, 196, 43, 28),
            _ => Windows.UI.Color.FromArgb(255, 0, 120, 212)
        });
    }

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
