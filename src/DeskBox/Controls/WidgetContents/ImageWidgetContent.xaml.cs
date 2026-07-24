using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class ImageWidgetContent : UserControl
{
    public ImageWidgetContent(ImageWidgetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public ImageWidgetViewModel? ViewModel => DataContext as ImageWidgetViewModel;

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel?.ApplySizeChanged(e.NewSize.Width, e.NewSize.Height);
    }

    private void AddImageButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.OnAddImageRequested();
    }
}
