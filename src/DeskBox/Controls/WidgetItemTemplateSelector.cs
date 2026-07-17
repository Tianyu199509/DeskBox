using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed class WidgetItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }

    public DataTemplate? StackTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item is WidgetStackItem ? StackTemplate : ItemTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
