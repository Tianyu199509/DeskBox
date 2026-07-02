using System.Numerics;
using System.ComponentModel;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class MusicWidgetContent : UserControl
{
    private const float AlbumArtHoverScale = 1.018f;
    private const double AlbumArtHoverOffset = 3.0;
    private const double MinimumResponsiveWidth = 200.0;
    private const double WideResponsiveWidth = 320.0;
    private const double WideAlbumArtSize = 82.0;
    private const double MinimumAlbumArtSize = 68.0;
    private const double WideIconButtonSize = 30.0;
    private const double CompactIconButtonSize = 26.0;
    private const double WidePrimaryButtonSize = 42.0;
    private const double CompactPrimaryButtonSize = 38.0;
    private bool _isProgressDragging;
    private bool _isProgressHovering;
    private bool _isInlineVolumeRefreshing;

    public MusicWidgetContent()
    {
        InitializeComponent();
        Loaded += MusicWidgetContent_Loaded;
        Unloaded += MusicWidgetContent_Unloaded;
        SizeChanged += (_, _) =>
        {
            ApplyResponsiveLayout();
            UpdateProgressVisuals();
        };
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

    private void MusicWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        UpdateProgressVisuals();
    }

    private void MusicWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        InlineVolumePanel.Visibility = Visibility.Collapsed;
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
        if (width <= 0)
        {
            return;
        }

        double compactRatio = width >= WideResponsiveWidth
            ? 1.0
            : Math.Clamp((width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth), 0.0, 1.0);

        double albumSize = Math.Round(Lerp(MinimumAlbumArtSize, WideAlbumArtSize, compactRatio));
        double iconButtonSize = Math.Round(Lerp(CompactIconButtonSize, WideIconButtonSize, compactRatio));
        double primaryButtonSize = Math.Round(Lerp(CompactPrimaryButtonSize, WidePrimaryButtonSize, compactRatio));
        double contentPadding = Math.Round(Lerp(10, 12, compactRatio));
        double columnSpacing = Math.Round(Lerp(8, 12, compactRatio));
        double rowSpacing = Math.Round(Lerp(6, 8, compactRatio));
        double controlsSpacing = Math.Round(Lerp(5, 10, compactRatio));
        double timelineColumnWidth = Math.Round(Lerp(28, 34, compactRatio));
        double progressTopMargin = Math.Round(Lerp(3, 5, compactRatio));
        double controlsTopMargin = Math.Round(Lerp(5, 8, compactRatio));

        ContentGrid.Padding = new Thickness(contentPadding, contentPadding, contentPadding, 11);
        ContentGrid.ColumnSpacing = columnSpacing;
        ContentGrid.RowSpacing = rowSpacing;
        AlbumColumn.Width = new GridLength(albumSize);
        TopRow.Height = new GridLength(albumSize);
        SetAlbumArtSize(albumSize);
        ProgressRow.Margin = new Thickness(0, progressTopMargin, 0, 0);
        ProgressRow.ColumnSpacing = Math.Round(Lerp(5, 8, compactRatio));
        PositionColumn.Width = new GridLength(timelineColumnWidth);
        DurationColumn.Width = new GridLength(timelineColumnWidth);
        ControlsPanel.Margin = new Thickness(0, controlsTopMargin, 0, 0);
        ControlsPanel.Spacing = controlsSpacing;
        SetButtonSize(PlaybackModeButton, iconButtonSize);
        SetButtonSize(PreviousButton, iconButtonSize);
        SetButtonSize(NextButton, iconButtonSize);
        SetButtonSize(VolumeButton, iconButtonSize);
        SetButtonSize(PlayPauseButton, primaryButtonSize);
        PlaybackModeButton.CornerRadius = new CornerRadius(iconButtonSize / 2);
        PreviousButton.CornerRadius = new CornerRadius(iconButtonSize / 2);
        NextButton.CornerRadius = new CornerRadius(iconButtonSize / 2);
        VolumeButton.CornerRadius = new CornerRadius(iconButtonSize / 2);
        PlayPauseButton.CornerRadius = new CornerRadius(primaryButtonSize / 2);
        PositionInlineVolumePanel();
        ViewModel?.UpdateVisualizerWidth(Math.Max(0, RhythmBackdrop.ActualWidth - 8));
    }

    private void SetAlbumArtSize(double size)
    {
        AlbumArtShadow.Width = size;
        AlbumArtShadow.Height = size;
        AlbumArtSurface.Width = size;
        AlbumArtSurface.Height = size;
        AlbumArtShadow.CornerRadius = new CornerRadius(Math.Max(10, size * 0.14));
        AlbumArtSurface.CornerRadius = new CornerRadius(Math.Max(10, size * 0.14));
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
        if (VolumeButton.ActualWidth <= 0 || VolumeButton.ActualHeight <= 0)
        {
            return;
        }

        var buttonOrigin = VolumeButton.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double panelWidth = InlineVolumePanel.Width > 0 ? InlineVolumePanel.Width : InlineVolumePanel.ActualWidth;
        double panelHeight = InlineVolumePanel.Height > 0 ? InlineVolumePanel.Height : InlineVolumePanel.ActualHeight;
        double left = buttonOrigin.X + (VolumeButton.ActualWidth / 2) - (panelWidth / 2);
        double top = buttonOrigin.Y - panelHeight - 6;

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
        if (ViewModel is null || ProgressHost.ActualWidth <= 0)
        {
            ProgressFill.Width = 0;
            ProgressThumb.Opacity = 0;
            return;
        }

        double maximum = Math.Max(1, ViewModel.SeekMaximum);
        double ratio = Math.Clamp(ViewModel.SeekValue / maximum, 0.0, 1.0);
        double width = Math.Max(0, ProgressHost.ActualWidth * ratio);
        ProgressFill.Width = width;
        ProgressThumb.Margin = new Thickness(width, 0, 0, 0);
        bool canInteract = ViewModel.CanInteractWithProgress;
        ProgressThumb.Opacity = canInteract && (_isProgressHovering || _isProgressDragging) ? 1 : 0;
        ProgressTrack.Opacity = ViewModel.HasSeekableTimeline ? 0.36 : 0.2;
    }
}
