using System.Diagnostics;
using System.ComponentModel;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// 番茄钟的原生番茄圆盘界面。布局变化只调整视觉尺寸，计时真值由 ViewModel 持有。
/// </summary>
public sealed partial class PomodoroWidgetContent : UserControl, IDisposable
{
    private const double TomatoVisualAspectRatio = 208d / 190d;
    private const double ProgressRingCenter = 95;
    private const double ProgressRingRadius = 90;
    private static readonly TimeSpan ProgressFrameInterval =
        TimeSpan.FromMilliseconds(33);

    private readonly List<Ellipse> _roundDots = [];
    private readonly Stopwatch _progressInterpolationClock = new();
    private DispatcherQueueTimer? _progressAnimationTimer;
    private ArcSegment? _progressArcSegment;
    private PomodoroTimerPhase? _renderedPhase;
    private string? _renderedPhaseText;
    private string? _renderedCountdownText;
    private string? _renderedRoundSummaryText;
    private string? _renderedPrimaryActionText;
    private string? _renderedPrimaryActionGlyph;
    private string? _renderedResetActionText;
    private string? _renderedSkipActionText;
    private int _renderedRoundCount = -1;
    private int _renderedCompletedRounds = -1;
    private int _renderedRoundNumber = -1;
    private bool _renderedIsFocusPhase;
    private bool _isResponsiveLayoutTransitionActive;
    private double _responsiveTargetWidth;
    private double _responsiveTargetHeight;
    private double _progressAnchor;
    private double _progressDurationSeconds = 1;
    private bool _progressShouldAdvance;
    private bool _isLoaded;
    private bool _visualUpdateQueued;
    private bool _isDisposed;

    public PomodoroWidgetContent(PomodoroWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        InitializeProgressArc();
        ViewModel = viewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ActualThemeChanged += PomodoroWidgetContent_ActualThemeChanged;
        Loaded += PomodoroWidgetContent_Loaded;
        Unloaded += PomodoroWidgetContent_Unloaded;
        UpdateVisuals(force: true);
    }

    public PomodoroWidgetViewModel ViewModel { get; }

    public void ApplyAppearance()
    {
        UpdateVisuals(force: true);
    }

    internal void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        _isResponsiveLayoutTransitionActive = true;
        _responsiveTargetWidth = Math.Max(0, targetContentWidth);
        _responsiveTargetHeight = Math.Max(0, targetContentHeight);
        if (!isCollapsing)
        {
            ApplyResponsiveLayout(_responsiveTargetWidth, _responsiveTargetHeight);
        }
    }

    internal void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        _responsiveTargetWidth = 0;
        _responsiveTargetHeight = 0;
        ApplyResponsiveLayout(finalContentWidth, finalContentHeight);
    }

    internal void CancelResponsiveLayoutTransition()
    {
        _isResponsiveLayoutTransitionActive = false;
        _responsiveTargetWidth = 0;
        _responsiveTargetHeight = 0;
        UpdateResponsiveLayout();
    }

    private void PomodoroWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        UpdateResponsiveLayout();
        UpdateVisuals(force: true);
    }

    private void PomodoroWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopProgressAnimationTimer();
    }

    private void PomodoroWidgetContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        UpdateVisuals(force: true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueVisualUpdate();
    }

    /// <summary>
    /// ViewModel 会为同一次状态快照发布多个相关属性。合并到下一次 UI
    /// 调度并缓存稳定状态，确保每秒刷新只更新倒计时文本与进度几何。
    /// </summary>
    private void QueueVisualUpdate()
    {
        if (_isDisposed || _visualUpdateQueued)
        {
            return;
        }

        _visualUpdateQueued = true;
        if (DispatcherQueue.TryEnqueue(() =>
        {
            _visualUpdateQueued = false;
            UpdateVisuals();
        }))
        {
            return;
        }

        _visualUpdateQueued = false;
        UpdateVisuals();
    }

    private void UpdateVisuals(bool force = false)
    {
        if (_isDisposed)
        {
            return;
        }

        PomodoroTimerPhase phase = ViewModel.Phase;
        bool phaseVisualChanged = force || _renderedPhase != phase;
        (string accentKey, string strongAccentKey, string softAccentKey) =
            GetPhasePaletteKeys();
        Brush accent = GetThemeBrush(accentKey);
        Brush strongAccent = GetThemeBrush(strongAccentKey);
        Brush softAccent = GetThemeBrush(softAccentKey);

        string phaseText = ViewModel.PhaseText;
        if (force || _renderedPhaseText != phaseText)
        {
            PhaseTextBlock.Text = phaseText;
            _renderedPhaseText = phaseText;
        }

        string countdownText = ViewModel.CountdownText;
        if (force || _renderedCountdownText != countdownText)
        {
            CountdownTextBlock.Text = countdownText;
            _renderedCountdownText = countdownText;
        }

        string roundSummaryText = ViewModel.RoundSummaryText;
        if (force || _renderedRoundSummaryText != roundSummaryText)
        {
            RoundSummaryTextBlock.Text = roundSummaryText;
            _renderedRoundSummaryText = roundSummaryText;
        }

        string primaryActionText = ViewModel.PrimaryActionText;
        if (force || _renderedPrimaryActionText != primaryActionText)
        {
            PrimaryActionTextBlock.Text = primaryActionText;
            ToolTipService.SetToolTip(PrimaryActionButton, primaryActionText);
            AutomationProperties.SetName(PrimaryActionButton, primaryActionText);
            _renderedPrimaryActionText = primaryActionText;
        }

        string primaryActionGlyph = ViewModel.PrimaryActionGlyph;
        if (force || _renderedPrimaryActionGlyph != primaryActionGlyph)
        {
            PrimaryActionIcon.Glyph = primaryActionGlyph;
            _renderedPrimaryActionGlyph = primaryActionGlyph;
        }

        string resetActionText = ViewModel.ResetActionText;
        if (force || _renderedResetActionText != resetActionText)
        {
            ToolTipService.SetToolTip(ResetButton, resetActionText);
            AutomationProperties.SetName(ResetButton, resetActionText);
            _renderedResetActionText = resetActionText;
        }

        string skipActionText = ViewModel.SkipActionText;
        if (force || _renderedSkipActionText != skipActionText)
        {
            ToolTipService.SetToolTip(SkipButton, skipActionText);
            AutomationProperties.SetName(SkipButton, skipActionText);
            _renderedSkipActionText = skipActionText;
        }

        if (phaseVisualChanged)
        {
            AmbientDisc.Fill = softAccent;
            TomatoDiscShape.Fill = accent;
            TomatoDiscShape.Stroke = strongAccent;
            ProgressTrackRing.Stroke = softAccent;
            ProgressArc.Stroke = accent;
            PrimaryActionButton.Background = accent;
            PrimaryActionButton.BorderBrush = strongAccent;
            PrimaryActionButton.Foreground = GetThemeBrush("PomodoroAccentTextBrush");
            ResetButton.Foreground = strongAccent;
            SkipButton.Foreground = strongAccent;
            _renderedPhase = phase;
        }

        AutomationProperties.SetName(
            DialHost,
            $"{phaseText}, {countdownText}, {roundSummaryText}");

        int roundCount = ViewModel.RoundCount;
        int completedRounds = ViewModel.CompletedRoundsInCycle;
        int roundNumber = ViewModel.RoundNumber;
        bool isFocusPhase = ViewModel.IsFocusPhase;
        if (force || phaseVisualChanged ||
            _renderedRoundCount != roundCount ||
            _renderedCompletedRounds != completedRounds ||
            _renderedRoundNumber != roundNumber ||
            _renderedIsFocusPhase != isFocusPhase)
        {
            UpdateRoundDots(accent);
            _renderedRoundCount = roundCount;
            _renderedCompletedRounds = completedRounds;
            _renderedRoundNumber = roundNumber;
            _renderedIsFocusPhase = isFocusPhase;
        }

        SyncProgressFromViewModel();
    }

    private (string Accent, string StrongAccent, string SoftAccent) GetPhasePaletteKeys()
    {
        if (ViewModel.IsFocusPhase)
        {
            return (
                "PomodoroFocusBrush",
                "PomodoroFocusStrongBrush",
                "PomodoroFocusSoftBrush");
        }

        return ViewModel.IsLongBreakPhase
            ? (
                "PomodoroLongBreakBrush",
                "PomodoroLongBreakStrongBrush",
                "PomodoroLongBreakSoftBrush")
            : (
                "PomodoroShortBreakBrush",
                "PomodoroShortBreakStrongBrush",
                "PomodoroShortBreakSoftBrush");
    }

    /// <summary>
    /// 进度环使用固定的 190×190 设计坐标，由外层 Viewbox 统一缩放。
    /// 几何对象只创建一次，后续帧仅修改圆弧终点与长弧标记。
    /// </summary>
    private void InitializeProgressArc()
    {
        var start = new Point(
            ProgressRingCenter,
            ProgressRingCenter - ProgressRingRadius);
        _progressArcSegment = new ArcSegment
        {
            Point = start,
            Size = new Size(ProgressRingRadius, ProgressRingRadius),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        };
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(_progressArcSegment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ProgressArc.Data = geometry;
        ProgressArc.Opacity = 0;
    }

    /// <summary>
    /// 低频 ViewModel 快照只负责校准锚点；帧定时器不会读取 ViewModel。
    /// </summary>
    private void SyncProgressFromViewModel()
    {
        double snapshotProgress = Math.Clamp(ViewModel.Progress, 0, 1);
        if (!double.IsFinite(snapshotProgress))
        {
            snapshotProgress = 0;
        }

        double durationSeconds = ViewModel.PhaseDuration.TotalSeconds;
        _progressAnchor = snapshotProgress;
        _progressDurationSeconds = double.IsFinite(durationSeconds) &&
            durationSeconds > 0
            ? durationSeconds
            : 1;
        _progressShouldAdvance = ViewModel.IsRunning &&
            durationSeconds > 0 &&
            snapshotProgress < 1;
        _progressInterpolationClock.Restart();
        UpdateProgressArc(snapshotProgress);
        UpdateProgressAnimationTimer();
    }

    private void UpdateProgressAnimationTimer()
    {
        if (_isLoaded && _progressShouldAdvance)
        {
            StartProgressAnimationTimer();
            return;
        }

        StopProgressAnimationTimer();
    }

    private void StartProgressAnimationTimer()
    {
        if (_progressAnimationTimer is not null)
        {
            return;
        }

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = ProgressFrameInterval;
        timer.IsRepeating = true;
        timer.Tick += ProgressAnimationTimer_Tick;
        _progressAnimationTimer = timer;
        timer.Start();
    }

    private void StopProgressAnimationTimer()
    {
        if (_progressAnimationTimer is not { } timer)
        {
            return;
        }

        _progressAnimationTimer = null;
        timer.Stop();
        timer.Tick -= ProgressAnimationTimer_Tick;
    }

    private void ProgressAnimationTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        if (_isDisposed || !_isLoaded || !_progressShouldAdvance)
        {
            StopProgressAnimationTimer();
            return;
        }

        double progress = _progressAnchor +
            _progressInterpolationClock.Elapsed.TotalSeconds /
            _progressDurationSeconds;
        UpdateProgressArc(progress);
        if (progress >= 1)
        {
            _progressShouldAdvance = false;
            StopProgressAnimationTimer();
        }
    }

    private void UpdateProgressArc(double progress)
    {
        if (_progressArcSegment is null)
        {
            return;
        }

        double normalizedProgress = Math.Clamp(progress, 0, 1);
        if (!double.IsFinite(normalizedProgress) || normalizedProgress <= 0)
        {
            ProgressArc.Opacity = 0;
            return;
        }

        double angle = Math.Min(359.999, normalizedProgress * 360);
        double radians = angle * Math.PI / 180;
        _progressArcSegment.Point = new Point(
            ProgressRingCenter + ProgressRingRadius * Math.Sin(radians),
            ProgressRingCenter - ProgressRingRadius * Math.Cos(radians));
        _progressArcSegment.IsLargeArc = angle > 180;
        ProgressArc.Opacity = 1;
    }

    private void UpdateRoundDots(Brush accent)
    {
        EnsureRoundDots();
        Brush pending = GetThemeBrush("PomodoroPendingDotBrush");
        int completed = ViewModel.CompletedRoundsInCycle;
        int activeIndex = ViewModel.IsFocusPhase ? ViewModel.RoundNumber - 1 : -1;
        for (int index = 0; index < _roundDots.Count; index++)
        {
            Ellipse dot = _roundDots[index];
            bool isCompleted = index < completed;
            bool isActive = index == activeIndex;
            dot.Fill = isCompleted || isActive ? accent : pending;
            dot.Opacity = isActive ? 1 : isCompleted ? 0.62 : 1;
            double size = isActive ? 8 : 6;
            dot.Width = size;
            dot.Height = size;
        }
    }

    private void EnsureRoundDots()
    {
        int targetCount = ViewModel.RoundCount;
        while (_roundDots.Count > targetCount)
        {
            int lastIndex = _roundDots.Count - 1;
            Ellipse dot = _roundDots[lastIndex];
            RoundDotsPanel.Children.Remove(dot);
            _roundDots.RemoveAt(lastIndex);
        }

        while (_roundDots.Count < targetCount)
        {
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6
            };
            _roundDots.Add(dot);
            RoundDotsPanel.Children.Add(dot);
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (_isResponsiveLayoutTransitionActive || RootGrid is null)
        {
            return;
        }

        ApplyResponsiveLayout(RootGrid.ActualWidth, RootGrid.ActualHeight);
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0)
        {
            return;
        }

        bool showActions = width >= 148 && height >= 148;
        bool showRounds = width >= 160 && height >= 232;
        bool dense = width < 250 || height < 270;
        ActionBar.Visibility = showActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        RoundPanel.Visibility = showRounds
            ? Visibility.Visible
            : Visibility.Collapsed;
        RootGrid.Padding = dense
            ? new Thickness(10, 5, 10, 9)
            : new Thickness(16, 10, 16, 14);

        double horizontalPadding = dense ? 20 : 32;
        double verticalPadding = dense ? 12 : 22;
        double accessoryHeight =
            (showRounds ? 22 : 0) +
            (showActions ? (dense ? 40 : 44) : 0) +
            (dense ? 8 : 14);
        double availableDialHeight = Math.Max(
            36,
            height - verticalPadding - accessoryHeight);
        double diameter = Math.Clamp(
            Math.Min(
                width - horizontalPadding,
                availableDialHeight / TomatoVisualAspectRatio),
            42,
            270);
        DialHost.Width = diameter;
        DialHost.Height = diameter * TomatoVisualAspectRatio;
        DialHost.Margin = dense
            ? new Thickness(0, 2, 0, 4)
            : new Thickness(0, 4, 0, 8);

        LeafCrownViewbox.Visibility = diameter >= 68
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool showPrimaryActionText = showActions && width >= 220;
        double sideButtonSize = width < 190 ? 32 : dense ? 36 : 40;
        SetCircularButtonSize(ResetButton, sideButtonSize);
        SetCircularButtonSize(SkipButton, sideButtonSize);
        PrimaryActionTextBlock.Visibility = showPrimaryActionText
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrimaryActionButton.Padding = showPrimaryActionText
            ? new Thickness(14, 0, 14, 0)
            : new Thickness(0);
        PrimaryActionButton.Height = dense ? 40 : 44;
        if (showPrimaryActionText)
        {
            double minimumWidth = dense ? 96 : 112;
            double availableWidth = Math.Max(
                minimumWidth,
                width - horizontalPadding - sideButtonSize * 2 - 20);
            PrimaryActionButton.Width = double.NaN;
            PrimaryActionButton.MinWidth = minimumWidth;
            PrimaryActionButton.MaxWidth = Math.Min(
                dense ? 156 : 184,
                availableWidth);
        }
        else
        {
            double buttonSize = dense ? 40 : 44;
            PrimaryActionButton.Width = buttonSize;
            PrimaryActionButton.MinWidth = buttonSize;
            PrimaryActionButton.MaxWidth = buttonSize;
        }
        PrimaryActionButton.CornerRadius = new CornerRadius(
            PrimaryActionButton.Height / 2);
    }

    private static void SetCircularButtonSize(Button button, double size)
    {
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
        button.CornerRadius = new CornerRadius(size / 2);
    }

    private Brush GetThemeBrush(string key)
    {
        return Resources[key] as Brush ??
            Application.Current.Resources[key] as Brush ??
            new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.StartPause();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Reset();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Skip();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isLoaded = false;
        _progressShouldAdvance = false;
        StopProgressAnimationTimer();
        _progressInterpolationClock.Stop();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ActualThemeChanged -= PomodoroWidgetContent_ActualThemeChanged;
        Loaded -= PomodoroWidgetContent_Loaded;
        Unloaded -= PomodoroWidgetContent_Unloaded;
    }
}
