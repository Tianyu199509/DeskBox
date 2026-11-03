using System.ComponentModel;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls.Todo;

public sealed partial class TodoQuickAddBar : UserControl, INotifyPropertyChanged
{
    private TodoWidgetViewModel? _viewModel;
    private bool _isComposerOpen;

    public TodoQuickAddBar()
    {
        InitializeComponent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SubmitRequested;

    public event EventHandler? ExpandRequested;

    public event EventHandler? EscapeUnfocusRequested;

    public event EventHandler<FrameworkElement>? DueDateFlyoutRequested;

    public bool IsComposerOpen => _isComposerOpen;

    public TodoWidgetViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value))
            {
                return;
            }

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            }

            RaiseAllBindings();
        }
    }

    public Visibility CollapsedButtonVisibility =>
        _isComposerOpen ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ExpandedCardVisibility =>
        _isComposerOpen ? Visibility.Visible : Visibility.Collapsed;

    public string InputText
    {
        get => _viewModel?.InputText ?? string.Empty;
        set
        {
            if (_viewModel is not null && !string.Equals(_viewModel.InputText, value, StringComparison.Ordinal))
            {
                _viewModel.InputText = value;
                RaisePropertyChanged(nameof(CanSaveDraft));
                RaisePropertyChanged(nameof(CollapsedCardText));
            }
        }
    }

    public string PlaceholderText => _viewModel?.AddPlaceholderText ?? string.Empty;

    public string CollapsedCardText
    {
        get
        {
            string draft = _viewModel?.InputText?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(draft))
            {
                return PlaceholderText;
            }

            int newlineIndex = draft.IndexOfAny(['\r', '\n']);
            return newlineIndex >= 0 ? draft[..newlineIndex] : draft;
        }
    }

    public bool CanSaveDraft => !string.IsNullOrWhiteSpace(_viewModel?.InputText);

    public double InputFontSize => _viewModel?.TextSize ?? SettingsService.DefaultTextSize;

    public double SecondaryFontSize => _viewModel?.SecondaryTextSize ?? (SettingsService.DefaultTextSize - 1);

    public string ImportantGlyph => _viewModel?.DraftImportantGlyph ?? "\uE734";

    public string ImportantTooltipText => _viewModel?.DraftImportantTooltipText ?? string.Empty;

    public string DueDateTooltipText => _viewModel?.DraftDueDateText ?? string.Empty;

    public string DueDateChipText => _viewModel?.DraftDueDateActive == true
        ? _viewModel.DraftDueDateText
        : string.Empty;

    public Visibility DueDateChipVisibility => _viewModel?.DraftDueDateActive == true
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ExpandTooltipText => _viewModel?.ExpandInputTooltipText ?? string.Empty;

    public string HintText => _viewModel?.QuickComposerHintText ?? string.Empty;

    public string CancelTooltipText => _viewModel?.CancelText ?? string.Empty;

    public string SaveTooltipText => _viewModel?.DetailSaveText ?? string.Empty;

    public void FocusInput()
    {
        OpenComposer(focus: true);
    }

    public void OpenComposer(bool focus = true)
    {
        if (!_isComposerOpen)
        {
            _isComposerOpen = true;
            RaisePropertyChanged(nameof(IsComposerOpen));
            RaisePropertyChanged(nameof(CollapsedButtonVisibility));
            RaisePropertyChanged(nameof(ExpandedCardVisibility));
        }

        if (focus)
        {
            if (DispatcherQueue is { } dispatcher)
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (_isComposerOpen)
                    {
                        InlineComposerTextBox.Focus(FocusState.Programmatic);
                        InlineComposerTextBox.SelectionStart = InlineComposerTextBox.Text.Length;
                        InlineComposerTextBox.SelectionLength = 0;
                    }
                });
            }
            else
            {
                InlineComposerTextBox.Focus(FocusState.Programmatic);
                InlineComposerTextBox.SelectionStart = InlineComposerTextBox.Text.Length;
                InlineComposerTextBox.SelectionLength = 0;
            }
        }
    }

    public void CloseComposer(bool clearDraft)
    {
        if (clearDraft)
        {
            if (_viewModel is not null)
            {
                _viewModel.InputText = string.Empty;
                _viewModel.DraftImportant = false;
                _viewModel.SetDraftDueDate(null, hasDueTime: false);
            }
            else
            {
                InlineComposerTextBox.Text = string.Empty;
            }
        }

        if (_isComposerOpen)
        {
            _isComposerOpen = false;
            RaisePropertyChanged(nameof(IsComposerOpen));
            RaisePropertyChanged(nameof(CollapsedButtonVisibility));
            RaisePropertyChanged(nameof(ExpandedCardVisibility));
            RaisePropertyChanged(nameof(CollapsedCardText));
            RaisePropertyChanged(nameof(CanSaveDraft));
        }
    }

    public void ReleaseVisualHandlers()
    {
        ViewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null:
            case "":
                RaiseAllBindings();
                break;
            case nameof(TodoWidgetViewModel.InputText):
                RaisePropertyChanged(nameof(InputText));
                RaisePropertyChanged(nameof(CanSaveDraft));
                RaisePropertyChanged(nameof(CollapsedCardText));
                break;
            case nameof(TodoWidgetViewModel.AddPlaceholderText):
                RaisePropertyChanged(nameof(PlaceholderText));
                RaisePropertyChanged(nameof(CollapsedCardText));
                break;
            case nameof(TodoWidgetViewModel.TextSize):
            case nameof(TodoWidgetViewModel.SecondaryTextSize):
                RaisePropertyChanged(nameof(InputFontSize));
                RaisePropertyChanged(nameof(SecondaryFontSize));
                break;
            case nameof(TodoWidgetViewModel.DraftImportant):
            case nameof(TodoWidgetViewModel.DraftImportantGlyph):
            case nameof(TodoWidgetViewModel.DraftImportantTooltipText):
                RaisePropertyChanged(nameof(ImportantGlyph));
                RaisePropertyChanged(nameof(ImportantTooltipText));
                break;
            case nameof(TodoWidgetViewModel.DraftDueDate):
            case nameof(TodoWidgetViewModel.DraftHasDueTime):
            case nameof(TodoWidgetViewModel.DraftDueDateText):
            case nameof(TodoWidgetViewModel.DraftDueDateActive):
                RaisePropertyChanged(nameof(DueDateTooltipText));
                RaisePropertyChanged(nameof(DueDateChipText));
                RaisePropertyChanged(nameof(DueDateChipVisibility));
                break;
            case nameof(TodoWidgetViewModel.ExpandInputTooltipText):
                RaisePropertyChanged(nameof(ExpandTooltipText));
                break;
            case nameof(TodoWidgetViewModel.QuickComposerHintText):
            case nameof(TodoWidgetViewModel.EditorEnterBehavior):
                RaisePropertyChanged(nameof(HintText));
                break;
            case nameof(TodoWidgetViewModel.CancelText):
                RaisePropertyChanged(nameof(CancelTooltipText));
                break;
            case nameof(TodoWidgetViewModel.DetailSaveText):
                RaisePropertyChanged(nameof(SaveTooltipText));
                break;
        }
    }

    private void AddTaskCardButton_Click(object sender, RoutedEventArgs e)
    {
        OpenComposer(focus: true);
    }

    private void InlineComposerTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ignore IME composition confirmation key events (VK_PROCESSKEY = 229).
        if ((int)e.OriginalKey == 229 || (int)e.Key == 229)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            bool ctrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
            bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
            string enterBehavior = _viewModel?.EditorEnterBehavior ?? SettingsService.EditorEnterBehaviorCtrlEnterSaves;

            if (!shift && (ctrl || SettingsService.ShouldSubmitEditorOnEnter(enterBehavior, ctrl)))
            {
                if (enterBehavior == SettingsService.EditorEnterBehaviorEnterSaves && ctrl)
                {
                    // In EnterSaves mode, Ctrl+Enter inserts a newline explicitly.
                    e.Handled = true;
                    InsertNewLineAtSelection();
                    return;
                }

                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(InlineComposerTextBox.Text))
                {
                    SubmitRequested?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            if (enterBehavior == SettingsService.EditorEnterBehaviorEnterSaves && ctrl)
            {
                e.Handled = true;
                InsertNewLineAtSelection();
                return;
            }

            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            bool isEmpty = string.IsNullOrWhiteSpace(InlineComposerTextBox.Text) &&
                           _viewModel?.DraftImportant != true &&
                           _viewModel?.DraftDueDate is null;
            CloseComposer(clearDraft: isEmpty);
            EscapeUnfocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void InsertNewLineAtSelection()
    {
        string text = InlineComposerTextBox.Text ?? string.Empty;
        int start = Math.Clamp(InlineComposerTextBox.SelectionStart, 0, text.Length);
        int length = Math.Clamp(InlineComposerTextBox.SelectionLength, 0, text.Length - start);
        InlineComposerTextBox.Text = string.Concat(
            text.AsSpan(0, start),
            "\r",
            text.AsSpan(start + length));
        InlineComposerTextBox.SelectionStart = start + 1;
        InlineComposerTextBox.SelectionLength = 0;
    }

    private void QuickDueDateButton_Click(object sender, RoutedEventArgs e)
    {
        DueDateFlyoutRequested?.Invoke(this, QuickDueDateButton);
    }

    private void QuickImportantButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.DraftImportant = !_viewModel.DraftImportant;
        FocusInput();
    }

    private void ExpandToDetailButton_Click(object sender, RoutedEventArgs e)
    {
        CloseComposer(clearDraft: false);
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InlineComposerCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseComposer(clearDraft: true);
        EscapeUnfocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InlineComposerSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InlineComposerTextBox.Text))
        {
            SubmitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseAllBindings()
    {
        RaisePropertyChanged(nameof(IsComposerOpen));
        RaisePropertyChanged(nameof(CollapsedButtonVisibility));
        RaisePropertyChanged(nameof(ExpandedCardVisibility));
        RaisePropertyChanged(nameof(InputText));
        RaisePropertyChanged(nameof(PlaceholderText));
        RaisePropertyChanged(nameof(CollapsedCardText));
        RaisePropertyChanged(nameof(CanSaveDraft));
        RaisePropertyChanged(nameof(InputFontSize));
        RaisePropertyChanged(nameof(SecondaryFontSize));
        RaisePropertyChanged(nameof(ImportantGlyph));
        RaisePropertyChanged(nameof(ImportantTooltipText));
        RaisePropertyChanged(nameof(DueDateTooltipText));
        RaisePropertyChanged(nameof(DueDateChipText));
        RaisePropertyChanged(nameof(DueDateChipVisibility));
        RaisePropertyChanged(nameof(ExpandTooltipText));
        RaisePropertyChanged(nameof(HintText));
        RaisePropertyChanged(nameof(CancelTooltipText));
        RaisePropertyChanged(nameof(SaveTooltipText));
    }

    private void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
