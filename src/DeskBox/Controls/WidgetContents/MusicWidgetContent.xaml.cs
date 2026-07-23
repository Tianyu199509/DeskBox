using System.Numerics;
using System.ComponentModel;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
namespace DeskBox.Controls.WidgetContents;

public sealed partial class MusicWidgetContent : UserControl, IDisposable
{
    private const float AlbumArtHoverScale = 1.018f;
    private const double AlbumArtHoverOffset = 3.0;
    private const double TitleMarqueeGap = 32.0;
    private const double TitleMarqueeStartDelayMs = 900.0;
    private const double TitleMarqueeSpeedPixelsPerSecond = 50.0;
    private const double TitleMarqueeOverflowTolerance = 4.0;
    private const int TitleMarqueeDeferredMeasureMs = 300;
    private const int ArtworkTransitionDurationMs = 420;
    private const double MinimumResponsiveWidth = 180.0;
    private const double WideResponsiveWidth = 320.0;
    private const double MinimumResponsiveHeight = 180.0;
    private const double WideResponsiveHeight = 240.0;
    private const double WideAlbumArtSize = 82.0;
    private const double MinimumAlbumArtSize = 60.0;
    private const double WideIconButtonSize = 30.0;
    private const double CompactIconButtonSize = 30.0;
    private const double WidePrimaryButtonSize = 42.0;
    private const double CompactPrimaryButtonSize = 30.0;
    private bool _isProgressDragging;
    private bool _isProgressHovering;
    private bool _isInlineVolumeRefreshing;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _titleMarqueeTimer;
    private DateTimeOffset _titleMarqueeStartedAt;
    private double _titleMarqueeDistance;
    private int _titleMarqueeMeasureVersion;
    private int _artworkTransitionVersion;
    private bool _isDisposed;
    private bool _isMinimalLayout;
    private bool _isRecordLayout;
    private Storyboard? _recordVinylRotationStoryboard;
    private bool _isRecordVinylRotating;
    private bool _isRecordHorizontalLayout;
    private Storyboard? _recordHorizontalVinylRotationStoryboard;
    private bool _isRecordHorizontalVinylRotating;
    private bool _isResponsiveLayoutTransitionActive;
    private Button? _volumeAnchorButton;

    public MusicWidgetContent()
    {
        InitializeComponent();
        Loaded += MusicWidgetContent_Loaded;
        Unloaded += MusicWidgetContent_Unloaded;
        SizeChanged += MusicWidgetContent_SizeChanged;
    }

    public MusicWidgetContent(MusicWidgetViewModel viewModel)
        : this()
    {
        ViewModel = viewModel;
    }

    public MusicWidgetViewModel? ViewModel
    {
        get => DataContext as MusicWidgetViewModel;
        set
        {
            if (DataContext is MusicWidgetViewModel oldViewModel)
            {
                oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            DataContext = value;

            if (value is not null)
            {
                value.PropertyChanged += ViewModel_PropertyChanged;
            }
            else
            {
                // ViewModel is being detached (Dispose path).
                // Stop the title marquee timer to prevent it from
                // referencing the old ViewModel after disposal.
                StopTitleMarquee();
            }

            UpdateProgressVisuals();
        }
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.PreviousAsync();
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.TogglePlayPauseAsync();
        }
    }

    private async void PlaybackModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.CyclePlaybackModeAsync();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.NextAsync();
        }
    }

    private async void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (InlineVolumePanel.Visibility == Visibility.Visible)
            {
                InlineVolumePanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (ViewModel is null)
            {
                return;
            }

            _volumeAnchorButton = sender as Button ?? VolumeButton;
            PositionInlineVolumePanel();
            InlineVolumePanel.Visibility = Visibility.Visible;
            await RefreshInlineVolumeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Show inline volume failed: {ex}");
            InlineVolumePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (InlineVolumePanel.Visibility != Visibility.Visible)
        {
            return;
        }

        FrameworkElement? sourceElement = e.OriginalSource as FrameworkElement;
        if (IsElementInside(sourceElement, InlineVolumePanel) ||
            IsElementInside(sourceElement, _volumeAnchorButton ?? VolumeButton))
        {
            return;
        }

        InlineVolumePanel.Visibility = Visibility.Collapsed;
    }

    private void InlineVolumePanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void MusicWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureTitleMarqueeTimer();
        ApplyResponsiveLayout();
        UpdateProgressVisuals();
        QueueTitleMarqueeUpdate();
    }

    private void MusicWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        InlineVolumePanel.Visibility = Visibility.Collapsed;
        StopTitleMarquee();
    }

    private void MusicWidgetContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isResponsiveLayoutTransitionActive)
        {
            ApplyResponsiveLayout();
        }
        UpdateProgressVisuals();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ++_titleMarqueeMeasureVersion;
        ++_artworkTransitionVersion;
        Loaded -= MusicWidgetContent_Loaded;
        Unloaded -= MusicWidgetContent_Unloaded;
        SizeChanged -= MusicWidgetContent_SizeChanged;
        if (_titleMarqueeTimer is not null)
        {
            _titleMarqueeTimer.Stop();
            _titleMarqueeTimer.Tick -= TitleMarqueeTimer_Tick;
            _titleMarqueeTimer = null;
        }

        ViewModel = null;
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (visible)
        {
            QueueTitleMarqueeUpdate();
        }
        else
        {
            StopTitleMarquee();
        }
    }

    internal void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (!double.IsFinite(targetContentWidth) || !double.IsFinite(targetContentHeight))
        {
            return;
        }

        _isResponsiveLayoutTransitionActive = true;
        if (!isCollapsing)
        {
            ApplyResponsiveLayout(targetContentWidth, targetContentHeight);
        }
    }

    internal void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        ApplyResponsiveLayout(finalContentWidth, finalContentHeight);
    }

    internal void CancelResponsiveLayoutTransition()
    {
        _isResponsiveLayoutTransitionActive = false;
        ApplyResponsiveLayout();
    }

    private void TitleMarqueeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var clip = ReferenceEquals(sender, MinimalTitleMarqueeHost)
            ? MinimalTitleMarqueeClip
            : TitleMarqueeClip;
        clip.Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
        QueueTitleMarqueeUpdate();
    }

    private void ProgressHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isProgressHovering = true;
        UpdateProgressVisuals();
    }

    private void ProgressHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isProgressHovering = false;
        if (!_isProgressDragging)
        {
            UpdateProgressVisuals();
        }
    }

    private void ProgressHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.CanInteractWithProgress != true)
        {
            return;
        }

        _isProgressDragging = true;
        ProgressHost.CapturePointer(e.Pointer);
        ViewModel.BeginSeek();
        UpdateSeekFromPointer(e);
        e.Handled = true;
    }

    private void ProgressHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isProgressDragging)
        {
            return;
        }

        UpdateSeekFromPointer(e);
        e.Handled = true;
    }

    private async void ProgressHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            UpdateSeekFromPointer(e);
            await ViewModel.CommitSeekAsync();
        }

        _isProgressDragging = false;
        ProgressHost.ReleasePointerCapture(e.Pointer);
        UpdateProgressVisuals();
        e.Handled = true;
    }

    private void AlbumArtSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAlbumArtCenterPoint();
    }

    private void AlbumArtSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.EnableCoverHoverMotion != true)
        {
            ResetAlbumArtMotion();
            return;
        }

        UpdateAlbumArtCenterPoint();
        AlbumArtSurface.Scale = new Vector3(AlbumArtHoverScale, AlbumArtHoverScale, 1);
    }

    private void AlbumArtSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.EnableCoverHoverMotion != true ||
            AlbumArtSurface.ActualWidth <= 0 ||
            AlbumArtSurface.ActualHeight <= 0)
        {
            ResetAlbumArtMotion();
            return;
        }

        var position = e.GetCurrentPoint(AlbumArtSurface).Position;
        double offsetX = ((position.X / AlbumArtSurface.ActualWidth) - 0.5) * AlbumArtHoverOffset;
        double offsetY = ((position.Y / AlbumArtSurface.ActualHeight) - 0.5) * AlbumArtHoverOffset;
        AlbumArtSurface.Translation = new Vector3((float)offsetX, (float)offsetY, 0);
        AlbumArtSurface.Scale = new Vector3(AlbumArtHoverScale, AlbumArtHoverScale, 1);
    }

    private void AlbumArtSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ResetAlbumArtMotion();
    }

    private void ResetAlbumArtMotion()
    {
        AlbumArtSurface.Translation = Vector3.Zero;
        AlbumArtSurface.Scale = Vector3.One;
    }

    private void ApplyResponsiveLayout()
    {
        double width = ActualWidth > 0 ? ActualWidth : RootGrid.ActualWidth;
        double height = ActualHeight > 0 ? ActualHeight : RootGrid.ActualHeight;
        ApplyResponsiveLayout(width, height);
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        string normalizedMode = SettingsService.NormalizeMusicDisplayMode(ViewModel?.DisplayMode);
        bool isRecordVertical = normalizedMode == SettingsService.MusicDisplayModeRecordVertical;
        bool isRecordHorizontal = normalizedMode == SettingsService.MusicDisplayModeRecordHorizontal;
        bool isRecord = isRecordVertical || isRecordHorizontal;
        bool useMinimalLayout = !isRecord && ShouldUseMinimalLayout(width, height, ViewModel?.DisplayMode);
        if ((_isMinimalLayout != useMinimalLayout) || (_isRecordLayout != isRecordVertical) || (_isRecordHorizontalLayout != isRecordHorizontal))
        {
            _isMinimalLayout = useMinimalLayout;
            _isRecordLayout = isRecordVertical;
            _isRecordHorizontalLayout = isRecordHorizontal;
            MinimalLayout.Visibility = (!isRecord && useMinimalLayout) ? Visibility.Visible : Visibility.Collapsed;
            ContentGrid.Visibility = (!isRecord && !useMinimalLayout) ? Visibility.Visible : Visibility.Collapsed;
            RecordLayout.Visibility = isRecordVertical ? Visibility.Visible : Visibility.Collapsed;
            RecordHorizontalLayout.Visibility = isRecordHorizontal ? Visibility.Visible : Visibility.Collapsed;
            RecordTonearm.Visibility = isRecordVertical ? Visibility.Visible : Visibility.Collapsed;
            InlineVolumePanel.Visibility = Visibility.Collapsed;
            ResetAlbumArtMotion();
            StopTitleMarquee();
            if (!isRecordVertical)
            {
                StopRecordVinylRotation();
            }
            if (!isRecordHorizontal)
            {
                StopRecordHorizontalVinylRotation();
            }
        }

        if (isRecordVertical)
        {
            ApplyRecordLayoutSizing(width, height);
            UpdateRecordVinylRotation();
            AnimateTonearmForState();
            QueueTitleMarqueeUpdate();
            DispatcherQueue.TryEnqueue(UpdateProgressVisuals);
            return;
        }

        if (isRecordHorizontal)
        {
            ApplyRecordHorizontalSizing(width, height);
            UpdateRecordHorizontalVinylRotation();
            QueueTitleMarqueeUpdate();
            DispatcherQueue.TryEnqueue(UpdateProgressVisuals);
            return;
        }

        if (useMinimalLayout)
        {
            QueueTitleMarqueeUpdate();
            return;
        }

        double widthRatio = Math.Clamp(
            (width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth),
            0.0,
            1.0);
        double heightRatio = Math.Clamp(
            (height - MinimumResponsiveHeight) / (WideResponsiveHeight - MinimumResponsiveHeight),
            0.0,
            1.0);
        double densityRatio = Math.Min(widthRatio, heightRatio);

        double albumSize = Math.Round(Lerp(MinimumAlbumArtSize, WideAlbumArtSize, densityRatio));
        double iconButtonSize = Math.Round(Lerp(CompactIconButtonSize, WideIconButtonSize, widthRatio));
        double primaryButtonSize = Math.Round(Lerp(CompactPrimaryButtonSize, WidePrimaryButtonSize, widthRatio));
        double contentPadding = Math.Round(Lerp(8, 12, densityRatio));
        double columnSpacing = Math.Round(Lerp(8, 12, widthRatio));
        double rowSpacing = Math.Round(Lerp(4, 8, heightRatio));
        double controlsSpacing = Math.Round(Lerp(3, 10, widthRatio));
        double timelineColumnWidth = Math.Round(Lerp(28, 34, widthRatio));
        double progressTopMargin = Math.Round(Lerp(0, 3, heightRatio));
        double controlsTopMargin = Math.Round(Lerp(2, 5, heightRatio));

        ContentGrid.Padding = new Thickness(contentPadding);
        ContentGrid.ColumnSpacing = columnSpacing;
        ContentGrid.RowSpacing = rowSpacing;
        AlbumColumn.Width = new GridLength(albumSize);
        TopRow.Height = new GridLength(albumSize);
        SetAlbumArtSize(albumSize);
        ProgressRow.Margin = new Thickness(0, progressTopMargin, 0, 0);
        ProgressRow.ColumnSpacing = Math.Round(Lerp(5, 8, widthRatio));
        PositionColumn.Width = new GridLength(timelineColumnWidth);
        DurationColumn.Width = new GridLength(timelineColumnWidth);
        TrackInfoGrid.RowSpacing = Math.Round(Lerp(2, 4, heightRatio));
        ControlsPanel.Margin = new Thickness(0, controlsTopMargin, 0, 0);
        ControlsPanel.Spacing = controlsSpacing;
        SetButtonSize(PlaybackModeButton, iconButtonSize);
        SetButtonSize(PreviousButton, primaryButtonSize);
        SetButtonSize(NextButton, primaryButtonSize);
        SetButtonSize(VolumeButton, iconButtonSize);
        SetButtonSize(PlayPauseButton, primaryButtonSize);
        PlaybackModeButton.CornerRadius = new CornerRadius(5);
        PreviousButton.CornerRadius = new CornerRadius(5);
        NextButton.CornerRadius = new CornerRadius(5);
        VolumeButton.CornerRadius = new CornerRadius(5);
        PlayPauseButton.CornerRadius = new CornerRadius(5);
        InlineVolumePanel.Width = Math.Clamp(width - 12, 156, 238);
        PositionInlineVolumePanel();
        QueueTitleMarqueeUpdate();
    }

    internal static bool ShouldUseMinimalLayout(double width, double height)
    {
        return width < MinimumResponsiveWidth || height < MinimumResponsiveHeight;
    }

    internal static bool ShouldUseMinimalLayout(double width, double height, string? displayMode)
    {
        return SettingsService.NormalizeMusicDisplayMode(displayMode) switch
        {
            SettingsService.MusicDisplayModeCover => true,
            SettingsService.MusicDisplayModeControls => false,
            _ => ShouldUseMinimalLayout(width, height)
        };
    }

    private void ApplyRecordLayoutSizing(double width, double height)
    {
        // Reuse the Controls-mode button sizing so both modes stay consistent.
        double widthRatio = Math.Clamp(
            (width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth),
            0.0,
            1.0);
        double iconButtonSize = Math.Round(Lerp(CompactIconButtonSize, WideIconButtonSize, widthRatio));
        double primaryButtonSize = Math.Round(Lerp(CompactPrimaryButtonSize, WidePrimaryButtonSize, widthRatio));

        SetButtonSize(RecordPlaybackModeButton, iconButtonSize);
        SetButtonSize(RecordPreviousButton, primaryButtonSize);
        SetButtonSize(RecordPlayPauseButton, primaryButtonSize);
        SetButtonSize(RecordNextButton, primaryButtonSize);
        SetButtonSize(RecordVolumeButton, iconButtonSize);
        RecordControlsPanel.Spacing = Math.Round(Lerp(4, 10, widthRatio));

        // On narrow grids drop the playback-mode button first; volume stays.
        RecordPlaybackModeButton.Visibility = width < 190 ? Visibility.Collapsed : Visibility.Visible;

        // Reserve room for the title/artist/progress/controls rows below the disc.
        double reserved = 108 + primaryButtonSize;
        double vinylSize = Math.Clamp(Math.Min(width - 24, height - reserved), 64, 230);
        RecordVinylHost.Width = vinylSize;
        RecordVinylHost.Height = vinylSize;

        bool small = vinylSize < 110;
        RecordLayout.Padding = new Thickness(small ? 8 : 12);
        RecordLayout.RowSpacing = small ? 4 : 6;
        RecordTitleText.FontSize = small ? 12.5 : 14;
        RecordArtistText.FontSize = small ? 10.5 : 11.5;
        UpdateTonearmLayout(vinylSize, small);
    }

    private void UpdateTonearmLayout(double vinylSize, bool small)
    {
        // The tonearm only reads well on a reasonably large disc.
        if (small || !_isRecordLayout)
        {
            RecordTonearm.Visibility = Visibility.Collapsed;
            return;
        }

        RecordTonearm.Visibility = Visibility.Visible;

        // Scale the tonearm with the disc; pivot sits just off the platter's top-right edge.
        double armWidth = Math.Round(vinylSize * 0.42);
        double armHeight = Math.Round(armWidth * 1.2);
        RecordTonearm.Width = armWidth;
        RecordTonearm.Height = armHeight;
        RecordTonearm.Margin = new Thickness(
            Math.Round(vinylSize * 0.72), Math.Round(-vinylSize * 0.30), 0, 0);

        var visual = ElementCompositionPreview.GetElementVisual(RecordTonearm);
        if (visual is not null)
        {
            // Design space is 100x120 with the bearing at (50, 20) -> (0.5 W, H/6).
            visual.CenterPoint = new Vector3((float)(armWidth * 0.5), (float)(armHeight / 6.0), 0f);
        }
    }

    private void EnsureRecordVinylRotationStoryboard()
    {
        if (_recordVinylRotationStoryboard is not null)
        {
            return;
        }

        _recordVinylRotationStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(5.0)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(rotate, RecordVinylRotateTransform);
        Storyboard.SetTargetProperty(rotate, "(RotateTransform.Angle)");
        _recordVinylRotationStoryboard.Children.Add(rotate);
    }

    private void UpdateRecordVinylRotation()
    {
        bool shouldRotate = RecordLayout.Visibility == Visibility.Visible &&
            ViewModel?.IsPlaying == true;
        if (shouldRotate == _isRecordVinylRotating)
        {
            return;
        }

        _isRecordVinylRotating = shouldRotate;
        EnsureRecordVinylRotationStoryboard();
        if (shouldRotate)
        {
            _recordVinylRotationStoryboard.Begin();
        }
        else
        {
            _recordVinylRotationStoryboard.Stop();
        }
    }

    private void StopRecordVinylRotation()
    {
        if (!_isRecordVinylRotating)
        {
            return;
        }

        _isRecordVinylRotating = false;
        _recordVinylRotationStoryboard?.Stop();
    }

    private void ApplyRecordHorizontalSizing(double width, double height)
    {
        // Reuse the Controls-mode button sizing so both modes stay consistent.
        double widthRatio = Math.Clamp(
            (width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth),
            0.0,
            1.0);
        double iconButtonSize = Math.Round(Lerp(CompactIconButtonSize, WideIconButtonSize, widthRatio));
        double primaryButtonSize = Math.Round(Lerp(CompactPrimaryButtonSize, WidePrimaryButtonSize, widthRatio));

        SetButtonSize(RecordHorizontalPlaybackModeButton, iconButtonSize);
        SetButtonSize(RecordHorizontalPreviousButton, primaryButtonSize);
        SetButtonSize(RecordHorizontalPlayPauseButton, primaryButtonSize);
        SetButtonSize(RecordHorizontalNextButton, primaryButtonSize);
        SetButtonSize(RecordHorizontalVolumeButton, iconButtonSize);
        double controlsSpacing = Math.Round(Lerp(3, 8, widthRatio));
        RecordHorizontalControlsPanel.Spacing = controlsSpacing;

        // On narrow grids drop the playback-mode button first; volume stays.
        bool small = width < 250;
        bool showPlaybackMode = !small;
        RecordHorizontalPlaybackModeButton.Visibility = showPlaybackMode ? Visibility.Visible : Visibility.Collapsed;

        double padding = small ? 8 : 12;
        double columnSpacing = small ? 8 : 12;
        RecordHorizontalLayout.Padding = new Thickness(padding);
        RecordHorizontalLayout.ColumnSpacing = columnSpacing;
        RecordHorizontalTitleText.FontSize = small ? 13.5 : 15;
        RecordHorizontalArtistText.FontSize = small ? 11 : 12;

        // Never let the vinyl squeeze the controls: budget the buttons' width first.
        int buttonCount = showPlaybackMode ? 5 : 4;
        double buttonsWidth = 3 * primaryButtonSize + (buttonCount - 3) * iconButtonSize +
            (buttonCount - 1) * controlsSpacing;
        double vinylBudget = width - buttonsWidth - padding * 2 - columnSpacing;
        double vinylSize = Math.Clamp(
            Math.Min(height - 2 * padding, Math.Min(vinylBudget, width * 0.45)), 56, 180);
        RecordHorizontalVinylHost.Width = vinylSize;
        RecordHorizontalVinylHost.Height = vinylSize;
    }

    private void EnsureRecordHorizontalVinylRotationStoryboard()
    {
        if (_recordHorizontalVinylRotationStoryboard is not null)
        {
            return;
        }

        _recordHorizontalVinylRotationStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(5.0)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(rotate, RecordHorizontalVinylRotateTransform);
        Storyboard.SetTargetProperty(rotate, "(RotateTransform.Angle)");
        _recordHorizontalVinylRotationStoryboard.Children.Add(rotate);
    }

    private void UpdateRecordHorizontalVinylRotation()
    {
        bool shouldRotate = RecordHorizontalLayout.Visibility == Visibility.Visible &&
            ViewModel?.IsPlaying == true;
        if (shouldRotate == _isRecordHorizontalVinylRotating)
        {
            return;
        }

        _isRecordHorizontalVinylRotating = shouldRotate;
        EnsureRecordHorizontalVinylRotationStoryboard();
        if (shouldRotate)
        {
            _recordHorizontalVinylRotationStoryboard.Begin();
        }
        else
        {
            _recordHorizontalVinylRotationStoryboard.Stop();
        }
    }

    private void StopRecordHorizontalVinylRotation()
    {
        if (!_isRecordHorizontalVinylRotating)
        {
            return;
        }

        _isRecordHorizontalVinylRotating = false;
        _recordHorizontalVinylRotationStoryboard?.Stop();
    }

    private void AnimateTonearmForState()
    {
        bool isPlaying = ViewModel?.IsPlaying == true;
        float targetAngle = isPlaying ? 0f : 22f;

        var visual = ElementCompositionPreview.GetElementVisual(RecordTonearm);
        if (visual is null)
        {
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, visual.RotationAngleInDegrees);
        anim.InsertKeyFrame(1f, targetAngle, easing);
        anim.Duration = TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("RotationAngleInDegrees", anim);
    }

    private void SetAlbumArtSize(double size)
    {
        AlbumArtShadow.Width = size;
        AlbumArtShadow.Height = size;
        AlbumArtSurface.Width = size;
        AlbumArtSurface.Height = size;
        double cornerRadius = Math.Max(8, size * 0.12);
        AlbumArtShadow.CornerRadius = new CornerRadius(cornerRadius);
        AlbumArtSurface.CornerRadius = new CornerRadius(cornerRadius);
        AlbumArtInnerBorder.CornerRadius = new CornerRadius(Math.Max(0, cornerRadius - 1));
        AlbumArtPlaceholderBackground.CornerRadius = new CornerRadius(cornerRadius);
    }

    private static void SetButtonSize(Button button, double size)
    {
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + (end - start) * Math.Clamp(progress, 0.0, 1.0);
    }

    private void PositionInlineVolumePanel()
    {
        Button anchor = _volumeAnchorButton ?? VolumeButton;
        if (anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0)
        {
            return;
        }

        var buttonOrigin = anchor.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double panelWidth = InlineVolumePanel.Width > 0 ? InlineVolumePanel.Width : InlineVolumePanel.ActualWidth;
        double panelHeight = InlineVolumePanel.Height > 0 ? InlineVolumePanel.Height : InlineVolumePanel.ActualHeight;
        double left = buttonOrigin.X + anchor.ActualWidth - panelWidth;
        double top = buttonOrigin.Y - panelHeight - 7;

        if (RootGrid.ActualWidth > 0)
        {
            left = Math.Clamp(left, 6, Math.Max(6, RootGrid.ActualWidth - panelWidth - 6));
        }

        if (top < 6)
        {
            top = 6;
        }

        InlineVolumePanel.Margin = new Thickness(Math.Round(left), Math.Round(top), 0, 0);
    }

    private static bool IsElementInside(FrameworkElement? sourceElement, FrameworkElement target)
    {
        DependencyObject? current = sourceElement;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task RefreshInlineVolumeAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        _isInlineVolumeRefreshing = true;
        try
        {
            await ViewModel.RefreshSystemVolumeAsync();
        }
        finally
        {
            _isInlineVolumeRefreshing = false;
        }
    }

    private async void InlineSystemVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInlineVolumeRefreshing ||
            InlineVolumePanel.Visibility != Visibility.Visible ||
            ViewModel is null)
        {
            return;
        }

        await ViewModel.SetSystemVolumeAsync(e.NewValue);
    }

    private void UpdateAlbumArtCenterPoint()
    {
        AlbumArtSurface.CenterPoint = new Vector3(
            (float)(AlbumArtSurface.ActualWidth / 2),
            (float)(AlbumArtSurface.ActualHeight / 2),
            0);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicWidgetViewModel.SeekValue) or
            nameof(MusicWidgetViewModel.SeekMaximum) or
            nameof(MusicWidgetViewModel.CanSeek) or
            nameof(MusicWidgetViewModel.HasSeekableTimeline) or
            nameof(MusicWidgetViewModel.CanInteractWithProgress))
        {
            UpdateProgressVisuals();
        }

        if (e.PropertyName is nameof(MusicWidgetViewModel.Title) or
            nameof(MusicWidgetViewModel.TitleTextSize) or
            nameof(MusicWidgetViewModel.MinimalTitleTextSize))
        {
            QueueTitleMarqueeUpdate();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.ThumbnailImage))
        {
            QueueArtworkTransition();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.DisplayMode))
        {
            ApplyResponsiveLayout();
        }

        if (e.PropertyName is nameof(MusicWidgetViewModel.IsPlaying) or
            nameof(MusicWidgetViewModel.PlaybackState))
        {
            UpdateRecordHorizontalVinylRotation();
            if (RecordTonearm.Visibility == Visibility.Visible)
            {
                AnimateTonearmForState();
            }
        }

    }

    private void QueueArtworkTransition()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(QueueArtworkTransition);
            return;
        }

        int version = ++_artworkTransitionVersion;
        if (ViewModel?.ThumbnailImage is null)
        {
            ResetArtworkVisual(MinimalArtworkImage);
            ResetArtworkVisual(AlbumArtworkImage);
            return;
        }

        PrepareArtworkVisual(MinimalArtworkImage);
        PrepareArtworkVisual(AlbumArtworkImage);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || version != _artworkTransitionVersion || ViewModel?.ThumbnailImage is null)
            {
                return;
            }

            StartArtworkTransition(MinimalArtworkImage);
            StartArtworkTransition(AlbumArtworkImage);
        });
    }

    private static void PrepareArtworkVisual(FrameworkElement image)
    {
        var visual = ElementCompositionPreview.GetElementVisual(image);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.CenterPoint = new Vector3(
            (float)(image.ActualWidth / 2),
            (float)(image.ActualHeight / 2),
            0);
        visual.Opacity = 0;
        visual.Scale = new Vector3(0.975f, 0.975f, 1);
    }

    private static void StartArtworkTransition(FrameworkElement image)
    {
        var visual = ElementCompositionPreview.GetElementVisual(image);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0),
            new Vector2(0, 1));

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(ArtworkTransitionDurationMs);
        opacityAnimation.InsertKeyFrame(0, 0);
        opacityAnimation.InsertKeyFrame(1, 1, easing);

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(ArtworkTransitionDurationMs);
        scaleAnimation.InsertKeyFrame(0, new Vector3(0.975f, 0.975f, 1));
        scaleAnimation.InsertKeyFrame(1, Vector3.One, easing);

        visual.Opacity = 1;
        visual.Scale = Vector3.One;
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Scale", scaleAnimation);
    }

    private static void ResetArtworkVisual(FrameworkElement image)
    {
        var visual = ElementCompositionPreview.GetElementVisual(image);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }

    private void EnsureTitleMarqueeTimer()
    {
        if (_titleMarqueeTimer is not null)
        {
            return;
        }

        _titleMarqueeTimer = DispatcherQueue.CreateTimer();
        _titleMarqueeTimer.Interval = TimeSpan.FromMilliseconds(16);
        _titleMarqueeTimer.IsRepeating = true;
        _titleMarqueeTimer.Tick += TitleMarqueeTimer_Tick;
    }

    private void QueueTitleMarqueeUpdate()
    {
        if (!IsLoaded)
        {
            return;
        }

        int version = ++_titleMarqueeMeasureVersion;
        _ = DispatcherQueue.TryEnqueue(() => UpdateTitleMarquee(version));
        _ = RunDeferredTitleMarqueeUpdateAsync(version);
    }

    private async Task RunDeferredTitleMarqueeUpdateAsync(int version)
    {
        await Task.Delay(TitleMarqueeDeferredMeasureMs);
        if (version != _titleMarqueeMeasureVersion || !IsLoaded)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => UpdateTitleMarquee(version));
    }

    private void UpdateTitleMarquee(int version)
    {
        if (version != _titleMarqueeMeasureVersion)
        {
            return;
        }

        var elements = GetActiveTitleMarqueeElements();
        double viewportWidth = elements.Host.ActualWidth;
        if (!IsLoaded || viewportWidth <= 0)
        {
            StopTitleMarquee();
            return;
        }

        double titleWidth = MeasureTitleWidth();
        if (titleWidth <= 0)
        {
            StopTitleMarquee();
            return;
        }

        bool shouldScroll = titleWidth > viewportWidth + TitleMarqueeOverflowTolerance;
        if (!shouldScroll)
        {
            StopTitleMarquee();
            return;
        }

        elements.Primary.Width = titleWidth;
        elements.Clone.Width = titleWidth;
        elements.Static.Opacity = 0;
        elements.Canvas.Visibility = Visibility.Visible;
        Canvas.SetLeft(elements.Primary, 0);
        Canvas.SetLeft(elements.Clone, titleWidth + TitleMarqueeGap);
        _titleMarqueeDistance = titleWidth + TitleMarqueeGap;
        _titleMarqueeStartedAt = DateTimeOffset.UtcNow;
        elements.Canvas.Translation = Vector3.Zero;
        EnsureTitleMarqueeTimer();
        _titleMarqueeTimer?.Start();
    }

    private void StopTitleMarquee()
    {
        _titleMarqueeTimer?.Stop();
        _titleMarqueeDistance = 0;
        ResetTitleMarqueeElements(TitleStaticText, TitleMarqueeCanvas, TitleTextPrimary, TitleTextClone);
        ResetTitleMarqueeElements(
            MinimalTitleStaticText,
            MinimalTitleMarqueeCanvas,
            MinimalTitleTextPrimary,
            MinimalTitleTextClone);
    }

    private void TitleMarqueeTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var elements = GetActiveTitleMarqueeElements();
        double titleWidth = MeasureTitleWidth();
        if (_titleMarqueeDistance <= 0 ||
            elements.Host.ActualWidth <= 0 ||
            titleWidth <= elements.Host.ActualWidth + TitleMarqueeOverflowTolerance)
        {
            StopTitleMarquee();
            return;
        }

        double elapsedMs = (DateTimeOffset.UtcNow - _titleMarqueeStartedAt).TotalMilliseconds;
        double movingMs = Math.Max(0, elapsedMs - TitleMarqueeStartDelayMs);
        double offset = movingMs * TitleMarqueeSpeedPixelsPerSecond / 1000.0;

        if (offset >= _titleMarqueeDistance)
        {
            _titleMarqueeStartedAt = DateTimeOffset.UtcNow;
            offset = 0;
        }

        elements.Canvas.Translation = new Vector3((float)-offset, 0, 0);
    }

    private double MeasureTitleWidth()
    {
        // Use ViewModel's Title directly to avoid binding propagation timing issues
        string? title = ViewModel?.Title;
        if (string.IsNullOrEmpty(title))
        {
            return 0;
        }

        var elements = GetActiveTitleMarqueeElements();
        elements.Measure.Text = title;
        elements.Measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredWidth = elements.Measure.DesiredSize.Width;
        if (double.IsFinite(desiredWidth) && desiredWidth > 0)
        {
            return Math.Ceiling(desiredWidth);
        }

        double actualWidth = elements.Primary.ActualWidth;
        return double.IsFinite(actualWidth) ? Math.Ceiling(actualWidth) : 0;
    }

    private (Grid Host, TextBlock Static, Canvas Canvas, TextBlock Primary, TextBlock Clone, TextBlock Measure)
        GetActiveTitleMarqueeElements()
    {
        return _isMinimalLayout
            ? (MinimalTitleMarqueeHost, MinimalTitleStaticText, MinimalTitleMarqueeCanvas,
                MinimalTitleTextPrimary, MinimalTitleTextClone, MinimalTitleMeasureText)
            : (TitleMarqueeHost, TitleStaticText, TitleMarqueeCanvas,
                TitleTextPrimary, TitleTextClone, TitleMeasureText);
    }

    private static void ResetTitleMarqueeElements(
        TextBlock staticText,
        Canvas canvas,
        TextBlock primary,
        TextBlock clone)
    {
        primary.ClearValue(WidthProperty);
        clone.ClearValue(WidthProperty);
        staticText.Opacity = 1;
        canvas.Visibility = Visibility.Collapsed;
        canvas.Translation = Vector3.Zero;
    }

    private void UpdateSeekFromPointer(PointerRoutedEventArgs e)
    {
        if (ViewModel is null || ProgressHost.ActualWidth <= 0)
        {
            return;
        }

        double x = e.GetCurrentPoint(ProgressHost).Position.X;
        double ratio = Math.Clamp(x / ProgressHost.ActualWidth, 0.0, 1.0);
        ViewModel.SeekValue = ratio * ViewModel.SeekMaximum;
        UpdateProgressVisuals();
    }

    private void UpdateProgressVisuals()
    {
        if (ViewModel is null)
        {
            ProgressFill.Width = 0;
            RecordProgressFill.Width = 0;
            ProgressThumb.Opacity = 0;
            return;
        }

        double maximum = Math.Max(1, ViewModel.SeekMaximum);
        double ratio = Math.Clamp(ViewModel.SeekValue / maximum, 0.0, 1.0);

        if (ProgressHost.ActualWidth > 0)
        {
            double width = Math.Max(0, ProgressHost.ActualWidth * ratio);
            ProgressFill.Width = width;
            ProgressThumb.Margin = new Thickness(width, 0, 0, 0);
            bool canInteract = ViewModel.CanInteractWithProgress;
            ProgressThumb.Opacity = canInteract && (_isProgressHovering || _isProgressDragging) ? 1 : 0;
            ProgressTrack.Opacity = ViewModel.HasSeekableTimeline ? 0.36 : 0.2;
        }
        else
        {
            ProgressFill.Width = 0;
            ProgressThumb.Opacity = 0;
        }

        if (RecordProgressHost.ActualWidth > 0)
        {
            RecordProgressFill.Width = Math.Max(0, RecordProgressHost.ActualWidth * ratio);
            RecordProgressTrack.Opacity = ViewModel.HasSeekableTimeline ? 0.36 : 0.2;
        }
        else
        {
            RecordProgressFill.Width = 0;
        }

        if (RecordHorizontalProgressHost.ActualWidth > 0)
        {
            RecordHorizontalProgressFill.Width = Math.Max(0, RecordHorizontalProgressHost.ActualWidth * ratio);
            RecordHorizontalProgressTrack.Opacity = ViewModel.HasSeekableTimeline ? 0.36 : 0.2;
        }
        else
        {
            RecordHorizontalProgressFill.Width = 0;
        }
    }
}
