using System.ComponentModel;
using DeskBox.Features.Appearance;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AppearanceSettingsSection : UserControl
{
    private AppearanceSettingsViewModel? _subscribedEditor;
    private IReadOnlyList<SettingsOption>? _materialEntryOptions;
    private bool _isSyncingMaterialEntry;

    public AppearanceSettingsSection()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SyncMaterialEntryCard();
    }

    public SettingsViewModel ViewModel { get; set; } = null!;

    public bool IsMaterialSubPageOpen => MaterialSubPagePanel.Visibility == Visibility.Visible;

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler? SubPageChanged;
    public event EventHandler<RoutedEventArgs>? ChooseBackgroundImageRequested;
    public event EventHandler<RoutedEventArgs>? ClearPerWidgetBackgroundsRequested;
    public event EventHandler<RoutedEventArgs>? OpenSystemTransparencyRequested;
    public event EventHandler? SliderDragStarted;
    public event EventHandler? SliderDragCompleted;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_subscribedEditor is not null)
        {
            _subscribedEditor.PropertyChanged -= OnEditorPropertyChanged;
            _subscribedEditor = null;
        }

        _materialEntryOptions = null;
        if (args.NewValue is AppearanceSettingsViewModel editor)
        {
            _subscribedEditor = editor;
            _subscribedEditor.PropertyChanged += OnEditorPropertyChanged;
        }

        SyncMaterialEntryCard(reloadOptions: true);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppearanceSettingsViewModel.CanEditCustomAccent) &&
            _subscribedEditor is { CanEditCustomAccent: true })
        {
            AccentColorExpander.IsExpanded = true;
        }

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(AppearanceSettingsViewModel.AvailableMaterialTypeOptions))
        {
            SyncMaterialEntryCard(reloadOptions: true);
        }
        else if (e.PropertyName == nameof(AppearanceSettingsViewModel.MaterialSummaryText) ||
            e.PropertyName == nameof(AppearanceSettingsViewModel.MaterialType))
        {
            SyncMaterialEntryCard();
        }
    }

    public void SyncMaterialEntryCard(bool reloadOptions = false)
    {
        if (_subscribedEditor is null)
        {
            return;
        }

        _isSyncingMaterialEntry = true;
        try
        {
            MaterialEntryCard.Description = _subscribedEditor.MaterialSummaryText;
            if (reloadOptions || _materialEntryOptions is null || !ReferenceEquals(MaterialEntryComboBox.ItemsSource, _materialEntryOptions))
            {
                _materialEntryOptions = _subscribedEditor.AvailableMaterialTypeOptions;
                MaterialEntryComboBox.ItemsSource = _materialEntryOptions;
            }

            SettingsOption? selected = _materialEntryOptions
                .FirstOrDefault(option => Equals(option.Value, _subscribedEditor.MaterialType));
            if (!ReferenceEquals(MaterialEntryComboBox.SelectedItem, selected))
            {
                MaterialEntryComboBox.SelectedItem = selected;
            }
        }
        finally
        {
            _isSyncingMaterialEntry = false;
        }
    }

    private void MaterialEntryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingMaterialEntry ||
            _subscribedEditor is null ||
            MaterialEntryComboBox.SelectedItem is not SettingsOption { Value: string materialType })
        {
            return;
        }

        _subscribedEditor.MaterialType = materialType;
    }

    private void MaterialEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenMaterialSubPage(animate: true);
    }

    public void OpenMaterialSubPage(bool animate = true)
    {
        bool wasAlreadyOpen = IsMaterialSubPageOpen;
        AppearanceTitleText.Visibility = Visibility.Collapsed;
        AppearanceOverviewPanel.Visibility = Visibility.Collapsed;
        MaterialSubPagePanel.Visibility = Visibility.Visible;
        ResetHostScrollToTop();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && !wasAlreadyOpen && IsLoaded)
        {
            MaterialSubPagePanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(MaterialSubPagePanel, 40f);
        }
    }

    public void CloseMaterialSubPage(bool animate = true)
    {
        if (!IsMaterialSubPageOpen)
        {
            return;
        }

        MaterialSubPagePanel.Visibility = Visibility.Collapsed;
        AppearanceTitleText.Visibility = Visibility.Visible;
        AppearanceOverviewPanel.Visibility = Visibility.Visible;
        SyncMaterialEntryCard();
        ResetHostScrollToTop();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && IsLoaded)
        {
            AppearanceOverviewPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(AppearanceOverviewPanel, -40f);
        }
    }

    public bool TryNavigateBack()
    {
        if (!IsMaterialSubPageOpen)
        {
            return false;
        }

        CloseMaterialSubPage(animate: true);
        return true;
    }

    private void ResetHostScrollToTop()
    {
        DependencyObject ancestor = this;
        while (ancestor is not null)
        {
            if (ancestor is ScrollViewer scroller)
            {
                scroller.ChangeView(null, 0, null, disableAnimation: true);
                return;
            }
            ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor);
        }
    }

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AppearanceSettingsViewModel editor ||
            sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        editor.NotifyAccentPresetPicked(AccentColorHelper.ToHex(color));
    }

    private void ChooseWidgetBackgroundImage_Click(object sender, RoutedEventArgs e) =>
        ChooseBackgroundImageRequested?.Invoke(sender, e);

    private void ClearWidgetBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppearanceSettingsViewModel editor)
        {
            editor.ClearCurrentBackgroundImage();
        }
    }

    private void ClearPerWidgetBackgroundsButton_Click(object sender, RoutedEventArgs e) =>
        ClearPerWidgetBackgroundsRequested?.Invoke(sender, e);

    private void OpenSystemTransparencySettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSystemTransparencyRequested?.Invoke(sender, e);

    private void AppearanceSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e) =>
        SliderDragStarted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        SliderDragStarted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_LostFocus(object sender, RoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
    }
}
