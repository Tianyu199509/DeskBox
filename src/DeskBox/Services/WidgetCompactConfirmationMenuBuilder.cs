using Microsoft.UI.Xaml.Controls;

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

        MenuFlyoutItem? cancelItem = null;
        if (!string.IsNullOrWhiteSpace(options.CancelText))
        {
            cancelItem = new MenuFlyoutItem
            {
                Text = options.CancelText,
                Icon = new FontIcon { Glyph = options.CancelGlyph }
            };
            cancelItem.Click += (_, _) => flyout.Hide();
        }

        if (options.CancelFirst && cancelItem is not null)
        {
            flyout.Items.Add(cancelItem);
        }

        var confirmItem = new MenuFlyoutItem
        {
            Text = options.ActionText,
            Icon = new FontIcon { Glyph = options.ActionGlyph }
        };
        if (options.IsDangerAction)
        {
            WidgetDangerActionStyle.Apply(confirmItem);
        }
        confirmItem.Click += async (_, _) =>
        {
            try
            {
                await options.ConfirmedAction();
            }
            catch (Exception ex)
            {
                App.Log($"[CompactConfirm] ConfirmedAction failed: {ex}");
            }
        };
        flyout.Items.Add(confirmItem);

        if (!options.CancelFirst && cancelItem is not null)
        {
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

    public bool CancelFirst { get; init; }
}
