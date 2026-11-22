using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Native WinUI 3 segmented vertical-capsule progress bar (<c>ProgressBar</c> with
/// <c>variant: "segmented"</c>). Adapts capsule count to available width during layout
/// while pooling <see cref="Border"/> elements for zero-allocation retained patching.
/// </summary>
public sealed partial class CustomWidgetSegmentedBar : Panel
{
    private double _minimum;
    private double _maximum = 100.0;
    private double _value;
    private double _pillHeight = 14.0;
    private double _idealPillWidth = 3.8;
    private double _pillGap = 2.5;
    private int _explicitSegments;
    private int _renderedSegmentCount;
    private int _activeSegmentCount;
    private Brush? _activeBrush;
    private Brush? _trackBrush;
    private IReadOnlyList<(Brush Fill, bool IsActive, string? Tooltip, string? HoverSummary)>? _buckets;
    private Action<string?>? _onHoverChanged;

    public CustomWidgetSegmentedBar()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        IsHitTestVisible = false;
        PointerExited += (_, _) => _onHoverChanged?.Invoke(null);
    }

    public void SetBuckets(
        IReadOnlyList<(Brush Fill, bool IsActive, string? Tooltip, string? HoverSummary)>? buckets,
        Action<string?>? onHoverChanged = null)
    {
        _buckets = buckets;
        _onHoverChanged = onHoverChanged;
        bool hasBuckets = buckets is { Count: > 0 };
        IsHitTestVisible = hasBuckets;
        int nextExplicit = hasBuckets ? Math.Clamp(buckets!.Count, 4, 80) : 0;
        if (_explicitSegments != nextExplicit)
        {
            _explicitSegments = nextExplicit;
            InvalidateMeasure();
        }
        else
        {
            RefreshSegmentBrushes();
        }
    }

    public double Minimum
    {
        get => _minimum;
        set
        {
            if (Math.Abs(_minimum - value) > 1e-4)
            {
                _minimum = value;
                RefreshSegmentBrushes();
            }
        }
    }

    public double Maximum
    {
        get => _maximum;
        set
        {
            double clamped = Math.Max(0.001, value);
            if (Math.Abs(_maximum - clamped) > 1e-4)
            {
                _maximum = clamped;
                RefreshSegmentBrushes();
            }
        }
    }

    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) > 1e-4)
            {
                _value = value;
                RefreshSegmentBrushes();
            }
        }
    }

    public double PillHeight
    {
        get => _pillHeight;
        set
        {
            double clamped = Math.Clamp(value, 6.0, 32.0);
            if (Math.Abs(_pillHeight - clamped) > 1e-4)
            {
                _pillHeight = clamped;
                InvalidateMeasure();
            }
        }
    }

    public double IdealPillWidth
    {
        get => _idealPillWidth;
        set
        {
            double clamped = Math.Clamp(value, 2.5, 12.0);
            if (Math.Abs(_idealPillWidth - clamped) > 1e-4)
            {
                _idealPillWidth = clamped;
                InvalidateMeasure();
            }
        }
    }

    public double PillGap
    {
        get => _pillGap;
        set
        {
            double clamped = Math.Clamp(value, 1.0, 8.0);
            if (Math.Abs(_pillGap - clamped) > 1e-4)
            {
                _pillGap = clamped;
                InvalidateMeasure();
            }
        }
    }

    public int ExplicitSegments
    {
        get => _explicitSegments;
        set
        {
            int clamped = value > 0 ? Math.Clamp(value, 4, 80) : 0;
            if (_explicitSegments != clamped)
            {
                _explicitSegments = clamped;
                InvalidateMeasure();
            }
        }
    }

    public Brush? ActiveBrush
    {
        get => _activeBrush;
        set
        {
            if (!ReferenceEquals(_activeBrush, value))
            {
                _activeBrush = value;
                RefreshSegmentBrushes();
            }
        }
    }

    public Brush? TrackBrush
    {
        get => _trackBrush;
        set
        {
            if (!ReferenceEquals(_trackBrush, value))
            {
                _trackBrush = value;
                RefreshSegmentBrushes();
            }
        }
    }

    public int TotalSegmentCount => _renderedSegmentCount;

    public int ActiveSegmentCount => _activeSegmentCount;

    public static int ComputeFilledSegmentCount(double value, double min, double max, int totalSegments)
    {
        if (totalSegments <= 0)
        {
            return 0;
        }

        double span = Math.Max(0.001, max - min);
        double clamped = Math.Clamp(value - min, 0.0, span);
        if (clamped <= 1e-6)
        {
            return 0;
        }

        double ratio = clamped / span;
        if (ratio >= 0.995)
        {
            return totalSegments;
        }

        int rounded = (int)Math.Round(ratio * totalSegments, MidpointRounding.AwayFromZero);
        if (totalSegments == 1)
        {
            return 1;
        }

        return Math.Clamp(rounded, 1, totalSegments - 1);
    }

    public static int ComputeTargetSegmentCount(
        double availableWidth,
        double idealPillWidth,
        double pillGap,
        int explicitSegments = 0)
    {
        if (explicitSegments > 0)
        {
            return Math.Clamp(explicitSegments, 4, 80);
        }

        if (!double.IsFinite(availableWidth) || availableWidth <= 24.0)
        {
            return 28;
        }

        double pitch = Math.Max(3.5, idealPillWidth + pillGap);
        int raw = (int)Math.Floor((availableWidth + pillGap) / pitch);
        return Math.Clamp(raw, 8, 64);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int targetCount = ComputeTargetSegmentCount(
            availableSize.Width,
            _idealPillWidth,
            _pillGap,
            _explicitSegments);

        EnsurePoolCapacity(targetCount);
        _renderedSegmentCount = targetCount;

        double measuredWidth = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : targetCount * _idealPillWidth + Math.Max(0, targetCount - 1) * _pillGap;

        double pillW = Math.Max(
            2.0,
            (measuredWidth - Math.Max(0, targetCount - 1) * _pillGap) / Math.Max(1, targetCount));
        double radius = Math.Min(2.2, Math.Min(pillW, _pillHeight) * 0.5);
        var corner = new CornerRadius(radius);
        var childMeasureSize = new Size(pillW, _pillHeight);

        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is Border pill)
            {
                if (i < targetCount)
                {
                    pill.Visibility = Visibility.Visible;
                    pill.CornerRadius = corner;
                    pill.Measure(childMeasureSize);
                }
                else
                {
                    pill.Visibility = Visibility.Collapsed;
                }
            }
        }

        RefreshSegmentBrushes();
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : measuredWidth,
            _pillHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = _renderedSegmentCount;
        if (count <= 0)
        {
            return new Size(finalSize.Width, _pillHeight);
        }

        double width = Math.Max(0.0, finalSize.Width);
        int arrangeCount = ComputeTargetSegmentCount(width, _idealPillWidth, _pillGap, _explicitSegments);
        if (arrangeCount != count && width > 24.0)
        {
            EnsurePoolCapacity(arrangeCount);
            count = arrangeCount;
            _renderedSegmentCount = count;
            RefreshSegmentBrushes();
        }

        double totalGaps = Math.Max(0, count - 1) * _pillGap;
        double pillW = Math.Max(1.8, (width - totalGaps) / count);
        double radius = Math.Min(2.2, Math.Min(pillW, _pillHeight) * 0.5);
        var corner = new CornerRadius(radius);
        double y = Math.Max(0.0, (finalSize.Height - _pillHeight) * 0.5);

        double x = 0.0;
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is Border pill)
            {
                if (i < count)
                {
                    if (pill.Visibility != Visibility.Visible)
                    {
                        pill.Visibility = Visibility.Visible;
                    }
                    pill.CornerRadius = corner;
                    double rightEdge = i == count - 1 ? width : x + pillW;
                    double w = Math.Max(1.5, rightEdge - x);
                    pill.Arrange(new Rect(x, y, w, _pillHeight));
                    x += pillW + _pillGap;
                }
                else if (pill.Visibility != Visibility.Collapsed)
                {
                    pill.Visibility = Visibility.Collapsed;
                }
            }
        }

        return new Size(finalSize.Width, Math.Max(_pillHeight, finalSize.Height));
    }

    private void EnsurePoolCapacity(int required)
    {
        while (Children.Count < required)
        {
            int index = Children.Count;
            var pill = new Border
            {
                IsHitTestVisible = _buckets is { Count: > 0 }
            };
            pill.PointerEntered += (_, _) =>
            {
                if (_buckets is not null && index < _buckets.Count)
                {
                    _onHoverChanged?.Invoke(_buckets[index].HoverSummary);
                }
            };
            Children.Add(pill);
        }
    }

    private void RefreshSegmentBrushes()
    {
        if (_buckets is { Count: > 0 } buckets)
        {
            int bucketCount = Math.Min(buckets.Count, _renderedSegmentCount > 0 ? _renderedSegmentCount : buckets.Count);
            int activeBuckets = 0;
            for (int i = 0; i < buckets.Count; i++)
            {
                if (buckets[i].IsActive)
                {
                    activeBuckets++;
                }
            }
            _activeSegmentCount = activeBuckets;

            for (int i = 0; i < bucketCount && i < Children.Count; i++)
            {
                if (Children[i] is Border pill)
                {
                    var b = buckets[i];
                    if (pill.IsHitTestVisible != true)
                    {
                        pill.IsHitTestVisible = true;
                    }
                    if (!ReferenceEquals(pill.Background, b.Fill))
                    {
                        pill.Background = b.Fill;
                    }
                    ToolTipService.SetToolTip(pill, string.IsNullOrWhiteSpace(b.Tooltip) ? null : b.Tooltip);
                }
            }
            return;
        }

        int count = _renderedSegmentCount > 0
            ? _renderedSegmentCount
            : Children.Count;
        if (count <= 0)
        {
            _activeSegmentCount = ComputeFilledSegmentCount(_value, _minimum, _maximum, 28);
            return;
        }

        int filled = ComputeFilledSegmentCount(_value, _minimum, _maximum, count);
        _activeSegmentCount = filled;

        for (int i = 0; i < count && i < Children.Count; i++)
        {
            if (Children[i] is Border pill)
            {
                if (pill.IsHitTestVisible)
                {
                    pill.IsHitTestVisible = false;
                    ToolTipService.SetToolTip(pill, null);
                }
                Brush? desired = i < filled ? _activeBrush : _trackBrush;
                if (!ReferenceEquals(pill.Background, desired))
                {
                    pill.Background = desired;
                }
            }
        }
    }
}
