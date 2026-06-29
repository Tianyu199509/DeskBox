using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent : UserControl
{
    public TodoWidgetContent()
    {
        InitializeComponent();
    }

    public TodoWidgetContent(TodoWidgetViewModel viewModel)
        : this()
    {
        ViewModel = viewModel;
    }

    public TodoWidgetViewModel? ViewModel
    {
        get => DataContext as TodoWidgetViewModel;
        set => DataContext = value;
    }

    private async void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel is not null)
        {
            await ViewModel.AddInputAsync();
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.AddInputAsync();
            AddTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void AllFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.SetFilter(TodoFilter.All);
    }

    private void ActiveFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.SetFilter(TodoFilter.Active);
    }

    private void CompletedFilterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.SetFilter(TodoFilter.Completed);
    }

    private async void ItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not CheckBox checkBox ||
            checkBox.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await ViewModel.SetCompletedAsync(item.Id, checkBox.IsChecked == true);
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await ViewModel.DeleteItemAsync(item.Id);
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ClearCompletedAsync();
        }
    }
}
