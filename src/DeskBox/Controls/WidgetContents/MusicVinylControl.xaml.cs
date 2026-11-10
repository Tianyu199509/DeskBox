using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class MusicVinylControl : UserControl
{
    public const double SecondsPerRevolution = 12.0;
    private const float LabelCenterCoordinate = 48f;

    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(
            nameof(ImageSource),
            typeof(ImageSource),
            typeof(MusicVinylControl),
            new PropertyMetadata(null, OnImageSourceChanged));

    private bool _isRotating;
    private float _baseAngleDegrees;
    private long _rotationStartedTimestamp;
    private ScalarKeyFrameAnimation? _rotationAnimation;
    private LinearEasingFunction? _rotationEasing;

    public MusicVinylControl()
    {
        InitializeComponent();
        Unloaded += MusicVinylControl_Unloaded;
    }

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public bool IsRotating => _isRotating;

    public void SetRotating(bool shouldRotate)
    {
        if (_isRotating == shouldRotate)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(RotatingLabelHost);
        visual.CenterPoint = new Vector3(LabelCenterCoordinate, LabelCenterCoordinate, 0f);

        if (shouldRotate)
        {
            _isRotating = true;
            _rotationStartedTimestamp = Stopwatch.GetTimestamp();

            _rotationAnimation?.Dispose();
            _rotationAnimation = null;

            Compositor compositor = visual.Compositor;
            _rotationEasing ??= compositor.CreateLinearEasingFunction();
            ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, _baseAngleDegrees);
            animation.InsertKeyFrame(1f, _baseAngleDegrees + 360f, _rotationEasing);
            animation.Duration = TimeSpan.FromSeconds(SecondsPerRevolution);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            _rotationAnimation = animation;
            visual.StartAnimation("RotationAngleInDegrees", animation);
        }
        else
        {
            _isRotating = false;
            if (_rotationStartedTimestamp != 0)
            {
                double elapsedSeconds = Stopwatch.GetElapsedTime(_rotationStartedTimestamp).TotalSeconds;
                _baseAngleDegrees = NormalizeAngle(
                    _baseAngleDegrees + (float)((elapsedSeconds / SecondsPerRevolution) * 360.0));
                _rotationStartedTimestamp = 0;
            }

            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = _baseAngleDegrees;
            _rotationAnimation?.Dispose();
            _rotationAnimation = null;
        }
    }

    public void ReleaseVisualResources()
    {
        Unloaded -= MusicVinylControl_Unloaded;
        SetRotating(false);
        _rotationAnimation?.Dispose();
        _rotationAnimation = null;
        _rotationEasing?.Dispose();
        _rotationEasing = null;
        ClearValue(ImageSourceProperty);
        ApplyImageSource(null);
    }

    private void MusicVinylControl_Unloaded(object sender, RoutedEventArgs e)
    {
        SetRotating(false);
    }

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MusicVinylControl control)
        {
            control.ApplyImageSource(e.NewValue as ImageSource);
        }
    }

    private void ApplyImageSource(ImageSource? source)
    {
        CoverBrush.ImageSource = source;
        bool hasCover = source is not null;
        CoverEllipse.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
        CoverRim.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderLabel.Visibility = hasCover ? Visibility.Collapsed : Visibility.Visible;
    }

    private static float NormalizeAngle(float degrees)
    {
        float remainder = degrees % 360f;
        return remainder < 0f ? remainder + 360f : remainder;
    }
}
