using DeskBox.Contracts;
using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Native WinUI 3 Fluent 2 leaf view for the desktop global search widget.
/// Adapts across L1 MicroTile, L2 CompactBar, L3W WideStrip, L3 MediumCard, and L4 LargeHub.
/// </summary>
public sealed partial class SearchWidgetContent : UserControl
{
    private readonly SearchWidgetViewModel _viewModel;
    private readonly LocalizationService _localization;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _instantHandoffTimer;

    private SearchWidgetLayoutTier _currentTier = SearchWidgetLayoutTier.MediumCard;
    private bool _isCompactCollapsed;
    private bool _isWindowVisible = true;
    private bool _isSyncingTextFromViewModel;
    private bool _disposed;
    private int _recentIconLoadGeneration;

    public SearchWidgetContent(
        SearchWidgetViewModel viewModel,
        LocalizationService localization)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        InitializeComponent();
        ImeCompositionGuard.AttachAncestors(SearchInputBox);
        SearchInputBox.TextCompositionEnded += SearchInputBox_TextCompositionEnded;

        if (DispatcherQueue is { } dq)
        {
            _instantHandoffTimer = dq.CreateTimer();
            _instantHandoffTimer.Interval = TimeSpan.FromMilliseconds(280);
            _instantHandoffTimer.IsRepeating = false;
            _instantHandoffTimer.Tick += InstantHandoffTimer_Tick;
        }

        _viewModel.StateRefreshed += ViewModel_StateRefreshed;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.FeedbackRequested += ViewModel_FeedbackRequested;
        ActualThemeChanged += SearchWidgetContent_ActualThemeChanged;

        ApplyLocalizedStrings();
        SyncFromResidentViewModel();
    }

    public SearchWidgetViewModel ViewModel => _viewModel;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    public void SyncFromResidentViewModel()
    {
        if (_disposed)
        {
            return;
        }

        ApplyLocalizedStrings();
        SyncInputFromViewModel();
        UpdateQuickModePillsVisualState();
        UpdateCalculatorAndIntentUI();
        UpdateStatusAndHotkeyUI();
        RebuildAllDataLists();
        UpdateTierVisuals();
    }

    public void ApplyAppearanceVisuals()
    {
        if (_disposed)
        {
            return;
        }

        SyncFromResidentViewModel();
    }

    public void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        if (collapsed)
        {
            _instantHandoffTimer?.Stop();
        }
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (!visible)
        {
            _instantHandoffTimer?.Stop();
        }
    }

    public void DisposeLeafOnly()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _recentIconLoadGeneration);
        if (_instantHandoffTimer is not null)
        {
            _instantHandoffTimer.Stop();
            _instantHandoffTimer.Tick -= InstantHandoffTimer_Tick;
        }

        SearchInputBox.TextCompositionEnded -= SearchInputBox_TextCompositionEnded;
        _viewModel.StateRefreshed -= ViewModel_StateRefreshed;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.FeedbackRequested -= ViewModel_FeedbackRequested;
        ActualThemeChanged -= SearchWidgetContent_ActualThemeChanged;
    }

    private void SearchWidgetContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyAppearanceVisuals();
    }

    private void ViewModel_StateRefreshed()
    {
        SyncFromResidentViewModel();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName is nameof(SearchWidgetViewModel.QueryText) or
            nameof(SearchWidgetViewModel.ActiveQuickMode))
        {
            SyncInputFromViewModel();
            UpdateQuickModePillsVisualState();
            UpdateCalculatorAndIntentUI();
            UpdateTierVisuals();
        }
        else if (e.PropertyName is nameof(SearchWidgetViewModel.HasCalcResult) or
                 nameof(SearchWidgetViewModel.CalcResultText))
        {
            UpdateCalculatorAndIntentUI();
        }
        else if (e.PropertyName is nameof(SearchWidgetViewModel.EffectivePrivacyMaskActive) or
                 nameof(SearchWidgetViewModel.IsPointerHovering))
        {
            UpdateTierVisuals();
        }
    }

    private void ViewModel_FeedbackRequested(object? sender, WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        UpdateTierVisuals(e.NewSize.Width, e.NewSize.Height);
    }

    private void UserControl_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.SetPointerHovering(true);
        UpdateCompactHoverQuickVisibility();
    }

    private void UserControl_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.SetPointerHovering(false);
        UpdateCompactHoverQuickVisibility();
    }

    private void SearchInputBox_Loaded(object sender, RoutedEventArgs e)
    {
        HideNativeTextBoxDeleteButton();
    }

    private void SearchInputBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "search-widget-input");
        ImeCompositionGuard.AttachAncestors(SearchInputBox);
    }

    private void SearchInputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        ImeCompositionGuard.AttachAncestors(SearchInputBox);
        HideNativeTextBoxDeleteButton();
        SearchInputFocusUnderline.Visibility = Visibility.Visible;
        UpdateCompactHoverQuickVisibility();
    }

    private void HideNativeTextBoxDeleteButton()
    {
        if (SearchRecommendationLayoutEngine.FindVisualChild<Button>(SearchInputBox, "DeleteButton") is { } nativeDeleteButton)
        {
            nativeDeleteButton.MaxWidth = 0;
            nativeDeleteButton.MaxHeight = 0;
            nativeDeleteButton.Opacity = 0;
            nativeDeleteButton.IsHitTestVisible = false;
        }
    }

    private void SearchInputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SearchInputFocusUnderline.Visibility = Visibility.Collapsed;
        UpdateCompactHoverQuickVisibility();
    }

    private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingTextFromViewModel || _disposed)
        {
            return;
        }

        if (ImeCompositionGuard.IsActivelyComposing(SearchInputBox))
        {
            _instantHandoffTimer?.Stop();
            return;
        }

        _viewModel.QueryText = SearchInputBox.Text ?? string.Empty;
        UpdateCompactHoverQuickVisibility();
        ScheduleInstantHandoffIfNeeded();
    }

    private void SearchInputBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _ = DispatcherQueue?.TryEnqueue(() =>
        {
            if (_disposed || _isSyncingTextFromViewModel || ImeCompositionGuard.IsActivelyComposing(SearchInputBox))
            {
                return;
            }

            _viewModel.QueryText = SearchInputBox.Text ?? string.Empty;
            UpdateCompactHoverQuickVisibility();
            ScheduleInstantHandoffIfNeeded();
        });
    }

    private async void SearchInputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_disposed || ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _instantHandoffTimer?.Stop();
            await _viewModel.SubmitAsync();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_viewModel.HasQueryText || _viewModel.HasActiveQuickMode)
            {
                e.Handled = true;
                _instantHandoffTimer?.Stop();
                _viewModel.ClearQueryAndMode();
            }
            return;
        }

        if (e.Key == VirtualKey.Back && ! _viewModel.HasQueryText && _viewModel.HasActiveQuickMode)
        {
            e.Handled = true;
            _viewModel.HandleEmptyBackspace();
        }
    }

    private void InstantHandoffTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_disposed || _isCompactCollapsed || !_isWindowVisible || ImeCompositionGuard.IsActivelyComposing(SearchInputBox))
        {
            return;
        }

        if (_viewModel.ShouldTriggerInstantHandoff())
        {
            _ = _viewModel.SubmitAsync();
        }
    }

    private void ScheduleInstantHandoffIfNeeded()
    {
        if (_instantHandoffTimer is null)
        {
            return;
        }

        _instantHandoffTimer.Stop();
        if (_viewModel.ShouldTriggerInstantHandoff() && !_isCompactCollapsed && _isWindowVisible)
        {
            _instantHandoffTimer.Start();
        }
    }

    private void MicroTileButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenFullPopup();
    }

    private void QuickTodoButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleQuickMode(SearchQuickMode.Todo);
        FocusInputForTyping();
    }

    private void QuickNoteButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleQuickMode(SearchQuickMode.Note);
        FocusInputForTyping();
    }

    private void QuickCommandButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleQuickMode(SearchQuickMode.Command);
        FocusInputForTyping();
    }

    private void ClearLatchedModeButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearQuickMode();
        FocusInputForTyping();
    }

    private void ClearInputButton_Click(object sender, RoutedEventArgs e)
    {
        _instantHandoffTimer?.Stop();
        _viewModel.ClearQueryAndMode();
        FocusInputForTyping();
    }

    private async void CalcResultButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SubmitAsync();
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        _instantHandoffTimer?.Stop();
        await _viewModel.SubmitAsync();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearRecentQueries();
    }

    private void PrivacyMaskButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.TogglePrivacyMask();
    }

    private void ExpandPopupButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenFullPopup();
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSearchSettings();
    }

    private void FocusInputForTyping()
    {
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "search-widget-focus");
        ImeCompositionGuard.AttachAncestors(SearchInputBox);
        if (DispatcherQueue is { } dispatcher)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (!_disposed && _isWindowVisible)
                {
                    SearchInputBox.Focus(FocusState.Programmatic);
                    SearchInputBox.SelectionStart = SearchInputBox.Text.Length;
                    SearchInputBox.SelectionLength = 0;
                }
            });
            return;
        }

        SearchInputBox.Focus(FocusState.Programmatic);
        SearchInputBox.SelectionStart = SearchInputBox.Text.Length;
        SearchInputBox.SelectionLength = 0;
    }

    private void ApplyLocalizedStrings()
    {
        MicroTitleText.Text = _localization.T("Search.Widget.MicroTitle");
        SearchInputBox.PlaceholderText = _viewModel.EffectivePlaceholderText;

        ApplyQuickPillContent(QuickTodoPrefix, QuickTodoLabel, _localization.T("Search.Widget.QuickTodo"));
        ApplyQuickPillContent(QuickNotePrefix, QuickNoteLabel, _localization.T("Search.Widget.QuickNote"));
        ApplyQuickPillContent(QuickCommandPrefix, QuickCommandLabel, _localization.T("Search.Widget.QuickCommand"));

        ToolTipService.SetToolTip(QuickTodoButton, _localization.T("Search.Widget.TodoHint"));
        ToolTipService.SetToolTip(QuickNoteButton, _localization.T("Search.Widget.NoteHint"));
        ToolTipService.SetToolTip(QuickCommandButton, _localization.T("Search.Widget.CommandHint"));
        ToolTipService.SetToolTip(CompactQuickTodoButton, _localization.T("Search.Widget.QuickTodo"));
        ToolTipService.SetToolTip(CompactQuickNoteButton, _localization.T("Search.Widget.QuickNote"));
        ToolTipService.SetToolTip(CompactQuickCommandButton, _localization.T("Search.Widget.QuickCommand"));
        ToolTipService.SetToolTip(ClearInputButton, _localization.T("Search.Widget.ClearInput"));
        ToolTipService.SetToolTip(ClearLatchedModeButton, _localization.T("Search.Widget.ClearInput"));

        HistorySectionHeaderText.Text = _localization.T("Search.Widget.PinnedAndRecent");
        RecentSubHeaderText.Text = _localization.T("Search.Widget.RecentQueries");
        ClearHistoryLabel.Text = _localization.T("Search.Widget.ClearHistory");
        EmptyHistoryText.Text = _localization.T("Search.Widget.EmptyHistory");

        RecentOpenedHeaderText.Text = _localization.T("Search.Widget.RecentOpened");
        EmptyRecentOpenedText.Text = _localization.T("Search.Widget.EmptyRecentOpened");

        ExpandPopupLabel.Text = _localization.T("Search.Widget.ExpandPopup");
        OpenSettingsLabel.Text = _localization.T("Search.Widget.Settings");

        AutomationProperties.SetName(SearchInputBox, _localization.T("Search.Widget.Placeholder"));
        AutomationProperties.SetName(SubmitButton, _localization.T("Search.Title"));
        AutomationProperties.SetName(QuickTodoButton, _localization.T("Search.Widget.QuickTodo"));
        AutomationProperties.SetName(QuickNoteButton, _localization.T("Search.Widget.QuickNote"));
        AutomationProperties.SetName(QuickCommandButton, _localization.T("Search.Widget.QuickCommand"));
        AutomationProperties.SetName(ClearHistoryButton, _localization.T("Search.Widget.ClearHistory"));
        AutomationProperties.SetName(PrivacyMaskButton, _localization.T("Search.Widget.PrivacyMask"));
        AutomationProperties.SetName(ExpandPopupButton, _localization.T("Search.Widget.ExpandPopup"));
        AutomationProperties.SetName(OpenSettingsButton, _localization.T("Search.Widget.Settings"));
        AutomationProperties.SetName(MicroTileButton, _localization.T("Search.Widget.MicroTitle"));
    }

    internal static (string Prefix, string Label) SplitQuickPillText(string localizedText)
    {
        string trimmed = localizedText?.Trim() ?? string.Empty;
        int spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex > 0 && spaceIndex + 1 < trimmed.Length)
        {
            return (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].TrimStart());
        }

        return (string.Empty, trimmed);
    }

    private static void ApplyQuickPillContent(TextBlock prefixBlock, TextBlock labelBlock, string localizedText)
    {
        (string prefix, string label) = SplitQuickPillText(localizedText);
        prefixBlock.Text = prefix;
        prefixBlock.Visibility = string.IsNullOrEmpty(prefix) ? Visibility.Collapsed : Visibility.Visible;
        labelBlock.Text = label;
    }

    private void SyncInputFromViewModel()
    {
        if (string.Equals(SearchInputBox.Text ?? string.Empty, _viewModel.QueryText, StringComparison.Ordinal))
        {
            SearchInputBox.PlaceholderText = _viewModel.EffectivePlaceholderText;
            return;
        }

        _isSyncingTextFromViewModel = true;
        try
        {
            SearchInputBox.Text = _viewModel.QueryText;
            SearchInputBox.SelectionStart = SearchInputBox.Text.Length;
            SearchInputBox.SelectionLength = 0;
            SearchInputBox.PlaceholderText = _viewModel.EffectivePlaceholderText;
        }
        finally
        {
            _isSyncingTextFromViewModel = false;
        }
    }

    private void UpdateQuickModePillsVisualState()
    {
        ApplyQuickPillState(QuickTodoButton, QuickTodoPrefix, QuickTodoLabel, _viewModel.ActiveQuickMode == SearchQuickMode.Todo);
        ApplyQuickPillState(QuickNoteButton, QuickNotePrefix, QuickNoteLabel, _viewModel.ActiveQuickMode == SearchQuickMode.Note);
        ApplyQuickPillState(QuickCommandButton, QuickCommandPrefix, QuickCommandLabel, _viewModel.ActiveQuickMode == SearchQuickMode.Command);

        if (_viewModel.HasActiveQuickMode)
        {
            LatchedModeLabel.Text = _viewModel.ActiveQuickMode switch
            {
                SearchQuickMode.Todo => _localization.T("Search.Widget.QuickTodo"),
                SearchQuickMode.Note => _localization.T("Search.Widget.QuickNote"),
                SearchQuickMode.Command => _localization.T("Search.Widget.QuickCommand"),
                _ => string.Empty
            };
            LatchedModeBadge.Visibility = Visibility.Visible;
            SearchBoxGlyphIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            LatchedModeBadge.Visibility = Visibility.Collapsed;
            SearchBoxGlyphIcon.Visibility = Visibility.Visible;
        }
    }

    private void ApplyQuickPillState(Button button, TextBlock prefix, TextBlock label, bool isActive)
    {
        if (isActive)
        {
            button.Background = ResolveThemeBrush("ControlFillColorSecondaryBrush");
            button.BorderBrush = ResolveThemeBrush("ControlStrokeColorDefaultBrush");
            button.BorderThickness = new Thickness(1);
            prefix.Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush");
            label.Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush");
        }
        else
        {
            button.Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
            button.BorderBrush = new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(0);
            prefix.Foreground = ResolveThemeBrush("TextFillColorTertiaryBrush");
            label.Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush");
        }
    }

    private void UpdateCalculatorAndIntentUI()
    {
        ClearInputButton.Visibility = _viewModel.HasQueryText ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Visibility = (_viewModel.HasQueryText || _viewModel.HasActiveQuickMode) && !_viewModel.HasCalcResult
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_viewModel.HasCalcResult)
        {
            CalcResultButtonText.Text = _viewModel.CalcResultText;
            ToolTipService.SetToolTip(
                CalcResultButton,
                $"{_viewModel.CalcNormalizedExpression} {_viewModel.CalcResultText}");
            CalcResultButton.Visibility = Visibility.Visible;
        }
        else
        {
            CalcResultButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateStatusAndHotkeyUI()
    {
        string hotkeyText = _viewModel.HotkeyDisplayText;
        bool showHotkey = _viewModel.HasHotkey && !string.IsNullOrEmpty(hotkeyText);
        MicroHotkeyText.Text = hotkeyText;
        MicroHotkeyBadge.Visibility = showHotkey && ActualHeight >= 95 ? Visibility.Visible : Visibility.Collapsed;

        InputHotkeyText.Text = hotkeyText;
        UpdateCompactHoverQuickVisibility();

        bool everythingOk = _viewModel.IsEverythingConnected;
        Brush statusBrush = new SolidColorBrush(everythingOk
            ? Color.FromArgb(255, 16, 185, 129)
            : Color.FromArgb(255, 245, 158, 11));
        FooterStatusDot.Fill = statusBrush;
        WideStripStatusDot.Fill = statusBrush;
        FooterStatusText.Text = _localization.T(
            everythingOk
                ? "Search.Widget.EverythingConnected"
                : "Search.Widget.EverythingUnavailable");
        ToolTipService.SetToolTip(WideStripStatusDot, FooterStatusText.Text);

        bool privacyOn = _viewModel.IsPrivacyMaskEnabled;
        PrivacyMaskIcon.Glyph = ResolvePrivacyMaskGlyph(privacyOn, WindowsCompatibilityService.IsWindows11OrLater);
        PrivacyMaskLabel.Text = _localization.T(
            privacyOn
                ? "Search.Widget.PrivacyMaskActive"
                : "Search.Widget.PrivacyMask");
    }

    internal static string ResolvePrivacyMaskGlyph(bool privacyOn, bool isWindows11OrLater) =>
        privacyOn ? (isWindows11OrLater ? "\uED1A" : "\uE72E") : "\uE890";

    private void UpdateCompactHoverQuickVisibility(double? overrideWidth = null)
    {
        double width = overrideWidth is > 0
            ? overrideWidth.Value
            : (ActualWidth > 0 ? ActualWidth : _viewModel.Config.Width);

        bool isCompactBar = _currentTier == SearchWidgetLayoutTier.CompactBar;
        bool showCompactHover = isCompactBar &&
                                _viewModel.IsPointerHovering &&
                                !_viewModel.HasQueryText &&
                                !_viewModel.HasActiveQuickMode &&
                                width >= 260;
        CompactHoverQuickPanel.Visibility = showCompactHover ? Visibility.Visible : Visibility.Collapsed;

        bool showInputHotkey = _viewModel.HasHotkey &&
                               !string.IsNullOrEmpty(InputHotkeyText.Text) &&
                               !_viewModel.HasQueryText &&
                               !_viewModel.HasActiveQuickMode &&
                               !_viewModel.HasCalcResult &&
                               !showCompactHover &&
                               (width >= 230 || _currentTier != SearchWidgetLayoutTier.CompactBar);
        InputHotkeyBadge.Visibility = showInputHotkey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTierVisuals(double? overrideWidth = null, double? overrideHeight = null)
    {
        double width = overrideWidth is > 0
            ? overrideWidth.Value
            : (ActualWidth > 0 ? ActualWidth : _viewModel.Config.Width);
        double height = overrideHeight is > 0
            ? overrideHeight.Value
            : (ActualHeight > 0 ? ActualHeight : _viewModel.Config.Height);
        _currentTier = SearchWidgetViewModel.ResolveLayoutTier(width, height);

        bool isMicroTile = _currentTier == SearchWidgetLayoutTier.MicroTile;
        MicroTileButton.Visibility = isMicroTile ? Visibility.Visible : Visibility.Collapsed;
        ExpandedRootGrid.Visibility = isMicroTile ? Visibility.Collapsed : Visibility.Visible;
        if (isMicroTile)
        {
            MicroHotkeyBadge.Visibility = _viewModel.HasHotkey && height >= 95 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (_currentTier == SearchWidgetLayoutTier.CompactBar)
        {
            ExpandedRootGrid.Padding = new Thickness(6, 6, 6, 6);
            ExpandedRootGrid.RowSpacing = 0;
            ExpandedRootGrid.VerticalAlignment = VerticalAlignment.Center;
            SearchInputContainer.MinHeight = 34;
            SearchInputBox.MinHeight = 28;
            SearchInputBox.Padding = new Thickness(0, 4, 0, 3);
            PillsAndStripRow.Visibility = Visibility.Collapsed;
            WideStripScroller.Visibility = Visibility.Collapsed;
            WideStripStatusDot.Visibility = Visibility.Collapsed;
            BodyGrid.Visibility = Visibility.Collapsed;
            FooterGrid.Visibility = Visibility.Collapsed;
            UpdateCalculatorAndIntentUI();
            UpdateCompactHoverQuickVisibility(width);
            return;
        }

        ExpandedRootGrid.VerticalAlignment = VerticalAlignment.Stretch;
        SearchInputContainer.MinHeight = 36;
        SearchInputBox.MinHeight = 30;
        SearchInputBox.Padding = new Thickness(0, 5, 0, 4);

        if (_currentTier == SearchWidgetLayoutTier.WideStrip)
        {
            ExpandedRootGrid.Padding = new Thickness(10, 10, 10, 8);
            ExpandedRootGrid.RowSpacing = height < 96 ? 5 : 6;
            PillsAndStripRow.Visibility = Visibility.Visible;
            bool showStrip = _viewModel.ShowHistory && _viewModel.WideStripItems.Count > 0;
            WideStripScroller.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;
            WideStripStatusDot.Visibility = Visibility.Visible;

            if (showStrip && height >= 116)
            {
                Grid.SetRow(WideStripScroller, 1);
                Grid.SetColumn(WideStripScroller, 0);
                Grid.SetColumnSpan(WideStripScroller, 3);
                WideStripScroller.Margin = new Thickness(0, 6, 0, 0);
            }
            else
            {
                Grid.SetRow(WideStripScroller, 0);
                Grid.SetColumn(WideStripScroller, 1);
                Grid.SetColumnSpan(WideStripScroller, 1);
                WideStripScroller.Margin = new Thickness(0);
            }

            BodyGrid.Visibility = Visibility.Collapsed;
            FooterGrid.Visibility = Visibility.Collapsed;
            UpdateCalculatorAndIntentUI();
            UpdateCompactHoverQuickVisibility(width);
            return;
        }

        // L3 MediumCard and L4 LargeHub
        Grid.SetRow(WideStripScroller, 0);
        Grid.SetColumn(WideStripScroller, 1);
        Grid.SetColumnSpan(WideStripScroller, 1);
        WideStripScroller.Margin = new Thickness(0);

        ExpandedRootGrid.Padding = new Thickness(10, 10, 10, 8);
        ExpandedRootGrid.RowSpacing = 8;
        PillsAndStripRow.Visibility = Visibility.Visible;
        WideStripScroller.Visibility = Visibility.Collapsed;
        WideStripStatusDot.Visibility = Visibility.Collapsed;

        bool isLarge = _currentTier == SearchWidgetLayoutTier.LargeHub;
        bool showHistory = _viewModel.ShowHistory;
        bool showRecentOpened = isLarge && _viewModel.ShowRecentOpened;

        if (showHistory && showRecentOpened)
        {
            BodyGrid.Visibility = Visibility.Visible;
            HistoryColumnDef.Width = new GridLength(7, GridUnitType.Star);
            RecentOpenedColumnDef.Width = new GridLength(5, GridUnitType.Star);
            HistoryCard.Visibility = Visibility.Visible;
            RecentOpenedCard.Visibility = Visibility.Visible;
            BodyGrid.ColumnSpacing = 10;
        }
        else if (showHistory)
        {
            BodyGrid.Visibility = Visibility.Visible;
            HistoryColumnDef.Width = new GridLength(1, GridUnitType.Star);
            RecentOpenedColumnDef.Width = new GridLength(0, GridUnitType.Pixel);
            HistoryCard.Visibility = Visibility.Visible;
            RecentOpenedCard.Visibility = Visibility.Collapsed;
            BodyGrid.ColumnSpacing = 0;
        }
        else if (showRecentOpened)
        {
            BodyGrid.Visibility = Visibility.Visible;
            HistoryColumnDef.Width = new GridLength(0, GridUnitType.Pixel);
            RecentOpenedColumnDef.Width = new GridLength(1, GridUnitType.Star);
            HistoryCard.Visibility = Visibility.Collapsed;
            RecentOpenedCard.Visibility = Visibility.Visible;
            BodyGrid.ColumnSpacing = 0;
        }
        else
        {
            BodyGrid.Visibility = Visibility.Collapsed;
            HistoryCard.Visibility = Visibility.Collapsed;
            RecentOpenedCard.Visibility = Visibility.Collapsed;
        }

        FooterGrid.Visibility = height >= 185 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyMaskButton.Visibility = isLarge ? Visibility.Visible : Visibility.Collapsed;
        OpenSettingsButton.Visibility = isLarge ? Visibility.Visible : Visibility.Collapsed;
        ExpandPopupButton.Visibility = isLarge ? Visibility.Collapsed : Visibility.Visible;

        UpdateCalculatorAndIntentUI();
        UpdateCompactHoverQuickVisibility(width);
    }

    private void RebuildAllDataLists()
    {
        RebuildPinnedQueries();
        RebuildRecentQueries();
        RebuildWideStripChips();
        RebuildRecentOpenedItems();

        bool hasAnyHistory = _viewModel.PinnedQueryItems.Count > 0 || _viewModel.RecentQueryItems.Count > 0;
        EmptyHistoryText.Visibility = hasAnyHistory ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.Visibility = _viewModel.RecentQueryItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildPinnedQueries()
    {
        PinnedQueriesWrap.Children.Clear();
        IReadOnlyList<SearchPinnedQueryItem> pinned = _viewModel.PinnedQueryItems;
        PinnedQueriesWrap.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (SearchPinnedQueryItem item in pinned)
        {
            PinnedQueriesWrap.Children.Add(BuildPinnedQueryPill(item));
        }
    }

    private FrameworkElement BuildPinnedQueryPill(SearchPinnedQueryItem item)
    {
        string rawQuery = item.Query;

        var container = new Border
        {
            Height = 24,
            Background = ResolveThemeBrush("ControlFillColorSecondaryBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2, 0, 2, 0),
            MaxWidth = 220
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        var runButton = new Button
        {
            Style = (Style)Resources["SearchPlainRowButtonStyle"],
            Padding = new Thickness(7, 1, 4, 1),
            MinHeight = 22,
            MinWidth = 0,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center
        };
        var runContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        runContent.Children.Add(new FontIcon
        {
            FontFamily = ResolveSymbolFontFamily(),
            FontSize = 9.5,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            Glyph = "\uE735"
        });
        runContent.Children.Add(new TextBlock
        {
            Text = item.DisplayText,
            FontSize = 11.5,
            FontWeight = FontWeights.Normal,
            Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 165,
            VerticalAlignment = VerticalAlignment.Center
        });
        runButton.Content = runContent;
        runButton.Click += (_, _) => _viewModel.RunQuery(rawQuery);
        ToolTipService.SetToolTip(runButton, _localization.T("Search.Widget.SearchNow"));
        AutomationProperties.SetName(runButton, $"{_localization.T("Search.Widget.SearchNow")}: {item.DisplayText}");

        var unpinButton = new Button
        {
            Style = (Style)Resources["SearchSubtleIconButtonStyle"],
            Width = 20,
            Height = 20,
            MinWidth = 20,
            MinHeight = 20,
            CornerRadius = new CornerRadius(10),
            Opacity = 0.48,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                FontFamily = ResolveSymbolFontFamily(),
                FontSize = 9,
                Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
                Glyph = "\uE711"
            }
        };
        unpinButton.Click += (_, _) => _viewModel.UnpinQuery(rawQuery);
        container.PointerEntered += (_, _) => unpinButton.Opacity = 1.0;
        container.PointerExited += (_, _) => unpinButton.Opacity = 0.48;
        unpinButton.PointerEntered += (_, _) => unpinButton.Opacity = 1.0;
        unpinButton.PointerExited += (_, _) => unpinButton.Opacity = 0.48;
        string unpinLabel = _localization.T("Search.Widget.UnpinQuery");
        ToolTipService.SetToolTip(unpinButton, unpinLabel);
        AutomationProperties.SetName(unpinButton, $"{unpinLabel}: {item.DisplayText}");

        var flyout = new MenuFlyout();
        var searchMenuItem = new MenuFlyoutItem
        {
            Text = _localization.T("Search.Widget.SearchNow"),
            Icon = new FontIcon { FontFamily = ResolveSymbolFontFamily(), Glyph = "\uE721" }
        };
        searchMenuItem.Click += (_, _) => _viewModel.RunQuery(rawQuery);

        var unpinMenuItem = new MenuFlyoutItem
        {
            Text = unpinLabel,
            Icon = new FontIcon { FontFamily = ResolveSymbolFontFamily(), Glyph = "\uE77A" }
        };
        unpinMenuItem.Click += (_, _) => _viewModel.UnpinQuery(rawQuery);

        flyout.Items.Add(searchMenuItem);
        flyout.Items.Add(unpinMenuItem);
        container.ContextFlyout = flyout;

        row.Children.Add(runButton);
        row.Children.Add(unpinButton);
        container.Child = row;
        return container;
    }

    private void RebuildRecentQueries()
    {
        RecentQueriesPanel.Children.Clear();
        IReadOnlyList<SearchRecentQueryItem> recents = _viewModel.RecentQueryItems;
        bool hasRecents = recents.Count > 0;
        RecentSubHeaderText.Visibility = hasRecents && _viewModel.PinnedQueryItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecentQueriesPanel.Visibility = hasRecents ? Visibility.Visible : Visibility.Collapsed;

        foreach (SearchRecentQueryItem item in recents)
        {
            RecentQueriesPanel.Children.Add(BuildRecentQueryRow(item));
        }
    }

    private FrameworkElement BuildRecentQueryRow(SearchRecentQueryItem item)
    {
        string rawQuery = item.Query;

        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0)
        };

        var rowGrid = new Grid
        {
            ColumnSpacing = 4,
            MinHeight = 28
        };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchButton = new Button
        {
            Style = (Style)Resources["SearchPlainRowButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 3, 6, 3),
            MinHeight = 26,
            CornerRadius = new CornerRadius(6)
        };
        var rowContent = new Grid
        {
            ColumnSpacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var historyIcon = new FontIcon
        {
            FontFamily = ResolveSymbolFontFamily(),
            FontSize = 11,
            Foreground = ResolveThemeBrush("TextFillColorTertiaryBrush"),
            Glyph = "\uE81C",
            VerticalAlignment = VerticalAlignment.Center
        };
        var queryText = new TextBlock
        {
            Text = item.DisplayText,
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(historyIcon, 0);
        Grid.SetColumn(queryText, 1);
        rowContent.Children.Add(historyIcon);
        rowContent.Children.Add(queryText);

        searchButton.Content = rowContent;
        searchButton.Click += (_, _) => _viewModel.RunQuery(rawQuery);
        ToolTipService.SetToolTip(
            searchButton,
            _viewModel.EffectivePrivacyMaskActive ? _localization.T("Search.Widget.SearchNow") : item.DisplayText);

        var pinButton = new Button
        {
            Style = (Style)Resources["SearchSubtleIconButtonStyle"],
            Content = new FontIcon
            {
                FontFamily = ResolveSymbolFontFamily(),
                FontSize = 11,
                Foreground = ResolveThemeBrush(item.IsPinned ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"),
                Glyph = item.IsPinned ? "\uE735" : "\uE734"
            }
        };
        pinButton.Click += (_, _) => _viewModel.TogglePinQuery(rawQuery);
        string pinLabel = _localization.T(item.IsPinned ? "Search.Widget.UnpinQuery" : "Search.Widget.PinQuery");
        ToolTipService.SetToolTip(pinButton, pinLabel);
        AutomationProperties.SetName(pinButton, $"{pinLabel}: {item.DisplayText}");

        var removeButton = new Button
        {
            Style = (Style)Resources["SearchSubtleIconButtonStyle"],
            Content = new FontIcon
            {
                FontFamily = ResolveSymbolFontFamily(),
                FontSize = 9.5,
                Glyph = "\uE711"
            }
        };
        removeButton.Click += (_, _) => _viewModel.RemoveRecentQuery(rawQuery);
        string removeLabel = _localization.T("Search.Widget.RemoveQuery");
        ToolTipService.SetToolTip(removeButton, removeLabel);
        AutomationProperties.SetName(removeButton, $"{removeLabel}: {item.DisplayText}");

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.0,
            IsHitTestVisible = false
        };
        actionsPanel.Children.Add(pinButton);
        actionsPanel.Children.Add(removeButton);

        void ShowRowActions(bool visible)
        {
            rowBorder.Background = visible
                ? ResolveThemeBrush("SubtleFillColorSecondaryBrush")
                : new SolidColorBrush(Colors.Transparent);
            actionsPanel.Opacity = visible ? 1.0 : 0.0;
            actionsPanel.IsHitTestVisible = visible;
        }

        rowBorder.PointerEntered += (_, _) => ShowRowActions(true);
        rowBorder.PointerExited += (_, _) =>
        {
            if (searchButton.FocusState == FocusState.Unfocused &&
                pinButton.FocusState == FocusState.Unfocused &&
                removeButton.FocusState == FocusState.Unfocused)
            {
                ShowRowActions(false);
            }
        };
        rowBorder.GotFocus += (_, _) => ShowRowActions(true);
        rowBorder.LostFocus += (_, _) =>
        {
            if (searchButton.FocusState == FocusState.Unfocused &&
                pinButton.FocusState == FocusState.Unfocused &&
                removeButton.FocusState == FocusState.Unfocused)
            {
                ShowRowActions(false);
            }
        };

        Grid.SetColumn(searchButton, 0);
        Grid.SetColumn(actionsPanel, 1);
        rowGrid.Children.Add(searchButton);
        rowGrid.Children.Add(actionsPanel);
        rowBorder.Child = rowGrid;
        return rowBorder;
    }

    private void RebuildWideStripChips()
    {
        WideStripChipsPanel.Children.Clear();
        foreach (SearchPinnedQueryItem chip in _viewModel.WideStripItems)
        {
            string rawQuery = chip.Query;
            bool isPinned = _viewModel.PinnedQueryItems.Any(
                p => string.Equals(p.Query, rawQuery, StringComparison.OrdinalIgnoreCase));

            if (isPinned)
            {
                WideStripChipsPanel.Children.Add(BuildPinnedQueryPill(chip));
                continue;
            }

            var button = new Button
            {
                Style = (Style)Resources["SearchModePillButtonStyle"],
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(12),
                Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush"),
                BorderThickness = new Thickness(0),
                Content = new TextBlock
                {
                    Text = chip.DisplayText,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Normal,
                    Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140
                }
            };
            button.Click += (_, _) => _viewModel.RunQuery(rawQuery);
            WideStripChipsPanel.Children.Add(button);
        }
    }

    private void RebuildRecentOpenedItems()
    {
        int generation = Interlocked.Increment(ref _recentIconLoadGeneration);
        RecentOpenedPanel.Children.Clear();
        IReadOnlyList<SearchRecentOpenedItem> items = _viewModel.RecentOpenedItems;
        EmptyRecentOpenedText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (SearchRecentOpenedItem item in items)
        {
            var button = new Button
            {
                Style = (Style)Resources["SearchListRowButtonStyle"],
                Padding = new Thickness(6, 5, 6, 5),
                MinHeight = 38
            };

            var contentGrid = new Grid { ColumnSpacing = 8 };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconHost = new Grid
            {
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center
            };
            var fallbackIcon = new FontIcon
            {
                FontFamily = ResolveSymbolFontFamily(),
                FontSize = 14,
                Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
                Glyph = item.Glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconHost.Children.Add(fallbackIcon);

            string? detailPath = item.Source.DetailPath;
            if (!string.IsNullOrWhiteSpace(detailPath))
            {
                var iconImage = new Image
                {
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    Visibility = Visibility.Collapsed
                };
                iconHost.Children.Add(iconImage);
                _ = LoadRecentOpenedIconAsync(detailPath, iconImage, fallbackIcon, generation);
            }

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            textStack.Children.Add(new TextBlock
            {
                Text = item.DisplayTitle,
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                Foreground = ResolveThemeBrush("TextFillColorPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = item.DisplaySubtitle,
                FontSize = 10.5,
                FontWeight = FontWeights.Normal,
                Foreground = ResolveThemeBrush("TextFillColorTertiaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            Grid.SetColumn(iconHost, 0);
            Grid.SetColumn(textStack, 1);
            contentGrid.Children.Add(iconHost);
            contentGrid.Children.Add(textStack);
            button.Content = contentGrid;

            if (!_viewModel.EffectivePrivacyMaskActive)
            {
                string tooltip = !string.IsNullOrWhiteSpace(detailPath) ? detailPath : item.DisplayTitle;
                if (!string.IsNullOrWhiteSpace(tooltip))
                {
                    ToolTipService.SetToolTip(button, tooltip);
                }
            }

            Models.SearchRecommendationItem source = item.Source;
            button.Click += async (_, _) => await _viewModel.OpenRecentItemAsync(source);
            RecentOpenedPanel.Children.Add(button);
        }
    }

    private async Task LoadRecentOpenedIconAsync(
        string detailPath,
        Image iconImage,
        FontIcon fallbackIcon,
        int generation)
    {
        try
        {
            BitmapImage? bitmap = await IconHelper.GetIconAsync(
                detailPath,
                hideShortcutArrowOverlay: true,
                showImageFilesAsIcons: false,
                decodePixelWidth: 40);
            if (bitmap is null || _disposed || generation != Volatile.Read(ref _recentIconLoadGeneration))
            {
                return;
            }

            iconImage.Source = bitmap;
            iconImage.Visibility = Visibility.Visible;
            fallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Keep fallback glyph if native shell icon lookup fails for stale paths.
        }
    }

    private Brush ResolveThemeBrush(string key)
    {
        if (Resources.TryGetValue(key, out object? localObj) && localObj is Brush localBrush)
        {
            return localBrush;
        }

        if (Application.Current?.Resources is { } appRes &&
            appRes.TryGetValue(key, out object? appObj) &&
            appObj is Brush appBrush)
        {
            return appBrush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    private FontFamily ResolveSymbolFontFamily()
    {
        if (Application.Current?.Resources is { } appRes &&
            appRes.TryGetValue("SymbolThemeFontFamily", out object? fontObj) &&
            fontObj is FontFamily family)
        {
            return family;
        }

        return new FontFamily(WindowsCompatibilityService.IsWindows11OrLater
            ? "Segoe Fluent Icons, Segoe MDL2 Assets"
            : "Segoe MDL2 Assets");
    }
}
