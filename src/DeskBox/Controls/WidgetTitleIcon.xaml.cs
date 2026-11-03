using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Controls;

public sealed partial class WidgetTitleIcon : UserControl
{
    private const double DesktopTitleIconScale = 1.3d;
    [ThreadStatic]
    private static WidgetTitleIconResources? s_sharedMonoResources;
    [ThreadStatic]
    private static Dictionary<string, SvgImageSource>? s_sharedColorSvgSources;
    private string? _currentColorAssetKey;
    private XamlPath? _activeMonoIconPath;
    private readonly Dictionary<string, XamlPath> _monoIconPaths = new(StringComparer.Ordinal);
    private SolidColorBrush? _transparentBrush;
    private SolidColorBrush? _accentBrush;
    private double? _surfaceCornerRadiusOverride;
    private bool _isCompactPresentation;

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph),
            typeof(string),
            typeof(WidgetTitleIcon),
            new PropertyMetadata("\uE8A5", OnAppearancePropertyChanged));

    public static readonly DependencyProperty IconKindProperty =
        DependencyProperty.Register(
            nameof(IconKind),
            typeof(string),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(WidgetTitleIconKindNames.Default, OnAppearancePropertyChanged));

    public static readonly DependencyProperty LabelTextProperty =
        DependencyProperty.Register(
            nameof(LabelText),
            typeof(string),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(string.Empty, OnAppearancePropertyChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(string),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(WidgetTitleIconModeNames.Color, OnAppearancePropertyChanged));

    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(AccentColorHelper.DefaultAccentColor, OnAppearancePropertyChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(14d, OnAppearancePropertyChanged));

    public static readonly DependencyProperty CustomEmojiProperty =
        DependencyProperty.Register(
            nameof(CustomEmoji),
            typeof(string),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(string.Empty, OnAppearancePropertyChanged));

    public static readonly DependencyProperty CustomImageSourceProperty =
        DependencyProperty.Register(
            nameof(CustomImageSource),
            typeof(ImageSource),
            typeof(WidgetTitleIcon),
            new PropertyMetadata(null, OnAppearancePropertyChanged));

    public WidgetTitleIcon()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateVisualState();
        Loaded += (_, _) => UpdateVisualState();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string IconKind
    {
        get => (string)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string LabelText
    {
        get => (string)GetValue(LabelTextProperty);
        set => SetValue(LabelTextProperty, value);
    }

    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>
    /// Per-widget emoji override. When non-empty it wins over every icon mode
    /// except <see cref="WidgetTitleIconMode.Hidden"/>, which stays a global
    /// off switch for the whole title icon.
    /// </summary>
    public string CustomEmoji
    {
        get => (string)GetValue(CustomEmojiProperty);
        set => SetValue(CustomEmojiProperty, value);
    }

    /// <summary>Per-widget image override; empty emoji takes precedence when both are set.</summary>
    public ImageSource? CustomImageSource
    {
        get => (ImageSource?)GetValue(CustomImageSourceProperty);
        set => SetValue(CustomImageSourceProperty, value);
    }

    public TextBlock TextLabelElement => LabelElement;

    public void SetSurfaceCornerRadiusOverride(double? radius)
    {
        _surfaceCornerRadiusOverride = radius is { } value
            ? Math.Max(0, value)
            : null;
        ApplySurfaceCornerRadiusOverride();
    }

    public void SetCompactPresentationMode(bool isCompact)
    {
        if (_isCompactPresentation == isCompact)
        {
            return;
        }

        _isCompactPresentation = isCompact;
        UpdateVisualState();
    }

    private static void OnAppearancePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetTitleIcon titleIcon)
        {
            titleIcon.UpdateVisualState();
        }
    }

    private void UpdateVisualState()
    {
        try
        {
            ApplyVisualState();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetTitleIcon] Failed to update visual state: {ex}");
            ApplyLegacyGlyphFallback();
        }
    }

    private void ApplyVisualState()
    {
        var mode = WidgetTitleIconModeNames.NormalizeMode(Mode);
        if (mode == WidgetTitleIconMode.Hidden)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        var kind = ResolveIconKind();
        double iconSize = ScaleIconSize(IconSize);
        var accent = AccentColor.A == 0 ? AccentColorHelper.DefaultAccentColor : AccentColor;

        ColorIcon.Visibility = Visibility.Collapsed;
        LineIconHost.Visibility = Visibility.Collapsed;
        FilledIconHost.Visibility = Visibility.Collapsed;
        HideMonoIconPaths();
        LabelElement.Visibility = Visibility.Collapsed;
        CustomImage.Visibility = Visibility.Collapsed;
        CustomEmojiElement.Visibility = Visibility.Collapsed;

        IconSurface.Padding = new Thickness(0);
        IconSurface.BorderThickness = new Thickness(0);
        IconSurface.BorderBrush = null;
        IconSurface.Background = _transparentBrush ??= new SolidColorBrush(Colors.Transparent);
        IconSurface.CornerRadius = new CornerRadius(0);
        IconSurface.Width = Math.Clamp(Math.Round(iconSize + 2), 15, 34);
        IconSurface.Height = Math.Clamp(Math.Round(iconSize + 2), 15, 34);
        IconSurface.MinWidth = 0;

        if (!string.IsNullOrEmpty(CustomEmoji))
        {
            ApplyCustomEmoji(iconSize);
        }
        else if (CustomImageSource is { } customSource)
        {
            ApplyCustomImage(customSource, iconSize);
        }
        else
        {
            switch (mode)
            {
                case WidgetTitleIconMode.LineMono:
                    ApplyMonoIcon(kind, iconSize, accent, filled: false);
                    break;

                case WidgetTitleIconMode.FilledMono:
                    ApplyMonoIcon(kind, iconSize, accent, filled: true);
                    break;

                case WidgetTitleIconMode.TextLabel:
                    ApplyTextLabel(kind, iconSize, accent);
                    break;

                default:
                    ApplyColorIcon(kind, iconSize);
                    break;
            }
        }

        ApplySurfaceCornerRadiusOverride();
    }

    private void ApplySurfaceCornerRadiusOverride()
    {
        if (_surfaceCornerRadiusOverride is { } radius)
        {
            IconSurface.CornerRadius = new CornerRadius(radius);
        }
    }

    private WidgetTitleIconKind ResolveIconKind()
    {
        var kind = WidgetTitleIconKindNames.NormalizeKind(IconKind);
        return kind == WidgetTitleIconKind.Default
            ? WidgetTitleIconKindNames.NormalizeKind(WidgetTitleIconKindNames.FromLegacyGlyph(Glyph))
            : kind;
    }

    private void ApplyColorIcon(WidgetTitleIconKind kind, double iconSize)
    {
        double imageSize = Math.Clamp(Math.Round(iconSize + 3), 18, 34);
        string assetName = WidgetTitleIconKindNames.GetColorAssetName(kind);
        IconSurface.Width = imageSize;
        IconSurface.Height = imageSize;
        ColorIcon.Width = imageSize;
        ColorIcon.Height = imageSize;
        string assetKey = $"{assetName}@{(int)imageSize}";
        if (!string.Equals(_currentColorAssetKey, assetKey, StringComparison.Ordinal))
        {
            ColorIcon.Source = GetOrCreateColorSvgSource(assetName, assetKey);
            _currentColorAssetKey = assetKey;
        }

        ColorIcon.Visibility = Visibility.Visible;
    }

    private void ApplyCustomEmoji(double iconSize)
    {
        double surfaceSize = Math.Clamp(Math.Round(iconSize + 3), 18, 34);
        IconSurface.Width = surfaceSize;
        double emojiFontSize = Math.Clamp(Math.Round(iconSize * 0.85), 11, 22);
        IconSurface.Height = Math.Clamp(Math.Ceiling(emojiFontSize * 1.6), surfaceSize, 32);
        CustomEmojiElement.Text = CustomEmoji;
        CustomEmojiElement.FontSize = emojiFontSize;
        CustomEmojiElement.TextLineBounds = TextLineBounds.Full;
        CustomEmojiElement.Margin = new Thickness(0, -1, 0, 0);
        CustomEmojiElement.Visibility = Visibility.Visible;
    }

    private void ApplyCustomImage(ImageSource source, double iconSize)
    {
        double imageSize = Math.Clamp(Math.Round(iconSize + 3), 18, 34);
        IconSurface.Width = imageSize;
        IconSurface.Height = imageSize;
        CustomImage.Width = imageSize;
        CustomImage.Height = imageSize;
        CustomImage.Source = source;
        CustomImage.Visibility = Visibility.Visible;
    }

    private void ApplyMonoIcon(WidgetTitleIconKind kind, double iconSize, Color accent, bool filled)
    {
        var iconPath = GetMonoIconPath(kind, filled);
        iconPath.Width = iconSize;
        iconPath.Height = iconSize;
        var accentBrush = GetOrUpdateAccentBrush(accent);
        if (filled || IsFluentFilledPathKind(kind))
        {
            iconPath.Fill = accentBrush;
            iconPath.Stroke = null;
            iconPath.StrokeThickness = 0;
        }
        else
        {
            iconPath.Fill = _transparentBrush ??= new SolidColorBrush(Colors.Transparent);
            iconPath.Stroke = accentBrush;
            iconPath.StrokeThickness = Math.Clamp(Math.Round(iconSize * 0.105, 1), 1.25, 1.7);
        }

        iconPath.Visibility = Visibility.Visible;

        var host = filled ? FilledIconHost : LineIconHost;
        host.Width = iconSize;
        host.Height = iconSize;
        host.Visibility = Visibility.Visible;
    }

    private void HideMonoIconPaths()
    {
        if (_activeMonoIconPath is { } iconPath)
        {
            iconPath.Visibility = Visibility.Collapsed;
        }
    }

    private XamlPath GetMonoIconPath(WidgetTitleIconKind kind, bool filled)
    {
        string pathName = filled
            ? kind switch
            {
                WidgetTitleIconKind.ManagedStorage => "FilledManagedStoragePath",
                WidgetTitleIconKind.MappedFolder => "FilledMappedFolderPath",
                WidgetTitleIconKind.QuickCapture => "FilledQuickCapturePath",
                WidgetTitleIconKind.Todo => "FilledTodoPath",
                WidgetTitleIconKind.Music => "FilledMusicPath",
                WidgetTitleIconKind.Weather => "FilledWeatherPath",
                WidgetTitleIconKind.Tags => "FilledTagsPath",
                WidgetTitleIconKind.Search => "FilledSearchPath",
                WidgetTitleIconKind.SystemMonitor => "FilledSystemMonitorPath",
                WidgetTitleIconKind.Pomodoro => "FilledPomodoroPath",
                WidgetTitleIconKind.Countdown => "FilledCountdownPath",
                WidgetTitleIconKind.AiMonitor => "FilledAiMonitorPath",
                WidgetTitleIconKind.Clipboard => "FilledClipboardPath",
                _ => "FilledDefaultPath"
            }
            : kind switch
            {
                WidgetTitleIconKind.ManagedStorage => "LineManagedStoragePath",
                WidgetTitleIconKind.MappedFolder => "LineMappedFolderPath",
                WidgetTitleIconKind.QuickCapture => "LineQuickCapturePath",
                WidgetTitleIconKind.Todo => "LineTodoPath",
                WidgetTitleIconKind.Music => "LineMusicPath",
                WidgetTitleIconKind.Weather => "LineWeatherPath",
                WidgetTitleIconKind.Tags => "LineTagsPath",
                WidgetTitleIconKind.Search => "LineSearchPath",
                WidgetTitleIconKind.SystemMonitor => "LineSystemMonitorPath",
                WidgetTitleIconKind.Pomodoro => "LinePomodoroPath",
                WidgetTitleIconKind.Countdown => "LineCountdownPath",
                WidgetTitleIconKind.AiMonitor => "LineAiMonitorPath",
                WidgetTitleIconKind.Clipboard => "LineClipboardPath",
                _ => "LineDefaultPath"
            };

        // Materialize only the selected geometry, then keep it for immediate
        // reuse when icon mode or widget kind changes again.
        if (_activeMonoIconPath is { } activePath && activePath.Name == pathName)
        {
            return activePath;
        }

        if (!_monoIconPaths.TryGetValue(pathName, out var iconPath))
        {
            var sharedResources = s_sharedMonoResources ??= new WidgetTitleIconResources();
            string templateKey = pathName + "Template";
            var template = (sharedResources.TryGetValue(templateKey, out object? resource)
                ? resource as DataTemplate
                : null) ?? (Resources.TryGetValue(templateKey, out object? localResource)
                    ? localResource as DataTemplate
                    : null);
            iconPath = template?.LoadContent() as XamlPath ??
                throw new InvalidOperationException($"Missing title icon template: {pathName}");
            (filled ? FilledIconHost : LineIconHost).Children.Add(iconPath);
            _monoIconPaths.Add(pathName, iconPath);
        }

        _activeMonoIconPath = iconPath;
        return iconPath;
    }

    private SvgImageSource GetOrCreateColorSvgSource(string assetName, string assetKey)
    {
        var uri = new Uri($"ms-appx:///Assets/WidgetTitleIcons/{assetName}.svg");
        if (DispatcherQueue?.HasThreadAccess != true)
        {
            return new SvgImageSource(uri);
        }

        var cache = s_sharedColorSvgSources ??= new Dictionary<string, SvgImageSource>(StringComparer.Ordinal);
        if (!cache.TryGetValue(assetKey, out var svgSource))
        {
            svgSource = new SvgImageSource(uri);
            cache[assetKey] = svgSource;
        }

        return svgSource;
    }

    private SolidColorBrush GetOrUpdateAccentBrush(Color accent)
    {
        if (_accentBrush is null)
        {
            _accentBrush = new SolidColorBrush(accent);
        }
        else if (!_accentBrush.Color.Equals(accent))
        {
            _accentBrush.Color = accent;
        }

        return _accentBrush;
    }

    private static bool IsFluentFilledPathKind(WidgetTitleIconKind kind)
    {
        return kind is WidgetTitleIconKind.ManagedStorage
            or WidgetTitleIconKind.MappedFolder
            or WidgetTitleIconKind.QuickCapture
            or WidgetTitleIconKind.Todo
            or WidgetTitleIconKind.Music
            or WidgetTitleIconKind.Search
            or WidgetTitleIconKind.Pomodoro
            or WidgetTitleIconKind.Countdown
            or WidgetTitleIconKind.AiMonitor
            or WidgetTitleIconKind.Clipboard;
    }

    private void ApplyLegacyGlyphFallback()
    {
        ColorIcon.Visibility = Visibility.Collapsed;
        LineIconHost.Visibility = Visibility.Collapsed;
        FilledIconHost.Visibility = Visibility.Collapsed;
        HideMonoIconPaths();
        CustomImage.Visibility = Visibility.Collapsed;
        CustomEmojiElement.Visibility = Visibility.Collapsed;

        string fallbackLabel = CreateShortLabel(LabelText);
        IconSurface.Width = double.NaN;
        double iconSize = ScaleIconSize(IconSize);
        IconSurface.Height = Math.Clamp(Math.Round(iconSize + 5), 20, 34);
        IconSurface.MinWidth = Math.Clamp(Math.Round(iconSize + 18), 36, 58);
        IconSurface.Padding = new Thickness(6, 0, 6, 1);
        IconSurface.Background = new SolidColorBrush(WithAlpha(AccentColor, 0x1C));
        IconSurface.BorderBrush = null;
        IconSurface.BorderThickness = new Thickness(0);
        IconSurface.CornerRadius = new CornerRadius(7);

        LabelElement.Text = fallbackLabel;
        LabelElement.FontSize = Math.Clamp(Math.Round(iconSize * 0.66), 10, 14);
        LabelElement.Foreground = new SolidColorBrush(AccentColor);
        LabelElement.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        ApplySurfaceCornerRadiusOverride();
    }

    private void ApplyTextLabel(WidgetTitleIconKind kind, double iconSize, Color accent)
    {
        string label = App.Current?.LocalizationService?.T(WidgetTitleIconKindNames.GetLocalizationKey(kind)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label))
        {
            label = CreateShortLabel(LabelText);
        }

        if (_isCompactPresentation)
        {
            label = CreateShortLabel(label);
        }

        double height = Math.Clamp(Math.Round(iconSize + 5), 20, 34);
        IconSurface.Width = double.NaN;
        IconSurface.Height = height;
        IconSurface.MinWidth = _isCompactPresentation
            ? Math.Clamp(Math.Round(iconSize + 5), 20, 34)
            : Math.Clamp(Math.Round(iconSize + 22), 40, 66);
        IconSurface.Padding = _isCompactPresentation
            ? new Thickness(0)
            : new Thickness(7, 0, 7, 1);
        IconSurface.Background = _isCompactPresentation
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(WithAlpha(accent, IsDarkTheme() ? (byte)0x30 : (byte)0x1C));
        IconSurface.BorderBrush = _isCompactPresentation
            ? null
            : new SolidColorBrush(WithAlpha(accent, IsDarkTheme() ? (byte)0x66 : (byte)0x46));
        IconSurface.BorderThickness = _isCompactPresentation ? new Thickness(0) : new Thickness(0.8);
        IconSurface.CornerRadius = _isCompactPresentation
            ? new CornerRadius(0)
            : new CornerRadius(Math.Clamp(Math.Round(height * 0.32), 6, 9));

        LabelElement.Text = label.Trim();
        LabelElement.FontSize = Math.Clamp(Math.Round(iconSize * 0.66), 10, 14);
        LabelElement.Foreground = new SolidColorBrush(accent);
        LabelElement.Visibility = Visibility.Visible;
    }

    private bool IsDarkTheme() => NeutralInteractionBrush.IsDarkTheme(this);

    private static string CreateShortLabel(string? text)
    {
        string trimmed = string.IsNullOrWhiteSpace(text) ? "D" : text.Trim();
        int maxChars = IsAsciiText(trimmed) ? 3 : 2;
        var chars = trimmed
            .Where(c => !char.IsWhiteSpace(c))
            .Take(maxChars)
            .ToArray();

        return chars.Length == 0
            ? "D"
            : new string(chars).ToUpperInvariant();
    }

    private static bool IsAsciiText(string text)
    {
        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static double ScaleIconSize(double rawIconSize)
    {
        double baseSize = double.IsFinite(rawIconSize) ? rawIconSize : 14;
        return Math.Clamp(baseSize * DesktopTitleIconScale, 11, 31);
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
