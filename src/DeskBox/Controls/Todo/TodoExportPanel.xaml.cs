using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DeskBox.Controls.Todo;

internal sealed partial class TodoExportPanel : UserControl
{
    private static readonly string[] s_supportedDateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-M-d",
        "yyyy/MM/dd",
        "yyyy/M/d",
        "yyyy.MM.dd",
        "yyyy.M.d"
    ];

    private readonly IReadOnlyList<TodoItem> _sourceItems;
    private readonly LocalizationService _loc;
    private readonly bool _isSelectionSubset;
    private readonly bool _showFlyoutChrome;
    private readonly HashSet<string> _selectedColors = new(StringComparer.Ordinal);
    private readonly Dictionary<TodoExportDatePreset, Button> _presetButtons;
    private readonly Dictionary<TodoExportDateBasis, (Button Button, Border Indicator)> _basisControls;
    private readonly Dictionary<string, (Button Button, Border Ring, FontIcon Check)> _colorChipControls = new(StringComparer.Ordinal);

    private readonly Style _segmentActiveStyle;
    private readonly Style _segmentIdleStyle;
    private readonly Style _chipActiveStyle;
    private readonly Style _chipIdleStyle;
    private readonly Style _colorDotButtonStyle;

    private TodoExportStatusScope _statusScope;
    private TodoExportDatePreset _datePreset = TodoExportDatePreset.AllTime;
    private TodoExportDateBasis _dateBasis = TodoExportDateBasis.ActiveInPeriod;
    private TodoExportFormat _format = TodoExportFormat.Xlsx;
    private DateOnly? _customStartDate;
    private DateOnly? _customEndDate;
    private bool _hasInvalidCustomDate;

    public TodoExportPanel(
        IReadOnlyList<TodoItem> sourceItems,
        LocalizationService localizationService,
        TodoExportStatusScope initialScope = TodoExportStatusScope.All,
        bool isSelectionSubset = false,
        bool showFlyoutChrome = false)
    {
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(localizationService);

        InitializeComponent();

        _sourceItems = sourceItems;
        _loc = localizationService;
        _isSelectionSubset = isSelectionSubset;
        _showFlyoutChrome = showFlyoutChrome;
        _statusScope = isSelectionSubset ? TodoExportStatusScope.All : initialScope;

        _segmentActiveStyle = (Style)Resources["ExportSegmentActiveButtonStyle"];
        _segmentIdleStyle = (Style)Resources["ExportSegmentIdleButtonStyle"];
        _chipActiveStyle = (Style)Resources["ExportChipActiveButtonStyle"];
        _chipIdleStyle = (Style)Resources["ExportChipIdleButtonStyle"];
        _colorDotButtonStyle = (Style)Resources["ExportColorDotButtonStyle"];

        _presetButtons = new Dictionary<TodoExportDatePreset, Button>
        {
            [TodoExportDatePreset.AllTime] = PresetAllTimeButton,
            [TodoExportDatePreset.ThisWeek] = PresetThisWeekButton,
            [TodoExportDatePreset.LastWeek] = PresetLastWeekButton,
            [TodoExportDatePreset.ThisMonth] = PresetThisMonthButton,
            [TodoExportDatePreset.LastMonth] = PresetLastMonthButton,
            [TodoExportDatePreset.ThisQuarter] = PresetThisQuarterButton,
            [TodoExportDatePreset.ThisYear] = PresetThisYearButton,
            [TodoExportDatePreset.CustomRange] = PresetCustomButton
        };

        _basisControls = new Dictionary<TodoExportDateBasis, (Button Button, Border Indicator)>
        {
            [TodoExportDateBasis.ActiveInPeriod] = (BasisActiveInPeriodButton, BasisActiveInPeriodIndicator),
            [TodoExportDateBasis.CompletedAt] = (BasisCompletedAtButton, BasisCompletedAtIndicator),
            [TodoExportDateBasis.CreatedAt] = (BasisCreatedAtButton, BasisCreatedAtIndicator),
            [TodoExportDateBasis.DueDate] = (BasisDueDateButton, BasisDueDateIndicator)
        };

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.Now.ToLocalTime().Date);
        _customStartDate = new DateOnly(today.Year, today.Month, 1);
        _customEndDate = today;

        if (showFlyoutChrome)
        {
            RootPanel.Width = 336;
            FlyoutHeaderGrid.Visibility = Visibility.Visible;
            FlyoutFooterPanel.Visibility = Visibility.Visible;
            DialogSubtitleText.Visibility = Visibility.Collapsed;
        }
        else
        {
            RootPanel.MinWidth = 360;
            RootPanel.MaxWidth = 420;
            FlyoutHeaderGrid.Visibility = Visibility.Collapsed;
            FlyoutFooterPanel.Visibility = Visibility.Collapsed;
            DialogSubtitleText.Visibility = isSelectionSubset ? Visibility.Visible : Visibility.Collapsed;
        }

        FilterCardBorder.Visibility = isSelectionSubset ? Visibility.Collapsed : Visibility.Visible;

        InitializeLocalizedLabels();
        BuildColorDotControls();

        CustomStartBox.Text = _customStartDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        CustomEndBox.Text = _customEndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        RefreshStatusVisuals();
        RefreshDatePresetVisuals();
        RefreshDateBasisVisuals();
        RefreshColorChipVisuals();
        RefreshFormatVisuals();
        UpdateDependentVisibility();

        CurrentOptions = BuildOptionsSnapshot();
        CurrentPreview = _isSelectionSubset
            ? BuildSelectionSubsetPreview(_sourceItems)
            : TodoExportService.BuildPreview(_sourceItems, CurrentOptions, DateTimeOffset.Now);
        ApplyPreviewToUi();
    }

    public TodoExportOptions CurrentOptions { get; private set; }

    public TodoExportPreview CurrentPreview { get; private set; }

    public bool CanExport => !_hasInvalidCustomDate && CurrentPreview.TotalCount > 0;

    public event EventHandler<TodoExportPreview>? PreviewChanged;

    public event EventHandler? ExportRequested;

    public event EventHandler? CancelRequested;

    private void InitializeLocalizedLabels()
    {
        FlyoutTitleText.Text = _isSelectionSubset
            ? _loc.T("Todo.Export.SelectedTitle")
            : _loc.T("Todo.Export.DialogTitle");

        if (_isSelectionSubset)
        {
            string subsetSubtitle = _loc.Format("Todo.Export.SelectedSubtitle", _sourceItems.Count);
            if (_showFlyoutChrome)
            {
                FlyoutSelectionSubtitleText.Text = subsetSubtitle;
                FlyoutSelectionSubtitleText.Visibility = Visibility.Visible;
            }
            else
            {
                DialogSubtitleText.Text = subsetSubtitle;
            }
        }

        string closeLabel = _loc.T("Common.Cancel");
        AutomationProperties.SetName(HeaderCloseButton, closeLabel);
        ToolTipService.SetToolTip(HeaderCloseButton, closeLabel);

        StatusSectionLabel.Text = _loc.T("Todo.Export.Section.Status");
        SetButtonLabel(StatusAllButton, _loc.T("Todo.Export.Scope.All"));
        SetButtonLabel(StatusCompletedButton, _loc.T("Todo.Export.Scope.CompletedOnly"));
        SetButtonLabel(StatusActiveButton, _loc.T("Todo.Export.Scope.ActiveOnly"));

        DateRangeSectionLabel.Text = _loc.T("Todo.Export.Section.DateRange");
        SetButtonLabel(PresetAllTimeButton, _loc.T("Todo.Export.DateRange.AllTime"));
        SetButtonLabel(PresetThisWeekButton, _loc.T("Todo.Export.DateRange.ThisWeek"));
        SetButtonLabel(PresetLastWeekButton, _loc.T("Todo.Export.DateRange.LastWeek"));
        SetButtonLabel(PresetThisMonthButton, _loc.T("Todo.Export.DateRange.ThisMonth"));
        SetButtonLabel(PresetLastMonthButton, _loc.T("Todo.Export.DateRange.LastMonth"));
        SetButtonLabel(PresetThisQuarterButton, _loc.T("Todo.Export.DateRange.ThisQuarter"));
        SetButtonLabel(PresetThisYearButton, _loc.T("Todo.Export.DateRange.ThisYear"));
        SetButtonLabel(PresetCustomButton, _loc.T("Todo.Export.DateRange.Custom"));

        AutomationProperties.SetName(CustomStartBox, _loc.T("Todo.Export.CustomStartDate"));
        AutomationProperties.SetName(CustomEndBox, _loc.T("Todo.Export.CustomEndDate"));

        LockedCompletedBasisText.Text = _loc.T("Todo.Export.Basis.LockedCompleted");
        BasisSectionLabel.Text = _loc.T("Todo.Export.Section.Basis");
        SetButtonLabel(BasisActiveInPeriodButton, _loc.T("Todo.Export.Basis.ActiveInPeriod"));
        SetButtonLabel(BasisCompletedAtButton, _loc.T("Todo.Export.Basis.CompletedAt"));
        SetButtonLabel(BasisCreatedAtButton, _loc.T("Todo.Export.Basis.CreatedAt"));
        SetButtonLabel(BasisDueDateButton, _loc.T("Todo.Export.Basis.DueDate"));

        ColorSectionLabel.Text = _loc.T("Todo.Export.Section.Colors");
        AllColorsHintText.Text = _loc.T("Todo.Export.Colors.All");
        SetButtonLabel(ClearColorsButton, _loc.T("Todo.Export.Colors.Clear"));

        FormatSectionLabel.Text = _loc.T("Todo.Export.Section.Format");
        SetButtonLabel(FormatXlsxButton, _loc.T("Todo.Export.Format.Xlsx"));
        SetButtonLabel(FormatCsvButton, _loc.T("Todo.Export.Format.Csv"));

        SetButtonLabel(CancelActionButton, _loc.T("Common.Cancel"));
        SetButtonLabel(ConfirmActionButton, _loc.T("Todo.Export.ConfirmButton"));
    }

    private static void SetButtonLabel(Button button, string label)
    {
        button.Content = label;
        AutomationProperties.SetName(button, label);
    }

    private void BuildColorDotControls()
    {
        ColorDotsGrid.Children.Clear();
        _colorChipControls.Clear();

        for (int index = 0; index < TodoItem.SupportedColorMarkers.Length; index++)
        {
            string colorMarker = TodoItem.SupportedColorMarkers[index];
            string colorName = _loc.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker));
            Color fillColor = ParseHexColor(TodoItem.GetColorMarkerHex(colorMarker));

            var dot = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(fillColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            WidgetTextShadow.SetCast(dot, false);

            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 9,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var dotHost = new Grid();
            dotHost.Children.Add(dot);
            dotHost.Children.Add(check);

            var ring = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Child = dotHost
            };

            var chipBtn = new Button
            {
                Style = _colorDotButtonStyle,
                Content = ring
            };
            ToolTipService.SetToolTip(chipBtn, colorName);
            AutomationProperties.SetName(chipBtn, colorName);

            string capturedColor = colorMarker;
            chipBtn.Click += (_, _) => ToggleColorMarker(capturedColor);

            Grid.SetColumn(chipBtn, index);
            ColorDotsGrid.Children.Add(chipBtn);
            _colorChipControls[colorMarker] = (chipBtn, ring, check);
        }
    }

    private void StatusAllButton_Click(object sender, RoutedEventArgs e) =>
        SetStatusScope(TodoExportStatusScope.All);

    private void StatusCompletedButton_Click(object sender, RoutedEventArgs e) =>
        SetStatusScope(TodoExportStatusScope.CompletedOnly);

    private void StatusActiveButton_Click(object sender, RoutedEventArgs e) =>
        SetStatusScope(TodoExportStatusScope.ActiveOnly);

    private void SetStatusScope(TodoExportStatusScope scope)
    {
        if (_statusScope == scope)
        {
            return;
        }

        _statusScope = scope;
        RefreshStatusVisuals();
        UpdateDependentVisibility();
        RecalculatePreview();
    }

    private void RefreshStatusVisuals()
    {
        ApplySegmentState(StatusAllButton, StatusAllIndicator, _statusScope == TodoExportStatusScope.All);
        ApplySegmentState(StatusCompletedButton, StatusCompletedIndicator, _statusScope == TodoExportStatusScope.CompletedOnly);
        ApplySegmentState(StatusActiveButton, StatusActiveIndicator, _statusScope == TodoExportStatusScope.ActiveOnly);
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out TodoExportDatePreset preset) &&
            _datePreset != preset)
        {
            _datePreset = preset;
            RefreshDatePresetVisuals();
            ValidateCustomDatesIfNeeded();
            UpdateDependentVisibility();
            RecalculatePreview();
        }
    }

    private void RefreshDatePresetVisuals()
    {
        foreach ((TodoExportDatePreset preset, Button btn) in _presetButtons)
        {
            btn.Style = preset == _datePreset ? _chipActiveStyle : _chipIdleStyle;
        }
    }

    private void BasisButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out TodoExportDateBasis basis) &&
            _dateBasis != basis)
        {
            _dateBasis = basis;
            RefreshDateBasisVisuals();
            RecalculatePreview();
        }
    }

    private void RefreshDateBasisVisuals()
    {
        foreach ((TodoExportDateBasis basis, (Button btn, Border indicator)) in _basisControls)
        {
            ApplySegmentState(btn, indicator, basis == _dateBasis);
        }
    }

    private void CustomDateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_datePreset != TodoExportDatePreset.CustomRange)
        {
            return;
        }

        ValidateCustomDatesIfNeeded();
        RecalculatePreview();
    }

    private void ValidateCustomDatesIfNeeded()
    {
        if (_datePreset != TodoExportDatePreset.CustomRange)
        {
            _hasInvalidCustomDate = false;
            return;
        }

        bool okStart = TryParseUserDate(CustomStartBox.Text, out DateOnly parsedStart);
        bool okEnd = TryParseUserDate(CustomEndBox.Text, out DateOnly parsedEnd);
        if (okStart && okEnd)
        {
            _customStartDate = parsedStart <= parsedEnd ? parsedStart : parsedEnd;
            _customEndDate = parsedStart <= parsedEnd ? parsedEnd : parsedStart;
            _hasInvalidCustomDate = false;
        }
        else
        {
            _hasInvalidCustomDate = true;
        }
    }

    private void ToggleColorMarker(string colorMarker)
    {
        if (!_selectedColors.Remove(colorMarker))
        {
            _selectedColors.Add(colorMarker);
        }

        RefreshColorChipVisuals();
        RecalculatePreview();
    }

    private void ClearColorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedColors.Count == 0)
        {
            return;
        }

        _selectedColors.Clear();
        RefreshColorChipVisuals();
        RecalculatePreview();
    }

    private void RefreshColorChipVisuals()
    {
        bool hasFilter = _selectedColors.Count > 0;
        foreach ((string color, (Button btn, Border ring, FontIcon check)) in _colorChipControls)
        {
            bool selected = _selectedColors.Contains(color);
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            ring.BorderBrush = selected
                ? new SolidColorBrush(ParseHexColor(TodoItem.GetColorMarkerHex(color)))
                : new SolidColorBrush(Colors.Transparent);
            btn.Opacity = !hasFilter
                ? 0.9
                : selected
                    ? 1.0
                    : 0.28;
        }

        AllColorsHintText.Visibility = hasFilter ? Visibility.Collapsed : Visibility.Visible;
        ClearColorsButton.Visibility = hasFilter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FormatXlsxButton_Click(object sender, RoutedEventArgs e) =>
        SetExportFormat(TodoExportFormat.Xlsx);

    private void FormatCsvButton_Click(object sender, RoutedEventArgs e) =>
        SetExportFormat(TodoExportFormat.Csv);

    private void SetExportFormat(TodoExportFormat format)
    {
        if (_format == format)
        {
            return;
        }

        _format = format;
        RefreshFormatVisuals();
        RecalculatePreview();
    }

    private void RefreshFormatVisuals()
    {
        ApplySegmentState(FormatXlsxButton, FormatXlsxIndicator, _format == TodoExportFormat.Xlsx);
        ApplySegmentState(FormatCsvButton, FormatCsvIndicator, _format == TodoExportFormat.Csv);
        FormatHintText.Text = _format == TodoExportFormat.Csv
            ? _loc.T("Todo.Export.Format.CsvHint")
            : _loc.T("Todo.Export.Format.XlsxHint");
    }

    private void ApplySegmentState(Button button, Border indicator, bool isActive)
    {
        button.Style = isActive ? _segmentActiveStyle : _segmentIdleStyle;
        indicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDependentVisibility()
    {
        if (_isSelectionSubset)
        {
            return;
        }

        CustomDateGrid.Visibility = _datePreset == TodoExportDatePreset.CustomRange
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool hasDateFilter = _datePreset != TodoExportDatePreset.AllTime;
        DateBasisSection.Visibility = hasDateFilter ? Visibility.Visible : Visibility.Collapsed;
        if (hasDateFilter)
        {
            bool lockedToCompleted = _statusScope == TodoExportStatusScope.CompletedOnly;
            LockedCompletedBasisHint.Visibility = lockedToCompleted ? Visibility.Visible : Visibility.Collapsed;
            DateBasisInteractivePanel.Visibility = lockedToCompleted ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void RecalculatePreview()
    {
        CurrentOptions = BuildOptionsSnapshot();
        CurrentPreview = _isSelectionSubset
            ? BuildSelectionSubsetPreview(_sourceItems)
            : TodoExportService.BuildPreview(_sourceItems, CurrentOptions, DateTimeOffset.Now);

        ApplyPreviewToUi();
        PreviewChanged?.Invoke(this, CurrentPreview);
    }

    private void ApplyPreviewToUi()
    {
        if (_hasInvalidCustomDate)
        {
            PreviewStatusIcon.Glyph = "\uE7BA";
            PreviewPrimaryText.Text = _loc.T("Todo.Export.CustomDateInvalid");
            PreviewSecondaryText.Visibility = Visibility.Collapsed;
        }
        else if (CurrentPreview.TotalCount == 0)
        {
            PreviewStatusIcon.Glyph = "\uE946";
            PreviewPrimaryText.Text = _loc.T("Todo.Export.PreviewEmpty");
            PreviewSecondaryText.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewStatusIcon.Glyph = "\uE946";
            PreviewPrimaryText.Text = _loc.Format(
                "Todo.Export.PreviewSummary",
                CurrentPreview.TotalCount,
                CurrentPreview.CompletedCount,
                CurrentPreview.ActiveCount);

            if (CurrentPreview.RangeStart is { } start && CurrentPreview.RangeEnd is { } end)
            {
                PreviewSecondaryText.Text = $"{start:yyyy-MM-dd} – {end:yyyy-MM-dd}";
                PreviewSecondaryText.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewSecondaryText.Visibility = Visibility.Collapsed;
            }
        }

        ConfirmActionButton.IsEnabled = CanExport;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanExport)
        {
            ExportRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private TodoExportOptions BuildOptionsSnapshot()
    {
        return new TodoExportOptions
        {
            StatusScope = _statusScope,
            DatePreset = _datePreset,
            DateBasis = _dateBasis,
            CustomStartDate = _customStartDate,
            CustomEndDate = _customEndDate,
            SelectedColors = _selectedColors.Count > 0
                ? new HashSet<string>(_selectedColors, StringComparer.Ordinal)
                : null,
            Format = _format
        };
    }

    private static TodoExportPreview BuildSelectionSubsetPreview(IReadOnlyList<TodoItem> items)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        TodoItem[] valid = items
            .Where(i => !i.IsDeleted && !string.IsNullOrWhiteSpace(i.Text))
            .ToArray();
        int completed = valid.Count(i => i.IsCompleted);
        int active = valid.Length - completed;
        int important = valid.Count(i => i.IsImportant);
        int overdue = valid.Count(i => TodoExportService.IsItemOverdue(i, now));
        return new TodoExportPreview(
            valid,
            valid.Length,
            completed,
            active,
            important,
            overdue,
            null,
            null);
    }

    private static bool TryParseUserDate(string? input, out DateOnly result)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            result = default;
            return false;
        }

        return DateOnly.TryParseExact(
            input.Trim(),
            s_supportedDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);
    }

    private static Color ParseHexColor(string hex)
    {
        string clean = hex.TrimStart('#');
        if (clean.Length == 6 &&
            byte.TryParse(clean[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(clean[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(clean[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }

        return Colors.Gray;
    }
}
