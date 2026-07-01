using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Non-creatable placeholder content used to validate the future widget content pipeline.
/// It is not exposed through user-facing creation flows.
/// </summary>
public sealed class PlaceholderWidgetContent : IWidgetContent
{
    private FrameworkElement? _view;
    private readonly WidgetContentDescriptor _descriptor;

    public PlaceholderWidgetContent(WidgetConfig config, WidgetContentDescriptor descriptor)
    {
        Config = config;
        _descriptor = descriptor;
    }

    public WidgetConfig Config { get; }
    public string WidgetId => Config.Id;
    public WidgetKind WidgetKind => Config.WidgetKind;
    public FrameworkElement View => _view ??= CreateView(_descriptor);

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task RefreshAsync()
    {
        return Task.CompletedTask;
    }

    public void ApplyAppearance()
    {
    }

    public void OnActivated()
    {
    }

    public void OnDeactivated()
    {
    }

    private static FrameworkElement CreateView(WidgetContentDescriptor descriptor)
    {
        var title = new TextBlock
        {
            Text = descriptor.DefaultTitle,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        var description = new TextBlock
        {
            Text = "Content placeholder",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB3, 0x80, 0x80, 0x80)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            Children =
            {
                new FontIcon
                {
                    Glyph = descriptor.DefaultGlyph,
                    FontSize = 24,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                title,
                description
            }
        };

        return new Grid
        {
            Padding = new Thickness(16),
            Children = { stack }
        };
    }
}
