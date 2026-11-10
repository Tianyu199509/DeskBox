using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
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
    private readonly Func<DateTimeOffset> _clock;
    private string _text;
    private bool _isCompleted;
    private bool _isImportant;
    private string? _colorMarker;
    private DateTimeOffset? _dueDate;
    private bool _hasDueTime;
    private TodoRecurrence? _recurrence;
    private DateTimeOffset? _completedAt;
    private int? _reminderOffsetMinutes;
    private DateTimeOffset? _snoozedUntil;
    private bool _isEditing;
    private bool _isCopySelected;
    private bool _isExpanded;
    private string _editText = string.Empty;
    private bool _isRecurringHistoryLead;
    private bool _isRecurringHistoryExpanded;
    private int _recurringHistoryItemCount;
    private bool _isDetailSelected;
    private bool _isFirstTodayGroupItem;
    private bool _isStepsExpanded;
    private string? _primaryUrl;
    private object[]? _cachedStepItemsSource;
    private object[]? _cachedInlineStepsSource;
    private object[]? _cachedAttachmentItemsSource;

    public TodoItemViewModel(
        TodoItem item,
        LocalizationService? localizationService = null,
        string previewCacheScope = TodoAttachmentViewModel.TodoPreviewCacheScope,
        Func<DateTimeOffset>? clock = null)
    {
        _item = item;
        _localizationService = localizationService;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _text = item.Text;
        _primaryUrl = ExtractPrimaryHttpUrl(item.Text);
        _isCompleted = item.IsCompleted;
        _isImportant = item.IsImportant;
        _colorMarker = TodoItem.NormalizeColorMarker(item.ColorMarker);
        item.ColorMarker = _colorMarker;
        _dueDate = item.DueDate;
        _hasDueTime = _dueDate is not null && item.HasDueTime;
        item.HasDueTime = _hasDueTime;
        if (_dueDate is { } initialDueDate && !_hasDueTime)
        {
            _dueDate = TodoWidgetStore.NormalizeAllDayDueDate(initialDueDate);
            item.DueDate = _dueDate;
        }
        _recurrence = TodoRecurrence.Normalize(item.Recurrence, _dueDate);
        item.Recurrence = _recurrence;
        _completedAt = item.CompletedAt;
        _reminderOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(item.ReminderOffsetMinutes);
        item.ReminderOffsetMinutes = _reminderOffsetMinutes;
        _snoozedUntil = item.SnoozedUntil;
        Steps.CollectionChanged += (_, _) =>
        {
            _cachedStepItemsSource = null;
            _cachedInlineStepsSource = null;
        };
        Attachments.CollectionChanged += (_, _) => _cachedAttachmentItemsSource = null;
        item.Steps ??= [];
        item.Attachments ??= [];
        foreach (TodoStep step in item.Steps.OrderBy(step => step.SortOrder))
        {
            Steps.Add(new TodoStepViewModel(step));
        }

        foreach (TodoAttachment attachment in item.Attachments)
        {
            Attachments.Add(new TodoAttachmentViewModel(attachment, previewCacheScope));
        }
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

    public ObservableCollection<TodoStepViewModel> Steps { get; } = [];

    // ItemsSource crosses the WinRT object-valued dependency-property ABI.
    // Keep the typed collection for product logic and expose a concrete object
    // array only at the UI boundary so populated steps remain Native AOT safe.
    public object[] StepItemsSource => _cachedStepItemsSource ??= Steps.Cast<object>().ToArray();

    public object[] InlineStepsSource => _cachedInlineStepsSource ??= Steps.Cast<object>().ToArray();

    public ObservableCollection<TodoAttachmentViewModel> Attachments { get; } = [];

    // AttachmentTileStrip.ItemsSource is object-valued and therefore crosses
    // the WinRT ABI. Keep the typed collection for product logic, but expose a
    // concrete object array at the UI boundary just like populated Todo steps.
    public object[] AttachmentItemsSource => _cachedAttachmentItemsSource ??= Attachments.Cast<object>().ToArray();

    public string Notes => _item.Notes ?? string.Empty;

    public int TotalStepCount => Steps.Count;

    public bool HasSteps => TotalStepCount > 0;

    public bool IsStepsExpanded
    {
        get => _isStepsExpanded;
        set
        {
            if (SetProperty(ref _isStepsExpanded, value))
            {
                OnPropertyChanged(nameof(StepsExpandChevronGlyph));
                OnPropertyChanged(nameof(ExpandedStepsVisibility));
                OnPropertyChanged(nameof(StepsExpandTooltipText));
            }
        }
    }

    public string StepsExpandChevronGlyph => IsStepsExpanded ? "\uE70E" : "\uE70D";

    public Visibility ExpandedStepsVisibility => HasSteps && IsStepsExpanded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string StepsExpandTooltipText => Format(
        IsStepsExpanded ? "Todo.Steps.Collapse" : "Todo.Steps.Expand",
        TotalStepCount);

    public int CompletedStepCount => Steps.Count(step => step.IsCompleted);

    public string StepProgressText => Steps.Count == 0 ? string.Empty : $"{CompletedStepCount}/{Steps.Count}";

    public Visibility StepProgressVisibility => Steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AttachmentSummaryVisibility => Attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string AttachmentSummaryText => Attachments.Count == 0 ? string.Empty : $"{Attachments.Count}";

    public Visibility NotesPlaceholderVisibility => string.IsNullOrWhiteSpace(Notes)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility NotesIndicatorVisibility => string.IsNullOrWhiteSpace(Notes)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility CardMetadataRowVisibility =>
        DueDate is not null || Steps.Count > 0 || Attachments.Count > 0 || !string.IsNullOrWhiteSpace(Notes)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string CreatedText => Format("Todo.Detail.Created", CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm"));

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
                UpdatePrimaryUrl(value);
            }
        }
    }

    public string? PrimaryUrl => _primaryUrl;

    public bool HasPrimaryUrl => !string.IsNullOrEmpty(_primaryUrl);

    public Visibility PrimaryUrlVisibility => HasPrimaryUrl
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string PrimaryUrlTooltipText => HasPrimaryUrl
        ? $"{LocalizedText("Common.Open")}: {_primaryUrl}"
        : string.Empty;

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
                OnPropertyChanged(nameof(CompletionBorderThickness));
                OnPropertyChanged(nameof(CompletionToggleText));
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
                                OnPropertyChanged(nameof(ContentOpacity));
                OnPropertyChanged(nameof(TextDecorations));
                OnPropertyChanged(nameof(HasActiveSnooze));
                OnPropertyChanged(nameof(ReminderMetadataVisibility));
                OnPropertyChanged(nameof(RecurrenceMetadataVisibility));
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
                OnPropertyChanged(nameof(ImportantToggleText));
                OnPropertyChanged(nameof(ImportantBadgeVisibility));
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
                OnPropertyChanged(nameof(MetadataColorText));
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
                if (value is null && _hasDueTime)
                {
                    _hasDueTime = false;
                    _item.HasDueTime = false;
                    OnPropertyChanged(nameof(HasDueTime));
                }
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(HasDueDate));
                                OnPropertyChanged(nameof(DueStatusVisibility));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
                OnPropertyChanged(nameof(HasActiveSnooze));
                OnPropertyChanged(nameof(MetadataDueDateText));
                OnPropertyChanged(nameof(ReminderMetadataVisibility));
                OnPropertyChanged(nameof(RecurrenceMetadataVisibility));
                OnPropertyChanged(nameof(ReminderSetVisibility));
                OnPropertyChanged(nameof(CardMetadataRowVisibility));
            }
        }
    }

    public bool HasDueTime
    {
        get => _dueDate is not null && _hasDueTime;
        internal set
        {
            bool effectiveValue = _dueDate is not null && value;
            if (SetProperty(ref _hasDueTime, effectiveValue))
            {
                _item.HasDueTime = effectiveValue;
                OnPropertyChanged(nameof(DueStatusText));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(DueStatusNormalVisibility));
                OnPropertyChanged(nameof(DueStatusOverdueVisibility));
                OnPropertyChanged(nameof(MetadataDueDateText));
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
            OnPropertyChanged(nameof(MetadataRecurrenceText));
            OnPropertyChanged(nameof(RecurrenceMetadataVisibility));
            OnPropertyChanged(nameof(RecurrenceSetVisibility));
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
                OnPropertyChanged(nameof(DueStatusText));
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
                OnPropertyChanged(nameof(MetadataReminderText));
                OnPropertyChanged(nameof(ReminderSetVisibility));
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
        internal set => SetProperty(ref _isEditing, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        internal set => SetProperty(ref _isExpanded, value);
    }

    public string EditText
    {
        get => _editText;
        set
        {
            if (SetProperty(ref _editText, value))
            {
                OnPropertyChanged(nameof(CanSaveEdit));
            }
        }
    }

    public bool CanSaveEdit => !string.IsNullOrWhiteSpace(EditText);

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

    public bool IsDetailSelected
    {
        get => _isDetailSelected;
        internal set
        {
            if (SetProperty(ref _isDetailSelected, value))
            {
                OnPropertyChanged(nameof(DetailSelectionVisibility));
            }
        }
    }

    public Visibility DetailSelectionVisibility => IsDetailSelected
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ImportantGlyph => IsImportant ? "\uE735" : "\uE734";

    public Visibility ImportantBadgeVisibility => IsImportant
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsFirstTodayGroupItem => _isFirstTodayGroupItem;

    public Visibility TodayGroupDividerVisibility => _isFirstTodayGroupItem
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string TodayGroupHeaderText => LocalizedText("Todo.Today.SectionHeader");

    public bool HasRedMarker => string.Equals(ColorMarker, TodoItem.RedColorMarker, StringComparison.Ordinal);

    public bool HasColorMarker => ColorMarker is not null;

    public Visibility ColorMarkerVisibility => HasColorMarker ? Visibility.Visible : Visibility.Collapsed;

    public Brush ColorMarkerBrush => SharedBrushCache.GetOrCreate(ParseColor(TodoItem.GetColorMarkerHex(ColorMarker)));

    public string CompletionGlyph => IsCompleted ? "\uE73E" : string.Empty;

    public double CompletionGlyphOpacity => IsCompleted ? 1d : 0d;

    public Thickness CompletionBorderThickness => IsCompleted
        ? new Thickness(0)
        : new Thickness(1.4);

    public string CompletionToggleText => LocalizedText(IsCompleted
        ? "Todo.Menu.MarkActive"
        : "Todo.Menu.MarkCompleted");

    public string ImportantToggleText => LocalizedText(IsImportant
        ? "Todo.Menu.UnmarkImportant"
        : "Todo.Menu.MarkImportant");

    public double ContentOpacity => IsCompleted ? 0.62d : 1d;

    public Windows.UI.Text.TextDecorations TextDecorations => IsCompleted
        ? Windows.UI.Text.TextDecorations.Strikethrough
        : Windows.UI.Text.TextDecorations.None;

    public bool IsOverdue
    {
        get
        {
            if (DueDate is not { } dueDate || IsCompleted)
            {
                return false;
            }

            DateTimeOffset localDue = dueDate.ToLocalTime();
            DateTimeOffset nowLocal = _clock().ToLocalTime();
            return HasDueTime
                ? localDue < nowLocal
                : localDue.Date < nowLocal.Date;
        }
    }

    public bool HasRecurrence => Recurrence is not null &&
                                 !string.Equals(RecurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);

    public bool HasDueDate => DueDate is not null;

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
            var today = _clock().ToLocalTime().Date;
            if (IsCompleted &&
                HasRecurrence &&
                CompletedAt is { } completedAt &&
                completedAt.ToLocalTime().Date == today &&
                !string.IsNullOrWhiteSpace(_item.GeneratedNextItemId) &&
                TodoRecurrenceService.TryGetNextDueDate(_item, completedAt, out DateTimeOffset nextDueDate))
            {
                DateTimeOffset localNextDue = nextDueDate.ToLocalTime();
                if (localNextDue.Date == today.AddDays(1))
                {
                    return Format("Todo.Recurrence.CompletedTodayTomorrow", RecurrenceSummaryText);
                }

                if (localNextDue.Date > today.AddDays(1))
                {
                    string formattedNextDate = localNextDue.Date.Year == today.Year
                        ? localNextDue.ToString("M/d")
                        : localNextDue.ToString("yyyy/M/d");
                    return Format("Todo.Recurrence.CompletedTodayNextDate", RecurrenceSummaryText, formattedNextDate);
                }
            }
            var due = localDueDate.Date;
            string statusText;

            if (HasDueTime)
            {
                string formattedDue = FormatDueDateTime(localDueDate);
                string formattedTime = FormatDueTime(localDueDate);
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
            }
            else
            {
                if (due == today)
                {
                    statusText = LocalizedText("Todo.Due.Today");
                }
                else if (due == today.AddDays(1))
                {
                    statusText = LocalizedText("Todo.Due.Tomorrow");
                }
                else
                {
                    string formattedDate = due.Year == today.Year
                        ? localDueDate.ToString("M/d")
                        : localDueDate.ToString("yyyy/M/d");
                    statusText = Format("Todo.Due.Date", formattedDate);
                }
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

    public string MetadataDueDateText
    {
        get
        {
            if (DueDate is not { } dueDate)
            {
                return LocalizedText("Todo.Menu.DueDate");
            }

            DateTimeOffset local = dueDate.ToLocalTime();
            var today = _clock().ToLocalTime().Date;
            var due = local.Date;

            if (due == today)
            {
                return HasDueTime
                    ? Format("Todo.Due.TodayAt", local.ToString("HH:mm"))
                    : LocalizedText("Todo.Due.Today");
            }

            if (due == today.AddDays(1))
            {
                return HasDueTime
                    ? Format("Todo.Due.TomorrowAt", local.ToString("HH:mm"))
                    : LocalizedText("Todo.Due.Tomorrow");
            }

            if (!HasDueTime)
            {
                return due.Year == today.Year
                    ? $"{local.Month}/{local.Day}"
                    : $"{local.Year}/{local.Month}/{local.Day}";
            }

            return $"{local.Month}/{local.Day} {local.Hour:00}:{local.Minute:00}";
        }
    }

    public string MetadataReminderText
    {
        get
        {
            if (ReminderOffsetMinutes is null)
            {
                return LocalizedText("Todo.Menu.Reminder");
            }

            if (TodoReminderOptions.IsReminderOff(ReminderOffsetMinutes))
            {
                return LocalizedText("Todo.Reminder.Off");
            }

            return ReminderOffsetMinutes switch
            {
                0 => LocalizedText("Todo.Reminder.AtDueTime"),
                60 => LocalizedText("Todo.Reminder.OneHourBefore"),
                1440 => LocalizedText("Todo.Reminder.OneDayBefore"),
                _ => Format("Todo.Reminder.MinutesBefore", ReminderOffsetMinutes.Value)
            };
        }
    }

    public string MetadataRecurrenceText => HasRecurrence
        ? RecurrenceSummaryText
        : LocalizedText("Todo.Menu.Recurrence");

    public string MetadataColorText => LocalizedText(TodoItem.GetColorMarkerLocalizationKey(ColorMarker));

    public Visibility ReminderMetadataVisibility => DueDate is not null && !IsCompleted
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RecurrenceMetadataVisibility => !IsCompleted
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ReminderSetVisibility => DueDate is not null &&
                                               !TodoReminderOptions.IsReminderOff(ReminderOffsetMinutes)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RecurrenceSetVisibility => HasRecurrence
        ? Visibility.Visible
        : Visibility.Collapsed;

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
        OnPropertyChanged(nameof(MetadataDueDateText));
        OnPropertyChanged(nameof(MetadataReminderText));
        OnPropertyChanged(nameof(MetadataRecurrenceText));
        OnPropertyChanged(nameof(MetadataColorText));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(CompletionToggleText));
        OnPropertyChanged(nameof(ImportantToggleText));
        OnPropertyChanged(nameof(TodayGroupHeaderText));
        OnPropertyChanged(nameof(StepsExpandTooltipText));
        OnPropertyChanged(nameof(PrimaryUrlTooltipText));
    }

    internal void RefreshDateDependentProperties()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueStatusText));
        OnPropertyChanged(nameof(DueStatusNormalVisibility));
        OnPropertyChanged(nameof(DueStatusOverdueVisibility));
        OnPropertyChanged(nameof(MetadataDueDateText));
    }

    internal void RefreshDetailProperties()
    {
        _cachedStepItemsSource = null;
        _cachedInlineStepsSource = null;
        _cachedAttachmentItemsSource = null;
        if (!HasSteps && _isStepsExpanded)
        {
            _isStepsExpanded = false;
            OnPropertyChanged(nameof(IsStepsExpanded));
            OnPropertyChanged(nameof(StepsExpandChevronGlyph));
        }

        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(NotesPlaceholderVisibility));
        OnPropertyChanged(nameof(NotesIndicatorVisibility));
        OnPropertyChanged(nameof(StepItemsSource));
        OnPropertyChanged(nameof(InlineStepsSource));
        OnPropertyChanged(nameof(AttachmentItemsSource));
        OnPropertyChanged(nameof(TotalStepCount));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(ExpandedStepsVisibility));
        OnPropertyChanged(nameof(StepsExpandTooltipText));
        OnPropertyChanged(nameof(CompletedStepCount));
        OnPropertyChanged(nameof(StepProgressText));
        OnPropertyChanged(nameof(StepProgressVisibility));
        OnPropertyChanged(nameof(AttachmentSummaryText));
        OnPropertyChanged(nameof(AttachmentSummaryVisibility));
        OnPropertyChanged(nameof(CardMetadataRowVisibility));
    }

    internal void UpdateTodayGroupHeaderState(bool isFirstTodayItem)
    {
        if (_isFirstTodayGroupItem != isFirstTodayItem)
        {
            _isFirstTodayGroupItem = isFirstTodayItem;
            OnPropertyChanged(nameof(IsFirstTodayGroupItem));
            OnPropertyChanged(nameof(TodayGroupDividerVisibility));
        }
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
        DateTime today = _clock().ToLocalTime().Date;
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

    public void ReleaseAttachmentThumbnails()
    {
        foreach (TodoAttachmentViewModel attachment in Attachments)
        {
            attachment.ReleaseThumbnail();
        }
    }

    private void UpdatePrimaryUrl(string? text)
    {
        string? extracted = ExtractPrimaryHttpUrl(text);
        if (!string.Equals(_primaryUrl, extracted, StringComparison.Ordinal))
        {
            _primaryUrl = extracted;
            OnPropertyChanged(nameof(PrimaryUrl));
            OnPropertyChanged(nameof(HasPrimaryUrl));
            OnPropertyChanged(nameof(PrimaryUrlVisibility));
            OnPropertyChanged(nameof(PrimaryUrlTooltipText));
        }
    }

    internal static string? ExtractPrimaryHttpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        int searchStart = 0;
        while (searchStart < span.Length)
        {
            int httpIdx = span[searchStart..].IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (httpIdx < 0)
            {
                break;
            }

            int start = searchStart + httpIdx;
            ReadOnlySpan<char> rest = span[start..];
            bool isHttp = rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            bool isHttps = !isHttp && rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isHttp && !isHttps)
            {
                searchStart = start + 4;
                continue;
            }

            int end = start + (isHttps ? 8 : 7);
            while (end < span.Length &&
                   !char.IsWhiteSpace(span[end]) &&
                   span[end] is not ('<' or '>' or '"' or '\'' or '`' or '）' or '】' or '》'))
            {
                end++;
            }

            ReadOnlySpan<char> candidateSpan = span[start..end];
            while (!candidateSpan.IsEmpty)
            {
                char tail = candidateSpan[^1];
                if (tail is '.' or ',' or ';' or ':' or '!' or '?' or ']' or '}' or '，' or '。' or '；' or '！' or '？')
                {
                    candidateSpan = candidateSpan[..^1];
                    continue;
                }

                if (tail == ')')
                {
                    int opens = 0;
                    int closes = 0;
                    foreach (char ch in candidateSpan)
                    {
                        if (ch == '(') opens++;
                        else if (ch == ')') closes++;
                    }

                    if (closes > opens)
                    {
                        candidateSpan = candidateSpan[..^1];
                        continue;
                    }
                }

                break;
            }

            string candidate = candidateSpan.ToString();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return uri.AbsoluteUri;
            }

            searchStart = end;
        }

        return null;
    }

    private static bool AreRecurrenceEqual(TodoRecurrence? left, TodoRecurrence? right)
    {
        string leftMode = TodoRecurrenceMode.Normalize(left?.Mode);
        string rightMode = TodoRecurrenceMode.Normalize(right?.Mode);
        return string.Equals(leftMode, rightMode, StringComparison.Ordinal) &&
               Nullable.Equals(left?.AnchorDueDate, right?.AnchorDueDate);
    }
}
