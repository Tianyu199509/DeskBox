using System.Numerics;
using System.ComponentModel;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class MusicWidgetContent : UserControl
{
    private const float AlbumArtHoverScale = 1.018f;
    private const double AlbumArtHoverOffset = 3.0;
    private bool _isProgressDragging;
    private bool _isProgressHovering;

    public MusicWidgetContent()
    {
        InitializeComponent();
        Loaded += MusicWidgetContent_Loaded;
        Unloaded += MusicWidgetContent_Unloaded;
        SizeChanged += (_, _) =>
        {
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

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.NextAsync();
        }
    }

    private void MusicWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateProgressVisuals();
    }

    private void MusicWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
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
