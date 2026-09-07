using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Contracts;

public sealed class WidgetHostContextMenuOpeningEventArgs(
    MenuFlyout menu) : EventArgs
{
    public MenuFlyout Menu { get; } = menu;

    public MenuFlyoutSubItem? TitleStyleItem { get; set; }

    public MenuFlyoutItem? CloseWidgetItem { get; set; }
}

/// <summary>
/// Lets hosted content contribute its own background actions while the current
/// window appends the shared widget-level actions to the same menu.
/// </summary>
public interface IWidgetHostContextMenuSource
{
    event EventHandler<WidgetHostContextMenuOpeningEventArgs>?
        HostContextMenuOpening;
}
