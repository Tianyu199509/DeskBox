using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;

namespace DeskBox.ViewModels;

public sealed partial class TodoItemViewModel : ObservableObject
{
    private readonly TodoItem _item;
    private string _text;
    private bool _isCompleted;

    public TodoItemViewModel(TodoItem item)
    {
        _item = item;
        _text = item.Text;
        _isCompleted = item.IsCompleted;
    }

    public TodoItem Item => _item;

    public string Id => _item.Id;

    public int SortOrder
    {
        get => _item.SortOrder;
        internal set
        {
            if (_item.SortOrder != value)
            {
                _item.SortOrder = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTimeOffset CreatedAt => _item.CreatedAt;

    public DateTimeOffset UpdatedAt
    {
        get => _item.UpdatedAt;
        internal set
        {
            if (_item.UpdatedAt != value)
            {
                _item.UpdatedAt = value;
                OnPropertyChanged();
            }
        }
    }

    public string Text
    {
        get => _text;
        internal set
        {
            if (SetProperty(ref _text, value))
            {
                _item.Text = value;
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        internal set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                _item.IsCompleted = value;
            }
        }
    }
}
