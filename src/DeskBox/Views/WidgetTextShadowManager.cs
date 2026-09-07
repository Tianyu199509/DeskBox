using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace DeskBox.Views;

/// <summary>
/// Adds a tight, opt-in contrast edge behind currently realized text only.
/// No composition objects or layout subscription remain after disposal.
/// </summary>
internal sealed class WidgetTextShadowManager : IDisposable
{
    // LayoutUpdated fires per frame during drag/resize; reconcile at most ~30Hz
    // with a trailing pass so the final layout is always reconciled.
    private const int MinReconcileIntervalMs = 32;

    private readonly FrameworkElement _root;
    private readonly FrameworkElement _host;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ContainerVisual _container;
    private readonly Dictionary<TextBlock, ShadowEntry> _entries =
        new(ReferenceEqualityComparer.Instance);
    private DispatcherQueueTimer? _reconcileTimer;
    private long _lastReconcileTick;
    private bool _reconcileQueued;
    private bool _disposed;
    private string _edgeMode = WidgetForegroundSettings.EdgeOff;
    private Color _shadowColor = Colors.Black;

    public WidgetTextShadowManager(FrameworkElement root, FrameworkElement host)
    {
        _root = root;
        _host = host;
        _dispatcherQueue = root.DispatcherQueue;
        Compositor compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _container = compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(host, _container);
        _root.LayoutUpdated += Root_LayoutUpdated;
    }

    public void Apply(string edgeMode, Color foregroundColor)
    {
        if (_disposed)
        {
            return;
        }

        _edgeMode = WidgetForegroundSettings.NormalizeEdgeMode(edgeMode);
        _shadowColor = RelativeLuminance(foregroundColor) >= 0.48
            ? Colors.Black
            : Colors.White;
        foreach (ShadowEntry entry in _entries.Values)
        {
            ApplyShadowStyle(entry.Shadow);
        }

        QueueReconcile();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _root.LayoutUpdated -= Root_LayoutUpdated;
        ReleaseReconcileTimer();
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        foreach (ShadowEntry entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
        _container.Dispose();
    }

    private void Root_LayoutUpdated(object? sender, object e) => QueueReconcile();

    private void QueueReconcile()
    {
        if (_disposed || _reconcileQueued)
        {
            return;
        }

        long delay = MinReconcileIntervalMs -
            (Environment.TickCount64 - _lastReconcileTick);
        if (delay <= 0)
        {
            EnqueueReconcile();
            return;
        }

        _reconcileQueued = true;
        _reconcileTimer ??= CreateReconcileTimer();
        _reconcileTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _reconcileTimer.Start();
    }

    private DispatcherQueueTimer CreateReconcileTimer()
    {
        DispatcherQueueTimer timer = _dispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => EnqueueReconcile();
        PerformanceLogger.RecordTransientUiTimerCreated();
        return timer;
    }

    private void ReleaseReconcileTimer()
    {
        if (_reconcileTimer is null)
        {
            return;
        }

        _reconcileTimer.Stop();
        _reconcileTimer = null;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void EnqueueReconcile()
    {
        if (_disposed)
        {
            return;
        }

        _reconcileQueued = true;
        if (!_dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                ReconcileVisibleText))
        {
            _reconcileQueued = false;
        }
    }

    private void ReconcileVisibleText()
    {
        _reconcileQueued = false;
        _lastReconcileTick = Environment.TickCount64;
        if (_disposed)
        {
            return;
        }

        _container.Size = new Vector2(
            (float)Math.Max(0, _host.ActualWidth),
            (float)Math.Max(0, _host.ActualHeight));
        var seen = new HashSet<TextBlock>(ReferenceEqualityComparer.Instance);
        foreach (TextBlock text in EnumerateVisibleTextBlocks())
        {
            if (!TryGetTextBounds(text, out Vector2 offset, out Vector2 size))
            {
                continue;
            }

            seen.Add(text);
            if (!_entries.TryGetValue(text, out ShadowEntry? entry))
            {
                entry = TryCreateEntry(text);
                if (entry is null)
                {
                    continue;
                }

                _entries.Add(text, entry);
            }

            entry.Visual.Offset = new Vector3(offset, 0);
            entry.Visual.Size = size;
        }

        foreach (TextBlock removed in _entries.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            ShadowEntry entry = _entries[removed];
            _container.Children.Remove(entry.Visual);
            entry.Dispose();
            _entries.Remove(removed);
        }
    }

    private IEnumerable<TextBlock> EnumerateVisibleTextBlocks()
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(_root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (ReferenceEquals(current, _host))
            {
                continue;
            }

            // Editable control templates manage caret/selection layers of their
            // own; avoid duplicating their internal text into the shadow layer.
            if (current is TextBox or RichEditBox or PasswordBox or AutoSuggestBox)
            {
                continue;
            }

            if (current is TextBlock text &&
                text.Visibility == Visibility.Visible &&
                text.Opacity > 0.01 &&
                text.ActualWidth > 0.5 &&
                text.ActualHeight > 0.5)
            {
                yield return text;
            }

            int childCount;
            try
            {
                childCount = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                continue;
            }

            for (int index = childCount - 1; index >= 0; index--)
            {
                try
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
                catch
                {
                    // A virtualized child can disappear while layout is settling.
                }
            }
        }
    }

    private bool TryGetTextBounds(TextBlock text, out Vector2 offset, out Vector2 size)
    {
        offset = default;
        size = default;
        try
        {
            Point origin = text.TransformToVisual(_root).TransformPoint(new Point());
            double width = text.ActualWidth;
            double height = text.ActualHeight;
            if (origin.X + width < 0 ||
                origin.Y + height < 0 ||
                origin.X > _root.ActualWidth ||
                origin.Y > _root.ActualHeight)
            {
                return false;
            }

            offset = new Vector2((float)origin.X, (float)origin.Y);
            size = new Vector2((float)width, (float)height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private ShadowEntry? TryCreateEntry(TextBlock text)
    {
        try
        {
            CompositionBrush? mask = text.GetAlphaMask();
            if (mask is null)
            {
                return null;
            }

            Compositor compositor = _container.Compositor;
            var shadow = compositor.CreateDropShadow();
            shadow.Mask = mask;
            ApplyShadowStyle(shadow);
            var visual = compositor.CreateSpriteVisual();
            visual.Shadow = shadow;
            _container.Children.InsertAtTop(visual);
            return new ShadowEntry(mask, shadow, visual);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyShadowStyle(DropShadow shadow)
    {
        bool strong = string.Equals(
            _edgeMode,
            WidgetForegroundSettings.EdgeStrong,
            StringComparison.Ordinal);
        shadow.Color = _shadowColor;
        shadow.BlurRadius = strong ? 2.75f : 1.75f;
        shadow.Opacity = strong ? 0.88f : 0.58f;
        shadow.Offset = strong
            ? new Vector3(0, 0.5f, 0)
            : new Vector3(0, 0.75f, 0);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) +
               (0.7152 * Linearize(color.G)) +
               (0.0722 * Linearize(color.B));
    }

    private sealed class ShadowEntry(
        CompositionBrush mask,
        DropShadow shadow,
        SpriteVisual visual) : IDisposable
    {
        public DropShadow Shadow { get; } = shadow;

        public SpriteVisual Visual { get; } = visual;

        public void Dispose()
        {
            Visual.Dispose();
            Shadow.Dispose();
            mask.Dispose();
        }
    }
}
