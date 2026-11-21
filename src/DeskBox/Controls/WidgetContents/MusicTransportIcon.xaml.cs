using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public enum MusicTransportIconKind
{
    Previous,
    Play,
    Pause,
    Next
}

public sealed partial class MusicTransportIcon : UserControl
{
    public const string PreviousGlyph = "\uF8AC"; // BackSolidBold (Win11)
    public const string PlayGlyph = "\uF5B0";     // PlaySolid
    public const string PauseGlyph = "\uF8AE";    // PauseBold (Win11)
    public const string NextGlyph = "\uF8AD";     // ForwardSolidBold (Win11)

    internal const string Win10PreviousGlyph = "\uE892";
    internal const string Win10PlayGlyph = "\uE768";
    internal const string Win10PauseGlyph = "\uE769";
    internal const string Win10NextGlyph = "\uE893";

    internal static string ResolveGlyph(MusicTransportIconKind kind, bool isWindows11OrLater) =>
        isWindows11OrLater
            ? kind switch
            {
                MusicTransportIconKind.Previous => PreviousGlyph,
                MusicTransportIconKind.Pause => PauseGlyph,
                MusicTransportIconKind.Next => NextGlyph,
                _ => PlayGlyph
            }
            : kind switch
            {
                MusicTransportIconKind.Previous => Win10PreviousGlyph,
                MusicTransportIconKind.Pause => Win10PauseGlyph,
                MusicTransportIconKind.Next => Win10NextGlyph,
                _ => Win10PlayGlyph
            };

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(MusicTransportIconKind),
        typeof(MusicTransportIcon),
        new PropertyMetadata(MusicTransportIconKind.Play, OnKindChanged));

    public MusicTransportIcon()
    {
        InitializeComponent();
        ApplyKind();
    }

    public MusicTransportIconKind Kind
    {
        get => (MusicTransportIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MusicTransportIcon icon)
        {
            icon.ApplyKind();
        }
    }

    private void ApplyKind()
    {
        TransportGlyphIcon.Glyph = ResolveGlyph(
            Kind,
            DeskBox.Services.WindowsCompatibilityService.IsWindows11OrLater);
    }
}
