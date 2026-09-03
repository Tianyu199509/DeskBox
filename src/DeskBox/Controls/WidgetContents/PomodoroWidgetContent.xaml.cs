using System.ComponentModel;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// 番茄钟的机械表盘式界面。布局变化只调整视觉尺寸，计时真值由 ViewModel 持有。
/// </summary>
public sealed partial class PomodoroWidgetContent : UserControl, IDisposable
{
    private readonly Ellipse[] _roundDots;
    private bool _isResponsiveLayoutTransitionActive;
    private double _responsiveTargetWidth;
    private double _responsiveTargetHeight;
    private bool _visualUpdateQueued;
    private bool _isDisposed;

    public PomodoroWidgetContent(PomodoroWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        _roundDots = [RoundDot1, RoundDot2, RoundDot3, RoundDot4];
        ViewModel = viewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.CompletionOccurred += ViewModel_CompletionOccurred;
        ActualThemeChanged += PomodoroWidgetContent_ActualThemeChanged;
        Loaded += PomodoroWidgetContent_Loaded;
        UpdateVisuals();
    }

    public PomodoroWidgetViewModel ViewModel { get; }

    public void ApplyAppearance()
    {
        UpdateVisuals();
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
        UpdateResponsiveLayout();
        UpdateVisuals();
    }

    private void PomodoroWidgetContent_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        UpdateVisuals();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueVisualUpdate();
    }

    /// <summary>
    /// ViewModel 会为同一次状态快照发布多个相关属性。合并到下一次 UI
    /// 调度可避免每秒重复重建画刷、圆点和进度几何。
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

    private void ViewModel_CompletionOccurred(object? sender, EventArgs e)
    {
        if (_isDisposed || !WindowsCompatibilityService.ShouldAnimate)
        {
            return;
        }

        var storyboard = new Storyboard();
        var scaleX = CreateCompletionPulseAnimation();
        var scaleY = CreateCompletionPulseAnimation();
        Storyboard.SetTarget(scaleX, DialScaleTransform);
        Storyboard.SetTarget(scaleY, DialScaleTransform);
        Storyboard.SetTargetProperty(scaleX, nameof(ScaleTransform.ScaleX));
        Storyboard.SetTargetProperty(scaleY, nameof(ScaleTransform.ScaleY));
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Begin();
    }

    private static DoubleAnimation CreateCompletionPulseAnimation()
    {
        return new DoubleAnimation
        {
            From = 1,
            To = 1.035,
            Duration = new Duration(TimeSpan.FromMilliseconds(170)),
            AutoReverse = true,
            EnableDependentAnimation = true
        };
    }

    private void UpdateVisuals()
    {
        if (_isDisposed)
        {
            return;
        }

        Brush accent = GetThemeBrush(
            ViewModel.IsFocusPhase
                ? "PomodoroFocusBrush"
                : "PomodoroBreakBrush");
        Brush softAccent = GetThemeBrush(
            ViewModel.IsFocusPhase
                ? "PomodoroFocusSoftBrush"
                : "PomodoroBreakSoftBrush");

        PhaseTextBlock.Text = ViewModel.PhaseText;
        CountdownTextBlock.Text = ViewModel.CountdownText;
        DescriptionTextBlock.Text = ViewModel.DescriptionText;
        RoundSummaryTextBlock.Text = ViewModel.RoundSummaryText;
        PrimaryActionTextBlock.Text = ViewModel.PrimaryActionText;
        PrimaryActionIcon.Glyph = ViewModel.PrimaryActionGlyph;

        PhasePill.Background = softAccent;
        PhaseDot.Fill = accent;
        PhaseTextBlock.Foreground = accent;
        AmbientDisc.Fill = softAccent;
        ProgressArc.Stroke = accent;
        PrimaryActionButton.Background = accent;
        PrimaryActionButton.BorderBrush = accent;
        PrimaryActionButton.Foreground = GetThemeBrush("PomodoroAccentTextBrush");

        ToolTipService.SetToolTip(ResetButton, ViewModel.ResetActionText);
        ToolTipService.SetToolTip(PrimaryActionButton, ViewModel.PrimaryActionText);
        ToolTipService.SetToolTip(SkipButton, ViewModel.SkipActionText);
        AutomationProperties.SetName(ResetButton, ViewModel.ResetActionText);
        AutomationProperties.SetName(PrimaryActionButton, ViewModel.PrimaryActionText);
        AutomationProperties.SetName(SkipButton, ViewModel.SkipActionText);
        AutomationProperties.SetName(
            DialHost,
            $"{ViewModel.PhaseText}, {ViewModel.CountdownText}, {ViewModel.RoundSummaryText}");

        UpdateRoundDots(accent);
        UpdateProgressArc();
    }

    private void UpdateRoundDots(Brush accent)
    {
        Brush pending = GetThemeBrush("PomodoroPendingDotBrush");
        int completed = ViewModel.CompletedRoundsInCycle;
        int activeIndex = ViewModel.IsFocusPhase ? ViewModel.RoundNumber - 1 : -1;
        for (int index = 0; index < _roundDots.Length; index++)
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

        bool showPhase = width >= 120 && height >= 105;
        bool showActions = width >= 150 && height >= 120;
        bool showDescription = width >= 230 && height >= 265;
        bool showRounds = width >= 150 && height >= 215;
        bool dense = width < 245 || height < 245;
        PhasePill.Visibility = showPhase
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActionBar.Visibility = showActions
            ? Visibility.Visible
            : Visibility.Collapsed;
        DescriptionTextBlock.Visibility = showDescription
            ? Visibility.Visible
            : Visibility.Collapsed;
        RoundPanel.Visibility = showRounds
            ? Visibility.Visible
            : Visibility.Collapsed;
        RootGrid.Padding = dense
            ? new Thickness(10, 6, 10, 9)
            : new Thickness(16, 10, 16, 14);

        double horizontalPadding = dense ? 20 : 32;
        double verticalPadding = dense ? 15 : 24;
        double accessoryHeight =
            (showPhase ? 26 : 0) +
            (showRounds ? 24 : 0) +
            (showActions ? (dense ? 40 : 44) : 0) +
            (dense ? 8 : 16);
        double diameter = Math.Clamp(
            Math.Min(
                width - horizontalPadding,
                height - verticalPadding - accessoryHeight),
            24,
            190);
        DialHost.Width = diameter;
        DialHost.Height = diameter;
        DialHost.Margin = dense
            ? new Thickness(0, 4, 0, 4)
            : new Thickness(0, 8, 0, 8);

        double stroke = Math.Clamp(Math.Round(diameter * 0.052), 3, 10);
        ProgressTrack.StrokeThickness = stroke;
        ProgressArc.StrokeThickness = stroke;
        CenterDisc.Margin = new Thickness(Math.Clamp(diameter * 0.083, 3, 16));
        CountdownTextBlock.FontSize = Math.Clamp(diameter * 0.285, 12, 54);
        DescriptionTextBlock.MaxWidth = Math.Max(90, diameter - 34);
        DescriptionTextBlock.FontSize = dense ? 9.5 : 10.5;

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

        UpdateProgressArc();
    }

    private static void SetCircularButtonSize(Button button, double size)
    {
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
        button.CornerRadius = new CornerRadius(size / 2);
    }

    private void UpdateProgressArc()
    {
        double diameter = DialHost.Width;
        double stroke = ProgressArc.StrokeThickness;
        if (!double.IsFinite(diameter) || diameter <= stroke ||
            ViewModel.Progress <= 0)
        {
            ProgressArc.Data = null;
            return;
        }

        double center = diameter / 2;
        double radius = Math.Max(1, (diameter - stroke) / 2);
        double angle = Math.Min(359.999, ViewModel.Progress * 360);
        double radians = angle * Math.PI / 180;
        var start = new Point(center, center - radius);
        var end = new Point(
            center + radius * Math.Sin(radians),
            center - radius * Math.Cos(radians));

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = angle > 180,
            SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        ProgressArc.Data = geometry;
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
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.CompletionOccurred -= ViewModel_CompletionOccurred;
        ActualThemeChanged -= PomodoroWidgetContent_ActualThemeChanged;
        Loaded -= PomodoroWidgetContent_Loaded;
    }
}
