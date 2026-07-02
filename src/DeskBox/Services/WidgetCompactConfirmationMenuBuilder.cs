using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

public static class WidgetCompactConfirmationMenuBuilder
{
    public static MenuFlyout CreateDeleteConfirmation(
        string title,
        string actionText,
        Func<Task> confirmedAction)
    {
        return CreateDeleteConfirmation(
            new WidgetCompactConfirmationOptions(
                title,
                actionText,
                confirmedAction));
    }

    public static MenuFlyout CreateDeleteConfirmation(WidgetCompactConfirmationOptions options)
    {
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = options.Title,
            Icon = new FontIcon { Glyph = options.TitleGlyph },
            IsEnabled = false
        });

        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = options.Message,
                Icon = new FontIcon { Glyph = options.MessageGlyph },
                IsEnabled = false
            });
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var confirmItem = new MenuFlyoutItem
        {
            Text = options.ActionText,
            Icon = new FontIcon
            {
                Glyph = options.ActionGlyph,
                Foreground = options.IsDangerAction
                    ? new SolidColorBrush(Colors.Red)
                    : null
            }
        };
        confirmItem.Click += async (_, _) => await options.ConfirmedAction();
        flyout.Items.Add(confirmItem);

        if (!string.IsNullOrWhiteSpace(options.CancelText))
        {
            var cancelItem = new MenuFlyoutItem
            {
                Text = options.CancelText,
                Icon = new FontIcon { Glyph = options.CancelGlyph }
            };
            cancelItem.Click += (_, _) => flyout.Hide();
            flyout.Items.Add(cancelItem);
        }

        return flyout;
    }
}

public sealed record WidgetCompactConfirmationOptions(
    string Title,
    string ActionText,
    Func<Task> ConfirmedAction)
{
    public string TitleGlyph { get; init; } = "\uE783";

    public string? Message { get; init; }

    public string MessageGlyph { get; init; } = "\uE783";

    public string ActionGlyph { get; init; } = "\uE74D";

    public bool IsDangerAction { get; init; } = true;

    public string? CancelText { get; init; }

    public string CancelGlyph { get; init; } = "\uE711";
}
