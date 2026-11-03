using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using DeskBox.Services;
using DeskBox.Services.Audio;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Controls.WidgetContents;

public enum MusicSpectrumMode
{
    SymmetricWave = 0,
    WaveformScrub = 1,
    PeakHoldBars = 2,
    AuroraSilk = 3,
    MatrixLed = 4,
    VinylRadialHalo = 5,
    MiniBadge = 6
}

/// <summary>
/// Hardware-accelerated WinUI 3 audio spectrum visualizer for the Music widget.
/// Supports 6 low-overhead visual styles:
/// 1. <see cref="MusicSpectrumMode.SymmetricWave"/> — center-anchored pill bars (Composition Scale.Y)
/// 2. <see cref="MusicSpectrumMode.WaveformScrub"/> — played/unplayed progress-split acoustic pills
/// 3. <see cref="MusicSpectrumMode.PeakHoldBars"/> — bottom equalizer + gravity peak-hold glowing caps
/// 4. <see cref="MusicSpectrumMode.AuroraSilk"/> — dual-layer smooth Catmull-Rom Bezier aurora ribbons
/// 5. <see cref="MusicSpectrumMode.MatrixLed"/> — segmented micro-LED dot matrix (Composition Opacity)
/// 6. <see cref="MusicSpectrumMode.VinylRadialHalo"/> — 360-degree pre-rotated radial halo bars
/// </summary>
public sealed partial class MusicSpectrumControl : Grid
{
    private const int FrameIntervalMs = 32; // ~31 fps smooth updates
    private const int MaxSupportedBars = 52;
    private const int LedRowCount = 5;
    private const int SilkAnchorCount = 15; // 14 cubic Bezier segments

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(MusicSpectrumControl),
            new PropertyMetadata(null, OnAccentBrushChanged));

    private readonly StackPanel _barPanel;
    private readonly Canvas _overlayCanvas;
    private readonly List<Border> _barElements = new(MaxSupportedBars);
    private readonly List<Visual> _barVisuals = new(MaxSupportedBars);
    private readonly List<Border> _capElements = new(MaxSupportedBars);
    private readonly List<Visual> _capVisuals = new(MaxSupportedBars);
    private readonly Visual?[,] _ledVisuals = new Visual?[MaxSupportedBars, LedRowCount];
    private readonly List<Border> _ledTopCells = new(MaxSupportedBars);
    private readonly List<Border> _ledBodyCells = new(MaxSupportedBars * (LedRowCount - 1));

    private readonly MusicSpectrumFft _fft = new();
    private readonly float[] _pcmScratch = new float[MusicSpectrumFft.FftSize];
    private readonly float[] _bands24 = new float[MusicSpectrumFft.BandCount];
    private readonly float[] _targetBars = new float[MaxSupportedBars];
    private readonly float[] _smoothedBars = new float[MaxSupportedBars];
    private readonly float[] _secondarySmoothedBars = new float[SilkAnchorCount];
    private readonly float[] _peakLevels = new float[MaxSupportedBars];
    private readonly float[] _peakHoldTimers = new float[MaxSupportedBars];
    private readonly float[] _peakVelocities = new float[MaxSupportedBars];

    private readonly double[] _silkX = new double[SilkAnchorCount];
    private readonly double[] _silkFrontY = new double[SilkAnchorCount];
    private readonly double[] _silkBackY = new double[SilkAnchorCount];
    private readonly float[] _lastCommittedSilkFront = new float[SilkAnchorCount];
    private readonly float[] _lastCommittedSilkBack = new float[SilkAnchorCount];
    private BezierSegment[]? _silkFrontSegments;
    private BezierSegment[]? _silkBackSegments;
    private BezierSegment[]? _silkCrestSegments;
    private PathFigure? _silkFrontFigure;
    private PathFigure? _silkBackFigure;
    private PathFigure? _silkCrestFigure;
    private XamlPath? _silkBackPath;
    private XamlPath? _silkFrontPath;
    private XamlPath? _silkCrestPath;
    private double _lastAuroraCommitWidth = -1.0;

    private MusicSpectrumMode _mode = MusicSpectrumMode.SymmetricWave;
    private int _barCount = 24;
    private double _barWidth = 3.0;
    private double _barSpacing = 3.0;
    private double _maxBarHeight = 18.0;
    private double _innerRadius = 64.0;
    private double _progressRatio;
    private int _lastPlayheadIndex = -2;
    private double _silkPhase;

    private LinearGradientBrush? _barFillBrush;
    private SolidColorBrush? _capFillBrush;
    private SolidColorBrush? _unplayedFillBrush;
    private LinearGradientBrush? _silkFrontFillBrush;
    private LinearGradientBrush? _silkBackFillBrush;
    private LinearGradientBrush? _silkCrestStrokeBrush;

    private Color _lastAccentColor = Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF);
    private DispatcherQueueTimer? _frameTimer;
    private IDisposable? _captureSubscription;
    private string? _sourceAppUserModelId;
    private string? _sourceDisplayName;
    private long _visibilityCallbackToken;
    private bool _visibilityCallbackRegistered;
    private long _lastTickTimestamp;
    private bool _shouldAnimate;
    private bool _isBuilt;
    private bool _isDisposed;

    public MusicSpectrumControl()
    {
        IsHitTestVisible = false;
        _barPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = _barSpacing,
            IsHitTestVisible = false
        };
        _overlayCanvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        Children.Add(_barPanel);
        Children.Add(_overlayCanvas);

        Loaded += MusicSpectrumControl_Loaded;
        Unloaded += MusicSpectrumControl_Unloaded;
        SizeChanged += MusicSpectrumControl_SizeChanged;
        _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        _visibilityCallbackRegistered = true;
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public MusicSpectrumMode Mode
    {
        get => _mode;
        set
        {
            if (_mode != value)
            {
                _mode = value;
                if (_isBuilt)
                {
                    RebuildVisualTree(force: true);
                }
            }
        }
    }

    public int BarCount
    {
        get => _barCount;
        set
        {
            int clamped = Math.Clamp(value, 2, MaxSupportedBars);
            if (_barCount != clamped)
            {
                _barCount = clamped;
                if (_isBuilt)
                {
                    RebuildVisualTree(force: true);
                }
            }
        }
    }

    public double BarWidth
    {
        get => _barWidth;
        set
        {
            double clamped = Math.Clamp(value, 1.4, 10.0);
            if (Math.Abs(_barWidth - clamped) > 0.01)
            {
                _barWidth = clamped;
                if (_isBuilt)
                {
                    RebuildVisualTree(force: true);
                }
            }
        }
    }

    public double BarSpacing
    {
        get => _barSpacing;
        set
        {
            double clamped = Math.Clamp(value, 1.0, 10.0);
            if (Math.Abs(_barSpacing - clamped) > 0.01)
            {
                _barSpacing = clamped;
                _barPanel.Spacing = clamped;
            }
        }
    }

    public double MaxBarHeight
    {
        get => _maxBarHeight;
        set
        {
            double clamped = Math.Clamp(value, 8.0, 56.0);
            if (Math.Abs(_maxBarHeight - clamped) > 0.05)
            {
                _maxBarHeight = clamped;
                if (_isBuilt)
                {
                    RebuildVisualTree(force: true);
                }
            }
        }
    }

    public double InnerRadius => _innerRadius;

    public double ProgressRatio => _progressRatio;

    public void SetProgressRatio(double ratio)
    {
        double clamped = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 0.0;
        if (Math.Abs(_progressRatio - clamped) <= 0.002)
        {
            return;
        }

        _progressRatio = clamped;
        if (_mode == MusicSpectrumMode.WaveformScrub && _barElements.Count > 0)
        {
            ApplyWaveformProgressBrushes(force: false);
        }
    }

    public void ConfigureGeometry(
        MusicSpectrumMode mode,
        int barCount,
        double barWidth,
        double barSpacing,
        double maxBarHeight)
    {
        int effectiveCount = mode == MusicSpectrumMode.AuroraSilk
            ? SilkAnchorCount
            : Math.Clamp(barCount, 2, MaxSupportedBars);
        double clampedWidth = Math.Clamp(barWidth, 1.4, 10.0);
        double clampedSpacing = Math.Clamp(barSpacing, 1.0, 10.0);
        double clampedHeight = Math.Clamp(maxBarHeight, 8.0, 56.0);

        bool needsRebuild = !_isBuilt ||
            _mode != mode ||
            _barCount != effectiveCount ||
            Math.Abs(_barWidth - clampedWidth) > 0.01 ||
            Math.Abs(_maxBarHeight - clampedHeight) > 0.05 ||
            !double.IsNaN(Width);

        _mode = mode;
        _barCount = effectiveCount;
        _barWidth = clampedWidth;
        _barSpacing = clampedSpacing;
        _maxBarHeight = clampedHeight;
        _barPanel.Spacing = clampedSpacing;
        Width = double.NaN;
        Height = clampedHeight;

        if (needsRebuild)
        {
            RebuildVisualTree(force: true);
        }
    }

    public void ConfigureRadialGeometry(
        int barCount,
        double barWidth,
        double maxBarHeight,
        double innerRadius)
    {
        int clampedCount = Math.Clamp(barCount, 16, MaxSupportedBars);
        double clampedWidth = Math.Clamp(barWidth, 1.4, 6.0);
        double clampedHeight = Math.Clamp(maxBarHeight, 6.0, 32.0);
        double clampedRadius = Math.Clamp(innerRadius, 18.0, 260.0);

        bool needsRebuild = !_isBuilt ||
            _mode != MusicSpectrumMode.VinylRadialHalo ||
            _barCount != clampedCount ||
            Math.Abs(_barWidth - clampedWidth) > 0.01 ||
            Math.Abs(_maxBarHeight - clampedHeight) > 0.05 ||
            Math.Abs(_innerRadius - clampedRadius) > 0.1;

        _mode = MusicSpectrumMode.VinylRadialHalo;
        _barCount = clampedCount;
        _barWidth = clampedWidth;
        _maxBarHeight = clampedHeight;
        _innerRadius = clampedRadius;

        double outerDiameter = (clampedRadius + clampedHeight + 2.0) * 2.0;
        Width = outerDiameter;
        Height = outerDiameter;

        if (needsRebuild)
        {
            RebuildVisualTree(force: true);
        }
    }

    public void SetPlaybackState(
        bool shouldAnimate,
        string? sourceAppUserModelId,
        string? sourceDisplayName,
        bool immediate = false)
    {
        if (_isDisposed)
        {
            return;
        }

        _shouldAnimate = shouldAnimate;
        _sourceAppUserModelId = sourceAppUserModelId;
        _sourceDisplayName = sourceDisplayName;

        if (shouldAnimate)
        {
            EnsureVisualTreeInitialized();
            if (_captureSubscription is null)
            {
                _captureSubscription = MusicSpectrumService.Shared.AcquireSubscription(
                    sourceAppUserModelId,
                    sourceDisplayName);
            }
            else
            {
                MusicSpectrumService.Shared.UpdateSubscriptionSource(
                    _captureSubscription,
                    sourceAppUserModelId,
                    sourceDisplayName);
            }

            StartFrameTimer();
            return;
        }

        ReleaseCaptureSubscription();
        _fft.Reset();

        if (!_isBuilt)
        {
            StopFrameTimer();
            return;
        }

        if (immediate || !IsLoaded)
        {
            StopFrameTimer();
            SnapToIdle();
        }
    }

    public void ReleaseVisualResources()
    {
        _isDisposed = true;
        _shouldAnimate = false;
        Loaded -= MusicSpectrumControl_Loaded;
        Unloaded -= MusicSpectrumControl_Unloaded;
        SizeChanged -= MusicSpectrumControl_SizeChanged;
        if (_visibilityCallbackRegistered)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackRegistered = false;
        }

        ReleaseCaptureSubscription();
        DestroyFrameTimer();
        ClearModeChildren();
        _barFillBrush = null;
        _capFillBrush = null;
        _unplayedFillBrush = null;
        _silkFrontFillBrush = null;
        _silkBackFillBrush = null;
        _silkCrestStrokeBrush = null;
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MusicSpectrumControl control && !control._isDisposed && control._isBuilt)
        {
            control.SyncBrushes();
        }
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isDisposed || Visibility != Visibility.Visible)
        {
            return;
        }

        EnsureVisualTreeInitialized();
        SyncBrushes();
        if (!_shouldAnimate)
        {
            SnapToIdle();
        }
    }

    private void MusicSpectrumControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_shouldAnimate || Visibility == Visibility.Visible)
        {
            EnsureVisualTreeInitialized();
            SyncBrushes();
        }

        if (_shouldAnimate)
        {
            if (_captureSubscription is null)
            {
                _captureSubscription = MusicSpectrumService.Shared.AcquireSubscription(
                    _sourceAppUserModelId,
                    _sourceDisplayName);
            }

            StartFrameTimer();
        }
        else if (_isBuilt)
        {
            SnapToIdle();
        }
    }

    private void MusicSpectrumControl_Unloaded(object sender, RoutedEventArgs e)
    {
        ReleaseCaptureSubscription();
        DestroyFrameTimer();
    }

    private void MusicSpectrumControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isDisposed || !_isBuilt || _mode != MusicSpectrumMode.AuroraSilk)
        {
            return;
        }

        UpdateAuroraSilkGeometry(force: true);
    }

    private void EnsureVisualTreeInitialized()
    {
        if (!_isBuilt)
        {
            RebuildVisualTree(force: true);
        }
    }

    private void ClearModeChildren()
    {
        _barPanel.Children.Clear();
        _overlayCanvas.Children.Clear();
        _barElements.Clear();
        _barVisuals.Clear();
        _capElements.Clear();
        _capVisuals.Clear();
        _ledTopCells.Clear();
        _ledBodyCells.Clear();
        Array.Clear(_ledVisuals, 0, _ledVisuals.Length);

        _silkFrontSegments = null;
        _silkBackSegments = null;
        _silkCrestSegments = null;
        _silkFrontFigure = null;
        _silkBackFigure = null;
        _silkCrestFigure = null;
        _silkBackPath = null;
        _silkFrontPath = null;
        _silkCrestPath = null;
        _lastPlayheadIndex = -2;
        _isBuilt = false;
    }

    private void RebuildVisualTree(bool force)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!force && _isBuilt)
        {
            return;
        }

        ClearModeChildren();
        SyncBrushes();

        switch (_mode)
        {
            case MusicSpectrumMode.AuroraSilk:
                BuildAuroraSilkTree();
                break;

            case MusicSpectrumMode.MatrixLed:
                BuildMatrixLedTree();
                break;

            case MusicSpectrumMode.PeakHoldBars:
                BuildPeakHoldBarsTree();
                break;

            case MusicSpectrumMode.VinylRadialHalo:
                BuildRadialHaloTree();
                break;

            case MusicSpectrumMode.WaveformScrub:
            case MusicSpectrumMode.MiniBadge:
            case MusicSpectrumMode.SymmetricWave:
            default:
                BuildStandardBarsTree();
                break;
        }

        _isBuilt = true;
    }

    private void BuildStandardBarsTree()
    {
        _overlayCanvas.Visibility = Visibility.Collapsed;
        _barPanel.Visibility = Visibility.Visible;
        _barPanel.Spacing = _barSpacing;
        _barPanel.VerticalAlignment = _mode == MusicSpectrumMode.MiniBadge
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;

        double cornerRadius = _barWidth * 0.5;
        float centerX = (float)(_barWidth * 0.5);
        float centerY = _mode == MusicSpectrumMode.MiniBadge
            ? (float)_maxBarHeight
            : (float)(_maxBarHeight * 0.5);

        for (int i = 0; i < _barCount; i++)
        {
            var bar = new Border
            {
                Width = _barWidth,
                Height = _maxBarHeight,
                CornerRadius = new CornerRadius(cornerRadius),
                Background = _barFillBrush,
                VerticalAlignment = _mode == MusicSpectrumMode.MiniBadge
                    ? VerticalAlignment.Bottom
                    : VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            Visual visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3(centerX, centerY, 0.0f);
            visual.RotationAngleInDegrees = 0.0f;

            float idleLevel = GetIdleScale(i, _barCount, _mode);
            _smoothedBars[i] = idleLevel;
            visual.Scale = new Vector3(1.0f, idleLevel, 1.0f);
            visual.Opacity = GetIdleOpacity(_mode);

            _barPanel.Children.Add(bar);
            _barElements.Add(bar);
            _barVisuals.Add(visual);
        }

        if (_mode == MusicSpectrumMode.WaveformScrub)
        {
            ApplyWaveformProgressBrushes(force: true);
        }
    }

    private void BuildPeakHoldBarsTree()
    {
        _overlayCanvas.Visibility = Visibility.Collapsed;
        _barPanel.Visibility = Visibility.Visible;
        _barPanel.Spacing = _barSpacing;
        _barPanel.VerticalAlignment = VerticalAlignment.Bottom;

        const double capHeight = 2.2;
        double barMaxH = Math.Max(6.0, _maxBarHeight - 3.2);
        double cornerRadius = Math.Min(2.0, _barWidth * 0.45);
        float centerX = (float)(_barWidth * 0.5);
        float centerY = (float)barMaxH;

        for (int i = 0; i < _barCount; i++)
        {
            var col = new Grid
            {
                Width = _barWidth,
                Height = _maxBarHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };

            var bar = new Border
            {
                Width = _barWidth,
                Height = barMaxH,
                CornerRadius = new CornerRadius(cornerRadius, cornerRadius, 1.0, 1.0),
                Background = _barFillBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };

            var cap = new Border
            {
                Width = _barWidth,
                Height = capHeight,
                CornerRadius = new CornerRadius(capHeight * 0.5),
                Background = _capFillBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };

            Visual barVisual = ElementCompositionPreview.GetElementVisual(bar);
            barVisual.CenterPoint = new Vector3(centerX, centerY, 0.0f);
            Visual capVisual = ElementCompositionPreview.GetElementVisual(cap);

            float idleLevel = GetIdleScale(i, _barCount, _mode);
            _smoothedBars[i] = idleLevel;
            _peakLevels[i] = idleLevel;
            _peakHoldTimers[i] = 0.0f;
            _peakVelocities[i] = 0.0f;

            barVisual.Scale = new Vector3(1.0f, idleLevel, 1.0f);
            barVisual.Opacity = 0.36f;

            float capOffset = (float)((idleLevel * barMaxH) + 1.2);
            capVisual.Offset = new Vector3(0.0f, -capOffset, 0.0f);
            capVisual.Opacity = 0.48f;

            col.Children.Add(bar);
            col.Children.Add(cap);
            _barPanel.Children.Add(col);

            _barElements.Add(bar);
            _barVisuals.Add(barVisual);
            _capElements.Add(cap);
            _capVisuals.Add(capVisual);
        }
    }

    private void BuildMatrixLedTree()
    {
        _overlayCanvas.Visibility = Visibility.Collapsed;
        _barPanel.Visibility = Visibility.Visible;
        _barPanel.Spacing = _barSpacing;
        _barPanel.VerticalAlignment = VerticalAlignment.Bottom;

        const double rowGap = 1.6;
        double cellHeight = Math.Max(2.0, (_maxBarHeight - ((LedRowCount - 1) * rowGap)) / LedRowCount);
        double cellRadius = Math.Min(1.5, Math.Min(_barWidth, cellHeight) * 0.38);

        for (int i = 0; i < _barCount; i++)
        {
            var col = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = rowGap,
                Width = _barWidth,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };

            float idleLevel = GetIdleScale(i, _barCount, _mode);
            _smoothedBars[i] = idleLevel;

            // Stack top-down in XAML, store bottom-up [0 = bottom .. LedRowCount-1 = top]
            for (int topDown = 0; topDown < LedRowCount; topDown++)
            {
                int rowFromBottom = (LedRowCount - 1) - topDown;
                bool isTopRow = rowFromBottom == LedRowCount - 1;
                var cell = new Border
                {
                    Width = _barWidth,
                    Height = cellHeight,
                    CornerRadius = new CornerRadius(cellRadius),
                    Background = isTopRow ? _capFillBrush : _barFillBrush,
                    IsHitTestVisible = false
                };

                Visual cellVisual = ElementCompositionPreview.GetElementVisual(cell);
                cellVisual.Opacity = rowFromBottom == 0 ? 0.36f : 0.12f;

                _ledVisuals[i, rowFromBottom] = cellVisual;
                if (isTopRow)
                {
                    _ledTopCells.Add(cell);
                }
                else
                {
                    _ledBodyCells.Add(cell);
                }

                col.Children.Add(cell);
            }

            _barPanel.Children.Add(col);
        }
    }

    private void BuildRadialHaloTree()
    {
        _barPanel.Visibility = Visibility.Collapsed;
        _overlayCanvas.Visibility = Visibility.Visible;

        double outerRadius = _innerRadius + _maxBarHeight + 2.0;
        double outerDiameter = outerRadius * 2.0;
        _overlayCanvas.Width = outerDiameter;
        _overlayCanvas.Height = outerDiameter;
        _overlayCanvas.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayCanvas.VerticalAlignment = VerticalAlignment.Center;

        double cornerRadius = _barWidth * 0.5;
        float centerX = (float)(_barWidth * 0.5);
        float centerY = (float)_maxBarHeight;

        for (int i = 0; i < _barCount; i++)
        {
            double angleDeg = i * (360.0 / _barCount);
            double angleRad = (angleDeg - 90.0) * (Math.PI / 180.0);

            double anchorX = outerRadius + (_innerRadius * Math.Cos(angleRad));
            double anchorY = outerRadius + (_innerRadius * Math.Sin(angleRad));

            var bar = new Border
            {
                Width = _barWidth,
                Height = _maxBarHeight,
                CornerRadius = new CornerRadius(cornerRadius),
                Background = _barFillBrush,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(bar, anchorX - (_barWidth * 0.5));
            Canvas.SetTop(bar, anchorY - _maxBarHeight);

            Visual visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3(centerX, centerY, 0.0f);
            visual.RotationAngleInDegrees = (float)angleDeg;

            float idleLevel = GetIdleScale(i, _barCount, _mode);
            _smoothedBars[i] = idleLevel;
            visual.Scale = new Vector3(1.0f, idleLevel, 1.0f);
            visual.Opacity = GetIdleOpacity(_mode);

            _overlayCanvas.Children.Add(bar);
            _barElements.Add(bar);
            _barVisuals.Add(visual);
        }
    }

    private void BuildAuroraSilkTree()
    {
        _barPanel.Visibility = Visibility.Collapsed;
        _overlayCanvas.Visibility = Visibility.Visible;
        _overlayCanvas.Width = double.NaN;
        _overlayCanvas.Height = double.NaN;
        _overlayCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
        _overlayCanvas.VerticalAlignment = VerticalAlignment.Stretch;

        int segCount = SilkAnchorCount - 1;
        _silkBackSegments = new BezierSegment[segCount];
        _silkFrontSegments = new BezierSegment[segCount];
        _silkCrestSegments = new BezierSegment[segCount];

        _silkBackFigure = new PathFigure { IsClosed = true, IsFilled = true };
        _silkFrontFigure = new PathFigure { IsClosed = true, IsFilled = true };
        _silkCrestFigure = new PathFigure { IsClosed = false, IsFilled = false };

        for (int i = 0; i < segCount; i++)
        {
            var backSeg = new BezierSegment();
            var frontSeg = new BezierSegment();
            var crestSeg = new BezierSegment();
            _silkBackSegments[i] = backSeg;
            _silkFrontSegments[i] = frontSeg;
            _silkCrestSegments[i] = crestSeg;
            _silkBackFigure.Segments.Add(backSeg);
            _silkFrontFigure.Segments.Add(frontSeg);
            _silkCrestFigure.Segments.Add(crestSeg);
        }

        // Closing bottom edges for filled ribbons
        _silkBackFigure.Segments.Add(new LineSegment());
        _silkBackFigure.Segments.Add(new LineSegment());
        _silkFrontFigure.Segments.Add(new LineSegment());
        _silkFrontFigure.Segments.Add(new LineSegment());

        var backGeom = new PathGeometry();
        backGeom.Figures.Add(_silkBackFigure);
        var frontGeom = new PathGeometry();
        frontGeom.Figures.Add(_silkFrontFigure);
        var crestGeom = new PathGeometry();
        crestGeom.Figures.Add(_silkCrestFigure);

        _silkBackPath = new XamlPath
        {
            Data = backGeom,
            Fill = _silkBackFillBrush,
            IsHitTestVisible = false
        };
        _silkFrontPath = new XamlPath
        {
            Data = frontGeom,
            Fill = _silkFrontFillBrush,
            IsHitTestVisible = false
        };
        _silkCrestPath = new XamlPath
        {
            Data = crestGeom,
            Stroke = _silkCrestStrokeBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };

        _overlayCanvas.Children.Add(_silkBackPath);
        _overlayCanvas.Children.Add(_silkFrontPath);
        _overlayCanvas.Children.Add(_silkCrestPath);

        for (int i = 0; i < SilkAnchorCount; i++)
        {
            float idle = GetIdleScale(i, SilkAnchorCount, _mode);
            _smoothedBars[i] = idle;
            _secondarySmoothedBars[i] = idle * 0.85f;
        }

        UpdateAuroraSilkGeometry(force: true);
    }

    private void UpdateAuroraSilkGeometry(bool force = false)
    {
        if (_silkFrontFigure is null ||
            _silkBackFigure is null ||
            _silkCrestFigure is null ||
            _silkFrontSegments is null ||
            _silkBackSegments is null ||
            _silkCrestSegments is null)
        {
            return;
        }

        double width = ActualWidth > 16.0 ? ActualWidth : 220.0;
        if (!force && Math.Abs(width - _lastAuroraCommitWidth) < 0.25)
        {
            float maxDelta = 0.0f;
            for (int i = 0; i < SilkAnchorCount; i++)
            {
                float frontDelta = MathF.Abs(_smoothedBars[i] - _lastCommittedSilkFront[i]);
                float backDelta = MathF.Abs(_secondarySmoothedBars[i] - _lastCommittedSilkBack[i]);
                if (frontDelta > maxDelta)
                {
                    maxDelta = frontDelta;
                }

                if (backDelta > maxDelta)
                {
                    maxDelta = backDelta;
                }
            }

            if (maxDelta < 0.0018f)
            {
                return;
            }
        }

        _lastAuroraCommitWidth = width;
        double height = _maxBarHeight;
        double baseline = height;

        for (int i = 0; i < SilkAnchorCount; i++)
        {
            _lastCommittedSilkFront[i] = _smoothedBars[i];
            _lastCommittedSilkBack[i] = _secondarySmoothedBars[i];

            double normX = i / (double)(SilkAnchorCount - 1);
            _silkX[i] = normX * width;

            // Taper endpoints cleanly to baseline so the aurora ribbon emerges smoothly from the card bottom
            double edgeTaper = Math.Sin(normX * Math.PI);
            double frontAmp = _smoothedBars[i] * edgeTaper * (height - 2.0);
            double backAmp = _secondarySmoothedBars[i] * edgeTaper * (height - 3.0);

            _silkFrontY[i] = Math.Clamp(baseline - frontAmp, 1.0, baseline);
            _silkBackY[i] = Math.Clamp(baseline - backAmp, 1.5, baseline);
        }

        WriteCatmullRomToBezier(_silkX, _silkBackY, baseline, width, _silkBackFigure, _silkBackSegments, closeBottom: true);
        WriteCatmullRomToBezier(_silkX, _silkFrontY, baseline, width, _silkFrontFigure, _silkFrontSegments, closeBottom: true);
        WriteCatmullRomToBezier(_silkX, _silkFrontY, baseline, width, _silkCrestFigure, _silkCrestSegments, closeBottom: false);
    }

    private static void WriteCatmullRomToBezier(
        double[] xs,
        double[] ys,
        double baseline,
        double width,
        PathFigure figure,
        BezierSegment[] segments,
        bool closeBottom)
    {
        int n = xs.Length;
        figure.StartPoint = new Point(xs[0], ys[0]);

        for (int i = 0; i < n - 1; i++)
        {
            int i0 = Math.Max(0, i - 1);
            int i1 = i;
            int i2 = i + 1;
            int i3 = Math.Min(n - 1, i + 2);

            double cp1X = xs[i1] + ((xs[i2] - xs[i0]) / 6.0);
            double cp1Y = Math.Clamp(ys[i1] + ((ys[i2] - ys[i0]) / 6.0), 0.5, baseline);
            double cp2X = xs[i2] - ((xs[i3] - xs[i1]) / 6.0);
            double cp2Y = Math.Clamp(ys[i2] - ((ys[i3] - ys[i1]) / 6.0), 0.5, baseline);

            BezierSegment seg = segments[i];
            seg.Point1 = new Point(cp1X, cp1Y);
            seg.Point2 = new Point(cp2X, cp2Y);
            seg.Point3 = new Point(xs[i2], ys[i2]);
        }

        if (closeBottom && figure.Segments.Count >= (n - 1) + 2)
        {
            if (figure.Segments[n - 1] is LineSegment bottomRight)
            {
                bottomRight.Point = new Point(width, baseline);
            }

            if (figure.Segments[n] is LineSegment bottomLeft)
            {
                bottomLeft.Point = new Point(0.0, baseline);
            }
        }
    }

    private void SyncBrushes()
    {
        Color accent = ExtractColor(AccentBrush) ?? _lastAccentColor;
        _lastAccentColor = accent;

        Color tipHighlight = MixWithWhite(accent, 0.36f);
        Color capGlow = MixWithWhite(accent, 0.72f);
        Color unplayedColor = Color.FromArgb(0x4C, accent.R, accent.G, accent.B);
        Color secondaryAurora = ShiftHueLightly(accent);

        if (_barFillBrush is null)
        {
            _barFillBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0.0),
                EndPoint = new Point(0.5, 1.0)
            };
            _barFillBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = tipHighlight });
            _barFillBrush.GradientStops.Add(new GradientStop { Offset = 0.55, Color = accent });
            _barFillBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = accent });
        }
        else if (_barFillBrush.GradientStops.Count == 3)
        {
            _barFillBrush.GradientStops[0].Color = tipHighlight;
            _barFillBrush.GradientStops[1].Color = accent;
            _barFillBrush.GradientStops[2].Color =
                _mode is MusicSpectrumMode.SymmetricWave or MusicSpectrumMode.WaveformScrub
                    ? tipHighlight
                    : accent;
        }

        if (_barFillBrush.GradientStops.Count == 3)
        {
            _barFillBrush.GradientStops[2].Color =
                _mode is MusicSpectrumMode.SymmetricWave or MusicSpectrumMode.WaveformScrub
                    ? tipHighlight
                    : accent;
        }

        if (_capFillBrush is null)
        {
            _capFillBrush = new SolidColorBrush(capGlow);
        }
        else
        {
            _capFillBrush.Color = capGlow;
        }

        if (_unplayedFillBrush is null)
        {
            _unplayedFillBrush = new SolidColorBrush(unplayedColor);
        }
        else
        {
            _unplayedFillBrush.Color = unplayedColor;
        }

        if (_silkFrontFillBrush is null)
        {
            _silkFrontFillBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0.0),
                EndPoint = new Point(0.5, 1.0)
            };
            _silkFrontFillBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(0xB8, tipHighlight.R, tipHighlight.G, tipHighlight.B) });
            _silkFrontFillBrush.GradientStops.Add(new GradientStop { Offset = 0.55, Color = Color.FromArgb(0x5C, accent.R, accent.G, accent.B) });
            _silkFrontFillBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0x05, accent.R, accent.G, accent.B) });
        }
        else if (_silkFrontFillBrush.GradientStops.Count == 3)
        {
            _silkFrontFillBrush.GradientStops[0].Color = Color.FromArgb(0xB8, tipHighlight.R, tipHighlight.G, tipHighlight.B);
            _silkFrontFillBrush.GradientStops[1].Color = Color.FromArgb(0x5C, accent.R, accent.G, accent.B);
            _silkFrontFillBrush.GradientStops[2].Color = Color.FromArgb(0x05, accent.R, accent.G, accent.B);
        }

        if (_silkBackFillBrush is null)
        {
            _silkBackFillBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0.0),
                EndPoint = new Point(0.5, 1.0)
            };
            _silkBackFillBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(0x6E, secondaryAurora.R, secondaryAurora.G, secondaryAurora.B) });
            _silkBackFillBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0x00, secondaryAurora.R, secondaryAurora.G, secondaryAurora.B) });
        }
        else if (_silkBackFillBrush.GradientStops.Count == 2)
        {
            _silkBackFillBrush.GradientStops[0].Color = Color.FromArgb(0x6E, secondaryAurora.R, secondaryAurora.G, secondaryAurora.B);
            _silkBackFillBrush.GradientStops[1].Color = Color.FromArgb(0x00, secondaryAurora.R, secondaryAurora.G, secondaryAurora.B);
        }

        if (_silkCrestStrokeBrush is null)
        {
            _silkCrestStrokeBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.0, 0.5),
                EndPoint = new Point(1.0, 0.5)
            };
            _silkCrestStrokeBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(0x00, capGlow.R, capGlow.G, capGlow.B) });
            _silkCrestStrokeBrush.GradientStops.Add(new GradientStop { Offset = 0.18, Color = Color.FromArgb(0xDC, capGlow.R, capGlow.G, capGlow.B) });
            _silkCrestStrokeBrush.GradientStops.Add(new GradientStop { Offset = 0.50, Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) });
            _silkCrestStrokeBrush.GradientStops.Add(new GradientStop { Offset = 0.82, Color = Color.FromArgb(0xDC, capGlow.R, capGlow.G, capGlow.B) });
            _silkCrestStrokeBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0x00, capGlow.R, capGlow.G, capGlow.B) });
        }
        else if (_silkCrestStrokeBrush.GradientStops.Count == 5)
        {
            _silkCrestStrokeBrush.GradientStops[0].Color = Color.FromArgb(0x00, capGlow.R, capGlow.G, capGlow.B);
            _silkCrestStrokeBrush.GradientStops[1].Color = Color.FromArgb(0xDC, capGlow.R, capGlow.G, capGlow.B);
            _silkCrestStrokeBrush.GradientStops[3].Color = Color.FromArgb(0xDC, capGlow.R, capGlow.G, capGlow.B);
            _silkCrestStrokeBrush.GradientStops[4].Color = Color.FromArgb(0x00, capGlow.R, capGlow.G, capGlow.B);
        }

        if (_mode == MusicSpectrumMode.WaveformScrub)
        {
            ApplyWaveformProgressBrushes(force: true);
        }
    }

    private void ApplyWaveformProgressBrushes(bool force)
    {
        int count = _barElements.Count;
        if (count == 0)
        {
            return;
        }

        int playheadIdx = _progressRatio <= 0.001
            ? -1
            : Math.Clamp((int)Math.Floor(_progressRatio * count), 0, count - 1);

        if (!force && playheadIdx == _lastPlayheadIndex)
        {
            return;
        }

        _lastPlayheadIndex = playheadIdx;
        for (int i = 0; i < count; i++)
        {
            if (i == playheadIdx)
            {
                _barElements[i].Background = _capFillBrush;
            }
            else if (i < playheadIdx)
            {
                _barElements[i].Background = _barFillBrush;
            }
            else
            {
                _barElements[i].Background = _unplayedFillBrush;
            }
        }
    }

    private void StartFrameTimer()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_frameTimer is null)
        {
            DispatcherQueue? queue = DispatcherQueue;
            if (queue is null)
            {
                return;
            }

            _frameTimer = queue.CreateTimer();
            _frameTimer.Interval = TimeSpan.FromMilliseconds(FrameIntervalMs);
            _frameTimer.IsRepeating = true;
            _frameTimer.Tick += FrameTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        if (!_frameTimer.IsRunning)
        {
            _lastTickTimestamp = Stopwatch.GetTimestamp();
            _frameTimer.Start();
        }
    }

    private void StopFrameTimer()
    {
        if (_frameTimer is { IsRunning: true })
        {
            _frameTimer.Stop();
        }
    }

    private void DestroyFrameTimer()
    {
        DispatcherQueueTimer? timer = _frameTimer;
        if (timer is null)
        {
            return;
        }

        _frameTimer = null;
        if (timer.IsRunning)
        {
            timer.Stop();
        }

        timer.Tick -= FrameTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void ReleaseCaptureSubscription()
    {
        IDisposable? sub = _captureSubscription;
        _captureSubscription = null;
        sub?.Dispose();
    }

    private void FrameTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed || !_isBuilt)
        {
            StopFrameTimer();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        float dt = (float)((now - _lastTickTimestamp) / (double)Stopwatch.Frequency);
        _lastTickTimestamp = now;
        dt = Math.Clamp(dt, 0.008f, 0.10f);

        if (_shouldAnimate)
        {
            _ = MusicSpectrumService.Shared.ReadLatestSamples(
                _pcmScratch,
                out int sampleRate,
                out bool captureAvailable);

            _fft.ComputeBands(
                _pcmScratch,
                sampleRate,
                isPlaying: true,
                captureAvailable: captureAvailable,
                deltaTimeSeconds: dt,
                outputBands24: _bands24);

            TickActiveFrame(dt);
            return;
        }

        if (TickSettlingFrame(dt))
        {
            SnapToIdle();
            StopFrameTimer();
        }
    }

    private void TickActiveFrame(float dt)
    {
        int count = _mode == MusicSpectrumMode.AuroraSilk ? SilkAnchorCount : _barCount;
        Span<float> targetSlice = _targetBars.AsSpan(0, count);

        switch (_mode)
        {
            case MusicSpectrumMode.MiniBadge:
                MusicSpectrumFft.MapMiniBadgeBars(_bands24, targetSlice);
                break;

            case MusicSpectrumMode.PeakHoldBars:
            case MusicSpectrumMode.MatrixLed:
                MusicSpectrumFft.MapLinearEqualizerBars(_bands24, targetSlice);
                break;

            case MusicSpectrumMode.VinylRadialHalo:
                MusicSpectrumFft.MapRadialHaloBars(_bands24, targetSlice);
                break;

            case MusicSpectrumMode.AuroraSilk:
            case MusicSpectrumMode.WaveformScrub:
            case MusicSpectrumMode.SymmetricWave:
            default:
                MusicSpectrumFft.MapSymmetricWaveBars(_bands24, targetSlice);
                break;
        }

        float attack = 1.0f - MathF.Exp(-dt * 24.0f);
        float release = 1.0f - MathF.Exp(-dt * 7.4f);

        if (_mode == MusicSpectrumMode.AuroraSilk)
        {
            _silkPhase += dt * 2.6;
            float backAttack = 1.0f - MathF.Exp(-dt * 14.0f);
            float backRelease = 1.0f - MathF.Exp(-dt * 5.0f);

            for (int i = 0; i < SilkAnchorCount; i++)
            {
                float desired = 0.10f + (0.90f * targetSlice[i]);
                float prev = _smoothedBars[i];
                float next = prev + ((desired - prev) * (desired > prev ? attack : release));
                _smoothedBars[i] = next;

                int mirrored = (SilkAnchorCount - 1) - i;
                float shimmer = 0.06f * MathF.Sin((float)_silkPhase + (i * 0.55f));
                float backDesired = Math.Clamp(0.08f + (0.78f * targetSlice[mirrored]) + shimmer, 0.05f, 1.0f);
                float backPrev = _secondarySmoothedBars[i];
                _secondarySmoothedBars[i] = backPrev + ((backDesired - backPrev) * (backDesired > backPrev ? backAttack : backRelease));
            }

            UpdateAuroraSilkGeometry();
            return;
        }

        if (_mode == MusicSpectrumMode.MatrixLed)
        {
            for (int i = 0; i < count; i++)
            {
                float desired = 0.12f + (0.88f * targetSlice[i]);
                float prev = _smoothedBars[i];
                float next = prev + ((desired - prev) * (desired > prev ? attack : release));
                _smoothedBars[i] = next;
                ApplyLedColumnOpacity(i, next);
            }

            return;
        }

        if (_mode == MusicSpectrumMode.PeakHoldBars)
        {
            double barMaxH = Math.Max(6.0, _maxBarHeight - 3.2);
            for (int i = 0; i < _barVisuals.Count; i++)
            {
                float desired = 0.13f + (0.87f * targetSlice[i]);
                float prev = _smoothedBars[i];
                float next = prev + ((desired - prev) * (desired > prev ? attack : release));
                _smoothedBars[i] = next;

                if (next >= _peakLevels[i])
                {
                    _peakLevels[i] = next;
                    _peakHoldTimers[i] = 0.18f;
                    _peakVelocities[i] = 0.0f;
                }
                else if (_peakHoldTimers[i] > 0.0f)
                {
                    _peakHoldTimers[i] -= dt;
                }
                else
                {
                    _peakVelocities[i] += 1.75f * dt;
                    _peakLevels[i] = Math.Max(next, _peakLevels[i] - (_peakVelocities[i] * dt));
                }

                Visual barVis = _barVisuals[i];
                barVis.Scale = new Vector3(1.0f, next, 1.0f);
                barVis.Opacity = Math.Clamp(0.48f + (0.52f * targetSlice[i]), 0.42f, 1.0f);

                Visual capVis = _capVisuals[i];
                float capOffset = (float)((_peakLevels[i] * barMaxH) + 1.2);
                capVis.Offset = new Vector3(0.0f, -capOffset, 0.0f);
                capVis.Opacity = Math.Clamp(0.68f + (0.32f * _peakLevels[i]), 0.58f, 1.0f);
            }

            return;
        }

        float minFloor = _mode switch
        {
            MusicSpectrumMode.WaveformScrub => 0.22f,
            MusicSpectrumMode.VinylRadialHalo => 0.18f,
            MusicSpectrumMode.MiniBadge => 0.20f,
            _ => 0.16f
        };

        int playheadIdx = _mode == MusicSpectrumMode.WaveformScrub ? _lastPlayheadIndex : -2;

        for (int i = 0; i < _barVisuals.Count; i++)
        {
            float boost = i == playheadIdx ? 0.10f : 0.0f;
            float desired = Math.Clamp(minFloor + ((1.0f - minFloor) * targetSlice[i]) + boost, 0.0f, 1.0f);
            float prev = _smoothedBars[i];
            float coeff = desired > prev ? attack : release;
            float next = prev + ((desired - prev) * coeff);
            _smoothedBars[i] = next;

            Visual visual = _barVisuals[i];
            visual.Scale = new Vector3(1.0f, next, 1.0f);

            if (_mode == MusicSpectrumMode.WaveformScrub)
            {
                visual.Opacity = i <= playheadIdx
                    ? Math.Clamp(0.82f + (0.18f * targetSlice[i]), 0.80f, 1.0f)
                    : Math.Clamp(0.54f + (0.30f * targetSlice[i]), 0.50f, 0.85f);
            }
            else if (_mode == MusicSpectrumMode.VinylRadialHalo)
            {
                visual.Opacity = Math.Clamp(0.38f + (0.62f * targetSlice[i]), 0.34f, 1.0f);
            }
            else if (_mode == MusicSpectrumMode.MiniBadge)
            {
                visual.Opacity = Math.Clamp(0.62f + (0.38f * targetSlice[i]), 0.55f, 1.0f);
            }
            else
            {
                visual.Opacity = Math.Clamp(0.42f + (0.58f * targetSlice[i]), 0.38f, 1.0f);
            }
        }
    }

    private bool TickSettlingFrame(float dt)
    {
        float settleRate = 1.0f - MathF.Exp(-dt * 11.0f);
        bool allSettled = true;

        if (_mode == MusicSpectrumMode.AuroraSilk)
        {
            for (int i = 0; i < SilkAnchorCount; i++)
            {
                float idle = GetIdleScale(i, SilkAnchorCount, _mode);
                float next = _smoothedBars[i] + ((idle - _smoothedBars[i]) * settleRate);
                float nextBack = _secondarySmoothedBars[i] + (((idle * 0.85f) - _secondarySmoothedBars[i]) * settleRate);
                if (MathF.Abs(next - idle) > 0.008f)
                {
                    allSettled = false;
                }

                _smoothedBars[i] = next;
                _secondarySmoothedBars[i] = nextBack;
            }

            UpdateAuroraSilkGeometry();
            return allSettled;
        }

        if (_mode == MusicSpectrumMode.MatrixLed)
        {
            for (int i = 0; i < _barCount; i++)
            {
                float idle = GetIdleScale(i, _barCount, _mode);
                float next = _smoothedBars[i] + ((idle - _smoothedBars[i]) * settleRate);
                if (MathF.Abs(next - idle) > 0.008f)
                {
                    allSettled = false;
                }

                _smoothedBars[i] = next;
                ApplyLedColumnOpacity(i, next);
            }

            return allSettled;
        }

        if (_mode == MusicSpectrumMode.PeakHoldBars)
        {
            double barMaxH = Math.Max(6.0, _maxBarHeight - 3.2);
            for (int i = 0; i < _barVisuals.Count; i++)
            {
                float idle = GetIdleScale(i, _barCount, _mode);
                float next = _smoothedBars[i] + ((idle - _smoothedBars[i]) * settleRate);
                float nextPeak = _peakLevels[i] + ((idle - _peakLevels[i]) * settleRate);
                if (MathF.Abs(next - idle) > 0.008f || MathF.Abs(nextPeak - idle) > 0.008f)
                {
                    allSettled = false;
                }

                _smoothedBars[i] = next;
                _peakLevels[i] = nextPeak;

                Visual barVis = _barVisuals[i];
                barVis.Scale = new Vector3(1.0f, next, 1.0f);
                barVis.Opacity += (0.36f - barVis.Opacity) * settleRate;

                Visual capVis = _capVisuals[i];
                float capOffset = (float)((nextPeak * barMaxH) + 1.2);
                capVis.Offset = new Vector3(0.0f, -capOffset, 0.0f);
                capVis.Opacity += (0.48f - capVis.Opacity) * settleRate;
            }

            return allSettled;
        }

        float idleOpacity = GetIdleOpacity(_mode);
        int count = _barVisuals.Count;
        for (int i = 0; i < count; i++)
        {
            float idleScale = GetIdleScale(i, count, _mode);
            float prev = _smoothedBars[i];
            float next = prev + ((idleScale - prev) * settleRate);
            if (MathF.Abs(next - idleScale) > 0.008f)
            {
                allSettled = false;
            }
            else
            {
                next = idleScale;
            }

            _smoothedBars[i] = next;
            Visual visual = _barVisuals[i];
            visual.Scale = new Vector3(1.0f, next, 1.0f);
            visual.Opacity += (idleOpacity - visual.Opacity) * settleRate;
        }

        return allSettled;
    }

    private void SnapToIdle()
    {
        if (_mode == MusicSpectrumMode.AuroraSilk)
        {
            for (int i = 0; i < SilkAnchorCount; i++)
            {
                float idle = GetIdleScale(i, SilkAnchorCount, _mode);
                _smoothedBars[i] = idle;
                _secondarySmoothedBars[i] = idle * 0.85f;
            }

            UpdateAuroraSilkGeometry(force: true);
            return;
        }

        if (_mode == MusicSpectrumMode.MatrixLed)
        {
            for (int i = 0; i < _barCount; i++)
            {
                float idle = GetIdleScale(i, _barCount, _mode);
                _smoothedBars[i] = idle;
                ApplyLedColumnOpacity(i, idle);
            }

            return;
        }

        if (_mode == MusicSpectrumMode.PeakHoldBars)
        {
            double barMaxH = Math.Max(6.0, _maxBarHeight - 3.2);
            for (int i = 0; i < _barVisuals.Count; i++)
            {
                float idle = GetIdleScale(i, _barCount, _mode);
                _smoothedBars[i] = idle;
                _peakLevels[i] = idle;
                _peakHoldTimers[i] = 0.0f;
                _peakVelocities[i] = 0.0f;

                Visual barVis = _barVisuals[i];
                barVis.Scale = new Vector3(1.0f, idle, 1.0f);
                barVis.Opacity = 0.36f;

                Visual capVis = _capVisuals[i];
                float capOffset = (float)((idle * barMaxH) + 1.2);
                capVis.Offset = new Vector3(0.0f, -capOffset, 0.0f);
                capVis.Opacity = 0.48f;
            }

            return;
        }

        int count = _barVisuals.Count;
        float idleOpacity = GetIdleOpacity(_mode);
        for (int i = 0; i < count; i++)
        {
            float idleScale = GetIdleScale(i, count, _mode);
            _smoothedBars[i] = idleScale;
            Visual visual = _barVisuals[i];
            visual.Scale = new Vector3(1.0f, idleScale, 1.0f);
            visual.Opacity = _mode == MusicSpectrumMode.WaveformScrub && i <= _lastPlayheadIndex
                ? 0.78f
                : idleOpacity;
        }
    }

    private void ApplyLedColumnOpacity(int colIndex, float level)
    {
        float lit = Math.Clamp(level, 0.0f, 1.0f) * LedRowCount;
        for (int r = 0; r < LedRowCount; r++)
        {
            Visual? cellVis = _ledVisuals[colIndex, r];
            if (cellVis is null)
            {
                continue;
            }

            if (r + 1 <= lit)
            {
                cellVis.Opacity = 0.96f;
            }
            else if (r < lit)
            {
                float frac = lit - r;
                cellVis.Opacity = 0.12f + (0.84f * frac);
            }
            else
            {
                cellVis.Opacity = 0.12f;
            }
        }
    }

    private static float GetIdleScale(int index, int count, MusicSpectrumMode mode)
    {
        if (mode == MusicSpectrumMode.MiniBadge)
        {
            return (index & 3) switch
            {
                0 => 0.26f,
                1 => 0.40f,
                2 => 0.22f,
                _ => 0.32f
            };
        }

        if (mode == MusicSpectrumMode.AuroraSilk)
        {
            return 0.09f;
        }

        if (mode == MusicSpectrumMode.VinylRadialHalo)
        {
            return 0.15f;
        }

        if (mode == MusicSpectrumMode.MatrixLed)
        {
            return 0.18f;
        }

        if (mode == MusicSpectrumMode.WaveformScrub)
        {
            // Subtle organic static waveform silhouette even when paused
            float wave = 0.22f + (0.14f * MathF.Abs(MathF.Sin((index * 0.65f) + 0.4f)));
            return Math.Clamp(wave, 0.18f, 0.40f);
        }

        if (count <= 1)
        {
            return 0.18f;
        }

        float center = (count - 1) * 0.5f;
        float dist = MathF.Abs(index - center) / Math.Max(1.0f, center);
        float bell = MathF.Cos(dist * MathF.PI * 0.5f);
        return 0.14f + (0.09f * bell);
    }

    private static float GetIdleOpacity(MusicSpectrumMode mode) => mode switch
    {
        MusicSpectrumMode.MiniBadge => 0.42f,
        MusicSpectrumMode.VinylRadialHalo => 0.28f,
        MusicSpectrumMode.WaveformScrub => 0.52f,
        _ => 0.34f
    };

    private static Color? ExtractColor(Brush? brush) =>
        brush is SolidColorBrush solid ? solid.Color : null;

    private static Color MixWithWhite(Color color, float amount)
    {
        byte r = (byte)Math.Clamp((int)MathF.Round(color.R + ((255 - color.R) * amount)), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(color.G + ((255 - color.G) * amount)), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(color.B + ((255 - color.B) * amount)), 0, 255);
        return Color.FromArgb(color.A == 0 ? (byte)0xFF : color.A, r, g, b);
    }

    private static Color ShiftHueLightly(Color color)
    {
        // Subtle iridescent aurora shift towards cyan/violet without HSV overhead
        byte r = (byte)Math.Clamp((int)((color.R * 0.72f) + (color.B * 0.28f) + 24), 0, 255);
        byte g = (byte)Math.Clamp((int)((color.G * 0.82f) + (color.R * 0.18f) + 12), 0, 255);
        byte b = (byte)Math.Clamp((int)((color.B * 0.85f) + 38), 0, 255);
        return Color.FromArgb(0xFF, r, g, b);
    }
}
