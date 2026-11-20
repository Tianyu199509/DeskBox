using System.Numerics;
using DeskBox.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class MusicWidgetContent
{
    internal const double OverflowMarqueeTolerance = 3.0;
    private const int TitleMarqueeDeferredMeasureMs = 110;
    private const int ArtistMarqueeDeferredMeasureMs = 130;
    private const int LyricMarqueeDeferredMeasureMs = 90;

    private readonly struct MarqueeSlotElements
    {
        public MarqueeSlotElements(
            Grid Host,
            RectangleGeometry Clip,
            TextBlock Static,
            Grid Canvas,
            TextBlock Primary,
            TextBlock? Clone,
            TextBlock Measure)
        {
            this.Host = Host;
            this.Clip = Clip;
            this.Static = Static;
            this.Canvas = Canvas;
            this.Primary = Primary;
            this.Clone = Clone;
            this.Measure = Measure;
        }

        public Grid Host { get; }
        public RectangleGeometry Clip { get; }
        public TextBlock Static { get; }
        public Grid Canvas { get; }
        public TextBlock Primary { get; }
        public TextBlock? Clone { get; }
        public TextBlock Measure { get; }
    }

    private ScalarKeyFrameAnimation? _titleMarqueeAnimation;
    private Grid? _titleMarqueeAnimatedCanvas;
    private int _titleMarqueeMeasureVersion;

    private ScalarKeyFrameAnimation? _artistMarqueeAnimation;
    private Grid? _artistMarqueeAnimatedCanvas;
    private int _artistMarqueeMeasureVersion;

    private ScalarKeyFrameAnimation? _lyricMarqueeAnimation;
    private Grid? _lyricMarqueeAnimatedCanvas;
    private int _lyricMarqueeMeasureVersion;

    private void QueueAllTextMarquees()
    {
        QueueTitleMarqueeUpdate();
        QueueArtistMarqueeUpdate();
        QueueLyricMarqueeUpdate();
    }

    private void StopAllTextMarquees()
    {
        StopTitleMarquee();
        StopArtistMarquee();
        StopLyricMarquee();
    }

    private void InvalidateAllTextMarqueeVersions()
    {
        ++_titleMarqueeMeasureVersion;
        ++_artistMarqueeMeasureVersion;
        ++_lyricMarqueeMeasureVersion;
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
        else if (RecordTitleMarqueeHost is not null && ReferenceEquals(sender, RecordTitleMarqueeHost))
        {
            clip = RecordTitleMarqueeClip;
        }
        else if (RecordHorizontalTitleMarqueeHost is not null && ReferenceEquals(sender, RecordHorizontalTitleMarqueeHost))
        {
            clip = RecordHorizontalTitleMarqueeClip;
        }

        if (clip is not null && e.NewSize.Width > 0)
        {
            clip.Rect = CreateHorizontalViewportClipRect(e.NewSize.Width, e.NewSize.Height);
        }

        QueueTitleMarqueeUpdate();
    }

    private void ArtistMarqueeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RectangleGeometry? clip = null;
        if (ControlsArtistMarqueeHost is not null && ReferenceEquals(sender, ControlsArtistMarqueeHost))
        {
            clip = ControlsArtistMarqueeClip;
        }
        else if (RecordArtistMarqueeHost is not null && ReferenceEquals(sender, RecordArtistMarqueeHost))
        {
            clip = RecordArtistMarqueeClip;
        }
        else if (RecordHorizontalArtistMarqueeHost is not null && ReferenceEquals(sender, RecordHorizontalArtistMarqueeHost))
        {
            clip = RecordHorizontalArtistMarqueeClip;
        }

        if (clip is not null && e.NewSize.Width > 0)
        {
            clip.Rect = CreateHorizontalViewportClipRect(e.NewSize.Width, e.NewSize.Height);
        }

        QueueArtistMarqueeUpdate();
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

    private bool CanRunInlineTextMarquee() =>
        IsLoaded &&
        _isHostWindowVisible &&
        !_isHostCompactCollapsed &&
        ViewModel?.IsPlaying == true &&
        ViewModel.LyricsOverlayVisibility != Visibility.Visible &&
        TextMarqueeAnimationsEnabled() &&
        AreSystemAnimationsEnabled();

    private void QueueTitleMarqueeUpdate()
    {
        if (!CanRunInlineTextMarquee())
        {
            StopTitleMarquee();
            return;
        }

        int version = ++_titleMarqueeMeasureVersion;
        StopTitleMarquee();
        _ = RunDeferredTitleMarqueeUpdateAsync(version);
    }

    private async Task RunDeferredTitleMarqueeUpdateAsync(int version)
    {
        await Task.Delay(TitleMarqueeDeferredMeasureMs);
        if (version != _titleMarqueeMeasureVersion || !CanRunInlineTextMarquee())
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => UpdateTitleMarquee(version));
    }

    private void UpdateTitleMarquee(int version)
    {
        if (version != _titleMarqueeMeasureVersion || !CanRunInlineTextMarquee())
        {
            return;
        }

        if (!TryGetActiveTitleMarqueeElements(out MarqueeSlotElements elements))
        {
            StopTitleMarquee();
            return;
        }

        string? titleText = ViewModel?.Title;
        if (!TryStartOverflowGlideAnimation(elements, titleText, lineDurationSeconds: 0, out ScalarKeyFrameAnimation? animation))
        {
            StopTitleMarquee();
            return;
        }

        _titleMarqueeAnimation?.Dispose();
        _titleMarqueeAnimation = animation;
        _titleMarqueeAnimatedCanvas = elements.Canvas;
    }

    private void StopTitleMarquee()
    {
        StopAnimatedCanvas(ref _titleMarqueeAnimatedCanvas, ref _titleMarqueeAnimation);
        ResetMarqueeSlotIfLoaded(TitleStaticText, TitleMarqueeCanvas, TitleTextPrimary, TitleTextClone);
        ResetMarqueeSlotIfLoaded(MinimalTitleStaticText, MinimalTitleMarqueeCanvas, MinimalTitleTextPrimary, MinimalTitleTextClone);
        ResetMarqueeSlotIfLoaded(RecordTitleText, RecordTitleMarqueeCanvas, RecordTitleTextPrimary, clone: null);
        ResetMarqueeSlotIfLoaded(RecordHorizontalTitleText, RecordHorizontalTitleMarqueeCanvas, RecordHorizontalTitleTextPrimary, clone: null);
    }

    private void QueueArtistMarqueeUpdate()
    {
        if (!CanRunInlineTextMarquee())
        {
            StopArtistMarquee();
            return;
        }

        int version = ++_artistMarqueeMeasureVersion;
        StopArtistMarquee();
        _ = RunDeferredArtistMarqueeUpdateAsync(version);
    }

    private async Task RunDeferredArtistMarqueeUpdateAsync(int version)
    {
        await Task.Delay(ArtistMarqueeDeferredMeasureMs);
        if (version != _artistMarqueeMeasureVersion || !CanRunInlineTextMarquee())
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => UpdateArtistMarquee(version));
    }

    private void UpdateArtistMarquee(int version)
    {
        if (version != _artistMarqueeMeasureVersion || !CanRunInlineTextMarquee())
        {
            return;
        }

        if (!TryGetActiveArtistMarqueeElements(out MarqueeSlotElements elements, out string? artistText))
        {
            StopArtistMarquee();
            return;
        }

        if (!TryStartOverflowGlideAnimation(elements, artistText, lineDurationSeconds: 0, out ScalarKeyFrameAnimation? animation))
        {
            StopArtistMarquee();
            return;
        }

        _artistMarqueeAnimation?.Dispose();
        _artistMarqueeAnimation = animation;
        _artistMarqueeAnimatedCanvas = elements.Canvas;
    }

    private void StopArtistMarquee()
    {
        StopAnimatedCanvas(ref _artistMarqueeAnimatedCanvas, ref _artistMarqueeAnimation);
        ResetMarqueeSlotIfLoaded(ControlsArtistText, ControlsArtistMarqueeCanvas, ControlsArtistTextPrimary, clone: null);
        ResetMarqueeSlotIfLoaded(RecordArtistText, RecordArtistMarqueeCanvas, RecordArtistTextPrimary, clone: null);
        ResetMarqueeSlotIfLoaded(RecordHorizontalArtistText, RecordHorizontalArtistMarqueeCanvas, RecordHorizontalArtistTextPrimary, clone: null);
    }

    private void QueueLyricMarqueeUpdate()
    {
        if (!CanRunInlineTextMarquee())
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
        if (version != _lyricMarqueeMeasureVersion || !CanRunInlineTextMarquee())
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => UpdateLyricMarquee(version));
    }

    private void UpdateLyricMarquee(int version)
    {
        if (version != _lyricMarqueeMeasureVersion || !CanRunInlineTextMarquee())
        {
            return;
        }

        if (!TryGetActiveLyricMarqueeElements(out MarqueeSlotElements elements, out string? lyricText))
        {
            StopLyricMarquee();
            return;
        }

        double lineDuration = ViewModel?.CurrentLyricDurationSeconds ?? 0;
        if (!TryStartOverflowGlideAnimation(elements, lyricText, lineDuration, out ScalarKeyFrameAnimation? animation))
        {
            StopLyricMarquee();
            return;
        }

        _lyricMarqueeAnimation?.Dispose();
        _lyricMarqueeAnimation = animation;
        _lyricMarqueeAnimatedCanvas = elements.Canvas;
    }

    private void StopLyricMarquee()
    {
        StopAnimatedCanvas(ref _lyricMarqueeAnimatedCanvas, ref _lyricMarqueeAnimation);
        ResetMarqueeSlotIfLoaded(SubtitleTextBlock, ControlsLyricMarqueeCanvas, ControlsLyricTextPrimary, ControlsLyricTextClone);
        ResetMarqueeSlotIfLoaded(MinimalLyricText, MinimalLyricMarqueeCanvas, MinimalLyricTextPrimary, MinimalLyricTextClone);
        ResetMarqueeSlotIfLoaded(RecordLyricText, RecordLyricMarqueeCanvas, RecordLyricTextPrimary, RecordLyricTextClone);
        ResetMarqueeSlotIfLoaded(RecordHorizontalLyricText, RecordHorizontalLyricMarqueeCanvas, RecordHorizontalLyricTextPrimary, RecordHorizontalLyricTextClone);
    }

    private static bool TryStartOverflowGlideAnimation(
        MarqueeSlotElements elements,
        string? content,
        double lineDurationSeconds,
        out ScalarKeyFrameAnimation? startedAnimation)
    {
        startedAnimation = null;
        double viewportWidth = elements.Host.ActualWidth;
        if (viewportWidth <= 0 || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        elements.Clip.Rect = CreateHorizontalViewportClipRect(viewportWidth, elements.Host.ActualHeight);
        elements.Measure.FontSize = elements.Static.FontSize;
        elements.Primary.FontSize = elements.Static.FontSize;
        if (elements.Clone is not null)
        {
            elements.Clone.FontSize = elements.Static.FontSize;
        }

        double textWidth = MeasureTextBlockWidth(elements.Measure, elements.Primary, content);
        if (textWidth <= viewportWidth + OverflowMarqueeTolerance)
        {
            return false;
        }

        double overflow = textWidth - viewportWidth;
        double travel = overflow + 2.0;

        elements.Primary.Text = content;
        elements.Primary.Width = textWidth + 2.0;
        if (elements.Clone is not null)
        {
            elements.Clone.Visibility = Visibility.Collapsed;
            elements.Clone.Margin = default;
        }

        elements.Canvas.Width = textWidth + 2.0;
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

        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();

        if (lineDurationSeconds > 0.8 && lineDurationSeconds < 18.0)
        {
            double startHoldSec = Math.Clamp(lineDurationSeconds * 0.18, 0.30, 0.65);
            double endHoldSec = Math.Clamp(lineDurationSeconds * 0.18, 0.32, 0.80);
            double idealGlideSec = Math.Clamp(0.90 + (travel / 68.0), 1.05, 4.40);
            double windowSec = Math.Max(0.68, lineDurationSeconds - startHoldSec - endHoldSec);
            double glideSec = Math.Clamp(Math.Min(idealGlideSec, windowSec), 0.68, 4.60);
            double tailHoldSec = Math.Max(endHoldSec, lineDurationSeconds - startHoldSec - glideSec + 1.5);
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

        visual.StartAnimation("Translation.X", animation);
        startedAnimation = animation;
        return true;
    }

    private static void StopAnimatedCanvas(
        ref Grid? animatedCanvasSlot,
        ref ScalarKeyFrameAnimation? animationSlot)
    {
        if (animatedCanvasSlot is { } animatedCanvas)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(animatedCanvas, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(animatedCanvas);
            visual.StopAnimation("Translation.X");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            animatedCanvas.Translation = Vector3.Zero;
        }

        animationSlot?.Dispose();
        animationSlot = null;
        animatedCanvasSlot = null;
    }

    private static void ResetMarqueeSlotIfLoaded(
        TextBlock? staticText,
        Grid? canvas,
        TextBlock? primary,
        TextBlock? clone)
    {
        if (staticText is null || canvas is null || primary is null)
        {
            return;
        }

        staticText.Opacity = 1;
        canvas.Visibility = Visibility.Collapsed;
        canvas.Width = double.NaN;
        canvas.Translation = Vector3.Zero;
        primary.Width = double.NaN;
        if (clone is not null)
        {
            clone.Width = double.NaN;
            clone.Margin = default;
            clone.Visibility = Visibility.Collapsed;
        }
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

    private bool TryGetActiveTitleMarqueeElements(out MarqueeSlotElements elements)
    {
        if (_isMinimalLayout)
        {
            if (MinimalTitleMarqueeHost is null ||
                MinimalTitleMarqueeClip is null ||
                MinimalTitleStaticText is null ||
                MinimalTitleMarqueeCanvas is null ||
                MinimalTitleTextPrimary is null ||
                MinimalTitleMeasureText is null)
            {
                elements = default;
                return false;
            }

            elements = new MarqueeSlotElements(
                MinimalTitleMarqueeHost,
                MinimalTitleMarqueeClip,
                MinimalTitleStaticText,
                MinimalTitleMarqueeCanvas,
                MinimalTitleTextPrimary,
                MinimalTitleTextClone,
                MinimalTitleMeasureText);
            return true;
        }

        if (_isRecordLayout)
        {
            if (RecordTitleMarqueeHost is null ||
                RecordTitleMarqueeClip is null ||
                RecordTitleText is null ||
                RecordTitleMarqueeCanvas is null ||
                RecordTitleTextPrimary is null ||
                RecordTitleMeasureText is null)
            {
                elements = default;
                return false;
            }

            elements = new MarqueeSlotElements(
                RecordTitleMarqueeHost,
                RecordTitleMarqueeClip,
                RecordTitleText,
                RecordTitleMarqueeCanvas,
                RecordTitleTextPrimary,
                Clone: null,
                RecordTitleMeasureText);
            return true;
        }

        if (_isRecordHorizontalLayout)
        {
            if (RecordHorizontalTitleMarqueeHost is null ||
                RecordHorizontalTitleMarqueeClip is null ||
                RecordHorizontalTitleText is null ||
                RecordHorizontalTitleMarqueeCanvas is null ||
                RecordHorizontalTitleTextPrimary is null ||
                RecordHorizontalTitleMeasureText is null)
            {
                elements = default;
                return false;
            }

            elements = new MarqueeSlotElements(
                RecordHorizontalTitleMarqueeHost,
                RecordHorizontalTitleMarqueeClip,
                RecordHorizontalTitleText,
                RecordHorizontalTitleMarqueeCanvas,
                RecordHorizontalTitleTextPrimary,
                Clone: null,
                RecordHorizontalTitleMeasureText);
            return true;
        }

        if (TitleMarqueeHost is null ||
            TitleMarqueeClip is null ||
            TitleStaticText is null ||
            TitleMarqueeCanvas is null ||
            TitleTextPrimary is null ||
            TitleMeasureText is null)
        {
            elements = default;
            return false;
        }

        elements = new MarqueeSlotElements(
            TitleMarqueeHost,
            TitleMarqueeClip,
            TitleStaticText,
            TitleMarqueeCanvas,
            TitleTextPrimary,
            TitleTextClone,
            TitleMeasureText);
        return true;
    }

    private bool TryGetActiveArtistMarqueeElements(
        out MarqueeSlotElements elements,
        out string? artistText)
    {
        artistText = null;
        if (ViewModel is null || _isMinimalLayout)
        {
            elements = default;
            return false;
        }

        if (_isRecordLayout)
        {
            if (RecordArtistMarqueeHost is null ||
                RecordArtistMarqueeClip is null ||
                RecordArtistText is null ||
                RecordArtistMarqueeCanvas is null ||
                RecordArtistTextPrimary is null ||
                RecordArtistMeasureText is null)
            {
                elements = default;
                return false;
            }

            artistText = ViewModel.Artist;
            elements = new MarqueeSlotElements(
                RecordArtistMarqueeHost,
                RecordArtistMarqueeClip,
                RecordArtistText,
                RecordArtistMarqueeCanvas,
                RecordArtistTextPrimary,
                Clone: null,
                RecordArtistMeasureText);
            return true;
        }

        if (_isRecordHorizontalLayout)
        {
            if (RecordHorizontalArtistMarqueeHost is null ||
                RecordHorizontalArtistMarqueeClip is null ||
                RecordHorizontalArtistText is null ||
                RecordHorizontalArtistMarqueeCanvas is null ||
                RecordHorizontalArtistTextPrimary is null ||
                RecordHorizontalArtistMeasureText is null)
            {
                elements = default;
                return false;
            }

            artistText = ViewModel.Artist;
            elements = new MarqueeSlotElements(
                RecordHorizontalArtistMarqueeHost,
                RecordHorizontalArtistMarqueeClip,
                RecordHorizontalArtistText,
                RecordHorizontalArtistMarqueeCanvas,
                RecordHorizontalArtistTextPrimary,
                Clone: null,
                RecordHorizontalArtistMeasureText);
            return true;
        }

        if (ViewModel.ControlsArtistVisibility != Visibility.Visible ||
            ControlsArtistMarqueeHost is null ||
            ControlsArtistMarqueeClip is null ||
            ControlsArtistText is null ||
            ControlsArtistMarqueeCanvas is null ||
            ControlsArtistTextPrimary is null ||
            ControlsArtistMeasureText is null)
        {
            elements = default;
            return false;
        }

        artistText = ViewModel.ControlsArtistText;
        elements = new MarqueeSlotElements(
            ControlsArtistMarqueeHost,
            ControlsArtistMarqueeClip,
            ControlsArtistText,
            ControlsArtistMarqueeCanvas,
            ControlsArtistTextPrimary,
            Clone: null,
            ControlsArtistMeasureText);
        return true;
    }

    private bool TryGetActiveLyricMarqueeElements(
        out MarqueeSlotElements elements,
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
                MinimalLyricMeasureText is null)
            {
                elements = default;
                return false;
            }

            lyricText = ViewModel.InlineLyricText;
            elements = new MarqueeSlotElements(
                MinimalLyricMarqueeHost,
                MinimalLyricMarqueeClip,
                MinimalLyricText,
                MinimalLyricMarqueeCanvas,
                MinimalLyricTextPrimary,
                MinimalLyricTextClone,
                MinimalLyricMeasureText);
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
                RecordLyricMeasureText is null)
            {
                elements = default;
                return false;
            }

            lyricText = ViewModel.InlineLyricText;
            elements = new MarqueeSlotElements(
                RecordLyricMarqueeHost,
                RecordLyricMarqueeClip,
                RecordLyricText,
                RecordLyricMarqueeCanvas,
                RecordLyricTextPrimary,
                RecordLyricTextClone,
                RecordLyricMeasureText);
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
                RecordHorizontalLyricMeasureText is null)
            {
                elements = default;
                return false;
            }

            lyricText = ViewModel.InlineLyricText;
            elements = new MarqueeSlotElements(
                RecordHorizontalLyricMarqueeHost,
                RecordHorizontalLyricMarqueeClip,
                RecordHorizontalLyricText,
                RecordHorizontalLyricMarqueeCanvas,
                RecordHorizontalLyricTextPrimary,
                RecordHorizontalLyricTextClone,
                RecordHorizontalLyricMeasureText);
            return true;
        }

        if (ViewModel.SubtitleVisibility != Visibility.Visible ||
            ControlsLyricMarqueeHost is null ||
            ControlsLyricMarqueeClip is null ||
            SubtitleTextBlock is null ||
            ControlsLyricMarqueeCanvas is null ||
            ControlsLyricTextPrimary is null ||
            ControlsLyricMeasureText is null)
        {
            elements = default;
            return false;
        }

        lyricText = ViewModel.SubtitleText;
        elements = new MarqueeSlotElements(
            ControlsLyricMarqueeHost,
            ControlsLyricMarqueeClip,
            SubtitleTextBlock,
            ControlsLyricMarqueeCanvas,
            ControlsLyricTextPrimary,
            ControlsLyricTextClone,
            ControlsLyricMeasureText);
        return true;
    }
}
