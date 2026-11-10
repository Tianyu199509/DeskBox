using DeskBox.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeskBox.Controls;

/// <summary>
/// Miniature, theme-independent previews for the Glance style gallery. Drawn
/// with a handful of primitives so the settings page stays light and needs no
/// bitmap assets per locale or theme.
/// </summary>
internal static class GlanceStylePreviewFactory
{
    private const double PreviewWidth = 112;
    private const double PreviewHeight = 72;

    public static IReadOnlyList<GlanceStyle> Styles { get; } =
    [
        GlanceStyle.PhotoClock,
        GlanceStyle.Editorial,
        GlanceStyle.TodayCard,
        GlanceStyle.WeekStrip,
        GlanceStyle.MonthCalendar
    ];

    public static string GetTextKey(GlanceStyle style) =>
        style switch
        {
            GlanceStyle.Editorial => "Glance.Style.Editorial",
            GlanceStyle.TodayCard => "Glance.Style.TodayCard",
            GlanceStyle.WeekStrip => "Glance.Style.WeekStrip",
            GlanceStyle.MonthCalendar => "Glance.Style.MonthCalendar",
            _ => "Glance.Style.PhotoClock"
        };

    public static FrameworkElement Create(GlanceStyle style, string label)
    {
        var panel = new StackPanel { Spacing = 6, Width = PreviewWidth };
        var frame = new Border
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            CornerRadius = new CornerRadius(8),
            Background = CreateBackdrop(style),
            Child = CreateContent(style)
        };
        panel.Children.Add(frame);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return panel;
    }

    private static Brush CreateBackdrop(GlanceStyle style)
    {
        (byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) = style switch
        {
            GlanceStyle.Editorial => ((byte)0x3A, (byte)0x5A, (byte)0x7A, (byte)0xC0, (byte)0x8A, (byte)0x5A),
            GlanceStyle.TodayCard => ((byte)0x2B, (byte)0x3A, (byte)0x55, (byte)0x4A, (byte)0x6A, (byte)0x8C),
            GlanceStyle.WeekStrip => ((byte)0x2F, (byte)0x5D, (byte)0x50, (byte)0x6A, (byte)0x9A, (byte)0x7A),
            GlanceStyle.MonthCalendar => ((byte)0x44, (byte)0x3A, (byte)0x66, (byte)0x7A, (byte)0x5C, (byte)0x8A),
            _ => ((byte)0x23, (byte)0x4E, (byte)0x7A, (byte)0x8A, (byte)0x6A, (byte)0xB0)
        };
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0xFF, r1, g1, b1) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0xFF, r2, g2, b2) });
        return brush;
    }

    private static UIElement CreateContent(GlanceStyle style) =>
        style switch
        {
            GlanceStyle.Editorial => CreateEditorial(),
            GlanceStyle.TodayCard => CreateTodayCard(),
            GlanceStyle.WeekStrip => CreateWeekStrip(),
            GlanceStyle.MonthCalendar => CreateMonth(),
            _ => CreatePhotoClock()
        };

    private static UIElement CreatePhotoClock()
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2
        };
        stack.Children.Add(Text("10:24", 22, bold: true, HorizontalAlignment.Center));
        stack.Children.Add(Bar(40, 4, 0.7, HorizontalAlignment.Center));
        return stack;
    }

    private static UIElement CreateEditorial()
    {
        var grid = new Grid { Padding = new Thickness(8) };
        var top = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        top.Children.Add(Bar(34, 5, 0.95, HorizontalAlignment.Left));
        top.Children.Add(Bar(22, 4, 0.6, HorizontalAlignment.Left));
        grid.Children.Add(top);
        TextBlock time = Text("10:24", 16, bold: true, HorizontalAlignment.Right);
        time.VerticalAlignment = VerticalAlignment.Bottom;
        grid.Children.Add(time);
        return grid;
    }

    private static UIElement CreateTodayCard()
    {
        var grid = new Grid { Padding = new Thickness(8, 5, 8, 6) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Bar(30, 4, 0.75, HorizontalAlignment.Left));
        TextBlock day = Text("8", 30, bold: true, HorizontalAlignment.Left);
        day.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(day, 1);
        grid.Children.Add(day);
        var badge = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2F, 0xB3, 0x6F))
        };
        grid.Children.Add(badge);
        var agenda = new StackPanel { Spacing = 3 };
        agenda.Children.Add(Bar(64, 3, 0.55, HorizontalAlignment.Left));
        agenda.Children.Add(Bar(48, 3, 0.45, HorizontalAlignment.Left));
        Grid.SetRow(agenda, 2);
        grid.Children.Add(agenda);
        return grid;
    }

    private static UIElement CreateWeekStrip()
    {
        var stack = new StackPanel { Padding = new Thickness(8, 8, 8, 6), Spacing = 8 };
        var header = new Grid();
        header.Children.Add(Text("10:24", 14, bold: true, HorizontalAlignment.Left));
        header.Children.Add(Bar(30, 4, 0.7, HorizontalAlignment.Right));
        stack.Children.Add(header);
        var week = new Grid { ColumnSpacing = 3 };
        for (int index = 0; index < 7; index++)
        {
            week.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new Border
            {
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(index == 3
                    ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                    : Windows.UI.Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
            };
            Grid.SetColumn(cell, index);
            week.Children.Add(cell);
        }

        stack.Children.Add(week);
        return stack;
    }

    private static UIElement CreateMonth()
    {
        var grid = new Grid
        {
            Padding = new Thickness(10, 8, 10, 8),
            RowSpacing = 4,
            ColumnSpacing = 4
        };
        for (int column = 0; column < 7; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int row = 0; row < 5; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int column = 0; column < 7; column++)
            {
                bool today = row == 2 && column == 4;
                var dot = new Ellipse
                {
                    Width = today ? 8 : 5,
                    Height = today ? 8 : 5,
                    Fill = new SolidColorBrush(today
                        ? Colors.White
                        : Windows.UI.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF))
                };
                Grid.SetRow(dot, row);
                Grid.SetColumn(dot, column);
                grid.Children.Add(dot);
            }
        }

        return grid;
    }

    private static TextBlock Text(string text, double size, bool bold, HorizontalAlignment alignment) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = alignment,
            IsTextSelectionEnabled = false
        };

    private static Border Bar(double width, double height, double opacity, HorizontalAlignment alignment) =>
        new()
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = alignment,
            Background = new SolidColorBrush(Colors.White),
            Opacity = opacity
        };
}
