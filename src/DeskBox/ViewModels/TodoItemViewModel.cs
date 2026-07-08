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
    private TodoRecurrence? _recurrence;
    private DateTimeOffset? _completedAt;
    private int? _reminderOffsetMinutes;
    private DateTimeOffset? _snoozedUntil;
    private bool _isEditing;
    private bool _isCopySelected;
    private string _editText = string.Empty;
    private bool _isRecurringHistoryLead;
    private bool _isRecurringHistoryExpanded;
    private int _recurringHistoryItemCount;

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
        _recurrence = TodoRecurrence.Normalize(item.Recurrence, item.DueDate);
        item.Recurrence = _recurrence;
        _completedAt = item.CompletedAt;
        _reminderOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(item.ReminderOffsetMinutes);
        item.ReminderOffsetMinutes = _reminderOffsetMinutes;
        _snoozedUntil = item.SnoozedUntil;
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
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
                OnPropertyChanged(nameof(ContentOpacity));
                OnPropertyChanged(nameof(TextDecorations));
                OnPropertyChanged(nameof(HasActiveSnooze));
            }
        }
    }

    public string? RecurrenceSeriesId
    {
        get => TodoRecurrenceService.NormalizeSeriesId(_item.RecurrenceSeriesId);
        internal set => _item.RecurrenceSeriesId = TodoRecurrenceService.NormalizeSeriesId(value);
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
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
                OnPropertyChanged(nameof(HasActiveSnooze));
            }
        }
    }

    public TodoRecurrence? Recurrence
    {
        get => _recurrence;
        internal set
        {
            TodoRecurrence? normalizedValue = TodoRecurrence.Normalize(value, DueDate);
            if (AreRecurrenceEqual(_recurrence, normalizedValue))
            {
                return;
            }

            _recurrence = normalizedValue;
            _item.Recurrence = normalizedValue?.Clone();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRecurrence));
            OnPropertyChanged(nameof(RecurrenceMode));
            OnPropertyChanged(nameof(RecurrenceSummaryText));
            OnPropertyChanged(nameof(DueStatusText));
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        internal set
        {
            if (SetProperty(ref _completedAt, value))
            {
                _item.CompletedAt = value;
            }
        }
    }

    public int? ReminderOffsetMinutes
    {
        get => _reminderOffsetMinutes;
        internal set
        {
            int? normalizedValue = TodoReminderOptions.NormalizeOffsetMinutes(value);
            if (SetProperty(ref _reminderOffsetMinutes, normalizedValue))
            {
                _item.ReminderOffsetMinutes = normalizedValue;
            }
        }
    }

    public DateTimeOffset? SnoozedUntil
    {
        get => _snoozedUntil;
        internal set
        {
            if (SetProperty(ref _snoozedUntil, value))
            {
                _item.SnoozedUntil = value;
                OnPropertyChanged(nameof(HasActiveSnooze));
                OnPropertyChanged(nameof(DueStatusText));
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

    public bool IsCopySelected
    {
        get => _isCopySelected;
        set
        {
            if (SetProperty(ref _isCopySelected, value))
            {
                OnPropertyChanged(nameof(CopySelectionVisibility));
            }
        }
    }

    public string ImportantGlyph => IsImportant ? "\uE735" : "\uE734";

    public bool HasRedMarker => string.Equals(ColorMarker, TodoItem.RedColorMarker, StringComparison.Ordinal);

    public bool HasColorMarker => ColorMarker is not null;

    public Visibility ColorMarkerVisibility => HasColorMarker ? Visibility.Visible : Visibility.Collapsed;

    public Brush ColorMarkerBrush => new SolidColorBrush(ParseColor(TodoItem.GetColorMarkerHex(ColorMarker)));

    public string MarkerGlyph => HasRedMarker ? "\uE915" : "\uE915";

    public string CompletionGlyph => IsCompleted ? "\uE73E" : string.Empty;

    public double CompletionGlyphOpacity => IsCompleted ? 1d : 0d;

    public double ContentOpacity => IsCompleted ? 0.62d : 1d;

    public Windows.UI.Text.TextDecorations TextDecorations => IsCompleted
        ? Windows.UI.Text.TextDecorations.Strikethrough
        : Windows.UI.Text.TextDecorations.None;

    public bool IsOverdue => DueDate is { } dueDate &&
                             dueDate.ToLocalTime() < DateTimeOffset.Now &&
                             !IsCompleted;

    public bool HasRecurrence => Recurrence is not null &&
                                 !string.Equals(RecurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);

    public bool HasActiveSnooze => SnoozedUntil is not null && DueDate is not null && !IsCompleted;

    public string RecurrenceMode => TodoRecurrenceMode.Normalize(Recurrence?.Mode);

    public string RecurrenceSummaryText => HasRecurrence
        ? LocalizedText(TodoRecurrenceService.GetLocalizationKey(RecurrenceMode))
        : string.Empty;

    public string DueStatusText
    {
        get
        {
            if (DueDate is not { } dueDate)
            {
                return string.Empty;
            }

            DateTimeOffset localDueDate = dueDate.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            var due = localDueDate.Date;
            string formattedDue = FormatDueDateTime(localDueDate);
            string formattedTime = FormatDueTime(localDueDate);
            string statusText;

            if (due == today)
            {
                statusText = Format("Todo.Due.TodayAt", formattedTime);
            }
            else if (due == today.AddDays(1))
            {
                statusText = Format("Todo.Due.TomorrowAt", formattedTime);
            }
            else
            {
                statusText = Format("Todo.Due.Date", formattedDue);
            }

            if (HasRecurrence)
            {
                statusText = $"{RecurrenceSummaryText} \u00B7 {statusText}";
            }

            if (HasActiveSnooze && SnoozedUntil is { } snoozedUntil)
            {
                statusText = $"{statusText} \u00B7 {Format("Todo.Reminder.SnoozedUntil", FormatSnoozeDateTime(snoozedUntil.ToLocalTime()))}";
            }

            return IsOverdue
                ? $"{statusText} \u00B7 {LocalizedText("Todo.Due.OverdueSuffix")}"
                : statusText;
        }
    }

    public Visibility DueStatusVisibility => DueDate is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DueStatusNormalVisibility => DueDate is not null && !IsOverdue
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DueStatusOverdueVisibility => IsOverdue
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CopySelectionVisibility => IsCopySelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TextVisibility => IsEditing ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EditVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRecurringHistoryLead => _isRecurringHistoryLead;

    public bool IsRecurringHistoryExpanded => _isRecurringHistoryExpanded;

    public int RecurringHistoryItemCount => _recurringHistoryItemCount;

    public int HiddenRecurringHistoryCount => Math.Max(0, RecurringHistoryItemCount - 1);

    public Visibility RecurringHistoryToggleVisibility => IsRecurringHistoryLead && RecurringHistoryItemCount > 1
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string RecurringHistoryToggleText => IsRecurringHistoryExpanded
        ? Format("Todo.RecurrenceHistory.Collapse", HiddenRecurringHistoryCount)
        : Format("Todo.RecurrenceHistory.Expand", HiddenRecurringHistoryCount);

    public string RecurringHistoryToggleGlyph => IsRecurringHistoryExpanded ? "\uE70D" : "\uE70E";

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
        OnPropertyChanged(nameof(RecurrenceSummaryText));
        OnPropertyChanged(nameof(DueStatusText));
        OnPropertyChanged(nameof(DueStatusNormalVisibility));
        OnPropertyChanged(nameof(DueStatusOverdueVisibility));
        OnPropertyChanged(nameof(RecurringHistoryToggleText));
    }

    internal void UpdateRecurringHistoryState(bool isLead, bool isExpanded, int itemCount)
    {
        bool changed = false;
        if (_isRecurringHistoryLead != isLead)
        {
            _isRecurringHistoryLead = isLead;
            OnPropertyChanged(nameof(IsRecurringHistoryLead));
            changed = true;
        }

        if (_isRecurringHistoryExpanded != isExpanded)
        {
            _isRecurringHistoryExpanded = isExpanded;
            OnPropertyChanged(nameof(IsRecurringHistoryExpanded));
            OnPropertyChanged(nameof(RecurringHistoryToggleGlyph));
            changed = true;
        }

        if (_recurringHistoryItemCount != itemCount)
        {
            _recurringHistoryItemCount = itemCount;
            OnPropertyChanged(nameof(RecurringHistoryItemCount));
            OnPropertyChanged(nameof(HiddenRecurringHistoryCount));
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(RecurringHistoryToggleVisibility));
            OnPropertyChanged(nameof(RecurringHistoryToggleText));
        }
    }

    private string LocalizedText(string key)
    {
        return _localizationService?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private string Format(string key, params object[] args)
    {
        return _localizationService?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static string FormatDueDateTime(DateTimeOffset dueDate)
    {
        return dueDate.Second == 0
            ? dueDate.ToString("yyyy/M/d HH:mm")
            : dueDate.ToString("yyyy/M/d HH:mm:ss");
    }

    private static string FormatDueTime(DateTimeOffset dueDate)
    {
        return dueDate.Second == 0
            ? dueDate.ToString("HH:mm")
            : dueDate.ToString("HH:mm:ss");
    }

    private string FormatSnoozeDateTime(DateTimeOffset snoozedUntil)
    {
        DateTime today = DateTimeOffset.Now.Date;
        string time = FormatDueTime(snoozedUntil);
        if (snoozedUntil.Date == today)
        {
            return time;
        }

        if (snoozedUntil.Date == today.AddDays(1))
        {
            return Format("Todo.Due.TomorrowAt", time);
        }

        return snoozedUntil.Second == 0
            ? snoozedUntil.ToString("yyyy/M/d HH:mm")
            : snoozedUntil.ToString("yyyy/M/d HH:mm:ss");
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

    private static bool AreRecurrenceEqual(TodoRecurrence? left, TodoRecurrence? right)
    {
        string leftMode = TodoRecurrenceMode.Normalize(left?.Mode);
        string rightMode = TodoRecurrenceMode.Normalize(right?.Mode);
        return string.Equals(leftMode, rightMode, StringComparison.Ordinal) &&
               Nullable.Equals(left?.AnchorDueDate, right?.AnchorDueDate);
    }
}
