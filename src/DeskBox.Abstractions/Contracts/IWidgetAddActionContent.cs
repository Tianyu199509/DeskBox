namespace DeskBox.Contracts;

/// <summary>
/// Optional content capability for widgets that can handle the title bar add action.
/// </summary>
public interface IWidgetAddActionContent
{
    Task AddFromTitleButtonAsync();
}
