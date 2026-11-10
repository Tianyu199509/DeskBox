using System.ComponentModel;
using System.Numerics;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Lyrics;
using DeskBox.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class MusicWidgetContent : UserControl, IDisposable
{
    private const float AlbumArtHoverScale = 1.018f;
    private const double AlbumArtHoverOffset = 3.0;
    private const double TitleMarqueeGap = 32.0;
    private const double TitleMarqueeStartDelayMs = 900.0;
    private const double TitleMarqueeSpeedPixelsPerSecond = 50.0;
    private const double TitleMarqueeOverflowTolerance = 4.0;
    private const int TitleMarqueeDeferredMeasureMs = 120;
    private const double LyricMarqueeGap = 28.0;
    private const double LyricMarqueeStartDelayMs = 450.0;
    private const double LyricMarqueeMinSpeedPixelsPerSecond = 46.0;
    private const double LyricMarqueeMaxSpeedPixelsPerSecond = 88.0;
    private const double LyricMarqueeOverflowTolerance = 3.0;
    private const int LyricMarqueeDeferredMeasureMs = 90;
    private const int LyricsSingleTapExitDelayMs = 230;
    private const long LyricsDoubleTapSuppressMs = 340;
    private const int ArtworkTransitionDurationMs =
        WidgetMotion.SpatialMilliseconds;
    private const double MinimumResponsiveWidth = 180.0;
    private const double WideResponsiveWidth = 320.0;
    private const double MinimumResponsiveHeight = 180.0;
    private const double WideResponsiveHeight = 280.0;
    private const double WideAlbumArtSize = 108.0;
    private const double MinimumAlbumArtSize = 60.0;
    private const double LyricsViewportEdgeFadeHeight = 40.0;
    private const double WideTransportButtonSize = 32.0;
    private const double CompactTransportButtonSize = 28.0;
    private const double PrimaryButtonSizeDelta = 4.0;
    private const double InlineVolumePanelMaximumWidth = 238.0;
    private const double InlineVolumePanelHorizontalInset = 6.0;
    private const double SourceFlyoutMaximumWidth = 280.0;
    private const double SourceFlyoutMinimumWidth = 136.0;
    private const double SourceFlyoutInset = 6.0;

    private bool _isProgressDragging;
    private Grid? _activeProgressDragHost;
    private Grid? _hoveredProgressHost;
    private bool _isInlineVolumeRefreshing;
    private bool _isInlineVolumeOpen;
    private bool _isInlineVolumeReadyForUserInput;
    private bool _hasInlineSystemVolumeSnapshot;
    private int _inlineVolumeOpenGeneration;
    private ScalarKeyFrameAnimation? _titleMarqueeAnimation;
    private Grid? _titleMarqueeAnimatedCanvas;
    private double _titleMarqueeDistance;
    private int _titleMarqueeMeasureVersion;
    private ScalarKeyFrameAnimation? _lyricMarqueeAnimation;
    private Grid? _lyricMarqueeAnimatedCanvas;
    private int _lyricMarqueeMeasureVersion;
    private int _artworkTransitionVersion;
    private bool? _lastTonearmPlayingState;
    private ScalarKeyFrameAnimation? _tonearmAnimation;
    private CubicBezierEasingFunction? _tonearmEasing;
    private bool _isDisposed;
    private bool _isLayoutHostReady;
    private bool _isHostWindowVisible;
    private bool _isHostCompactCollapsed;
    private bool _isMinimalLayout;
    private bool _isRecordLayout;
    private bool _isRecordHorizontalLayout;
    private bool _isResponsiveLayoutTransitionActive;
    private int _lyricsTapVersion;
    private long _lastLyricsDoubleTapTick;
    private ScrollViewer? _lyricsScrollViewer;
    private Button? _volumeAnchorButton;
    private MenuFlyout? _sourceFlyout;
    private int _sourceFlyoutGeneration;
    private readonly PointerEventHandler _rootPointerPressedHandler;
    private readonly PointerEventHandler _inlineVolumePointerPressedHandler;
    private uint _lastInlineVolumePressedPointerId;
    private long _lastInlineVolumePressedTick;

    public MusicWidgetContent()
        : this(viewModel: null)
    {
    }

    public MusicWidgetContent(MusicWidgetViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            DataContext = viewModel;
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SyncInitialLayoutFlagsFromDisplayMode(viewModel.DisplayMode);
        }

        InitializeComponent();
        _rootPointerPressedHandler = RootGrid_PointerPressed;
        _inlineVolumePointerPressedHandler = InlineVolumePanel_PointerPressedHandled;
        RootGrid.AddHandler(
            UIElement.PointerPressedEvent,
            _rootPointerPressedHandler,
            handledEventsToo: true);
        Loaded += MusicWidgetContent_Loaded;
        Unloaded += MusicWidgetContent_Unloaded;
        SizeChanged += MusicWidgetContent_SizeChanged;
    }

    public bool IsMinimalLayoutLoaded =>
        _isLayoutHostReady &&
        !IsLyricsOverlayActive &&
        !_isRecordLayout &&
        !_isRecordHorizontalLayout &&
        _isMinimalLayout;

    public bool IsStandardControlsLayoutLoaded =>
        _isLayoutHostReady &&
        !IsLyricsOverlayActive &&
        !_isRecordLayout &&
        !_isRecordHorizontalLayout &&
        !_isMinimalLayout;

    public bool IsRecordLayoutLoaded =>
        _isLayoutHostReady &&
        !IsLyricsOverlayActive &&
        _isRecordLayout;

    public bool IsRecordHorizontalLayoutLoaded =>
        _isLayoutHostReady &&
        !IsLyricsOverlayActive &&
        !_isRecordLayout &&
        _isRecordHorizontalLayout;

    public bool IsLyricsOverlayLoaded =>
        _isLayoutHostReady &&
        IsLyricsOverlayActive;

    public bool IsInlineVolumeLoaded =>
        _isLayoutHostReady &&
        _isInlineVolumeOpen &&
        !IsLyricsOverlayActive;

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
                SyncInitialLayoutFlagsFromDisplayMode(value.DisplayMode);
            }
            else
            {
                StopTitleMarquee();
                StopLyricMarquee();
                StopSpectrumVisuals(immediate: true);
            }

            if (_isDisposed)
            {
                return;
            }

            if (value is not null)
            {
                Bindings.Update();
            }

            if (!_isLayoutHostReady)
            {
                return;
            }

            if (value is null)
            {
                Bindings.Update();
            }
            UpdateSourcePickerLabels();
            ApplyAlbumArtCornerRadius();
            UpdateProgressVisuals();
            UpdateLyricsOverlayState();
            UpdateSpectrumVisuals();
        }
    }

    private void SyncInitialLayoutFlagsFromDisplayMode(string? displayMode)
    {
        double width = ActualWidth > 0 ? ActualWidth : (RootGrid?.ActualWidth ?? 0);
        double height = ActualHeight > 0 ? ActualHeight : (RootGrid?.ActualHeight ?? 0);
        if (width > 0 && height > 0)
        {
            return;
        }

        string normalizedMode = SettingsService.NormalizeMusicDisplayMode(displayMode);
        _isRecordLayout = normalizedMode == SettingsService.MusicDisplayModeRecordVertical;
        _isRecordHorizontalLayout = normalizedMode == SettingsService.MusicDisplayModeRecordHorizontal;
        _isMinimalLayout = !_isRecordLayout &&
            !_isRecordHorizontalLayout &&
            normalizedMode == SettingsService.MusicDisplayModeCover;
    }

    private void PlayerSubtree_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (RecordLayout is not null && ReferenceEquals(sender, RecordLayout))
        {
            _lastTonearmPlayingState = null;
        }

        UpdateSourcePickerLabels();
        ApplyAlbumArtCornerRadius();

        double width = ActualWidth > 0 ? ActualWidth : (RootGrid?.ActualWidth ?? 0);
        double height = ActualHeight > 0 ? ActualHeight : (RootGrid?.ActualHeight ?? 0);
        if (width > 0 && height > 0)
        {
            if (_isRecordLayout)
            {
                ApplyRecordLayoutSizing(width, height);
            }
            else if (_isRecordHorizontalLayout)
            {
                ApplyRecordHorizontalSizing(width, height);
            }
            else if (_isMinimalLayout)
            {
                ApplyMinimalLayoutSizing(width, height);
            }
            else
            {
                ApplyStandardControlsLayoutSizing(width, height);
            }
        }

        UpdateProgressVisuals();
        UpdateRecordVinylRotation();
        UpdateRecordHorizontalVinylRotation();
        UpdateSpectrumVisuals();
        if (_isRecordLayout)
        {
            AnimateTonearmForState();
        }

        QueueTitleMarqueeUpdate();
        QueueLyricMarqueeUpdate();
    }

    private void LyricsOverlayPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        DetachLyricsScrollViewer();
        ApplyAlbumArtCornerRadius();
        EnsureLyricsScrollViewerAttached();
        UpdateLyricsItemHighlighting();
        ScrollActiveLyricIntoView();
    }

    private void InlineVolumePanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        AttachInlineVolumePointerHandler();
        ApplyAlbumArtCornerRadius();
        SyncInlineVolumeSliderAccent();
        double width = ActualWidth > 0 ? ActualWidth : RootGrid.ActualWidth;
        if (width > 0)
        {
            UpdateInlineVolumePanelLayout(width);
        }
    }

    private void EnsureInlineVolumePanelLoaded()
    {
        _isInlineVolumeReadyForUserInput = false;
        _hasInlineSystemVolumeSnapshot = false;
        _isInlineVolumeOpen = true;
        _isInlineVolumeRefreshing = true;
        try
        {
            if (!_isDisposed)
            {
                Bindings.Update();
            }

            if (InlineVolumePanel is not null)
            {
                InlineVolumePanel.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isInlineVolumeRefreshing = false;
        }

        AttachInlineVolumePointerHandler();
        ApplyAlbumArtCornerRadius();
        SyncInlineVolumeSliderAccent();
        double width = ActualWidth > 0 ? ActualWidth : RootGrid.ActualWidth;
        if (width > 0)
        {
            UpdateInlineVolumePanelLayout(width);
        }
    }

    private void AttachInlineVolumePointerHandler()
    {
        if (InlineVolumePanel is null)
        {
            return;
        }

        InlineVolumePanel.RemoveHandler(UIElement.PointerPressedEvent, _inlineVolumePointerPressedHandler);
        InlineVolumePanel.AddHandler(
            UIElement.PointerPressedEvent,
            _inlineVolumePointerPressedHandler,
            handledEventsToo: true);
    }

    private void DetachInlineVolumePointerHandler()
    {
        if (InlineVolumePanel is not null)
        {
            InlineVolumePanel.RemoveHandler(UIElement.PointerPressedEvent, _inlineVolumePointerPressedHandler);
        }
    }

    private void EnsureLyricsOverlayLoaded()
    {
        if (!_isDisposed)
        {
            Bindings.Update();
        }
    }

    private void HideInlineVolumePanelIfLoaded()
    {
        DetachInlineVolumePointerHandler();
        bool wasOpen = _isInlineVolumeOpen || InlineVolumePanel is not null;
        _isInlineVolumeReadyForUserInput = false;
        _hasInlineSystemVolumeSnapshot = false;
        ++_inlineVolumeOpenGeneration;
        _isInlineVolumeOpen = false;
        _volumeAnchorButton = null;
        if (wasOpen && !_isDisposed)
        {
            Bindings.Update();
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

    private void ArtworkLyricsTrigger_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.ShowLyrics || !ViewModel.HasSession)
        {
            return;
        }

        HideInlineVolumePanelIfLoaded();
        CloseSourceFlyout();
        ViewModel.ToggleLyricsView();
        e.Handled = true;
    }

    private void InlineLyricRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        bool interactive = ViewModel?.ShowLyrics == true && ViewModel.HasSession;
        SetInlineLyricHoverOpacity(sender as Grid, interactive ? 1.0 : 0.0);
    }

    private void InlineLyricRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetInlineLyricHoverOpacity(sender as Grid, 0.0);
    }

    private static void SetInlineLyricHoverOpacity(Grid? row, double opacity)
    {
        if (row is null)
        {
            return;
        }

        foreach (UIElement child in row.Children)
        {
            if (child is Border { Tag: "HoverPill" } hoverPill)
            {
                hoverPill.Opacity = opacity;
                return;
            }
        }
    }

    private async void LyricsOverlayPanel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        e.Handled = true;
        if (Environment.TickCount64 - _lastLyricsDoubleTapTick < LyricsDoubleTapSuppressMs)
        {
            return;
        }

        LrcLine? tappedLine = FindTappedLyricLine(e.OriginalSource as DependencyObject);
        if (tappedLine is null || !ViewModel.CanInteractWithProgress)
        {
            ++_lyricsTapVersion;
            ViewModel.CloseLyricsView();
            return;
        }

        int tapVersion = ++_lyricsTapVersion;
        await Task.Delay(LyricsSingleTapExitDelayMs);
        if (_isDisposed ||
            tapVersion != _lyricsTapVersion ||
            Environment.TickCount64 - _lastLyricsDoubleTapTick < LyricsDoubleTapSuppressMs)
        {
            return;
        }

        ViewModel?.CloseLyricsView();
    }

    private async void LyricLineItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ++_lyricsTapVersion;
        _lastLyricsDoubleTapTick = Environment.TickCount64;
        e.Handled = true;

        if (ViewModel is not null && (sender as FrameworkElement)?.DataContext is LrcLine clickedLine)
        {
            await ViewModel.SeekToLyricLineAsync(clickedLine);
        }
    }

    private LrcLine? FindTappedLyricLine(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null &&
               !ReferenceEquals(current, LyricsListView) &&
               !ReferenceEquals(current, LyricsOverlayPanel))
        {
            if (current is FrameworkElement { DataContext: LrcLine line })
            {
                return line;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button anchor || ViewModel is not MusicWidgetViewModel viewModel)
        {
            return;
        }

        int generation = ++_sourceFlyoutGeneration;
        HideInlineVolumePanelIfLoaded();
        if (_sourceFlyout is not null)
        {
            CloseSourceFlyout();
            return;
        }

        try
        {
            IReadOnlyList<MusicSessionOption> options = await viewModel.GetAvailableSessionOptionsAsync();
            if (_isDisposed ||
                generation != _sourceFlyoutGeneration ||
                !ReferenceEquals(viewModel, ViewModel) ||
                !anchor.IsLoaded)
            {
                return;
            }

            MenuFlyout flyout = CreateSourceFlyout(viewModel, options);
            _sourceFlyout = flyout;
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_sourceFlyout, flyout))
                {
                    _sourceFlyout = null;
                }
            };

            flyout.ShowAt(
                anchor,
                new FlyoutShowOptions
                {
                    Placement = MinimalSourceButton is not null && ReferenceEquals(anchor, MinimalSourceButton)
                        ? FlyoutPlacementMode.Top
                        : FlyoutPlacementMode.Bottom,
                    ShowMode = FlyoutShowMode.Standard
                });
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Show source picker failed: {ex}");
            CloseSourceFlyout();
        }
    }

    private async void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CloseSourceFlyout();
            if (_isInlineVolumeOpen && InlineVolumePanel is not null)
            {
                HideInlineVolumePanelIfLoaded();
                return;
            }

            if (ViewModel is null)
            {
                return;
            }

            int openGeneration = ++_inlineVolumeOpenGeneration;
            _isInlineVolumeReadyForUserInput = false;
            _hasInlineSystemVolumeSnapshot = false;
            _volumeAnchorButton = sender as Button ?? VolumeButton;
            EnsureInlineVolumePanelLoaded();
            if (InlineVolumePanel is null)
            {
                return;
            }

            bool hasSystemVolume = await RefreshInlineVolumeAsync();
            if (_isDisposed ||
                !IsLoaded ||
                !_isInlineVolumeOpen ||
                openGeneration != _inlineVolumeOpenGeneration ||
                InlineVolumePanel is null ||
                ViewModel is null)
            {
                return;
            }

            if (InlineSystemVolumeSlider is not null)
            {
                InlineSystemVolumeSlider.IsEnabled = hasSystemVolume;
            }

            SyncInlineVolumeSlidersFromViewModel();
            InlineVolumePanel.Visibility = Visibility.Visible;
            InlineVolumePanel.UpdateLayout();
            PositionInlineVolumePanel();
            _hasInlineSystemVolumeSnapshot = hasSystemVolume;
            _isInlineVolumeReadyForUserInput = true;
            PlayInlineVolumeEntranceAnimation();
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Show inline volume failed: {ex}");
            HideInlineVolumePanelIfLoaded();
        }
    }

    private async void SessionMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !_isInlineVolumeReadyForUserInput || !ViewModel.HasSessionVolume)
        {
            return;
        }

        await ViewModel.ToggleSessionMuteAsync();
        SyncInlineVolumeSlidersFromViewModel();
    }

    private async void SystemMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !_isInlineVolumeReadyForUserInput || !_hasInlineSystemVolumeSnapshot)
        {
            return;
        }

        await ViewModel.ToggleSystemMuteAsync();
        SyncInlineVolumeSlidersFromViewModel();
    }

    private async void InlineVolumePanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || !_isInlineVolumeOpen || !_isInlineVolumeReadyForUserInput)
        {
            return;
        }

        int delta = e.GetCurrentPoint(InlineVolumePanel).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        e.Handled = true;
        double step = Math.Sign(delta) * 0.02;
        if (ViewModel.HasSessionVolume &&
            InlineSessionVolumeSlider is not null &&
            IsPointerPointInside(e, InlineSessionVolumeSlider))
        {
            double sessionTarget = Math.Clamp(ViewModel.SessionVolume + step, 0.0, 1.0);
            await ViewModel.SetSessionVolumeAsync(sessionTarget);
            SyncInlineVolumeSlidersFromViewModel();
            return;
        }

        if (!_hasInlineSystemVolumeSnapshot)
        {
            return;
        }

        double target = Math.Clamp(ViewModel.SystemVolume + step, 0.0, 1.0);
        await ViewModel.SetSystemVolumeAsync(target);
        SyncInlineVolumeSlidersFromViewModel();
    }

    private void SyncInlineVolumeSliderAccent()
    {
        if (InlineVolumePanel is null || ViewModel is null)
        {
            return;
        }

        Brush accent = ViewModel.MusicAccentBrush;
        ResourceDictionary resources = InlineVolumePanel.Resources;
        resources["SliderTrackValueFill"] = accent;
        resources["SliderTrackValueFillPointerOver"] = accent;
        resources["SliderTrackValueFillPressed"] = accent;
        resources["SliderThumbBackground"] = accent;
        resources["SliderThumbBackgroundPointerOver"] = accent;
        resources["SliderThumbBackgroundPressed"] = accent;
    }

    private void PlayInlineVolumeEntranceAnimation()
    {
        if (InlineVolumePanel is null || !AreSystemAnimationsEnabled())
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(InlineVolumePanel);
        visual.StopAnimation("Opacity");

        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));

        ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.Duration = TimeSpan.FromMilliseconds(120);
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f, easing);

        visual.Opacity = 1f;
        visual.StartAnimation("Opacity", opacityAnim);
    }

    private void InlineVolumePanel_PointerPressedHandled(object sender, PointerRoutedEventArgs e)
    {
        _lastInlineVolumePressedPointerId = e.Pointer.PointerId;
        _lastInlineVolumePressedTick = Environment.TickCount64;
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isInlineVolumeOpen || InlineVolumePanel is null || InlineVolumePanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_lastInlineVolumePressedPointerId == e.Pointer.PointerId &&
            Environment.TickCount64 - _lastInlineVolumePressedTick < 250)
        {
            return;
        }

        if (InlineSessionVolumeSlider?.PointerCaptures is { Count: > 0 } ||
            InlineSystemVolumeSlider?.PointerCaptures is { Count: > 0 })
        {
            return;
        }

        Button? anchor = _volumeAnchorButton ?? VolumeButton;
        DependencyObject? sourceElement = e.OriginalSource as DependencyObject;
        if (IsPointerPointInside(e, InlineVolumePanel) ||
            IsPointerPointInside(e, anchor) ||
            IsElementInside(sourceElement, InlineVolumePanel) ||
            IsElementInside(sourceElement, anchor))
        {
            return;
        }

        HideInlineVolumePanelIfLoaded();
    }

    private void InlineVolumePanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void MusicWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        _isLayoutHostReady = true;
        double width = ActualWidth > 0 ? ActualWidth : (RootGrid?.ActualWidth ?? 0);
        double height = ActualHeight > 0 ? ActualHeight : (RootGrid?.ActualHeight ?? 0);
        if (width <= 0 || height <= 0)
        {
            ApplyActivePlayerLayoutVisibility();
        }
        else
        {
            ApplyResponsiveLayout(width, height);
        }

        UpdateProgressVisuals();
        UpdateLyricsOverlayState();
        UpdateSpectrumVisuals();
        QueueTitleMarqueeUpdate();
        QueueLyricMarqueeUpdate();
    }

    private void MusicWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachLyricsScrollViewer();
        HideInlineVolumePanelIfLoaded();
        CloseSourceFlyout();
        ResetAlbumArtMotion();
        StopTitleMarquee();
        StopLyricMarquee();
        StopRecordVinylRotation();
        StopRecordHorizontalVinylRotation();
        StopSpectrumVisuals(immediate: true);
        StopTonearmAnimation();
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
        _isInlineVolumeOpen = false;
        _isInlineVolumeReadyForUserInput = false;
        _hasInlineSystemVolumeSnapshot = false;
        ++_inlineVolumeOpenGeneration;
        _volumeAnchorButton = null;
        ++_titleMarqueeMeasureVersion;
        ++_lyricMarqueeMeasureVersion;
        ++_lyricsTapVersion;
        ++_artworkTransitionVersion;
        Loaded -= MusicWidgetContent_Loaded;
        Unloaded -= MusicWidgetContent_Unloaded;
        SizeChanged -= MusicWidgetContent_SizeChanged;
        if (LyricsListView is not null)
        {
            LyricsListView.ContainerContentChanging -= LyricsListView_ContainerContentChanging;
        }

        DetachLyricsScrollViewer();
        DetachInlineVolumePointerHandler();
        RootGrid.RemoveHandler(UIElement.PointerPressedEvent, _rootPointerPressedHandler);
        CloseSourceFlyout();
        ResetAlbumArtMotion();
        StopTitleMarquee();
        StopLyricMarquee();

        StopRecordVinylRotation();
        StopRecordHorizontalVinylRotation();
        StopSpectrumVisuals(immediate: true);
        StopTonearmAnimation();
        ViewModel = null;
        if (LyricsListView is not null)
        {
            LyricsListView.ItemsSource = null;
        }

        Bindings?.StopTracking();
        if (AlbumArtworkImage is not null)
        {
            AlbumArtworkImage.Source = null;
        }

        if (MinimalArtworkImage?.Fill is ImageBrush minimalArtworkBrush)
        {
            minimalArtworkBrush.ImageSource = null;
        }

        if (LyricsOverlayBackdropImage is not null)
        {
            LyricsOverlayBackdropImage.Source = null;
        }

        RecordVinylDisc?.ReleaseVisualResources();
        RecordHorizontalVinylDisc?.ReleaseVisualResources();
        MinimalSpectrumBar?.ReleaseVisualResources();
        MinimalRadialHalo?.ReleaseVisualResources();
        ControlsSpectrumBar?.ReleaseVisualResources();
        ControlsArtworkHalo?.ReleaseVisualResources();
        RecordVinylHalo?.ReleaseVisualResources();
        RecordSpectrumBar?.ReleaseVisualResources();
        RecordHorizontalSpectrumBar?.ReleaseVisualResources();
        RecordHorizontalVinylHalo?.ReleaseVisualResources();
        Content = null;
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isHostWindowVisible = visible;
        if (visible && !_isHostCompactCollapsed)
        {
            UpdateProgressVisuals();
            QueueTitleMarqueeUpdate();
            QueueLyricMarqueeUpdate();
            UpdateRecordVinylRotation();
            UpdateRecordHorizontalVinylRotation();
            UpdateSpectrumVisuals();
            if (_isRecordLayout && RecordTonearm?.Visibility == Visibility.Visible)
            {
                AnimateTonearmForState();
            }

            ScrollActiveLyricIntoView();
        }
        else
        {
            HideInlineVolumePanelIfLoaded();
            CloseSourceFlyout();
            StopTitleMarquee();
            StopLyricMarquee();
            StopRecordVinylRotation();
            StopRecordHorizontalVinylRotation();
            StopSpectrumVisuals(immediate: true);
        }
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        if (_isHostCompactCollapsed == collapsed)
        {
            return;
        }

        _isHostCompactCollapsed = collapsed;
        if (collapsed || !_isHostWindowVisible)
        {
            HideInlineVolumePanelIfLoaded();
            CloseSourceFlyout();
            StopTitleMarquee();
            StopLyricMarquee();
            StopRecordVinylRotation();
            StopRecordHorizontalVinylRotation();
            StopSpectrumVisuals(immediate: true);
            return;
        }

        UpdateProgressVisuals();
        QueueTitleMarqueeUpdate();
        QueueLyricMarqueeUpdate();
        UpdateRecordVinylRotation();
        UpdateRecordHorizontalVinylRotation();
        UpdateSpectrumVisuals();
        if (_isRecordLayout && RecordTonearm?.Visibility == Visibility.Visible)
        {
            AnimateTonearmForState();
        }

        ScrollActiveLyricIntoView();
    }

    public void ApplyPerformanceSettings()
    {
        StopTitleMarquee();
        StopLyricMarquee();
        StopRecordVinylRotation();
        StopRecordHorizontalVinylRotation();
        StopSpectrumVisuals(immediate: true);
        if (!IsLoaded ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed)
        {
            return;
        }

        QueueTitleMarqueeUpdate();
        QueueLyricMarqueeUpdate();
        UpdateRecordVinylRotation();
        UpdateRecordHorizontalVinylRotation();
        UpdateSpectrumVisuals();
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

    private static Windows.Foundation.Rect CreateHorizontalViewportClipRect(double width, double height) =>
        new(0, -12, Math.Max(0, width), Math.Max(36, height) + 24);

    private void TitleMarqueeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RectangleGeometry? clip = null;
        if (MinimalTitleMarqueeHost is not null && ReferenceEquals(sender, MinimalTitleMarqueeHost))
        {
            clip = MinimalTitleMarqueeClip;
        }
        else if (TitleMarqueeHost is not null && ReferenceEquals(sender, TitleMarqueeHost))
        {
            clip = TitleMarqueeClip;
        }

        if (clip is not null && e.NewSize.Width > 0)
        {
            clip.Rect = CreateHorizontalViewportClipRect(e.NewSize.Width, e.NewSize.Height);
        }

        QueueTitleMarqueeUpdate();
    }

    private void LyricMarqueeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RectangleGeometry? clip = null;
        if (MinimalLyricMarqueeHost is not null && ReferenceEquals(sender, MinimalLyricMarqueeHost))
        {
            clip = MinimalLyricMarqueeClip;
        }
        else if (ControlsLyricMarqueeHost is not null && ReferenceEquals(sender, ControlsLyricMarqueeHost))
        {
            clip = ControlsLyricMarqueeClip;
        }
        else if (RecordLyricMarqueeHost is not null && ReferenceEquals(sender, RecordLyricMarqueeHost))
        {
            clip = RecordLyricMarqueeClip;
        }
        else if (RecordHorizontalLyricMarqueeHost is not null && ReferenceEquals(sender, RecordHorizontalLyricMarqueeHost))
        {
            clip = RecordHorizontalLyricMarqueeClip;
        }

        if (clip is not null && e.NewSize.Width > 0)
        {
            clip.Rect = CreateHorizontalViewportClipRect(e.NewSize.Width, e.NewSize.Height);
        }

        QueueLyricMarqueeUpdate();
    }

    private void ProgressHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hoveredProgressHost = sender as Grid;
        UpdateProgressVisuals();
    }

    private void ProgressHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(_hoveredProgressHost, sender))
        {
            _hoveredProgressHost = null;
        }

        if (!_isProgressDragging)
        {
            UpdateProgressVisuals();
        }
    }

    private void ProgressHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.CanInteractWithProgress != true || sender is not Grid host)
        {
            return;
        }

        _isProgressDragging = true;
        _activeProgressDragHost = host;
        host.CapturePointer(e.Pointer);
        ViewModel.BeginSeek();
        UpdateSeekFromPointer(host, e);
        e.Handled = true;
    }

    private void ProgressHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isProgressDragging || _activeProgressDragHost is not { } host)
        {
            return;
        }

        UpdateSeekFromPointer(host, e);
        e.Handled = true;
    }

    private async void ProgressHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Grid? host = _activeProgressDragHost ?? sender as Grid;
        if (ViewModel is not null && _isProgressDragging && host is not null)
        {
            UpdateSeekFromPointer(host, e);
            await ViewModel.CommitSeekAsync();
        }

        _isProgressDragging = false;
        _activeProgressDragHost = null;
        host?.ReleasePointerCapture(e.Pointer);
        UpdateProgressVisuals();
        e.Handled = true;
    }

    private void AlbumArtSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAlbumArtCenterPoint();
    }

    private void AlbumArtSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (AlbumArtSurface is null)
        {
            return;
        }

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
        if (AlbumArtSurface is null)
        {
            return;
        }

        if (ViewModel?.EnableCoverHoverMotion != true ||
            AlbumArtSurface.ActualWidth <= 0 ||
            AlbumArtSurface.ActualHeight <= 0)
        {
            ResetAlbumArtMotion();
            return;
        }

        Windows.Foundation.Point position = e.GetCurrentPoint(AlbumArtSurface).Position;
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
        if (AlbumArtSurface is null)
        {
            return;
        }

        AlbumArtSurface.Translation = Vector3.Zero;
        AlbumArtSurface.Scale = Vector3.One;
    }

    private void ApplyResponsiveLayout()
    {
        double width = ActualWidth > 0 ? ActualWidth : (RootGrid?.ActualWidth ?? 0);
        double height = ActualHeight > 0 ? ActualHeight : (RootGrid?.ActualHeight ?? 0);
        ApplyResponsiveLayout(width, height);
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        if (width <= 0 || height <= 0 || _isDisposed)
        {
            return;
        }

        UpdateInlineVolumePanelLayout(width);

        string normalizedMode = SettingsService.NormalizeMusicDisplayMode(ViewModel?.DisplayMode);
        bool isRecordVertical = normalizedMode == SettingsService.MusicDisplayModeRecordVertical;
        bool isRecordHorizontal = normalizedMode == SettingsService.MusicDisplayModeRecordHorizontal;
        bool isRecord = isRecordVertical || isRecordHorizontal;
        bool useMinimalLayout = !isRecord && ShouldUseMinimalLayout(width, height, ViewModel?.DisplayMode);

        bool layoutModeChanged = (_isMinimalLayout != useMinimalLayout) ||
            (_isRecordLayout != isRecordVertical) ||
            (_isRecordHorizontalLayout != isRecordHorizontal);
        _isMinimalLayout = useMinimalLayout;
        _isRecordLayout = isRecordVertical;
        _isRecordHorizontalLayout = isRecordHorizontal;

        if (!_isLayoutHostReady)
        {
            return;
        }

        if (layoutModeChanged)
        {
            _hoveredProgressHost = null;
            _activeProgressDragHost = null;
            _isProgressDragging = false;
            HideInlineVolumePanelIfLoaded();
            CloseSourceFlyout();
            ResetAlbumArtMotion();
            StopTitleMarquee();
            StopLyricMarquee();
            StopSpectrumVisuals(immediate: true);
            if (!isRecordVertical)
            {
                StopRecordVinylRotation();
                StopTonearmAnimation();
            }

            if (!isRecordHorizontal)
            {
                StopRecordHorizontalVinylRotation();
            }
        }

        if (IsLyricsOverlayActive)
        {
            ApplyActivePlayerLayoutVisibility();
            ApplyAlbumArtCornerRadius();
            ScrollActiveLyricIntoView();
            return;
        }

        ApplyActivePlayerLayoutVisibility();

        if (isRecordVertical)
        {
            ApplyRecordLayoutSizing(width, height);
            UpdateRecordVinylRotation();
            UpdateSpectrumVisuals();
            AnimateTonearmForState();
            QueueTitleMarqueeUpdate();
            QueueLyricMarqueeUpdate();
            DispatcherQueue.TryEnqueue(UpdateProgressVisuals);
            return;
        }

        if (isRecordHorizontal)
        {
            ApplyRecordHorizontalSizing(width, height);
            UpdateRecordHorizontalVinylRotation();
            UpdateSpectrumVisuals();
            QueueTitleMarqueeUpdate();
            QueueLyricMarqueeUpdate();
            DispatcherQueue.TryEnqueue(UpdateProgressVisuals);
            return;
        }

        if (useMinimalLayout)
        {
            ApplyMinimalLayoutSizing(width, height);
            ApplyAlbumArtCornerRadius();
            UpdateSpectrumVisuals();
            QueueTitleMarqueeUpdate();
            QueueLyricMarqueeUpdate();
            DispatcherQueue.TryEnqueue(UpdateProgressVisuals);
            return;
        }

        ApplyStandardControlsLayoutSizing(width, height);
        UpdateSpectrumVisuals();
        PositionInlineVolumePanel();
        QueueTitleMarqueeUpdate();
        QueueLyricMarqueeUpdate();
        DispatcherQueue.TryEnqueue(UpdateProgressVisuals);
    }

    internal static bool TryResolveSpectrumMode(
        string? spectrumStyle,
        string autoFallbackStyle,
        out MusicSpectrumMode mode)
    {
        string normalized = SettingsService.NormalizeMusicSpectrumStyle(spectrumStyle);
        if (normalized == SettingsService.MusicSpectrumStyleOff)
        {
            mode = MusicSpectrumMode.SymmetricWave;
            return false;
        }

        string effective = normalized == SettingsService.MusicSpectrumStyleAuto
            ? autoFallbackStyle
            : normalized;

        mode = effective switch
        {
            SettingsService.MusicSpectrumStyleWaveformScrub => MusicSpectrumMode.WaveformScrub,
            SettingsService.MusicSpectrumStylePeakHoldBars => MusicSpectrumMode.PeakHoldBars,
            SettingsService.MusicSpectrumStyleAuroraSilk => MusicSpectrumMode.AuroraSilk,
            SettingsService.MusicSpectrumStyleMatrixLed => MusicSpectrumMode.MatrixLed,
            SettingsService.MusicSpectrumStyleVinylRadialHalo => MusicSpectrumMode.VinylRadialHalo,
            _ => MusicSpectrumMode.SymmetricWave
        };
        return true;
    }

    private static void ConfigureLinearStripGeometry(
        MusicSpectrumControl control,
        MusicSpectrumMode mode,
        double usableWidth,
        double maxHeight)
    {
        switch (mode)
        {
            case MusicSpectrumMode.AuroraSilk:
                control.ConfigureGeometry(mode, 15, 3.0, 3.0, maxHeight);
                break;

            case MusicSpectrumMode.WaveformScrub:
            {
                int bars = Math.Clamp((int)Math.Floor((usableWidth + 2.2) / 4.8), 14, 48);
                control.ConfigureGeometry(mode, bars, barWidth: 2.6, barSpacing: 2.2, maxBarHeight: maxHeight);
                break;
            }

            case MusicSpectrumMode.MatrixLed:
            {
                int cols = Math.Clamp((int)Math.Floor((usableWidth + 2.4) / 7.0), 10, 34);
                control.ConfigureGeometry(mode, cols, barWidth: 4.6, barSpacing: 2.4, maxBarHeight: maxHeight);
                break;
            }

            case MusicSpectrumMode.PeakHoldBars:
            {
                int bars = Math.Clamp((int)Math.Floor((usableWidth + 2.6) / 5.8), 12, 40);
                control.ConfigureGeometry(mode, bars, barWidth: 3.2, barSpacing: 2.6, maxBarHeight: maxHeight);
                break;
            }

            default:
            {
                int evenBars = Math.Clamp(((int)Math.Floor((usableWidth + 2.8) / 5.8)) & ~1, 12, 42);
                control.ConfigureGeometry(mode, evenBars, barWidth: 3.0, barSpacing: 2.8, maxBarHeight: maxHeight);
                break;
            }
        }
    }

    private void ApplyMinimalLayoutSizing(double width, double height)
    {
        bool hasSession = ViewModel?.HasSession == true;
        MusicSpectrumMode mode = MusicSpectrumMode.SymmetricWave;
        bool hasSpectrum = hasSession &&
            width >= 116 &&
            AreSystemAnimationsEnabled() &&
            TryResolveSpectrumMode(
                ViewModel?.SpectrumStyle,
                SettingsService.MusicSpectrumStyleSymmetricWave,
                out mode);

        if (hasSpectrum && mode == MusicSpectrumMode.VinylRadialHalo)
        {
            if (MinimalSpectrumBar is not null)
            {
                MinimalSpectrumBar.Visibility = Visibility.Collapsed;
            }

            if (MinimalSpectrumScrim is not null)
            {
                MinimalSpectrumScrim.Visibility = Visibility.Collapsed;
            }

            if (MinimalRadialHalo is not null)
            {
                double haloRadius = Math.Clamp(Math.Min(width, Math.Max(90, height - 52)) * 0.24, 22, 62);
                MinimalRadialHalo.ConfigureRadialGeometry(
                    barCount: haloRadius < 34 ? 28 : 36,
                    barWidth: 2.2,
                    maxBarHeight: 11.0,
                    innerRadius: haloRadius);
                MinimalRadialHalo.Opacity = 0.94;
                MinimalRadialHalo.Visibility = Visibility.Visible;
            }

            if (MinimalLyricRow is not null)
            {
                MinimalLyricRow.Margin = new Thickness(10, 0, 10, 53);
            }

            if (MinimalCoverLyricScrim is not null)
            {
                MinimalCoverLyricScrim.Height = 122;
            }

            return;
        }

        if (MinimalRadialHalo is not null)
        {
            MinimalRadialHalo.Visibility = Visibility.Collapsed;
        }

        if (hasSpectrum && MinimalSpectrumBar is not null)
        {
            double usableWidth = Math.Max(80, width - 24);
            double stripHeight = mode == MusicSpectrumMode.AuroraSilk ? 20.0 : 17.0;
            ConfigureLinearStripGeometry(MinimalSpectrumBar, mode, usableWidth, stripHeight);
            MinimalSpectrumBar.Opacity = 0.96;
            MinimalSpectrumBar.Visibility = Visibility.Visible;

            if (MinimalSpectrumScrim is not null)
            {
                MinimalSpectrumScrim.Visibility = Visibility.Visible;
            }

            if (MinimalLyricRow is not null)
            {
                MinimalLyricRow.Margin = new Thickness(10, 0, 10, 71);
            }

            if (MinimalCoverLyricScrim is not null)
            {
                MinimalCoverLyricScrim.Height = 144;
            }

            return;
        }

        if (MinimalSpectrumBar is not null)
        {
            MinimalSpectrumBar.Visibility = Visibility.Collapsed;
        }

        if (MinimalSpectrumScrim is not null)
        {
            MinimalSpectrumScrim.Visibility = Visibility.Collapsed;
        }

        if (MinimalLyricRow is not null)
        {
            MinimalLyricRow.Margin = new Thickness(10, 0, 10, 53);
        }

        if (MinimalCoverLyricScrim is not null)
        {
            MinimalCoverLyricScrim.Height = 122;
        }
    }

    private void ApplyStandardControlsLayoutSizing(double width, double height)
    {
        if (ContentGrid is null)
        {
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

        double transportButtonSize = ResolveTransportButtonSize(width);
        double primaryButtonSize = ResolvePrimaryTransportButtonSize(width);
        double contentPadding = Math.Round(Lerp(8, 12, densityRatio));
        double columnSpacing = Math.Round(Lerp(10, 14, widthRatio));
        double rowSpacing = Math.Round(Lerp(3, 7, heightRatio));
        double controlsSpacing = Math.Round(Lerp(6, 16, widthRatio));
        double timelineColumnWidth = Math.Round(Lerp(28, 34, widthRatio));
        double progressTopMargin = Math.Round(Lerp(0, 2, heightRatio));
        double controlsTopMargin = Math.Round(Lerp(1, 4, heightRatio));

        bool showTimeline = ViewModel?.TimelineRowVisibility == Visibility.Visible;
        double timelineHeight = showTimeline ? 24 + progressTopMargin : 0;
        double reservedVertical = (contentPadding * 2) +
            (rowSpacing * (showTimeline ? 2 : 1)) +
            timelineHeight +
            primaryButtonSize +
            controlsTopMargin;
        double maxAlbumByWidth = Math.Min(
            WideAlbumArtSize,
            Math.Max(MinimumAlbumArtSize, Math.Round(width * 0.37)));
        double maxAlbumByHeight = Math.Max(MinimumAlbumArtSize, Math.Floor(height - reservedVertical));
        double albumSize = Math.Clamp(
            Math.Min(maxAlbumByWidth, maxAlbumByHeight),
            MinimumAlbumArtSize,
            WideAlbumArtSize);

        ContentGrid.Padding = new Thickness(contentPadding);
        ContentGrid.ColumnSpacing = columnSpacing;
        ContentGrid.RowSpacing = rowSpacing;
        double rightColumnWidth = Math.Max(0, width - (contentPadding * 2) - albumSize - columnSpacing);
        MusicSpectrumMode controlsSpectrumMode = MusicSpectrumMode.AuroraSilk;
        bool hasControlsSpectrum = ViewModel?.HasSession == true &&
            AreSystemAnimationsEnabled() &&
            TryResolveSpectrumMode(
                ViewModel?.SpectrumStyle,
                SettingsService.MusicSpectrumStyleAuroraSilk,
                out controlsSpectrumMode);

        if (hasControlsSpectrum && controlsSpectrumMode == MusicSpectrumMode.VinylRadialHalo)
        {
            if (ControlsSpectrumBar is not null)
            {
                ControlsSpectrumBar.Visibility = Visibility.Collapsed;
            }

            if (ControlsArtworkHalo is not null)
            {
                double haloRadius = Math.Max(22.0, (albumSize * 0.5) - 2.0);
                ControlsArtworkHalo.ConfigureRadialGeometry(
                    barCount: albumSize < 78 ? 28 : 36,
                    barWidth: 2.1,
                    maxBarHeight: 9.5,
                    innerRadius: haloRadius);
                ControlsArtworkHalo.Opacity = 0.88;
                ControlsArtworkHalo.Visibility = Visibility.Visible;
            }
        }
        else if (hasControlsSpectrum)
        {
            if (ControlsArtworkHalo is not null)
            {
                ControlsArtworkHalo.Visibility = Visibility.Collapsed;
            }

            if (ControlsSpectrumBar is not null)
            {
                double usableWidth = Math.Max(110.0, width - (contentPadding * 2));
                double barHeight = controlsSpectrumMode == MusicSpectrumMode.AuroraSilk ? 24.0 : 18.0;
                ConfigureLinearStripGeometry(ControlsSpectrumBar, controlsSpectrumMode, usableWidth, barHeight);
                ControlsSpectrumBar.Margin = new Thickness(0, 0, 0, -(contentPadding - 1));
                ControlsSpectrumBar.Opacity = controlsSpectrumMode == MusicSpectrumMode.AuroraSilk ? 0.86 : 0.54;
                ControlsSpectrumBar.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (ControlsSpectrumBar is not null)
            {
                ControlsSpectrumBar.Visibility = Visibility.Collapsed;
            }

            if (ControlsArtworkHalo is not null)
            {
                ControlsArtworkHalo.Visibility = Visibility.Collapsed;
            }
        }

        if (SourceButton is not null)
        {
            SourceButton.Height = 20;
            SourceButton.MinHeight = 20;
            const double sourceTextMinWidth = 166;
            bool showSourceText = rightColumnWidth >= sourceTextMinWidth &&
                ViewModel?.SourceBadgeTextVisibility == Visibility.Visible;
            SourceButton.Padding = showSourceText
                ? new Thickness(5, 0, 7, 0)
                : new Thickness(4, 0, 4, 0);
            if (ControlsSourceButtonText is not null)
            {
                ControlsSourceButtonText.Visibility = showSourceText
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ControlsSourceButtonText.MaxWidth = Math.Clamp(Math.Round(rightColumnWidth * 0.42), 48, 96);
            }
        }

        if (AlbumColumn is not null)
        {
            AlbumColumn.Width = new GridLength(albumSize);
        }

        if (TopRow is not null)
        {
            TopRow.Height = new GridLength(1, GridUnitType.Star);
            TopRow.MinHeight = albumSize;
        }

        SetAlbumArtSize(albumSize);
        if (ProgressRow is not null)
        {
            ProgressRow.Margin = new Thickness(0, progressTopMargin, 0, 0);
            ProgressRow.ColumnSpacing = Math.Round(Lerp(5, 8, widthRatio));
        }

        if (PositionColumn is not null)
        {
            PositionColumn.MinWidth = timelineColumnWidth;
        }

        if (DurationColumn is not null)
        {
            DurationColumn.MinWidth = timelineColumnWidth;
        }

        if (TrackInfoGrid is not null)
        {
            TrackInfoGrid.RowSpacing = Math.Round(Lerp(2, 5, heightRatio));
        }

        if (ControlsPanel is not null)
        {
            ControlsPanel.Margin = new Thickness(0, controlsTopMargin, 0, 0);
            ControlsPanel.Spacing = controlsSpacing;
        }

        if (PlaybackModeButton is not null)
        {
            SetButtonSize(PlaybackModeButton, transportButtonSize);
        }

        if (PreviousButton is not null)
        {
            SetButtonSize(PreviousButton, transportButtonSize);
        }

        if (PlayPauseButton is not null)
        {
            SetPrimaryButtonSize(PlayPauseButton, primaryButtonSize);
        }

        if (NextButton is not null)
        {
            SetButtonSize(NextButton, transportButtonSize);
        }

        if (VolumeButton is not null)
        {
            SetButtonSize(VolumeButton, transportButtonSize);
        }
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

    internal static double ResolveTransportButtonSize(double width)
    {
        double widthRatio = Math.Clamp(
            (width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth),
            0.0,
            1.0);
        return Math.Round(Lerp(
            CompactTransportButtonSize,
            WideTransportButtonSize,
            widthRatio));
    }

    internal static double ResolvePrimaryTransportButtonSize(double width) =>
        ResolveTransportButtonSize(width) + PrimaryButtonSizeDelta;

    internal static double ResolveInlineVolumePanelWidth(double width)
    {
        double availableWidth = Math.Max(
            0,
            width - InlineVolumePanelHorizontalInset * 2);
        return Math.Min(InlineVolumePanelMaximumWidth, availableWidth);
    }

    internal static double ResolveSourceFlyoutMaxWidth(double width)
    {
        return Math.Min(
            SourceFlyoutMaximumWidth,
            Math.Max(0, width - SourceFlyoutInset * 2));
    }

    internal static double ResolveSourceFlyoutMaxHeight(double height)
    {
        return Math.Max(0, height - SourceFlyoutInset * 2);
    }

    internal static bool ShouldShowHorizontalVolumeControl(double width) => width >= 220;

    internal static bool ShouldShowHorizontalPlaybackModeControl(double width) => width >= 292;

    private void ApplyRecordLayoutSizing(double width, double height)
    {
        if (RecordLayout is null)
        {
            return;
        }

        if (RecordPlaybackModeButton is null)
        {
            App.Log("[MusicWidget] Failed to restore RecordLayout child elements; skipping sizing.");
            return;
        }

        double widthRatio = Math.Clamp(
            (width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth),
            0.0,
            1.0);
        bool compactHeight = height < 235;
        double transportButtonSize = compactHeight ? 26 : ResolveTransportButtonSize(width);
        double primaryButtonSize = compactHeight ? 30 : ResolvePrimaryTransportButtonSize(width);

        SetButtonSize(RecordPlaybackModeButton, transportButtonSize);
        SetButtonSize(RecordPreviousButton, transportButtonSize);
        SetPrimaryButtonSize(RecordPlayPauseButton, primaryButtonSize);
        SetButtonSize(RecordNextButton, transportButtonSize);
        SetButtonSize(RecordVolumeButton, transportButtonSize);
        RecordControlsPanel.Spacing = Math.Round(Lerp(6, 14, widthRatio));
        RecordControlsPanel.Margin = new Thickness(0, compactHeight ? 0 : 3, 0, 0);

        RecordPlaybackModeButton.Visibility = width < 190 ? Visibility.Collapsed : Visibility.Visible;

        bool showLyricsRow = ViewModel?.InlineLyricVisibility == Visibility.Visible;
        bool showTimelineRow = ViewModel?.TimelineRowVisibility == Visibility.Visible;
        double padding = compactHeight ? 6 : (height < 280 ? 8 : 12);
        double rowSpacing = compactHeight ? 2 : (height < 280 ? 4 : 5);
        RecordLayout.Padding = new Thickness(padding);
        RecordLayout.RowSpacing = rowSpacing;
        RecordTitlePanel.Margin = new Thickness(0, compactHeight ? 0 : 3, 0, 0);
        RecordProgressRow.Margin = new Thickness(0, compactHeight ? 0 : 2, 0, 0);
        RecordProgressHost.Height = compactHeight ? 18 : 24;

        MusicSpectrumMode recordSpectrumMode = MusicSpectrumMode.VinylRadialHalo;
        bool hasRecordSpectrum = !compactHeight &&
            ViewModel?.HasSession == true &&
            width >= 132 &&
            AreSystemAnimationsEnabled() &&
            TryResolveSpectrumMode(
                ViewModel?.SpectrumStyle,
                SettingsService.MusicSpectrumStyleVinylRadialHalo,
                out recordSpectrumMode);
        bool useRecordHalo = hasRecordSpectrum && recordSpectrumMode == MusicSpectrumMode.VinylRadialHalo;
        bool showSpectrumRow = hasRecordSpectrum && !useRecordHalo;
        double spectrumHeight = recordSpectrumMode == MusicSpectrumMode.AuroraSilk
            ? (height < 285 ? 16 : 20)
            : (height < 285 ? 14 : 18);

        if (RecordSpectrumBar is not null)
        {
            if (showSpectrumRow)
            {
                double usableSpectrumWidth = Math.Max(92, Math.Min(width - (padding * 2) - 8, 280));
                ConfigureLinearStripGeometry(RecordSpectrumBar, recordSpectrumMode, usableSpectrumWidth, spectrumHeight);
                RecordSpectrumBar.Opacity = 0.96;
                RecordSpectrumBar.Visibility = Visibility.Visible;
            }
            else
            {
                RecordSpectrumBar.Visibility = Visibility.Collapsed;
            }
        }

        int activeRowsAroundVinyl = 2 +
            (showSpectrumRow ? 1 : 0) +
            (showLyricsRow ? 1 : 0) +
            (showTimelineRow ? 1 : 0);
        double reserved = (padding * 2) +
            (activeRowsAroundVinyl * rowSpacing) +
            (compactHeight ? 40 : 46) +
            (showSpectrumRow ? (spectrumHeight + 2) : 0) +
            (showLyricsRow ? 24 : 0) +
            (showTimelineRow ? (compactHeight ? 18 : 26) : 0) +
            primaryButtonSize + (compactHeight ? 0 : 3);
        double haloClearance = useRecordHalo ? (height < 280 ? 22 : 28) : 0;
        double vinylSize = Math.Clamp(
            Math.Min(width - (padding * 2) - haloClearance, height - reserved - haloClearance),
            52,
            230);
        RecordVinylHost.MaxWidth = vinylSize;
        RecordVinylHost.MaxHeight = vinylSize;
        RecordVinylHost.Width = vinylSize;
        RecordVinylHost.Height = vinylSize;

        if (RecordVinylHalo is not null)
        {
            if (useRecordHalo)
            {
                double haloBarHeight = vinylSize < 108 ? 9.5 : (vinylSize < 150 ? 12.0 : 14.5);
                int haloBars = vinylSize < 108 ? 32 : (vinylSize < 165 ? 40 : 44);
                RecordVinylHalo.ConfigureRadialGeometry(
                    barCount: haloBars,
                    barWidth: 2.3,
                    maxBarHeight: haloBarHeight,
                    innerRadius: (vinylSize * 0.5) + 3.5);
                RecordVinylHalo.Opacity = 0.94;
                RecordVinylHalo.Visibility = Visibility.Visible;
            }
            else
            {
                RecordVinylHalo.Visibility = Visibility.Collapsed;
            }
        }

        if (RecordSourceButton is not null)
        {
            double badgeHeight = compactHeight ? 20 : 22;
            RecordSourceButton.Height = badgeHeight;
            RecordSourceButton.MinHeight = badgeHeight;

            double radius = vinylSize * 0.5;
            double dy = Math.Max(0, radius - badgeHeight);
            double halfChord = Math.Sqrt(Math.Max(0, (radius * radius) - (dy * dy)));
            double clearLeftWidth = ((width - (padding * 2)) * 0.5) - halfChord - 6;
            bool showSourceText = clearLeftWidth >= 62 &&
                ViewModel?.SourceBadgeTextVisibility == Visibility.Visible;
            RecordSourceButton.Padding = showSourceText
                ? new Thickness(5, 0, 7, 0)
                : new Thickness(4, 0, 4, 0);
            if (RecordSourceButtonText is not null)
            {
                RecordSourceButtonText.Visibility = showSourceText
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RecordSourceButtonText.MaxWidth = Math.Max(36, Math.Floor(clearLeftWidth - 24));
            }
        }

        bool small = vinylSize < 110;
        double baseTitleSize = ViewModel?.TitleTextSize ?? 16;
        double baseSecondarySize = ViewModel?.SecondaryTextSize ?? 12;
        double baseLyricSize = ViewModel?.LyricTextSize ?? 14;

        RecordTitleText.FontSize = compactHeight
            ? Math.Max(12, baseTitleSize - 3.5)
            : (small ? Math.Max(12.5, baseTitleSize - 2.5) : Math.Max(14, baseTitleSize - 1));
        RecordArtistText.FontSize = compactHeight
            ? Math.Max(10.5, baseSecondarySize - 1.5)
            : (small ? Math.Max(11, baseSecondarySize - 1) : baseSecondarySize);
        double lyricFontSize = compactHeight
            ? Math.Max(12, baseLyricSize - 1.5)
            : (small ? Math.Max(12.5, baseLyricSize - 0.5) : baseLyricSize);
        RecordLyricText.FontSize = lyricFontSize;
        RecordLyricTextPrimary.FontSize = lyricFontSize;
        RecordLyricTextClone.FontSize = lyricFontSize;
        RecordLyricMeasureText.FontSize = lyricFontSize;

        RecordArtistText.MaxWidth = Math.Max(
            0,
            width - RecordLayout.Padding.Left - RecordLayout.Padding.Right - 8);
        UpdateTonearmLayout(vinylSize, small);
    }

    private void UpdateTonearmLayout(double vinylSize, bool small)
    {
        if (RecordTonearm is null)
        {
            return;
        }

        if (small || !_isRecordLayout || IsLyricsOverlayActive)
        {
            RecordTonearm.Visibility = Visibility.Collapsed;
            return;
        }

        RecordTonearm.Visibility = Visibility.Visible;

        double armWidth = Math.Round(vinylSize * 0.42);
        double armHeight = Math.Round(armWidth * 1.2);
        RecordTonearm.Width = armWidth;
        RecordTonearm.Height = armHeight;
        RecordTonearm.Margin = new Thickness(
            Math.Round(vinylSize * 0.72), Math.Round(-vinylSize * 0.30), 0, 0);

        Visual? visual = ElementCompositionPreview.GetElementVisual(RecordTonearm);
        if (visual is not null)
        {
            visual.CenterPoint = new Vector3((float)(armWidth * 0.5), (float)(armHeight / 6.0), 0f);
        }
    }

    private void UpdateRecordVinylRotation()
    {
        if (RecordVinylDisc is null)
        {
            return;
        }

        bool shouldRotate = IsLoaded &&
            _isHostWindowVisible &&
            !_isHostCompactCollapsed &&
            RecordLayout?.Visibility == Visibility.Visible &&
            ViewModel?.IsPlaying == true &&
            VinylRotationAnimationsEnabled() &&
            AreSystemAnimationsEnabled();
        RecordVinylDisc.SetRotating(shouldRotate);
    }

    private void StopRecordVinylRotation()
    {
        RecordVinylDisc?.SetRotating(false);
    }

    private void ApplyRecordHorizontalSizing(double width, double height)
    {
        if (RecordHorizontalLayout is null)
        {
            return;
        }

        if (RecordHorizontalPlaybackModeButton is null)
        {
            App.Log("[MusicWidget] Failed to restore RecordHorizontalLayout child elements; skipping sizing.");
            return;
        }

        double widthRatio = Math.Clamp(
            (width - MinimumResponsiveWidth) / (WideResponsiveWidth - MinimumResponsiveWidth),
            0.0,
            1.0);
        double transportButtonSize = ResolveTransportButtonSize(width);
        double primaryButtonSize = ResolvePrimaryTransportButtonSize(width);

        SetButtonSize(RecordHorizontalPlaybackModeButton, transportButtonSize);
        SetButtonSize(RecordHorizontalPreviousButton, transportButtonSize);
        SetPrimaryButtonSize(RecordHorizontalPlayPauseButton, primaryButtonSize);
        SetButtonSize(RecordHorizontalNextButton, transportButtonSize);
        SetButtonSize(RecordHorizontalVolumeButton, transportButtonSize);
        double controlsSpacing = Math.Round(Lerp(5, 12, widthRatio));
        RecordHorizontalControlsPanel.Spacing = controlsSpacing;

        bool small = width < 280;
        bool showVolume = ShouldShowHorizontalVolumeControl(width);
        bool showPlaybackMode = ShouldShowHorizontalPlaybackModeControl(width);
        RecordHorizontalPlaybackModeButton.Visibility = showPlaybackMode ? Visibility.Visible : Visibility.Collapsed;
        RecordHorizontalVolumeButton.Visibility = showVolume ? Visibility.Visible : Visibility.Collapsed;

        double padding = small ? 8 : 12;
        double columnSpacing = small ? 8 : 14;
        RecordHorizontalLayout.Padding = new Thickness(padding);
        RecordHorizontalLayout.ColumnSpacing = columnSpacing;
        RecordHorizontalRightPanel.RowSpacing = height < 195 ? 2 : 4;

        double baseTitleSize = ViewModel?.TitleTextSize ?? 15.5;
        double baseSecondarySize = ViewModel?.SecondaryTextSize ?? 12;
        double baseLyricSize = ViewModel?.LyricTextSize ?? 14;

        RecordHorizontalTitleText.FontSize = small
            ? Math.Max(13, baseTitleSize - 2.5)
            : Math.Max(14, baseTitleSize - 1);
        RecordHorizontalArtistText.FontSize = small
            ? Math.Max(11, baseSecondarySize - 1)
            : baseSecondarySize;
        double lyricFontSize = small
            ? Math.Max(12.5, baseLyricSize - 0.5)
            : baseLyricSize;
        RecordHorizontalLyricText.FontSize = lyricFontSize;
        RecordHorizontalLyricTextPrimary.FontSize = lyricFontSize;
        RecordHorizontalLyricTextClone.FontSize = lyricFontSize;
        RecordHorizontalLyricMeasureText.FontSize = lyricFontSize;

        MusicSpectrumMode horizSpectrumMode = MusicSpectrumMode.PeakHoldBars;
        bool hasHorizSpectrum = ViewModel?.HasSession == true &&
            AreSystemAnimationsEnabled() &&
            TryResolveSpectrumMode(
                ViewModel?.SpectrumStyle,
                SettingsService.MusicSpectrumStylePeakHoldBars,
                out horizSpectrumMode);
        bool useHorizHalo = hasHorizSpectrum && horizSpectrumMode == MusicSpectrumMode.VinylRadialHalo;
        double horizHaloClearance = useHorizHalo ? 22.0 : 0.0;

        int buttonCount = 3 + (showVolume ? 1 : 0) + (showPlaybackMode ? 1 : 0);
        double buttonsWidth = (buttonCount - 1) * transportButtonSize + primaryButtonSize +
            (buttonCount - 1) * controlsSpacing;
        double vinylBudget = width - buttonsWidth - padding * 2 - columnSpacing;
        double vinylSize = Math.Clamp(
            Math.Min(height - (2 * padding) - horizHaloClearance, Math.Min(vinylBudget - horizHaloClearance, width * 0.45)),
            56,
            180);
        RecordHorizontalVinylHost.Width = vinylSize;
        RecordHorizontalVinylHost.Height = vinylSize;

        double rightColumnWidth = Math.Max(0, width - vinylSize - horizHaloClearance - (padding * 2) - columnSpacing);

        if (useHorizHalo)
        {
            if (RecordHorizontalSpectrumBar is not null)
            {
                RecordHorizontalSpectrumBar.Visibility = Visibility.Collapsed;
            }

            if (RecordHorizontalVinylHalo is not null)
            {
                RecordHorizontalVinylHalo.ConfigureRadialGeometry(
                    barCount: vinylSize < 96 ? 30 : 36,
                    barWidth: 2.1,
                    maxBarHeight: 10.5,
                    innerRadius: (vinylSize * 0.5) + 3.0);
                RecordHorizontalVinylHalo.Opacity = 0.92;
                RecordHorizontalVinylHalo.Visibility = Visibility.Visible;
            }
        }
        else if (hasHorizSpectrum)
        {
            if (RecordHorizontalVinylHalo is not null)
            {
                RecordHorizontalVinylHalo.Visibility = Visibility.Collapsed;
            }

            if (RecordHorizontalSpectrumBar is not null)
            {
                double usableStripWidth = Math.Max(110.0, width - (padding * 2));
                double stripHeight = horizSpectrumMode == MusicSpectrumMode.AuroraSilk ? 22.0 : 17.0;
                ConfigureLinearStripGeometry(RecordHorizontalSpectrumBar, horizSpectrumMode, usableStripWidth, stripHeight);
                RecordHorizontalSpectrumBar.Margin = new Thickness(0, 0, 0, -(padding - 1));
                RecordHorizontalSpectrumBar.Opacity = horizSpectrumMode == MusicSpectrumMode.AuroraSilk ? 0.85 : 0.54;
                RecordHorizontalSpectrumBar.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (RecordHorizontalSpectrumBar is not null)
            {
                RecordHorizontalSpectrumBar.Visibility = Visibility.Collapsed;
            }

            if (RecordHorizontalVinylHalo is not null)
            {
                RecordHorizontalVinylHalo.Visibility = Visibility.Collapsed;
            }
        }

        if (RecordHorizontalSourceButton is not null)
        {
            RecordHorizontalSourceButton.Height = 20;
            RecordHorizontalSourceButton.MinHeight = 20;
            const double sourceTextMinWidth = 166;
            bool showSourceText = rightColumnWidth >= sourceTextMinWidth &&
                ViewModel?.SourceBadgeTextVisibility == Visibility.Visible;
            RecordHorizontalSourceButton.Padding = showSourceText
                ? new Thickness(5, 0, 7, 0)
                : new Thickness(4, 0, 4, 0);
            if (RecordHorizontalSourceButtonText is not null)
            {
                RecordHorizontalSourceButtonText.Visibility = showSourceText
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RecordHorizontalSourceButtonText.MaxWidth = Math.Clamp(Math.Round(rightColumnWidth * 0.42), 48, 96);
            }
        }
    }

    private void UpdateRecordHorizontalVinylRotation()
    {
        if (RecordHorizontalVinylDisc is null)
        {
            return;
        }

        bool shouldRotate = IsLoaded &&
            _isHostWindowVisible &&
            !_isHostCompactCollapsed &&
            RecordHorizontalLayout?.Visibility == Visibility.Visible &&
            ViewModel?.IsPlaying == true &&
            VinylRotationAnimationsEnabled() &&
            AreSystemAnimationsEnabled();
        RecordHorizontalVinylDisc.SetRotating(shouldRotate);
    }

    private void StopRecordHorizontalVinylRotation()
    {
        RecordHorizontalVinylDisc?.SetRotating(false);
    }

    private void UpdateSpectrumVisuals(bool immediate = false)
    {
        string normalizedStyle = SettingsService.NormalizeMusicSpectrumStyle(ViewModel?.SpectrumStyle);
        bool canAnimateBase = !_isDisposed &&
            normalizedStyle != SettingsService.MusicSpectrumStyleOff &&
            IsLoaded &&
            _isHostWindowVisible &&
            !_isHostCompactCollapsed &&
            !IsLyricsOverlayActive &&
            ViewModel?.HasSession == true &&
            ViewModel.IsPlaying &&
            AreSystemAnimationsEnabled();

        string? sourceAppId = ViewModel?.SourceAppUserModelId;
        string? sourceDisplayName = ViewModel?.RawSourceDisplayName;
        bool isRecord = _isRecordLayout || _isRecordHorizontalLayout;

        bool minimalActive = canAnimateBase && !isRecord && _isMinimalLayout &&
            MinimalLayout?.Visibility == Visibility.Visible;
        MinimalSpectrumBar?.SetPlaybackState(
            minimalActive && MinimalSpectrumBar.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);
        MinimalRadialHalo?.SetPlaybackState(
            minimalActive && MinimalRadialHalo.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);

        bool controlsActive = canAnimateBase && !isRecord && !_isMinimalLayout &&
            ContentGrid?.Visibility == Visibility.Visible;
        ControlsSpectrumBar?.SetPlaybackState(
            controlsActive && ControlsSpectrumBar.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);
        ControlsArtworkHalo?.SetPlaybackState(
            controlsActive && ControlsArtworkHalo.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);

        bool recordVertActive = canAnimateBase && _isRecordLayout &&
            RecordLayout?.Visibility == Visibility.Visible;
        RecordVinylHalo?.SetPlaybackState(
            recordVertActive && RecordVinylHalo.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);
        RecordSpectrumBar?.SetPlaybackState(
            recordVertActive && RecordSpectrumBar.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);

        bool recordHorizActive = canAnimateBase && _isRecordHorizontalLayout &&
            RecordHorizontalLayout?.Visibility == Visibility.Visible;
        RecordHorizontalSpectrumBar?.SetPlaybackState(
            recordHorizActive && RecordHorizontalSpectrumBar.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);
        RecordHorizontalVinylHalo?.SetPlaybackState(
            recordHorizActive && RecordHorizontalVinylHalo.Visibility == Visibility.Visible,
            sourceAppId,
            sourceDisplayName,
            immediate);
    }

    private void StopSpectrumVisuals(bool immediate = true)
    {
        MinimalSpectrumBar?.SetPlaybackState(false, null, null, immediate);
        MinimalRadialHalo?.SetPlaybackState(false, null, null, immediate);
        ControlsSpectrumBar?.SetPlaybackState(false, null, null, immediate);
        ControlsArtworkHalo?.SetPlaybackState(false, null, null, immediate);
        RecordVinylHalo?.SetPlaybackState(false, null, null, immediate);
        RecordSpectrumBar?.SetPlaybackState(false, null, null, immediate);
        RecordHorizontalSpectrumBar?.SetPlaybackState(false, null, null, immediate);
        RecordHorizontalVinylHalo?.SetPlaybackState(false, null, null, immediate);
    }

    private void AnimateTonearmForState()
    {
        if (RecordTonearm is null)
        {
            return;
        }

        bool isPlaying = ViewModel?.IsPlaying == true;
        if (_lastTonearmPlayingState == isPlaying)
        {
            return;
        }

        _lastTonearmPlayingState = isPlaying;
        float targetAngle = isPlaying ? 0f : 22f;

        Visual? visual = ElementCompositionPreview.GetElementVisual(RecordTonearm);
        if (visual is null)
        {
            return;
        }

        if (!AreSystemAnimationsEnabled())
        {
            StopTonearmAnimation(resetState: false);
            visual.RotationAngleInDegrees = targetAngle;
            return;
        }

        _tonearmAnimation?.Dispose();
        _tonearmAnimation = null;

        Compositor compositor = visual.Compositor;
        _tonearmEasing ??= compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0f),
            new Vector2(0f, 1f));
        ScalarKeyFrameAnimation anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(1f, targetAngle, _tonearmEasing);
        anim.Duration = TimeSpan.FromMilliseconds(WidgetMotion.SpatialMilliseconds);
        _tonearmAnimation = anim;
        visual.StartAnimation("RotationAngleInDegrees", anim);
    }

    private void StopTonearmAnimation(bool resetState = true)
    {
        if (RecordTonearm is not null)
        {
            Visual? visual = ElementCompositionPreview.GetElementVisual(RecordTonearm);
            visual?.StopAnimation("RotationAngleInDegrees");
        }

        _tonearmAnimation?.Dispose();
        _tonearmAnimation = null;
        _tonearmEasing?.Dispose();
        _tonearmEasing = null;
        if (resetState)
        {
            _lastTonearmPlayingState = null;
        }
    }

    private void SetAlbumArtSize(double size)
    {
        if (AlbumArtShadow is not null)
        {
            AlbumArtShadow.Width = size;
            AlbumArtShadow.Height = size;
        }

        if (AlbumArtSurface is not null)
        {
            AlbumArtSurface.Width = size;
            AlbumArtSurface.Height = size;
        }

        ApplyAlbumArtCornerRadius();
    }

    private void ApplyAlbumArtCornerRadius()
    {
        double cornerRadius = ViewModel is { } viewModel
            ? viewModel.ArtworkBackdropCornerRadius.TopLeft
            : 8;
        if (AlbumArtShadow is not null)
        {
            AlbumArtShadow.CornerRadius = new CornerRadius(cornerRadius);
        }

        if (AlbumArtSurface is not null)
        {
            AlbumArtSurface.CornerRadius = new CornerRadius(cornerRadius);
        }

        if (AlbumArtInnerBorder is not null)
        {
            AlbumArtInnerBorder.CornerRadius = new CornerRadius(Math.Max(0, cornerRadius - 1));
        }

        if (AlbumArtPlaceholderBackground is not null)
        {
            AlbumArtPlaceholderBackground.CornerRadius = new CornerRadius(cornerRadius);
        }

        if (MinimalControlPanel is not null)
        {
            double concentricRadius = cornerRadius <= 0 ? 0 : Math.Max(4, cornerRadius - 2);
            MinimalControlPanel.CornerRadius = new CornerRadius(concentricRadius);
        }

        if (LyricsOverlayPanel is not null)
        {
            LyricsOverlayPanel.CornerRadius = new CornerRadius(cornerRadius);
            if (LyricsOverlayClipHost is not null)
            {
                LyricsOverlayClipHost.CornerRadius = new CornerRadius(cornerRadius);
            }
        }

        if (InlineVolumePanel is not null)
        {
            InlineVolumePanel.CornerRadius = new CornerRadius(cornerRadius);
        }
    }

    private static void SetButtonSize(Button button, double size)
    {
        button.Width = size;
        button.Height = size;
        button.MinWidth = size;
        button.MinHeight = size;
    }

    private static void SetPrimaryButtonSize(Button button, double size)
    {
        SetButtonSize(button, size);
        button.CornerRadius = new CornerRadius(size <= 30 ? 6 : 8);
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + (end - start) * Math.Clamp(progress, 0.0, 1.0);
    }

    private void UpdateInlineVolumePanelLayout(double width)
    {
        if (InlineVolumePanel is null)
        {
            return;
        }

        InlineVolumePanel.Width = ResolveInlineVolumePanelWidth(width);
        PositionInlineVolumePanel();
    }

    private MenuFlyout CreateSourceFlyout(
        MusicWidgetViewModel viewModel,
        IReadOnlyList<MusicSessionOption> options)
    {
        var flyout = new MenuFlyout
        {
            MenuFlyoutPresenterStyle = CreateSourceFlyoutPresenterStyle()
        };
        string groupName = $"MusicPlaybackSource_{GetHashCode()}_{_sourceFlyoutGeneration}";

        var followSystemItem = new RadioMenuFlyoutItem
        {
            Text = viewModel.FollowSystemSourceLabel,
            GroupName = groupName,
            IsChecked = viewModel.IsFollowingSystemSession
        };
        followSystemItem.Click += (_, _) =>
            BeginSourceSelection(flyout, viewModel, sessionId: null);
        flyout.Items.Add(followSystemItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (options.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = viewModel.NoAvailableSourcesLabel,
                IsEnabled = false
            });
            return flyout;
        }

        int generation = _sourceFlyoutGeneration;
        foreach (MusicSessionOption option in options)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = option.SourceDisplayName,
                GroupName = groupName,
                IsChecked = string.Equals(
                    option.SessionId,
                    viewModel.PreferredSessionId,
                    StringComparison.Ordinal)
            };
            item.Click += (_, _) =>
                BeginSourceSelection(flyout, viewModel, option.SessionId);
            flyout.Items.Add(item);
            _ = PopulateSourceFlyoutItemIconAsync(item, option, generation);
        }

        return flyout;
    }

    private async Task PopulateSourceFlyoutItemIconAsync(
        RadioMenuFlyoutItem item,
        MusicSessionOption option,
        int generation)
    {
        try
        {
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? icon = await MusicSourceAppIconResolver
                .ResolveIconAsync(DispatcherQueue, option.SourceAppUserModelId, option.SourceDisplayName);
            if (_isDisposed || icon is null || generation != _sourceFlyoutGeneration)
            {
                return;
            }

            item.Icon = new ImageIcon
            {
                Source = icon,
                Width = 16,
                Height = 16
            };
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicWidget] Source flyout icon resolve skipped: {ex.Message}");
        }
    }

    private void UpdateSourcePickerLabels()
    {
        string label = ViewModel?.SourcePickerTooltip ?? string.Empty;
        if (MinimalSourceButton is not null)
        {
            SetSourceButtonLabel(MinimalSourceButton, label);
        }

        if (SourceButton is not null)
        {
            SetSourceButtonLabel(SourceButton, label);
        }

        if (RecordSourceButton is not null)
        {
            SetSourceButtonLabel(RecordSourceButton, label);
        }

        if (RecordHorizontalSourceButton is not null)
        {
            SetSourceButtonLabel(RecordHorizontalSourceButton, label);
        }
    }

    private static void SetSourceButtonLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private Style CreateSourceFlyoutPresenterStyle()
    {
        double width = ActualWidth > 0 ? ActualWidth : (RootGrid?.ActualWidth ?? 0);
        double height = ActualHeight > 0 ? ActualHeight : (RootGrid?.ActualHeight ?? 0);
        double maxWidth = ResolveSourceFlyoutMaxWidth(width);
        double maxHeight = ResolveSourceFlyoutMaxHeight(height);
        var style = new Style(typeof(MenuFlyoutPresenter))
        {
            BasedOn = (Style)Application.Current.Resources[typeof(MenuFlyoutPresenter)]
        };

        if (maxWidth > 0)
        {
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, maxWidth));
            style.Setters.Add(new Setter(
                FrameworkElement.MinWidthProperty,
                Math.Min(SourceFlyoutMinimumWidth, maxWidth)));
        }

        if (maxHeight > 0)
        {
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, maxHeight));
        }

        style.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollModeProperty,
            ScrollMode.Enabled));
        style.Setters.Add(new Setter(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto));
        return style;
    }

    private void BeginSourceSelection(
        MenuFlyout flyout,
        MusicWidgetViewModel viewModel,
        string? sessionId)
    {
        flyout.Hide();
        _ = SelectSourceAsync(viewModel, sessionId);
    }

    private static async Task SelectSourceAsync(
        MusicWidgetViewModel viewModel,
        string? sessionId)
    {
        try
        {
            bool selected = await viewModel.SelectSessionAsync(sessionId);
            if (!selected)
            {
                App.LogVerbose("[MusicWidget] Selected source closed before it could be activated.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Select source failed: {ex}");
        }
    }

    private void CloseSourceFlyout()
    {
        ++_sourceFlyoutGeneration;
        MenuFlyout? flyout = _sourceFlyout;
        _sourceFlyout = null;
        flyout?.Hide();
    }

    private void PositionInlineVolumePanel()
    {
        if (InlineVolumePanel is null)
        {
            return;
        }

        Button? anchor = _volumeAnchorButton ?? VolumeButton;
        if (anchor is null || !anchor.IsLoaded || anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0)
        {
            return;
        }

        Windows.Foundation.Point buttonOrigin = anchor.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double panelWidth = InlineVolumePanel.Width > 0 ? InlineVolumePanel.Width : InlineVolumePanel.ActualWidth;
        double panelHeight = InlineVolumePanel.ActualHeight > 0 ? InlineVolumePanel.ActualHeight : 56;
        double left = buttonOrigin.X + anchor.ActualWidth - panelWidth;
        double top = buttonOrigin.Y - panelHeight - 7;

        if (RootGrid.ActualWidth > 0)
        {
            left = Math.Clamp(
                left,
                InlineVolumePanelHorizontalInset,
                Math.Max(
                    InlineVolumePanelHorizontalInset,
                    RootGrid.ActualWidth - panelWidth - InlineVolumePanelHorizontalInset));
        }

        if (RootGrid.ActualHeight > 0)
        {
            top = Math.Clamp(
                top,
                InlineVolumePanelHorizontalInset,
                Math.Max(
                    InlineVolumePanelHorizontalInset,
                    RootGrid.ActualHeight - panelHeight - InlineVolumePanelHorizontalInset));
        }

        InlineVolumePanel.Margin = new Thickness(Math.Round(left), Math.Round(top), 0, 0);
    }

    private static bool IsPointerPointInside(PointerRoutedEventArgs e, FrameworkElement? element)
    {
        if (element is null ||
            !element.IsLoaded ||
            element.Visibility != Visibility.Visible ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point pt = e.GetCurrentPoint(element).Position;
            return pt.X >= -2 &&
                pt.Y >= -2 &&
                pt.X <= element.ActualWidth + 2 &&
                pt.Y <= element.ActualHeight + 2;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElementInside(DependencyObject? sourceElement, DependencyObject? target)
    {
        if (target is null)
        {
            return false;
        }

        DependencyObject? current = sourceElement;
        while (current is not null)
        {
            if (ReferenceEquals(current, target) || Equals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task RefreshInlineVolumeForActiveSourceAsync()
    {
        if (_isDisposed ||
            !_isInlineVolumeOpen ||
            InlineVolumePanel?.Visibility != Visibility.Visible ||
            InlineSessionVolumeSlider?.PointerCaptures is { Count: > 0 } ||
            InlineSystemVolumeSlider?.PointerCaptures is { Count: > 0 })
        {
            return;
        }

        int generation = _inlineVolumeOpenGeneration;
        try
        {
            bool hasSystemVolume = await RefreshInlineVolumeAsync();
            if (_isDisposed ||
                !_isInlineVolumeOpen ||
                generation != _inlineVolumeOpenGeneration ||
                InlineVolumePanel?.Visibility != Visibility.Visible)
            {
                return;
            }

            _hasInlineSystemVolumeSnapshot = hasSystemVolume;
            if (InlineSystemVolumeSlider is not null)
            {
                InlineSystemVolumeSlider.IsEnabled = hasSystemVolume;
            }

            InlineVolumePanel.UpdateLayout();
            PositionInlineVolumePanel();
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicWidget] Active source inline volume sync skipped: {ex.Message}");
        }
    }

    private async Task<bool> RefreshInlineVolumeAsync()
    {
        if (ViewModel is null)
        {
            return false;
        }

        _isInlineVolumeRefreshing = true;
        try
        {
            bool hasSystemVolume = await ViewModel.RefreshVolumeAsync();
            SyncInlineVolumeSlidersFromViewModelCore();
            return hasSystemVolume;
        }
        finally
        {
            _isInlineVolumeRefreshing = false;
        }
    }

    private void SyncInlineVolumeSlidersFromViewModel()
    {
        if (_isDisposed || ViewModel is null)
        {
            return;
        }

        _isInlineVolumeRefreshing = true;
        try
        {
            SyncInlineVolumeSlidersFromViewModelCore();
        }
        finally
        {
            _isInlineVolumeRefreshing = false;
        }
    }

    private void SyncInlineVolumeSlidersFromViewModelCore()
    {
        if (_isDisposed || ViewModel is null)
        {
            return;
        }

        if (InlineSessionVolumeSlider is not null)
        {
            InlineSessionVolumeSlider.Value = ViewModel.SessionVolume;
        }

        if (InlineSystemVolumeSlider is not null)
        {
            InlineSystemVolumeSlider.Value = ViewModel.SystemVolume;
        }
    }

    internal static bool ShouldApplyInlineVolumeSliderChange(
        bool isRefreshing,
        bool isReadyForUserInput,
        bool isPanelOpen,
        Visibility panelVisibility,
        bool hasViewModel) =>
        !isRefreshing &&
        isReadyForUserInput &&
        isPanelOpen &&
        panelVisibility == Visibility.Visible &&
        hasViewModel;

    private async void InlineSessionVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (InlineVolumePanel is null ||
            ViewModel is null ||
            !ShouldApplyInlineVolumeSliderChange(
                _isInlineVolumeRefreshing,
                _isInlineVolumeReadyForUserInput && ViewModel.HasSessionVolume,
                _isInlineVolumeOpen,
                InlineVolumePanel.Visibility,
                hasViewModel: true))
        {
            return;
        }

        await ViewModel.SetSessionVolumeAsync(e.NewValue);
    }

    private async void InlineSystemVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (InlineVolumePanel is null ||
            ViewModel is null ||
            !ShouldApplyInlineVolumeSliderChange(
                _isInlineVolumeRefreshing,
                _isInlineVolumeReadyForUserInput && _hasInlineSystemVolumeSnapshot,
                _isInlineVolumeOpen,
                InlineVolumePanel.Visibility,
                hasViewModel: true))
        {
            return;
        }

        await ViewModel.SetSystemVolumeAsync(e.NewValue);
    }

    private void UpdateAlbumArtCenterPoint()
    {
        if (AlbumArtSurface is null)
        {
            return;
        }

        AlbumArtSurface.CenterPoint = new Vector3(
            (float)(AlbumArtSurface.ActualWidth / 2),
            (float)(AlbumArtSurface.ActualHeight / 2),
            0);
    }

    private bool IsLyricsOverlayActive =>
        ViewModel?.LyricsOverlayVisibility == Visibility.Visible;

    private void ApplyActivePlayerLayoutVisibility()
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsLyricsOverlayActive)
        {
            _hoveredProgressHost = null;
            _activeProgressDragHost = null;
            _isProgressDragging = false;
            _isInlineVolumeOpen = false;
            _isInlineVolumeReadyForUserInput = false;
            _hasInlineSystemVolumeSnapshot = false;
            ++_inlineVolumeOpenGeneration;
            _volumeAnchorButton = null;
            _lastTonearmPlayingState = null;

            CloseSourceFlyout();
            ResetAlbumArtMotion();
            StopTitleMarquee();
            StopLyricMarquee();
            StopRecordVinylRotation();
            StopRecordHorizontalVinylRotation();
            StopSpectrumVisuals(immediate: true);
            StopTonearmAnimation();

            Bindings.Update();
            return;
        }

        DetachLyricsScrollViewer();
        Bindings.Update();

        bool isRecord = _isRecordLayout || _isRecordHorizontalLayout;
        if (MinimalLayout is not null)
        {
            MinimalLayout.Visibility = (!isRecord && _isMinimalLayout) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ContentGrid is not null)
        {
            ContentGrid.Visibility = (!isRecord && !_isMinimalLayout) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (RecordLayout is not null)
        {
            RecordLayout.Visibility = _isRecordLayout ? Visibility.Visible : Visibility.Collapsed;
        }

        if (RecordHorizontalLayout is not null)
        {
            RecordHorizontalLayout.Visibility = _isRecordHorizontalLayout ? Visibility.Visible : Visibility.Collapsed;
        }

        if (RecordTonearm is not null && !_isRecordLayout)
        {
            RecordTonearm.Visibility = Visibility.Collapsed;
        }

        UpdateSourcePickerLabels();
        ApplyAlbumArtCornerRadius();
    }

    private void UpdateLyricsOverlayState()
    {
        if (_isDisposed)
        {
            return;
        }

        bool shouldShow = IsLyricsOverlayActive;
        if (!shouldShow)
        {
            ++_lyricsTapVersion;
            DetachLyricsScrollViewer();
            if (LyricsListView is not null)
            {
                LyricsListView.ClearValue(ItemsControl.ItemsSourceProperty);
            }

            if (LyricsOverlayBackdropImage is not null)
            {
                LyricsOverlayBackdropImage.ClearValue(Image.SourceProperty);
            }

            ApplyActivePlayerLayoutVisibility();
            ApplyResponsiveLayout();
            UpdateProgressVisuals();
            return;
        }

        ApplyActivePlayerLayoutVisibility();
        EnsureLyricsOverlayLoaded();
        if (LyricsOverlayPanel is not null)
        {
            ApplyAlbumArtCornerRadius();
            LyricsOverlayPanel.Visibility = Visibility.Visible;
            EnsureLyricsScrollViewerAttached();
            UpdateLyricsItemHighlighting();
            ScrollActiveLyricIntoView();
        }
    }

    private ScrollViewer? EnsureLyricsScrollViewerAttached()
    {
        if (_lyricsScrollViewer is not null)
        {
            return _lyricsScrollViewer;
        }

        if (LyricsListView is null)
        {
            return null;
        }

        ScrollViewer? viewer = FindDescendantScrollViewer(LyricsListView);
        if (viewer is not null)
        {
            _lyricsScrollViewer = viewer;
            viewer.ViewChanged += LyricsScrollViewer_ViewChanged;
        }

        return viewer;
    }

    private void DetachLyricsScrollViewer()
    {
        if (_lyricsScrollViewer is { } viewer)
        {
            viewer.ViewChanged -= LyricsScrollViewer_ViewChanged;
            _lyricsScrollViewer = null;
        }
    }

    private void LyricsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateLyricsViewportEdgeAttenuation();
    }

    private void UpdateLyricsViewportEdgeAttenuation()
    {
        if (_isDisposed ||
            LyricsListView is null ||
            LyricsOverlayPanel?.Visibility != Visibility.Visible ||
            _lyricsScrollViewer is not { ViewportHeight: > 0 } viewer)
        {
            return;
        }

        int activeIndex = ViewModel?.ActiveLyricIndex ?? -1;
        int count = LyricsListView.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (LyricsListView.ContainerFromIndex(i) is ListViewItem container)
            {
                double baseOpacity = ComputeBaseLyricDistanceOpacity(i, activeIndex);
                container.Opacity = baseOpacity * ComputeViewportEdgeFactor(container, viewer);
            }
        }
    }

    private static double ComputeBaseLyricDistanceOpacity(int itemIndex, int activeIndex)
    {
        if (activeIndex < 0)
        {
            return itemIndex == 0 ? 0.90 : (itemIndex == 1 ? 0.52 : 0.28);
        }

        int distance = Math.Abs(itemIndex - activeIndex);
        return distance switch
        {
            0 => 1.0,
            1 => 0.52,
            2 => 0.30,
            _ => 0.18
        };
    }

    private static double ComputeViewportEdgeFactor(ListViewItem container, ScrollViewer? viewer)
    {
        if (viewer is null || viewer.ViewportHeight <= LyricsViewportEdgeFadeHeight * 2 || container.ActualHeight <= 0)
        {
            return 1.0;
        }

        try
        {
            double topY = container.TransformToVisual(viewer)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double centerY = topY + (container.ActualHeight * 0.5);
            double edgeDist = Math.Min(centerY, viewer.ViewportHeight - centerY);
            if (edgeDist >= LyricsViewportEdgeFadeHeight)
            {
                return 1.0;
            }

            return Math.Clamp(edgeDist / LyricsViewportEdgeFadeHeight, 0.06, 1.0);
        }
        catch
        {
            return 1.0;
        }
    }

    private void LyricsListView_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        ApplyLyricContainerStyle(container, args.ItemIndex, ViewModel?.ActiveLyricIndex ?? -1);
    }

    private void UpdateLyricsItemHighlighting()
    {
        if (LyricsListView is null || LyricsOverlayPanel?.Visibility != Visibility.Visible)
        {
            return;
        }

        EnsureLyricsScrollViewerAttached();
        int activeIndex = ViewModel?.ActiveLyricIndex ?? -1;
        int count = LyricsListView.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (LyricsListView.ContainerFromIndex(i) is ListViewItem container)
            {
                ApplyLyricContainerStyle(container, i, activeIndex);
            }
        }
    }

    private void ApplyLyricContainerStyle(ListViewItem container, int itemIndex, int activeIndex)
    {
        bool isActive = itemIndex == activeIndex;
        int distance = activeIndex >= 0 ? Math.Abs(itemIndex - activeIndex) : itemIndex;
        double baseOpacity = ComputeBaseLyricDistanceOpacity(itemIndex, activeIndex);
        container.Opacity = baseOpacity * ComputeViewportEdgeFactor(container, _lyricsScrollViewer);

        Grid? rootGrid = container.ContentTemplateRoot as Grid ?? FindDescendantGrid(container);
        if (rootGrid is null)
        {
            return;
        }

        double inactivePrimarySize = Math.Clamp((ViewModel?.TextSize ?? 14.0) + 1.0, 14.5, 17.5);
        double activePrimarySize = inactivePrimarySize + 3.5;
        double inactiveSecondarySize = Math.Max(12.0, inactivePrimarySize - 2.0);
        double activeSecondarySize = inactiveSecondarySize + 1.5;

        foreach (UIElement child in rootGrid.Children)
        {
            if (child is StackPanel panel && panel.Children.Count > 0)
            {
                if (panel.Children[0] is TextBlock primary)
                {
                    primary.FontWeight = isActive
                        ? FontWeights.Bold
                        : (distance == 1 ? FontWeights.Medium : FontWeights.Normal);
                    primary.FontSize = isActive ? activePrimarySize : inactivePrimarySize;
                }

                if (panel.Children.Count > 1 && panel.Children[1] is TextBlock secondary)
                {
                    secondary.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
                    secondary.FontSize = isActive ? activeSecondarySize : inactiveSecondarySize;
                    secondary.Opacity = isActive ? 0.94 : 0.82;
                    secondary.Visibility = string.IsNullOrWhiteSpace(secondary.Text)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            }
        }
    }

    private static Grid? FindDescendantGrid(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid grid)
            {
                return grid;
            }

            Grid? nested = FindDescendantGrid(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void ScrollActiveLyricIntoView()
    {
        if (LyricsListView is null ||
            LyricsOverlayPanel?.Visibility != Visibility.Visible ||
            ViewModel is null)
        {
            return;
        }

        int activeIdx = ViewModel.ActiveLyricIndex;
        if (activeIdx < 0 || activeIdx >= ViewModel.LyricLines.Count)
        {
            UpdateLyricsItemHighlighting();
            return;
        }

        LrcLine activeLine = ViewModel.LyricLines[activeIdx];
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || LyricsListView is null || LyricsOverlayPanel?.Visibility != Visibility.Visible)
            {
                return;
            }

            ScrollViewer? scrollViewer = EnsureLyricsScrollViewerAttached();
            if (scrollViewer is not null && scrollViewer.ViewportHeight > 0)
            {
                double verticalPad = Math.Max(36, Math.Round(scrollViewer.ViewportHeight * 0.40));
                if (Math.Abs(LyricsListView.Padding.Top - verticalPad) > 1.5)
                {
                    LyricsListView.Padding = new Thickness(18, verticalPad, 18, verticalPad);
                }
            }

            ListViewItem? realizedContainer = LyricsListView.ContainerFromIndex(activeIdx) as ListViewItem;
            if (realizedContainer is null)
            {
                LyricsListView.ScrollIntoView(activeLine, ScrollIntoViewAlignment.Leading);
                LyricsListView.UpdateLayout();
                realizedContainer = LyricsListView.ContainerFromIndex(activeIdx) as ListViewItem;
            }

            if (scrollViewer is not null && realizedContainer is not null && scrollViewer.ViewportHeight > 0)
            {
                double currentY = realizedContainer.TransformToVisual(scrollViewer)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                double desiredY = Math.Max(8, (scrollViewer.ViewportHeight - realizedContainer.ActualHeight) * 0.50);
                double targetOffset = Math.Clamp(
                    scrollViewer.VerticalOffset + (currentY - desiredY),
                    0,
                    scrollViewer.ScrollableHeight);
                _ = scrollViewer.ChangeView(
                    horizontalOffset: null,
                    verticalOffset: targetOffset,
                    zoomFactor: null,
                    disableAnimation: !AreSystemAnimationsEnabled());
            }
            else
            {
                LyricsListView.ScrollIntoView(activeLine, ScrollIntoViewAlignment.Leading);
            }

            UpdateLyricsItemHighlighting();
        });
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            ScrollViewer? nested = FindDescendantScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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

        if (e.PropertyName is nameof(MusicWidgetViewModel.InlineLyricText) or
            nameof(MusicWidgetViewModel.SubtitleText) or
            nameof(MusicWidgetViewModel.LyricTextSize) or
            nameof(MusicWidgetViewModel.MinimalLyricTextSize))
        {
            QueueLyricMarqueeUpdate();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.TextSize))
        {
            ApplyResponsiveLayout();
            UpdateLyricsItemHighlighting();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.ThumbnailImage))
        {
            QueueArtworkTransition();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.ArtworkBackdropCornerRadius))
        {
            ApplyAlbumArtCornerRadius();
        }

        if (e.PropertyName is nameof(MusicWidgetViewModel.DisplayMode) or
            nameof(MusicWidgetViewModel.SpectrumStyle) or
            nameof(MusicWidgetViewModel.HasSession) or
            nameof(MusicWidgetViewModel.ShowLyrics) or
            nameof(MusicWidgetViewModel.InlineLyricVisibility) or
            nameof(MusicWidgetViewModel.SubtitleVisibility) or
            nameof(MusicWidgetViewModel.TimelineRowVisibility) or
            nameof(MusicWidgetViewModel.SourceBadgeTextVisibility))
        {
            ApplyResponsiveLayout();
        }

        if (e.PropertyName is nameof(MusicWidgetViewModel.SourceAppUserModelId) or
            nameof(MusicWidgetViewModel.SourceDisplayName))
        {
            UpdateSpectrumVisuals();
            if (_isInlineVolumeOpen && InlineVolumePanel?.Visibility == Visibility.Visible)
            {
                _ = RefreshInlineVolumeForActiveSourceAsync();
            }
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.SourcePickerTooltip))
        {
            UpdateSourcePickerLabels();
        }

        if (e.PropertyName is nameof(MusicWidgetViewModel.LyricsOverlayVisibility) or
            nameof(MusicWidgetViewModel.IsLyricsViewOpen))
        {
            UpdateLyricsOverlayState();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.LyricsListVisibility))
        {
            UpdateLyricsItemHighlighting();
            ScrollActiveLyricIntoView();
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.MusicAccentBrush))
        {
            if (_isInlineVolumeOpen)
            {
                SyncInlineVolumeSliderAccent();
            }

            if (_isHostWindowVisible && !_isHostCompactCollapsed)
            {
                UpdateLyricsItemHighlighting();
            }
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.ActiveLyricIndex))
        {
            if (_isHostWindowVisible && !_isHostCompactCollapsed)
            {
                ScrollActiveLyricIntoView();
                QueueLyricMarqueeUpdate();
            }
        }

        if (e.PropertyName == nameof(MusicWidgetViewModel.SessionVolumeRowVisibility) &&
            _isInlineVolumeOpen &&
            InlineVolumePanel?.Visibility == Visibility.Visible)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (_isInlineVolumeOpen && InlineVolumePanel is not null)
                {
                    InlineVolumePanel.UpdateLayout();
                    PositionInlineVolumePanel();
                }
            });
        }

        if (e.PropertyName is nameof(MusicWidgetViewModel.IsPlaying) or
            nameof(MusicWidgetViewModel.PlaybackState))
        {
            UpdateRecordHorizontalVinylRotation();
            UpdateRecordVinylRotation();
            UpdateSpectrumVisuals(immediate: false);
            if (_isHostWindowVisible &&
                !_isHostCompactCollapsed &&
                RecordTonearm?.Visibility == Visibility.Visible)
            {
                AnimateTonearmForState();
            }

            if (ViewModel?.IsPlaying == true)
            {
                QueueTitleMarqueeUpdate();
                QueueLyricMarqueeUpdate();
            }
            else
            {
                StopTitleMarquee();
                StopLyricMarquee();
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
        if (!_isHostWindowVisible || _isHostCompactCollapsed ||
            ViewModel?.ThumbnailImage is null ||
            !AreSystemAnimationsEnabled())
        {
            if (MinimalArtworkImage is not null)
            {
                ResetArtworkVisual(MinimalArtworkImage);
            }

            if (AlbumArtworkImage is not null)
            {
                ResetArtworkVisual(AlbumArtworkImage);
            }

            return;
        }

        if (MinimalArtworkImage is not null)
        {
            PrepareArtworkVisual(MinimalArtworkImage);
        }

        if (AlbumArtworkImage is not null)
        {
            PrepareArtworkVisual(AlbumArtworkImage);
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || version != _artworkTransitionVersion || ViewModel?.ThumbnailImage is null)
            {
                return;
            }

            if (MinimalArtworkImage is not null)
            {
                StartArtworkTransition(MinimalArtworkImage);
            }

            if (AlbumArtworkImage is not null)
            {
                StartArtworkTransition(AlbumArtworkImage);
            }
        });
    }

    private static void PrepareArtworkVisual(FrameworkElement image)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(image);
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
        Visual visual = ElementCompositionPreview.GetElementVisual(image);
        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0),
            new Vector2(0, 1));

        ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(ArtworkTransitionDurationMs);
        opacityAnimation.InsertKeyFrame(0, 0);
        opacityAnimation.InsertKeyFrame(1, 1, easing);

        Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
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
        Visual visual = ElementCompositionPreview.GetElementVisual(image);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }

    private void QueueTitleMarqueeUpdate()
    {
        if (!IsLoaded ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed ||
            ViewModel?.IsPlaying != true ||
            !TextMarqueeAnimationsEnabled() ||
            !AreSystemAnimationsEnabled())
        {
            return;
        }

        int version = ++_titleMarqueeMeasureVersion;
        StopTitleMarquee();
        _ = RunDeferredTitleMarqueeUpdateAsync(version);
    }

    private async Task RunDeferredTitleMarqueeUpdateAsync(int version)
    {
        await Task.Delay(TitleMarqueeDeferredMeasureMs);
        if (version != _titleMarqueeMeasureVersion ||
            !IsLoaded ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed ||
            ViewModel?.IsPlaying != true ||
            !TextMarqueeAnimationsEnabled() ||
            !AreSystemAnimationsEnabled())
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => UpdateTitleMarquee(version));
    }

    private void UpdateTitleMarquee(int version)
    {
        if (version != _titleMarqueeMeasureVersion ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed ||
            ViewModel?.IsPlaying != true ||
            !TextMarqueeAnimationsEnabled() ||
            !AreSystemAnimationsEnabled())
        {
            return;
        }

        if (!TryGetActiveTitleMarqueeElements(out var elements))
        {
            StopTitleMarquee();
            return;
        }

        double viewportWidth = elements.Host.ActualWidth;
        if (!IsLoaded || viewportWidth <= 0)
        {
            StopTitleMarquee();
            return;
        }

        elements.Clip.Rect = CreateHorizontalViewportClipRect(viewportWidth, elements.Host.ActualHeight);
        elements.Measure.FontSize = elements.Static.FontSize;
        elements.Primary.FontSize = elements.Static.FontSize;
        elements.Clone.FontSize = elements.Static.FontSize;

        double titleWidth = MeasureTitleWidth(elements);
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

        string titleText = ViewModel?.Title ?? string.Empty;
        double distance = titleWidth + TitleMarqueeGap;
        elements.Primary.Text = titleText;
        elements.Clone.Text = titleText;
        elements.Primary.Width = titleWidth;
        elements.Clone.Width = titleWidth;
        elements.Clone.Visibility = Visibility.Visible;
        elements.Clone.Margin = new Thickness(distance, 0, 0, 0);
        elements.Canvas.Width = distance + titleWidth;
        elements.Static.Opacity = 0;
        elements.Canvas.Visibility = Visibility.Visible;
        _titleMarqueeDistance = distance;
        elements.Canvas.Translation = Vector3.Zero;

        double movingDurationSeconds = _titleMarqueeDistance / TitleMarqueeSpeedPixelsPerSecond;
        double totalDurationSeconds = TitleMarqueeStartDelayMs / 1000.0 + movingDurationSeconds;
        float movementStartProgress = (float)(TitleMarqueeStartDelayMs / 1000.0 / totalDurationSeconds);
        ElementCompositionPreview.SetIsTranslationEnabled(elements.Canvas, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(elements.Canvas);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        using StepEasingFunction holdStep = visual.Compositor.CreateStepEasingFunction();
        holdStep.IsFinalStepSingleFrame = true;
        ScalarKeyFrameAnimation animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(movementStartProgress, 0, holdStep);
        animation.InsertKeyFrame(1, (float)-_titleMarqueeDistance);
        animation.Duration = TimeSpan.FromSeconds(totalDurationSeconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        _titleMarqueeAnimation?.Dispose();
        _titleMarqueeAnimation = animation;
        _titleMarqueeAnimatedCanvas = elements.Canvas;
        visual.StartAnimation("Translation.X", animation);
    }

    private void StopTitleMarquee()
    {
        if (_titleMarqueeAnimatedCanvas is { } animatedCanvas)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(animatedCanvas, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(animatedCanvas);
            visual.StopAnimation("Translation.X");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            animatedCanvas.Translation = Vector3.Zero;
        }

        _titleMarqueeAnimation?.Dispose();
        _titleMarqueeAnimation = null;
        _titleMarqueeAnimatedCanvas = null;
        _titleMarqueeDistance = 0;
        if (TitleStaticText is not null &&
            TitleMarqueeCanvas is not null &&
            TitleTextPrimary is not null &&
            TitleTextClone is not null)
        {
            ResetMarqueeElements(TitleStaticText, TitleMarqueeCanvas, TitleTextPrimary, TitleTextClone);
        }

        if (MinimalTitleStaticText is not null &&
            MinimalTitleMarqueeCanvas is not null &&
            MinimalTitleTextPrimary is not null &&
            MinimalTitleTextClone is not null)
        {
            ResetMarqueeElements(
                MinimalTitleStaticText,
                MinimalTitleMarqueeCanvas,
                MinimalTitleTextPrimary,
                MinimalTitleTextClone);
        }
    }

    private void QueueLyricMarqueeUpdate()
    {
        if (!IsLoaded ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed ||
            ViewModel?.IsPlaying != true ||
            ViewModel.LyricsOverlayVisibility == Visibility.Visible ||
            !TextMarqueeAnimationsEnabled() ||
            !AreSystemAnimationsEnabled())
        {
            StopLyricMarquee();
            return;
        }

        int version = ++_lyricMarqueeMeasureVersion;
        StopLyricMarquee();
        _ = RunDeferredLyricMarqueeUpdateAsync(version);
    }

    private async Task RunDeferredLyricMarqueeUpdateAsync(int version)
    {
        await Task.Delay(LyricMarqueeDeferredMeasureMs);
        if (version != _lyricMarqueeMeasureVersion ||
            !IsLoaded ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed ||
            ViewModel?.IsPlaying != true ||
            ViewModel.LyricsOverlayVisibility == Visibility.Visible ||
            !TextMarqueeAnimationsEnabled() ||
            !AreSystemAnimationsEnabled())
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => UpdateLyricMarquee(version));
    }

    private void UpdateLyricMarquee(int version)
    {
        if (version != _lyricMarqueeMeasureVersion ||
            !IsLoaded ||
            !_isHostWindowVisible ||
            _isHostCompactCollapsed ||
            ViewModel?.IsPlaying != true ||
            ViewModel.LyricsOverlayVisibility == Visibility.Visible ||
            !TextMarqueeAnimationsEnabled() ||
            !AreSystemAnimationsEnabled())
        {
            return;
        }

        if (!TryGetActiveLyricMarqueeElements(out var elements, out string? lyricText))
        {
            StopLyricMarquee();
            return;
        }

        double viewportWidth = elements.Host.ActualWidth;
        if (viewportWidth <= 0 || string.IsNullOrWhiteSpace(lyricText))
        {
            StopLyricMarquee();
            return;
        }

        elements.Clip.Rect = CreateHorizontalViewportClipRect(viewportWidth, elements.Host.ActualHeight);

        elements.Measure.FontSize = elements.Static.FontSize;
        elements.Primary.FontSize = elements.Static.FontSize;
        double lyricWidth = MeasureTextBlockWidth(elements.Measure, elements.Primary, lyricText);
        if (lyricWidth <= viewportWidth + LyricMarqueeOverflowTolerance)
        {
            StopLyricMarquee();
            return;
        }

        double overflow = lyricWidth - viewportWidth;
        double travel = overflow + 2.0;

        elements.Primary.Text = lyricText;
        elements.Primary.Width = lyricWidth + 2.0;
        elements.Clone.Visibility = Visibility.Collapsed;
        elements.Clone.Margin = default;
        elements.Canvas.Width = lyricWidth + 2.0;
        elements.Static.Opacity = 0;
        elements.Canvas.Visibility = Visibility.Visible;
        elements.Canvas.Translation = Vector3.Zero;

        ElementCompositionPreview.SetIsTranslationEnabled(elements.Canvas, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(elements.Canvas);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        Compositor compositor = visual.Compositor;
        using StepEasingFunction holdStep = compositor.CreateStepEasingFunction();
        holdStep.IsFinalStepSingleFrame = true;
        using CubicBezierEasingFunction smoothGlide = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0.0f),
            new Vector2(0.18f, 1.0f));

        ScalarKeyFrameAnimation animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        double lineDuration = ViewModel?.CurrentLyricDurationSeconds ?? 0;

        if (lineDuration > 0.8 && lineDuration < 18.0)
        {
            double startHoldSec = Math.Clamp(lineDuration * 0.18, 0.30, 0.65);
            double endHoldSec = Math.Clamp(lineDuration * 0.18, 0.32, 0.80);
            double idealGlideSec = Math.Clamp(0.90 + (travel / 68.0), 1.05, 4.40);
            double windowSec = Math.Max(0.68, lineDuration - startHoldSec - endHoldSec);
            double glideSec = Math.Clamp(Math.Min(idealGlideSec, windowSec), 0.68, 4.60);
            double tailHoldSec = Math.Max(endHoldSec, lineDuration - startHoldSec - glideSec + 1.5);
            double totalDurationSeconds = startHoldSec + glideSec + tailHoldSec;

            float holdProgress = (float)Math.Clamp(startHoldSec / totalDurationSeconds, 0.03, 0.65);
            float glideEndProgress = (float)Math.Clamp(
                (startHoldSec + glideSec) / totalDurationSeconds,
                holdProgress + 0.08f,
                0.98f);

            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(holdProgress, 0f, holdStep);
            animation.InsertKeyFrame(glideEndProgress, (float)-travel, smoothGlide);
            animation.InsertKeyFrame(1f, (float)-travel, holdStep);
            animation.Duration = TimeSpan.FromSeconds(totalDurationSeconds);
            animation.IterationBehavior = AnimationIterationBehavior.Count;
            animation.IterationCount = 1;
            animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        }
        else
        {
            const double startHoldSec = 0.95;
            const double endHoldSec = 1.50;
            double glideForwardSec = Math.Clamp(1.10 + (travel / 64.0), 1.25, 4.60);
            double glideBackSec = Math.Clamp(0.95 + (travel / 85.0), 1.05, 3.60);
            double totalDurationSeconds = startHoldSec + glideForwardSec + endHoldSec + glideBackSec;

            float p1 = (float)(startHoldSec / totalDurationSeconds);
            float p2 = (float)((startHoldSec + glideForwardSec) / totalDurationSeconds);
            float p3 = (float)((startHoldSec + glideForwardSec + endHoldSec) / totalDurationSeconds);

            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(p1, 0f, holdStep);
            animation.InsertKeyFrame(p2, (float)-travel, smoothGlide);
            animation.InsertKeyFrame(p3, (float)-travel, holdStep);
            animation.InsertKeyFrame(1f, 0f, smoothGlide);
            animation.Duration = TimeSpan.FromSeconds(totalDurationSeconds);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
        }

        _lyricMarqueeAnimation?.Dispose();
        _lyricMarqueeAnimation = animation;
        _lyricMarqueeAnimatedCanvas = elements.Canvas;
        visual.StartAnimation("Translation.X", animation);
    }

    private void StopLyricMarquee()
    {
        if (_lyricMarqueeAnimatedCanvas is { } animatedCanvas)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(animatedCanvas, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(animatedCanvas);
            visual.StopAnimation("Translation.X");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            animatedCanvas.Translation = Vector3.Zero;
        }

        _lyricMarqueeAnimation?.Dispose();
        _lyricMarqueeAnimation = null;
        _lyricMarqueeAnimatedCanvas = null;

        if (SubtitleTextBlock is not null &&
            ControlsLyricMarqueeCanvas is not null &&
            ControlsLyricTextPrimary is not null &&
            ControlsLyricTextClone is not null)
        {
            ResetMarqueeElements(SubtitleTextBlock, ControlsLyricMarqueeCanvas, ControlsLyricTextPrimary, ControlsLyricTextClone);
        }

        if (MinimalLyricText is not null &&
            MinimalLyricMarqueeCanvas is not null &&
            MinimalLyricTextPrimary is not null &&
            MinimalLyricTextClone is not null)
        {
            ResetMarqueeElements(
                MinimalLyricText,
                MinimalLyricMarqueeCanvas,
                MinimalLyricTextPrimary,
                MinimalLyricTextClone);
        }

        if (RecordLyricText is not null &&
            RecordLyricMarqueeCanvas is not null &&
            RecordLyricTextPrimary is not null &&
            RecordLyricTextClone is not null)
        {
            ResetMarqueeElements(
                RecordLyricText,
                RecordLyricMarqueeCanvas,
                RecordLyricTextPrimary,
                RecordLyricTextClone);
        }

        if (RecordHorizontalLyricText is not null &&
            RecordHorizontalLyricMarqueeCanvas is not null &&
            RecordHorizontalLyricTextPrimary is not null &&
            RecordHorizontalLyricTextClone is not null)
        {
            ResetMarqueeElements(
                RecordHorizontalLyricText,
                RecordHorizontalLyricMarqueeCanvas,
                RecordHorizontalLyricTextPrimary,
                RecordHorizontalLyricTextClone);
        }
    }

    private static bool AreSystemAnimationsEnabled()
    {
        return WindowsCompatibilityService.ShouldAnimateContinuousMedia;
    }

    private static bool TextMarqueeAnimationsEnabled()
    {
        return Application.Current is not App app ||
            app.SettingsService is null ||
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)
                .AllowTextMarqueeAnimations;
    }

    private static bool VinylRotationAnimationsEnabled()
    {
        return Application.Current is not App app ||
            app.SettingsService is null ||
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)
                .AllowVinylRotationAnimations;
    }

    private double MeasureTitleWidth(
        (Grid Host, RectangleGeometry Clip, TextBlock Static, Grid Canvas, TextBlock Primary, TextBlock Clone, TextBlock Measure) elements)
    {
        return MeasureTextBlockWidth(elements.Measure, elements.Primary, ViewModel?.Title);
    }

    private static double MeasureTextBlockWidth(TextBlock measureText, TextBlock fallbackPrimary, string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        measureText.Text = content;
        measureText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredWidth = measureText.DesiredSize.Width;
        if (double.IsFinite(desiredWidth) && desiredWidth > 0)
        {
            return Math.Ceiling(desiredWidth);
        }

        double actualWidth = fallbackPrimary.ActualWidth;
        return double.IsFinite(actualWidth) ? Math.Ceiling(actualWidth) : 0;
    }

    private bool TryGetActiveTitleMarqueeElements(
        out (Grid Host, RectangleGeometry Clip, TextBlock Static, Grid Canvas, TextBlock Primary, TextBlock Clone, TextBlock Measure) elements)
    {
        if (_isMinimalLayout)
        {
            if (MinimalTitleMarqueeHost is null ||
                MinimalTitleMarqueeClip is null ||
                MinimalTitleStaticText is null ||
                MinimalTitleMarqueeCanvas is null ||
                MinimalTitleTextPrimary is null ||
                MinimalTitleTextClone is null ||
                MinimalTitleMeasureText is null)
            {
                elements = default;
                return false;
            }

            elements = (MinimalTitleMarqueeHost, MinimalTitleMarqueeClip, MinimalTitleStaticText, MinimalTitleMarqueeCanvas,
                MinimalTitleTextPrimary, MinimalTitleTextClone, MinimalTitleMeasureText);
            return true;
        }

        if (_isRecordLayout ||
            _isRecordHorizontalLayout ||
            TitleMarqueeHost is null ||
            TitleMarqueeClip is null ||
            TitleStaticText is null ||
            TitleMarqueeCanvas is null ||
            TitleTextPrimary is null ||
            TitleTextClone is null ||
            TitleMeasureText is null)
        {
            elements = default;
            return false;
        }

        elements = (TitleMarqueeHost, TitleMarqueeClip, TitleStaticText, TitleMarqueeCanvas,
            TitleTextPrimary, TitleTextClone, TitleMeasureText);
        return true;
    }

    private bool TryGetActiveLyricMarqueeElements(
        out (Grid Host, RectangleGeometry Clip, TextBlock Static, Grid Canvas, TextBlock Primary, TextBlock Clone, TextBlock Measure) elements,
        out string? lyricText)
    {
        lyricText = null;
        if (ViewModel is null)
        {
            elements = default;
            return false;
        }

        if (_isMinimalLayout)
        {
            if (ViewModel.InlineLyricVisibility != Visibility.Visible ||
                MinimalLyricMarqueeHost is null ||
                MinimalLyricMarqueeClip is null ||
                MinimalLyricText is null ||
                MinimalLyricMarqueeCanvas is null ||
                MinimalLyricTextPrimary is null ||
                MinimalLyricTextClone is null ||
                MinimalLyricMeasureText is null)
            {
                elements = default;
                return false;
            }

            lyricText = ViewModel.InlineLyricText;
            elements = (MinimalLyricMarqueeHost, MinimalLyricMarqueeClip, MinimalLyricText,
                MinimalLyricMarqueeCanvas, MinimalLyricTextPrimary, MinimalLyricTextClone, MinimalLyricMeasureText);
            return true;
        }

        if (_isRecordLayout)
        {
            if (ViewModel.InlineLyricVisibility != Visibility.Visible ||
                RecordLyricMarqueeHost is null ||
                RecordLyricMarqueeClip is null ||
                RecordLyricText is null ||
                RecordLyricMarqueeCanvas is null ||
                RecordLyricTextPrimary is null ||
                RecordLyricTextClone is null ||
                RecordLyricMeasureText is null)
            {
                elements = default;
                return false;
            }

            lyricText = ViewModel.InlineLyricText;
            elements = (RecordLyricMarqueeHost, RecordLyricMarqueeClip, RecordLyricText,
                RecordLyricMarqueeCanvas, RecordLyricTextPrimary, RecordLyricTextClone, RecordLyricMeasureText);
            return true;
        }

        if (_isRecordHorizontalLayout)
        {
            if (ViewModel.InlineLyricVisibility != Visibility.Visible ||
                RecordHorizontalLyricMarqueeHost is null ||
                RecordHorizontalLyricMarqueeClip is null ||
                RecordHorizontalLyricText is null ||
                RecordHorizontalLyricMarqueeCanvas is null ||
                RecordHorizontalLyricTextPrimary is null ||
                RecordHorizontalLyricTextClone is null ||
                RecordHorizontalLyricMeasureText is null)
            {
                elements = default;
                return false;
            }

            lyricText = ViewModel.InlineLyricText;
            elements = (RecordHorizontalLyricMarqueeHost, RecordHorizontalLyricMarqueeClip, RecordHorizontalLyricText,
                RecordHorizontalLyricMarqueeCanvas, RecordHorizontalLyricTextPrimary, RecordHorizontalLyricTextClone, RecordHorizontalLyricMeasureText);
            return true;
        }

        if (ViewModel.SubtitleVisibility != Visibility.Visible ||
            ControlsLyricMarqueeHost is null ||
            ControlsLyricMarqueeClip is null ||
            SubtitleTextBlock is null ||
            ControlsLyricMarqueeCanvas is null ||
            ControlsLyricTextPrimary is null ||
            ControlsLyricTextClone is null ||
            ControlsLyricMeasureText is null)
        {
            elements = default;
            return false;
        }

        lyricText = ViewModel.SubtitleText;
        elements = (ControlsLyricMarqueeHost, ControlsLyricMarqueeClip, SubtitleTextBlock,
            ControlsLyricMarqueeCanvas, ControlsLyricTextPrimary, ControlsLyricTextClone, ControlsLyricMeasureText);
        return true;
    }

    private static void ResetMarqueeElements(
        TextBlock staticText,
        Grid canvas,
        TextBlock primary,
        TextBlock clone)
    {
        primary.ClearValue(WidthProperty);
        clone.ClearValue(WidthProperty);
        clone.Margin = default;
        clone.Visibility = Visibility.Visible;
        canvas.ClearValue(WidthProperty);
        staticText.Opacity = 1;
        canvas.Visibility = Visibility.Collapsed;
        canvas.Translation = Vector3.Zero;
    }

    private void UpdateSeekFromPointer(Grid host, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || host.ActualWidth <= 0)
        {
            return;
        }

        double x = e.GetCurrentPoint(host).Position.X;
        double ratio = Math.Clamp(x / host.ActualWidth, 0.0, 1.0);
        ViewModel.SeekValue = ratio * ViewModel.SeekMaximum;
        UpdateProgressVisuals();
    }

    private void UpdateProgressVisuals()
    {
        if (_isHostCompactCollapsed && !_isProgressDragging)
        {
            return;
        }

        if (ViewModel is null)
        {
            if (ProgressFillScale is not null)
            {
                ProgressFillScale.ScaleX = 0;
            }

            if (ProgressThumb is not null)
            {
                ProgressThumb.Opacity = 0;
            }

            if (MinimalProgressScale is not null)
            {
                MinimalProgressScale.ScaleX = 0;
            }

            if (RecordProgressFillScale is not null)
            {
                RecordProgressFillScale.ScaleX = 0;
            }

            if (RecordProgressThumb is not null)
            {
                RecordProgressThumb.Opacity = 0;
            }

            if (RecordHorizontalProgressFillScale is not null)
            {
                RecordHorizontalProgressFillScale.ScaleX = 0;
            }

            if (RecordHorizontalProgressThumb is not null)
            {
                RecordHorizontalProgressThumb.Opacity = 0;
            }

            return;
        }

        double maximum = Math.Max(1, ViewModel.SeekMaximum);
        double ratio = Math.Clamp(ViewModel.SeekValue / maximum, 0.0, 1.0);
        double spectrumRatio = ViewModel.HasSeekableTimeline ? ratio : 0.0;
        MinimalSpectrumBar?.SetProgressRatio(spectrumRatio);
        ControlsSpectrumBar?.SetProgressRatio(spectrumRatio);
        RecordSpectrumBar?.SetProgressRatio(spectrumRatio);
        RecordHorizontalSpectrumBar?.SetProgressRatio(spectrumRatio);
        bool canInteract = ViewModel.CanInteractWithProgress;
        double trackOpacity = ViewModel.HasSeekableTimeline ? 0.34 : 0.2;

        if (ProgressHost is not null &&
            ProgressTrack is not null &&
            ProgressFillScale is not null &&
            ProgressThumb is not null)
        {
            UpdateSingleProgressBar(
                ProgressHost,
                ProgressTrack,
                ProgressFillScale,
                ProgressThumb,
                ratio,
                canInteract,
                trackOpacity);
        }

        if (MinimalProgressScale is not null)
        {
            MinimalProgressScale.ScaleX = ViewModel.HasSeekableTimeline ? ratio : 0;
        }

        if (RecordProgressHost is not null &&
            RecordProgressTrack is not null &&
            RecordProgressFillScale is not null &&
            RecordProgressThumb is not null)
        {
            UpdateSingleProgressBar(
                RecordProgressHost,
                RecordProgressTrack,
                RecordProgressFillScale,
                RecordProgressThumb,
                ratio,
                canInteract,
                trackOpacity);
        }

        if (RecordHorizontalProgressHost is not null &&
            RecordHorizontalProgressTrack is not null &&
            RecordHorizontalProgressFillScale is not null &&
            RecordHorizontalProgressThumb is not null)
        {
            UpdateSingleProgressBar(
                RecordHorizontalProgressHost,
                RecordHorizontalProgressTrack,
                RecordHorizontalProgressFillScale,
                RecordHorizontalProgressThumb,
                ratio,
                canInteract,
                trackOpacity);
        }
    }

    private void UpdateSingleProgressBar(
        Grid host,
        Border track,
        ScaleTransform fillScale,
        UIElement thumb,
        double ratio,
        bool canInteract,
        double trackOpacity)
    {
        if (host.ActualWidth > 0)
        {
            fillScale.ScaleX = ratio;
            float thumbX = (float)Math.Max(0, host.ActualWidth * ratio);
            thumb.Translation = new Vector3(thumbX - 5f, 0f, 0f);
            bool activeHost = ReferenceEquals(_hoveredProgressHost, host) ||
                              (_isProgressDragging && ReferenceEquals(_activeProgressDragHost, host));
            thumb.Opacity = canInteract && activeHost ? 1 : 0;
            track.Opacity = trackOpacity;
        }
        else
        {
            fillScale.ScaleX = 0;
            thumb.Opacity = 0;
        }
    }
}
