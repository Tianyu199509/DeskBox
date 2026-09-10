using Windows.UI;
namespace DeskBox.MusicPackage.Models;

public sealed class MusicInstance
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "Music";
    public bool IsDefaultTitle { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
}
public sealed record MusicWidgetSettings
{
    public bool UseArtworkBackdrop { get; init; } = true;
    public bool EnableCoverHoverMotion { get; init; } = true;
    public string DisplayMode { get; init; } = "Auto";
}
public sealed record MusicEnvironmentSnapshot
{
    public MusicWidgetSettings Music { get; init; } = new();
    public string Locale { get; init; } = "en-US";
    public double TextSize { get; init; } = 11.5;
    public double CornerRadius { get; init; } = 8;
    public Color Accent { get; init; } = Color.FromArgb(255, 0, 120, 212);
    public bool UsesSystemAccentColor { get; init; } = true;
    public bool IsDark { get; init; }
    public bool AllowSystemAnimations { get; init; } = true;
    public bool AllowTextMarqueeAnimations { get; init; } = true;
    public bool AllowVinylRotationAnimations { get; init; } = true;
}

