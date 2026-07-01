using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.ViewModels;

public sealed partial class TodoItemViewModel : ObservableObject
{
    private readonly TodoItem _item;
    private readonly LocalizationService? _localizationService;
    private string _text;
    private bool _isCompleted;
    private bool _isImportant;
    private string? _colorMarker;
    private DateTimeOffset? _dueDate;
    private bool _isEditing;
    private string _editText = string.Empty;

    public TodoItemViewModel(TodoItem item, LocalizationService? localizationService = null)
    {
        _item = item;
        _localizationService = localizationService;
        _text = item.Text;
        _isCompleted = item.IsCompleted;
        _isImportant = item.IsImportant;
        _colorMarker = TodoItem.NormalizeColorMarker(item.ColorMarker);
        item.ColorMarker = _colorMarker;
        _dueDate = item.DueDate;
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
                OnPropertyChanged(nameof(CompletionGlyph));
                OnPropertyChanged(nameof(CompletionGlyphOpacity));
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(IsOverdue));
            }
        }
    }

    public bool IsImportant
    {
        get => _isImportant;
        internal set
        {
            if (SetProperty(ref _isImportant, value))
            {
                _item.IsImportant = value;
                OnPropertyChanged(nameof(ImportantGlyph));
            }
        }
    }

    public string? ColorMarker
    {
        get => _colorMarker;
        internal set
        {
            string? normalizedValue = TodoItem.NormalizeColorMarker(value);
            if (SetProperty(ref _colorMarker, normalizedValue))
            {
                _item.ColorMarker = normalizedValue;
                OnPropertyChanged(nameof(HasRedMarker));
                OnPropertyChanged(nameof(HasColorMarker));
                OnPropertyChanged(nameof(ColorMarkerVisibility));
                OnPropertyChanged(nameof(ColorMarkerBrush));
                OnPropertyChanged(nameof(MarkerGlyph));
            }
        }
    }

    public DateTimeOffset? DueDate
    {
        get => _dueDate;
        internal set
        {
            if (SetProperty(ref _dueDate, value))
            {
                _item.DueDate = value;
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(DueStatusVisibility));
                OnPropertyChanged(nameof(IsOverdue));
            }
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        internal set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(TextVisibility));
                OnPropertyChanged(nameof(EditVisibility));
            }
        }
    }

    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    public string ImportantGlyph => IsImportant ? "\uE735" : "\uE734";

    public bool HasRedMarker => string.Equals(ColorMarker, TodoItem.RedColorMarker, StringComparison.Ordinal);

    public bool HasColorMarker => ColorMarker is not null;

    public Visibility ColorMarkerVisibility => HasColorMarker ? Visibility.Visible : Visibility.Collapsed;

    public Brush ColorMarkerBrush => new SolidColorBrush(ParseColor(TodoItem.GetColorMarkerHex(ColorMarker)));

    public string MarkerGlyph => HasRedMarker ? "\uE915" : "\uE915";

    public string CompletionGlyph => IsCompleted ? "\uE73E" : string.Empty;

    public double CompletionGlyphOpacity => IsCompleted ? 1d : 0d;

    public bool IsOverdue => DueDate is { } dueDate &&
                             dueDate.Date < DateTimeOffset.Now.Date &&
                             !IsCompleted;

    public string DueStatusText
    {
        get
        {
            if (DueDate is not { } dueDate)
            {
                return string.Empty;
            }

            var today = DateTimeOffset.Now.Date;
            var due = dueDate.Date;
            if (due < today && !IsCompleted)
            {
                return Format("Todo.Due.Overdue", due.ToString("yyyy/M/d"));
            }

            if (due == today)
            {
                return LocalizedText("Todo.Due.Today");
            }

            if (due == today.AddDays(1))
            {
                return LocalizedText("Todo.Due.Tomorrow");
            }

            return Format("Todo.Due.Date", due.ToString("yyyy/M/d"));
        }
    }

    public Visibility DueStatusVisibility => DueDate is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TextVisibility => IsEditing ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EditVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    internal void BeginEdit()
    {
        EditText = Text;
        IsEditing = true;
    }

    internal void CancelEdit()
    {
        EditText = Text;
        IsEditing = false;
    }

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DueStatusText));
    }

    private string LocalizedText(string key)
    {
        return _localizationService?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private string Format(string key, params object[] args)
    {
        return _localizationService?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length != 6 ||
            !byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) ||
            !byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) ||
            !byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            return Colors.Gray;
        }

        return ColorHelper.FromArgb(0xFF, red, green, blue);
    }
}
