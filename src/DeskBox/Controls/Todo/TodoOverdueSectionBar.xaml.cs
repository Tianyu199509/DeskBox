using System.ComponentModel;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.Todo;

public sealed partial class TodoOverdueSectionBar : UserControl, INotifyPropertyChanged
{
    private TodoWidgetViewModel? _viewModel;

    public TodoOverdueSectionBar()
    {
        InitializeComponent();
        SyncBarVisibility();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ToggleExpandRequested;

    public event EventHandler? RescheduleRequested;

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

    public Visibility BarVisibility => _viewModel?.TodayOverdueBarVisibility ?? Visibility.Collapsed;

    public Visibility ToggleButtonVisibility => _viewModel?.OverdueSectionToggleVisibility ?? Visibility.Visible;

    public Visibility TabSummaryVisibility => _viewModel?.OverdueSectionSummaryVisibility ?? Visibility.Collapsed;

    public string HeaderText => _viewModel?.TodayOverdueHeaderText ?? string.Empty;

    public string TabSummaryText => _viewModel?.OverdueTabSummaryText ?? string.Empty;

    public string ExpandGlyph => _viewModel?.TodayOverdueExpandGlyph ?? "\uE70D";

    public string ToggleTooltipText => _viewModel?.TodayOverdueToggleTooltipText ?? string.Empty;

    public string RescheduleText => _viewModel?.RescheduleOverdueText ?? string.Empty;

    public double SecondaryFontSize => _viewModel?.SecondaryTextSize ?? (SettingsService.DefaultTextSize - 1);

    public double IconFontSize => _viewModel?.SmallIconSize ?? 11;

    public void ReleaseVisualHandlers()
    {
        ViewModel = null;
    }

    private void ToggleExpandButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RescheduleButton_Click(object sender, RoutedEventArgs e)
    {
        RescheduleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case null:
            case "":
                RaiseAllBindings();
                break;
            case nameof(TodoWidgetViewModel.TodayOverdueBarVisibility):
                SyncBarVisibility();
                RaisePropertyChanged(nameof(BarVisibility));
                break;
            case nameof(TodoWidgetViewModel.OverdueSectionToggleVisibility):
            case nameof(TodoWidgetViewModel.OverdueSectionSummaryVisibility):
                RaisePropertyChanged(nameof(ToggleButtonVisibility));
                RaisePropertyChanged(nameof(TabSummaryVisibility));
                break;
            case nameof(TodoWidgetViewModel.TodayOverdueHeaderText):
                RaisePropertyChanged(nameof(HeaderText));
                break;
            case nameof(TodoWidgetViewModel.OverdueTabSummaryText):
                RaisePropertyChanged(nameof(TabSummaryText));
                break;
            case nameof(TodoWidgetViewModel.TodayOverdueExpandGlyph):
                RaisePropertyChanged(nameof(ExpandGlyph));
                break;
            case nameof(TodoWidgetViewModel.TodayOverdueToggleTooltipText):
                RaisePropertyChanged(nameof(ToggleTooltipText));
                break;
            case nameof(TodoWidgetViewModel.RescheduleOverdueText):
                RaisePropertyChanged(nameof(RescheduleText));
                break;
            case nameof(TodoWidgetViewModel.SecondaryTextSize):
            case nameof(TodoWidgetViewModel.SmallIconSize):
                RaisePropertyChanged(nameof(SecondaryFontSize));
                RaisePropertyChanged(nameof(IconFontSize));
                break;
        }
    }

    private void RaiseAllBindings()
    {
        SyncBarVisibility();
        RaisePropertyChanged(nameof(BarVisibility));
        RaisePropertyChanged(nameof(ToggleButtonVisibility));
        RaisePropertyChanged(nameof(TabSummaryVisibility));
        RaisePropertyChanged(nameof(HeaderText));
        RaisePropertyChanged(nameof(TabSummaryText));
        RaisePropertyChanged(nameof(ExpandGlyph));
        RaisePropertyChanged(nameof(ToggleTooltipText));
        RaisePropertyChanged(nameof(RescheduleText));
        RaisePropertyChanged(nameof(SecondaryFontSize));
        RaisePropertyChanged(nameof(IconFontSize));
    }

    private void SyncBarVisibility()
    {
        Visibility = BarVisibility;
    }

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
